using System;
using Godot;
using System.Collections.Generic;

namespace Widgets;

/// <summary>
/// A text input control with composable overflow handling — clip, shrink-to-fit, multi-line,
/// scroll, spellchecking, and optional collapse-when-unfocused — built around a single string source-of-truth
/// and designed for subclassing. An alternative to Godot's built-in <c>LineEdit</c> and
/// <c>TextEdit</c> when their design choices don't fit the use case.
/// </summary>
[Tool, GlobalClass, Icon("res://addons/text_widget/icon.svg")]
public partial class TextWidget : Control
{
    // ===== Signals =====

    /// <summary>
    /// Emitted when the user submits the text (presses Enter).
    /// Subclasses can override <see cref="_OnSubmit"/> to add behavior before this fires.
    /// </summary>
    [Signal] public delegate void SubmittedEventHandler();
    
    /// <summary>
    /// Emitted when <see cref="Text"/> changes through any path — user input, paste, undo/redo, or
    /// programmatic assignment. Provides both the previous and new values so subscribers can diff.
    /// </summary>
    [Signal] public delegate void TextChangedEventHandler(string oldText, string newText);
    
    /// <summary>
    /// Emitted when the widget transitions between rendering as multi-line and collapsing to single-line.
    /// With <see cref="OverflowDisplay.Clip"/> + <see cref="OverflowDisplay.Multiline"/>, this follows focus —
    /// expands on focus enter, collapses on focus exit. Fires every transition.
    /// </summary>
    /// <remarks>
    /// Useful for parent layouts that need to react to the widget's vertical footprint changing — adjust
    /// surrounding padding, save scroll position, dim other widgets, etc. Subclasses that want to react
    /// to the same event should override <see cref="_OnMultilineActiveChanged"/> instead; this signal is
    /// for external observers.
    /// </remarks>
    /// <param name="nowActive"><c>true</c> if the widget is now expanded to multi-line, <c>false</c> if collapsed to single-line.</param>
    [Signal] public delegate void MultilineActiveChangedEventHandler(bool nowActive);

    /// <summary>
    /// Emitted whenever <see cref="CaretIndex"/> changes — through arrow keys, mouse clicks, edits,
    /// programmatic assignment, or any other path. Fires once per movement, after the new index is
    /// committed.
    /// </summary>
    /// <remarks>
    /// Useful for status displays (line/column indicators), scroll synchronization between widgets,
    /// IME positioning, or any consumer that needs to track cursor position without subclassing.
    /// Edits that move the caret as a side effect of changing the text emit both <see cref="TextChanged"/>
    /// and this signal — order is text first, then caret.
    /// </remarks>
    /// <param name="oldIndex">The caret's character index before the movement.</param>
    /// <param name="newIndex">The caret's character index after the movement.</param>
    [Signal] public delegate void CaretMovedEventHandler(int oldIndex, int newIndex);

    /// <summary>
    /// Emitted when the active text selection changes — either through selection extension, collapse,
    /// or replacement. Fires when <see cref="SelectionAnchorIndex"/> or <see cref="CaretIndex"/> moves
    /// in a way that alters <see cref="SelectionStartIndex"/> or <see cref="SelectionEndIndex"/>.
    /// </summary>
    /// <remarks>
    /// Useful for selection-dependent UI like copy/cut buttons enabling and disabling based on
    /// <see cref="HasSelection"/>, or for synchronizing selection state across paired widgets.
    /// A selection that grows and then shrinks back to the same character range still fires once
    /// per movement — this is not debounced.
    /// </remarks>
    /// <param name="start">The new <see cref="SelectionStartIndex"/> — leftmost character in the selection. Equal to <paramref name="end"/> when no selection is active.</param>
    /// <param name="end">The new <see cref="SelectionEndIndex"/> — one past the rightmost character in the selection. Equal to <paramref name="start"/> when no selection is active.</param>
    [Signal] public delegate void SelectionChangedEventHandler(int start, int end);

    /// <summary>
    /// Bitmask of overflow-handling behaviors. Multiple flags compose: e.g. <see cref="ShrinkToFit"/> | <see cref="Clip"/>
    /// shrinks toward the minimum font size, then clips whatever doesn't fit at that floor.
    /// </summary>
    [Flags]
    public enum OverflowDisplay
    {
        /// <summary>No overflow handling. Text may draw past the widget's rect (useful only if a parent clips for you).</summary>
        // ReSharper disable once UnusedMember.Global
        None        = 0,
        /// <summary>Drawing is clipped to the widget's rect. Without this flag, glyphs that fall outside still render. When unfocused, a non-empty <see cref="OverflowMarker"/> is appended to indicate truncation.</summary>
        Clip        = 1 << 0,
        /// <summary>Font size shrinks from the effective font size down to <see cref="MinFontSize"/> until the text fits on one line. If even the minimum doesn't fit, the result is then subject to <see cref="Clip"/> if that flag is also set.</summary>
        ShrinkToFit = 1 << 1,
        /// <summary>Text wraps onto additional lines when it exceeds the widget width. Composes with <see cref="ShrinkToFit"/> (shrink runs first) and <see cref="Clip"/> (collapses to first line + marker when unfocused).</summary>
        Multiline   = 1 << 2
    }
    
    /// <summary>
    /// How input length is constrained. Only meaningful for single-line widgets — multiline relies on
    /// <see cref="Scrollable"/> and <see cref="MaxLines"/> instead.
    /// </summary>
    public enum CharacterLimit
    {
        /// <summary>No length limit. Content past the viewport scrolls horizontally to follow the caret.</summary>
        None,
        /// <summary>Reject input that would not fit in the visible viewport. Measured against the smallest font size the widget will use (<see cref="MinFontSize"/> when ShrinkToFit is set, otherwise the resolved font size). Existing text exceeding the budget is preserved; only further input is refused.</summary>
        Visible,
        /// <summary>Reject input that would push the text past <see cref="MaxLength"/> characters. Existing text exceeding the limit is preserved; only further input is refused. Paste truncates to fit.</summary>
        Fixed
    }
    
    /// <summary>The widget's text content.</summary>
    [ExportGroup("Text")]
    [Export] public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            var oldCaret = CaretIndex;
            var oldStart = SelectionStartIndex;
            var oldEnd = SelectionEndIndex;
            SetTextInternal(value);
            ClampCaret();
            ClearHistory();
            InvalidateShaping();
            ResetScroll();
            QueueRedraw();
            UpdateMinimumSize();
            EmitCaretMovedIfNeeded(oldCaret);
            EmitSelectionChangedIfNeeded(oldStart, oldEnd);
        }
    }

    /// <summary>Placeholder shown when <see cref="Text"/> is empty. Subclasses can override <see cref="_GetPlaceholderText"/> to compute dynamically.</summary>
    [Export] public string Placeholder { get; set; } = "";

    /// <summary>Font size in pixels. Set to 0 to inherit the themed default (LineEdit's <c>font_size</c>).</summary>
    [Export(PropertyHint.Range, "0,128,1")] public int FontSize
    {
        get => _fontSize;
        set
        {
            var clamped = Mathf.Max(0, value);
            if (_fontSize == clamped) return;
            _fontSize = clamped;
            // Maintain MinFontSize <= effective font size. Resolve uses the new _fontSize.
            ClampMinFontSizeAgainstEffective();
            InvalidateShaping();
            QueueRedraw();
            UpdateMinimumSize();
        }
    }

    /// <summary>Horizontal alignment of text within the widget's content rect.</summary>
    [ExportGroup("Layout")]
    [Export] public HorizontalAlignment HorizontalAlignment
    {
        get => _horizontalAlignment;
        set { _horizontalAlignment = value; QueueRedraw(); }
    }

    /// <summary>Vertical alignment of text within the widget's content rect. Ignored when actively scrolling (scroll mode top-anchors and uses the scroll offset for vertical positioning).</summary>
    [Export] public VerticalAlignment VerticalAlignment
    {
        get => _verticalAlignment;
        set { _verticalAlignment = value; QueueRedraw(); }
    }

    /// <summary>Whether the user can edit the text.</summary>
    [ExportGroup("Behavior")]
    [Export] public bool Editable
    {
        get => _editable;
        set
        {
            if (_editable == value) return;
            _editable = value;
            if (!_editable) ClearHistory();
            QueueRedraw();
        }
    }

    /// <summary>Whether the widget releases focus when the user submits via Enter.</summary>
    [Export] public bool ReleaseFocusOnSubmit { get; set; } = true;
    
    /// <summary>How the widget constrains input length. See <see cref="CharacterLimit"/> for the available modes.</summary>
    [Export] public CharacterLimit LimitMode
    {
        get => _limitMode;
        set { _limitMode = value; NotifyPropertyListChanged(); }
    }

    /// <summary>
    /// Maximum number of characters when <see cref="LimitMode"/> is <see cref="CharacterLimit.Fixed"/>.
    /// Ignored otherwise. Lowering this below the current text length does not retroactively truncate;
    /// only further input and paste are clamped.
    /// </summary>
    [Export(PropertyHint.Range, "1,8192,1")] public int MaxLength { get; set; } = 256;
    
    /// <summary>
    /// Whether the widget computes and renders spell-check marks. When enabled, words not recognized
    /// by <see cref="Spelling.IsCorrect"/> are underlined with a wavy squiggle. The dictionary loads
    /// lazily on first check; call <see cref="Spelling.Load"/> in your editor lifecycle to warm it
    /// up and avoid a first-keystroke hitch.
    /// </summary>
    [Export] public bool SpellCheck
    {
        get => _spellCheck;
        set
        {
            if (_spellCheck == value) return;
            _spellCheck = value;
            _textMarksDirty = true;
            QueueRedraw();
        }
    }

    /// <summary>
    /// Combinable overflow-handling flags. See <see cref="OverflowDisplay"/> for which combinations make sense.
    /// </summary>
    [ExportGroup("Overflow")]
    [Export(PropertyHint.Flags, "Clip,Shrink To Fit,Multiline")] public OverflowDisplay DisplayOverflow
    {
        get => _displayOverflow;
        set
        {
            if (_displayOverflow == value) return;
            var lostMultiline = _displayOverflow.HasFlag(OverflowDisplay.Multiline) && !value.HasFlag(OverflowDisplay.Multiline);
            _displayOverflow = value;
            UpdateClipContents();
            NotifyPropertyListChanged();
            InvalidateShaping();
            QueueRedraw();
            UpdateMinimumSize();
            if (lostMultiline) ResetScroll();
            RecomputeMultilineActive();
        }
    }

    /// <summary>Text appended after truncated content when <see cref="DisplayOverflow"/> includes <see cref="OverflowDisplay.Clip"/> and the widget is unfocused. Empty string disables the marker (silent clip). Defaults to a single ellipsis character.</summary>
    [Export, ExportSubgroup("Clip")] public string OverflowMarker
    {
        get => _overflowMarker;
        set
        {
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
            value ??= "";
            if (_overflowMarker == value) return;
            _overflowMarker = value;
            InvalidateShaping();
            QueueRedraw();
        }
    }

    /// <summary>Lower bound on font size when <see cref="DisplayOverflow"/> includes <see cref="OverflowDisplay.ShrinkToFit"/>. Cannot exceed the effective font size; assignments above that are clamped down on the way in.</summary>
    [Export(PropertyHint.Range, "4,128,1"), ExportSubgroup("Shrink To Fit")] public int MinFontSize
    {
        get => _minFontSize;
        set
        {
            var clamped = Mathf.Clamp(value, 1, ResolveFontSize());
            if (_minFontSize == clamped) return;
            _minFontSize = clamped;
            InvalidateShaping();
            QueueRedraw();
            UpdateMinimumSize();
        }
    }

    /// <summary>
    /// When set, viewport height is capped at <see cref="MaxLines"/> visible lines and content beyond scrolls within.
    /// When unset, the widget grows vertically to fit all wrapped content. Only meaningful when
    /// <see cref="OverflowDisplay.Multiline"/> is in <see cref="DisplayOverflow"/>.
    /// </summary>
    [ExportSubgroup("Multiline")]
    [Export] public bool Scrollable
    {
        get => _scrollable;
        set
        {
            if (_scrollable == value) return;
            _scrollable = value;
            if (!_scrollable) ResetScroll();
            UpdateClipContents();
            NotifyPropertyListChanged();
            UpdateMinimumSize();
            QueueRedraw();
        }
    }

    /// <summary>
    /// Minimum number of visual lines of height the widget reports to its parent. Acts as a floor: the widget
    /// reserves space for at least this many lines even when content is shorter. Only meaningful when
    /// <see cref="OverflowDisplay.Multiline"/> is in <see cref="DisplayOverflow"/>.
    /// </summary>
    [ExportSubgroup("Multiline/Dimensions")]
    [Export(PropertyHint.Range, "1,32,1")] public int MinLines
    {
        get => _minLines;
        set
        {
            var clamped = Mathf.Max(1, value);
            if (_minLines == clamped) return;
            _minLines = clamped;
            if (_maxLines != 0 && _maxLines < _minLines)
            {
                _maxLines = _minLines;
                NotifyPropertyListChanged();
            }
            UpdateMinimumSize();
        }
    }

    /// <summary>
    /// Maximum number of visual lines visible at once when <see cref="Scrollable"/> is set. Content beyond that
    /// scrolls within the viewport. A value of 0 means unlimited (the widget grows to fit content). If
    /// <see cref="MaxLines"/> is below <see cref="MinLines"/>, the floor wins and the ceiling is ignored.
    /// Only meaningful when both <see cref="OverflowDisplay.Multiline"/> and <see cref="Scrollable"/> are set.
    /// </summary>
    [Export(PropertyHint.Range, "0,64,1")] public int MaxLines
    {
        get => _maxLines;
        set
        {
            var clamped = Mathf.Max(0, value);
            if (_maxLines == clamped) return;
            _maxLines = clamped;
            if (_maxLines != 0 && _maxLines < _minLines)
            {
                _minLines = _maxLines;
                NotifyPropertyListChanged();
            }
            UpdateMinimumSize();
            ClampScrollToContentY();
        }
    }

    /// <summary>Extra pixels added between visual lines when <see cref="DisplayOverflow"/> includes <see cref="OverflowDisplay.Multiline"/>. Negative values tighten line stacking below the font's natural height; 0 uses the font's natural height.</summary>
    [Export(PropertyHint.Range, "-16,32,1")] public int LineSpacing
    {
        get => _lineSpacing;
        set
        {
            if (_lineSpacing == value) return;
            _lineSpacing = value;
            InvalidateShaping();
            QueueRedraw();
            UpdateMinimumSize();
        }
    }
    
        /// <summary>
    /// The color used to draw text content. Falls back to the themed <c>font_color</c>
    /// (resolving against LineEdit when no project-level theme defines one) when no
    /// override is set.
    /// </summary>
    public Color FontColor => _fontColorEnabled ? _fontColor : GetColor(THEME_COLOR_FONT);

    /// <summary>
    /// The color of the text outline, when <see cref="OutlineSize"/> is positive.
    /// Falls back to the themed <c>font_outline_color</c>. Has no visible effect when
    /// outline size is zero.
    /// </summary>
    public Color FontOutlineColor => _fontOutlineColorEnabled ? _fontOutlineColor : GetColor(THEME_COLOR_FONT_OUTLINE);

    /// <summary>
    /// The color used to draw text that falls within the active selection. Falls back
    /// to the themed <c>font_selected_color</c>, or to <see cref="FontColor"/> when the
    /// theme doesn't define one. Has no visible effect when the override isn't enabled
    /// — selected text just uses <see cref="FontColor"/>. Drawn over the selection
    /// rectangle, so it appears on top of <see cref="SelectionColor"/>.
    /// </summary>
    public Color FontSelectedColor => _fontSelectedColorEnabled ? _fontSelectedColor : (HasColor(THEME_COLOR_FONT_SELECTED) ? GetColor(THEME_COLOR_FONT_SELECTED) : FontColor);

    /// <summary>
    /// The color used to draw the placeholder text shown when <see cref="Text"/> is
    /// empty. Falls back to the themed <c>font_placeholder_color</c>. Does not affect
    /// the placeholder string itself — that's controlled by <see cref="Placeholder"/>
    /// or <see cref="_GetPlaceholderText"/>.
    /// </summary>
    public Color PlaceholderColor => _placeholderColorEnabled ? _placeholderColor : GetColor(THEME_COLOR_PLACEHOLDER);

    /// <summary>
    /// The color used to draw text when <see cref="Editable"/> is false. Falls back to
    /// <see cref="FontColor"/>. Override this to visually distinguish read-only state
    /// — e.g. a dimmed gray instead of the editable text color.
    /// </summary>
    public Color ReadOnlyColor => _readOnlyColorEnabled ? _readOnlyColor : FontColor;

    /// <summary>
    /// The color of the caret (text cursor) and the scrollbar thumb/track, when the
    /// widget is focused and editable. Falls back to the themed <c>caret_color</c>.
    /// </summary>
    public Color CaretColor => _caretColorEnabled ? _caretColor : GetColor(THEME_COLOR_CARET);

    /// <summary>
    /// The color of the rectangle drawn behind selected text. The actual fill is drawn
    /// at 50% alpha of this value, so opaque colors render as semi-transparent
    /// highlights. Falls back to the themed <c>selection_color</c>.
    /// </summary>
    public Color SelectionColor => _selectionColorEnabled ? _selectionColor : GetColor(THEME_COLOR_SELECTION);

    /// <summary>
    /// Width in pixels of the outline drawn behind glyphs when <see cref="FontOutlineColor"/>
    /// is set. Zero disables the outline. Falls back to the themed <c>outline_size</c>
    /// constant, or zero when no theme provides one.
    /// </summary>
    public int OutlineSize => _outlineSizeEnabled ? _outlineSize : (HasConstant(THEME_CONSTANT_OUTLINE_SIZE) ? GetConstant(THEME_CONSTANT_OUTLINE_SIZE) : 0);

    /// <summary>
    /// Width in pixels of the caret rectangle. Falls back to the themed <c>caret_width</c>
    /// constant. Always at least 1 — values below 1 are clamped up so the caret remains
    /// visible regardless of theme or override.
    /// </summary>
    public int CaretWidth => Mathf.Max(1, _caretWidthEnabled ? _caretWidth : GetConstant(THEME_CONSTANT_CARET_WIDTH));
    
    /// <summary>
    /// True if there's an active text selection — caret and selection anchor are at different
    /// positions. False when they coincide, regardless of whether there was a selection earlier
    /// in the session. Reflects the current state, not history.
    /// </summary>
    public bool HasSelection => SelectionAnchorIndex != CaretIndex;

    /// <summary>
    /// The caret's current character position as an index into <see cref="Text"/>. Range is
    /// <c>[0, Text.Length]</c> — note the inclusive upper bound, since the caret can sit past
    /// the final character. When <see cref="HasSelection"/> is true, this is one end of the
    /// selection; the other is <see cref="SelectionAnchorIndex"/>.
    /// </summary>
    public int CaretIndex { get; private set; }

    /// <summary>
    /// The selection's fixed end — the position the caret was at when the selection began. Stays
    /// put while the caret moves to extend or shrink the selection. Equal to <see cref="CaretIndex"/>
    /// when no selection is active.
    /// </summary>
    public int SelectionAnchorIndex { get; private set; }

    /// <summary>
    /// The lower of <see cref="CaretIndex"/> and <see cref="SelectionAnchorIndex"/> — the leftmost
    /// character index in the active selection. Equal to <see cref="CaretIndex"/> when no selection
    /// is active. Pair with <see cref="SelectionEndIndex"/> to get the selection's character range
    /// as a half-open interval <c>[start, end)</c>.
    /// </summary>
    public int SelectionStartIndex => Mathf.Min(SelectionAnchorIndex, CaretIndex);

    /// <summary>
    /// The higher of <see cref="CaretIndex"/> and <see cref="SelectionAnchorIndex"/> — one past the
    /// rightmost character index in the active selection. Equal to <see cref="CaretIndex"/> when no
    /// selection is active. Pair with <see cref="SelectionStartIndex"/> to get the selection's
    /// character range as a half-open interval <c>[start, end)</c>.
    /// </summary>
    public int SelectionEndIndex => Mathf.Max(SelectionAnchorIndex, CaretIndex);

    /// <summary>
    /// Whether the widget is currently rendering as multi-line. With Clip + Multiline this follows focus
    /// (collapsed when unfocused, expanded when focused). Without Clip, equals the Multiline flag.
    /// </summary>
    public bool IsMultilineActive { get; private set; }
    
    private string _text = "";
    private int _fontSize = 16;
    private HorizontalAlignment _horizontalAlignment = HorizontalAlignment.Left;
    private VerticalAlignment _verticalAlignment = VerticalAlignment.Center;
    private bool _editable = true;
    private OverflowDisplay _displayOverflow = OverflowDisplay.Clip;
    private string _overflowMarker = "…";
    private int _minFontSize = 8;
    private bool _scrollable;
    private int _minLines = 1;
    private int _maxLines;
    private int _lineSpacing;
    private bool _caretVisible = true;
    private double _caretBlinkAccum;
    private const double CARET_BLINK_INTERVAL = 0.65;
    private const double CARET_BLINK_PAUSE_AFTER_ACTION = 0.3;
    private double _caretActionPauseRemaining;
    private CharacterLimit _limitMode;
    private bool _spellCheck;
    

    // ===== Entry Points =====

    public override void _Ready()
    {
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.Ibeam;
        UpdateClipContents();
        SetProcess(true);
        IsMultilineActive = ComputeIsMultilineActive();
        TooltipText = " ";
    }
     
    public override void _ExitTree()
    {
        FreeLineRids();
        InvalidateMarker();
        DisposeContextMenu();
    }
     
    public override void _Notification(int what)
    {
        switch (what)
        {
            case (int)NotificationThemeChanged: OnThemeChanged(); break;
            case (int)NotificationResized:      OnResized();      break;
            case (int)NotificationFocusEnter:
            case (int)NotificationFocusExit:    OnFocusChanged(); break;
        }
    }
     
    public override void _Process(double delta)
    {
        if (_selecting && _scrollable) HandleDragScrollPoll();
        UpdateCaretBlink(delta);
        
        if (!_editDebounceActive || Time.GetTicksMsec() - _lastEditTimeMs < SPELL_CHECK_DEBOUNCE_MS) return;
        
        _editDebounceActive = false;
        QueueRedraw();
    }
     
    public override void _GuiInput(InputEvent @event)
    {
        var handled = @event switch
        {
            InputEventMouseButton mb when _selecting && !mb.Pressed => HandleSelectionEnd(),
            InputEventMouseButton mb => HandleMouseButton(mb),
            InputEventMouseMotion mm => HandleMouseMotion(mm),
            InputEventKey key        => HandleKey(key),
            _                        => false
        };
        if (handled) AcceptEvent();
    }
     
    public override Vector2 _GetMinimumSize()
    {
        var style = GetStyle(THEME_STYLE_NORMAL);
        var font = GetFont(THEME_FONT_DEFAULT);
        var margins = style.GetMinimumSize();
        var themedSize = ResolveFontSize();
     
        var isMultiline = _displayOverflow.HasFlag(OverflowDisplay.Multiline);
        var collapsed = isMultiline && !IsMultilineActive;
     
        // Single-line, or multi-line collapsed-when-unfocused: one line of height.
        if (!isMultiline || collapsed) return new Vector2(0f, font.GetHeight(themedSize) + margins.Y);
     
        return new Vector2(0f, ComputeMultilineMinHeight(font, themedSize) + margins.Y);
    }
     
    public override void _Draw()
    {
        EnsureShapedWithHotReloadGuard();
        DrawBackground();

        if (_lineRids.Count == 0) return;

        if (ShouldDrawSelection()) DrawSelection();
        DrawText();
        DrawSelectedText();
        DrawOverflowMarker();
        DrawTextMarks();
        DrawScrollbar();
        if (ShouldDrawCaret()) DrawCaret();
    }
     
    public override void _ValidateProperty(Godot.Collections.Dictionary property) => ResolvePropertyUsageOverride(ref property);

    // ===== Extension hooks =====
    /// <summary>
    /// Returns the screen-space position of the caret's bottom-left corner — the natural anchor
    /// point for placing a popup (autocomplete list, suggestion tooltip, etc.) immediately below
    /// the caret. Adds the widget's global rect offset to the caret's local draw position, so the
    /// result is in absolute screen coordinates suitable for a <see cref="Popup"/>.
    /// </summary>
    /// <remarks>
    /// The returned point is the bottom-left of where the caret rectangle currently draws — i.e.
    /// one line-height below the baseline ascent, at the caret's horizontal position. Callers
    /// positioning popups below the caret should use this directly; callers wanting to render
    /// above the caret should subtract the popup's own height.
    /// </remarks>
    public Vector2 GetCaretScreenPos()
    {
        EnsureShaped();
        if (_lineRids.Count == 0) return GetGlobalPosition();

        var font = GetFont(THEME_FONT_DEFAULT);
        var fontSize = _resolvedFontSize > 0 ? _resolvedFontSize : ResolveFontSize();
        var ascent = font.GetAscent(fontSize);
        var lineHeight = font.GetHeight(fontSize);

        var (line, col) = LocateCaret(CaretIndex);
        if (line >= _lineRids.Count) return GetGlobalPosition();

        var caretX = GetLineDrawOriginX(line) + GetCaretRelativeXOnLine(line, col);
        var caretBottomY = GetLineBaselineY(line) - ascent + lineHeight;

        return GetGlobalPosition() + new Vector2(caretX, caretBottomY);
    }
    
    /// <summary>
    /// Returns the text shown when <see cref="Text"/> is empty. Override to compute the placeholder
    /// dynamically — e.g. deriving it from a type, validation state, or context the subclass owns.
    /// The default returns the <see cref="Placeholder"/> export.
    /// </summary>
    protected virtual string _GetPlaceholderText() => Placeholder;
    
    /// <summary>
    /// Called when the user submits the text (Enter without Shift). Runs before the <see cref="Submitted"/>
    /// signal fires and before focus is released per <see cref="ReleaseFocusOnSubmit"/>. Override to
    /// validate, parse, or transform the text. Throwing or modifying <see cref="Text"/> here is supported;
    /// the surrounding submit pipeline doesn't depend on the hook leaving state untouched.
    /// </summary>
    protected virtual void _OnSubmit() { }

    /// <summary>
    /// Called when the widget transitions between actively rendering as multi-line versus collapsed
    /// to single-line. With Clip + Multiline, this follows focus: expands on focus enter, collapses
    /// on focus exit. Fires every transition.
    /// </summary>
    protected virtual void _OnMultilineActiveChanged(bool nowActive) { }
    
    /// <summary>
    /// Called when <see cref="Text"/> changes through any path. Runs before <see cref="TextChanged"/>
    /// fires. Default does nothing. Override to validate, reformat, or react to content changes.
    /// </summary>
    protected virtual void _OnTextChanged(string oldText, string newText) { }
    
    /// <summary>
    /// Called when the text or caret position changes in a way that could affect completion state —
    /// any edit, or any caret movement. Default does nothing. Override to recompute completion
    /// candidates, show/hide a popup, etc.
    /// </summary>
    protected virtual void _OnCompletionContextChanged(string text, int caretIndex) { }

    /// <summary>
    /// Called for key events before the base handles them, giving subclasses a chance to intercept
    /// (e.g. routing Tab/Up/Down/Enter/Escape to a completion popup). Return <c>true</c> if handled —
    /// the base will then skip its own handling and accept the event. Default returns <c>false</c>.
    /// </summary>
    protected virtual bool _OnCompletionKeyPressed(InputEventKey key) => false;
    
    /// <summary>
    /// Returns the set of marks (squiggles, underlines, highlights) to render over the text. The
    /// default implementation emits spell-check marks when <see cref="SpellCheck"/> is set. Subclasses
    /// can override to add their own marks alongside the base:
    /// <code>
    /// protected override IEnumerable&lt;TextMark&gt; _GetTextMarks() =&gt;
    ///     base._GetTextMarks().Concat(MyParseErrorMarks());
    /// </code>
    /// Positions in returned marks are character indices into <see cref="Text"/>. Marks are recomputed
    /// when the text changes; if a subclass's marks depend on external state, call
    /// <see cref="InvalidateTextMarks"/> when that state changes.
    /// </summary>
    protected virtual IEnumerable<TextMark> _GetTextMarks() => SpellCheck && !Engine.IsEditorHint() ? GetSpellCheckMarks() : [];
    
    public override Control _MakeCustomTooltip(string forText)
    {
        // Returning null here means: use the default Godot tooltip popup with `forText`
        // as its content. We override _GetTooltip (below) to compute that text dynamically
        // from the mark under the cursor; this hook just lets the popup render normally.
        return null!;
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (string.IsNullOrEmpty(_text)) return "";
        var charIndex = CharIndexAtLocalPos(atPosition);
        if (!TryGetTextMarkAt(charIndex, out var mark)) return "";
        return mark.Tooltip ?? "";
    }

    /// <summary>Force the next draw to re-collect marks from <see cref="_GetTextMarks"/>.</summary>
    protected void InvalidateTextMarks()
    {
        _textMarksDirty = true;
        QueueRedraw();
    }
    
    /// <summary>
    /// Returns the first text mark containing <paramref name="charIndex"/>, skipping marks that
    /// are currently visually suppressed by the active-edit range. Caret-edge inclusivity matches
    /// the squiggle: a click at the trailing edge of a mark still hits the mark.
    /// </summary>
    protected bool TryGetTextMarkAt(int charIndex, out TextMark mark)
    {
        EnsureTextMarks();
        mark = default;
        if (_cachedTextMarks == null) return false;
        foreach (var m in _cachedTextMarks)
        {
            if (charIndex < m.Start || charIndex > m.End || IsMarkSuppressed(m)) continue;
            mark = m;
            return true;
        }
        return false;
    }
    
    // Data Structures
    
    /// <summary>
    /// A semantic mark on a text range — used for spell-check squiggles, parse-error underlines,
    /// find/replace highlights, etc. Positions are character indices into <see cref="Text"/> and
    /// remain valid until the text changes. The <see cref="Category"/> tag lets consumers (e.g. the
    /// context menu) distinguish marks emitted by different subsystems.
    /// </summary>
    protected readonly struct TextMark(int start, int end, Color color, string? tooltip = null, string? category = null)
    {
        public readonly int Start = start;
        public readonly int End = end;
        public readonly Color Color = color;
        public readonly string? Tooltip = tooltip;
        public readonly string? Category = category;
    }
}