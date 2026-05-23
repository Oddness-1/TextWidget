using Godot;

namespace Widgets;

public partial class TextWidget
{
        /// <summary>
    /// Replaces the text from <paramref name="start"/> to <paramref name="end"/> with
    /// <paramref name="replacement"/>, then positions the caret. Records a single undo snapshot —
    /// the entire replacement undoes in one step regardless of length.
    /// </summary>
    /// <param name="start">The starting character index (inclusive) of the range to replace.</param>
    /// <param name="end">The ending character index (exclusive) of the range to replace.</param>
    /// <param name="replacement">The text to insert in place of the replaced range. Empty or null inserts nothing — equivalent to deletion.</param>
    /// <param name="caretOffset">
    /// Where to place the caret relative to the start of the inserted text. <c>0</c> puts the caret
    /// at the start of the replacement; <c>replacement.Length</c> puts it at the end. The default
    /// <c>-1</c> means "end of replacement" — equivalent to passing <c>replacement.Length</c> but
    /// without needing to compute it at the call site. Values outside <c>[0, replacement.Length]</c>
    /// are clamped.
    /// </param>
    /// <remarks>
    /// Intended for any caller that needs to perform a range edit while controlling caret placement —
    /// completion providers swapping a partial token for a full one, auto-pairing logic placing the
    /// caret between an inserted opener/closer, find-and-replace operations, etc. The selection is
    /// collapsed to the caret position after the replacement; if you need to preserve a selection,
    /// re-establish it manually after this call.
    /// </remarks>
    // ReSharper disable once MemberCanBeProtected.Global
    public void ReplaceRange(int start, int end, string replacement, int caretOffset = -1)
    {
        start = Mathf.Clamp(start, 0, _text.Length);
        end = Mathf.Clamp(end, start, _text.Length);
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
        replacement ??= string.Empty;
        
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;

        RecordSnapshot(EditKind.Other);
        SetTextInternal(_text.Remove(start, end - start).Insert(start, replacement));
        CaretIndex = caretOffset < 0
            ? start + replacement.Length
            : start + Mathf.Clamp(caretOffset, 0, replacement.Length);
        SelectionAnchorIndex = CaretIndex;
        AfterEdit();
        FlushCoalescing();
        
        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }

    private void SetTextInternal(string newText)
    {
        if (_text == newText) return;
        var old = _text;
        _text = newText;
        _textMarksDirty = true;
        if (!IsInsideTree()) return;
        _OnTextChanged(old, newText);
        EmitSignal(SignalName.TextChanged, old, newText);
    }
    
    private void InsertText(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        s = ClampInputToLimit(s);
        if (string.IsNullOrEmpty(s)) return;
        
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;
        
        RecordSnapshot(EditKind.Insert);
        if (HasSelection) DeleteSelectionInternal();
        SetTextInternal(_text.Insert(CaretIndex, s));
        CaretIndex += s.Length;
        SelectionAnchorIndex = CaretIndex;
        AfterEdit();
        if (IsWordBreakChar(s)) FlushCoalescing();
        
        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }

    private void DeleteBackward()
    {
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;
        
        if (HasSelection)
        {
            RecordSnapshot(EditKind.Delete);
            DeleteSelectionInternal();
            AfterEdit();
            FlushCoalescing();
            return;
        }
        if (CaretIndex <= 0) return;
        RecordSnapshot(EditKind.Delete);
        var prev = ToPrevGrapheme(CaretIndex);
        var deleted = _text.Substring(prev, CaretIndex - prev);
        SetTextInternal(_text.Remove(prev, CaretIndex - prev));
        CaretIndex = prev;
        SelectionAnchorIndex = CaretIndex;
        AfterEdit();
        if (IsWordBreakChar(deleted)) FlushCoalescing();
        
        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }

    private void DeleteForward()
    {
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;
        
        if (HasSelection)
        {
            RecordSnapshot(EditKind.Delete);
            DeleteSelectionInternal();
            AfterEdit();
            FlushCoalescing();
            return;
        }
        if (CaretIndex >= _text.Length) return;
        RecordSnapshot(EditKind.Delete);
        var next = ToNextGrapheme(CaretIndex);
        var deleted = _text.Substring(CaretIndex, next - CaretIndex);
        SetTextInternal(_text.Remove(CaretIndex, next - CaretIndex));
        SelectionAnchorIndex = CaretIndex;
        AfterEdit();
        if (IsWordBreakChar(deleted)) FlushCoalescing();
        
        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }
    
    private void DeleteWordBackward()
    {
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;
        
        if (HasSelection) { DeleteBackward(); return; }
        if (CaretIndex <= 0) return;
        var target = PrevWordBoundary(CaretIndex);
        if (target >= CaretIndex) return;
        RecordSnapshot(EditKind.Delete);
        SetTextInternal(_text.Remove(target, CaretIndex - target));
        CaretIndex = target;
        SelectionAnchorIndex = CaretIndex;
        AfterEdit();
        FlushCoalescing();
        
        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }

    private void DeleteWordForward()
    {
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;
        
        if (HasSelection) { DeleteForward(); return; }
        if (CaretIndex >= _text.Length) return;
        var target = NextWordBoundary(CaretIndex);
        if (target <= CaretIndex) return;
        RecordSnapshot(EditKind.Delete);
        SetTextInternal(_text.Remove(CaretIndex, target - CaretIndex));
        SelectionAnchorIndex = CaretIndex;
        AfterEdit();
        FlushCoalescing();
        
        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }

    private void DeleteSelectionInternal()
    {
        if (!HasSelection) return;

        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;

        var start = SelectionStart();
        var end = SelectionEnd();
        SetTextInternal(_text.Remove(start, end - start));
        CaretIndex = start;
        SelectionAnchorIndex = start;

        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }

    private void AfterEdit()
    {
        InvalidateShaping();
        _preferredCaretX = -1f;
        ResetCaretBlink();
        QueueRedraw();
        UpdateMinimumSize();
        ClampScrollToContentY();
        ClampScrollToContentX();
        ScrollCaretIntoViewY();
        ScrollCaretIntoViewX();
        ResetErrorDebounce();
        _OnCompletionContextChanged(_text, CaretIndex);
    }

    private void ResetErrorDebounce()
    {
        _lastEditTimeMs = Time.GetTicksMsec();
        _editDebounceActive = true;
    }

    private void MoveCaretTo(int newIndex, bool extend)
    {
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;

        CaretIndex = Mathf.Clamp(newIndex, 0, _text.Length);
        if (!extend) SelectionAnchorIndex = CaretIndex;
        _preferredCaretX = -1f;
        ResetCaretBlink();
        ScrollCaretIntoViewY();
        ScrollCaretIntoViewX();
        QueueRedraw();
        FlushCoalescing();
        _OnCompletionContextChanged(_text, CaretIndex);

        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }

    private void ClampCaret()
    {
        CaretIndex = Mathf.Clamp(CaretIndex, 0, _text.Length);
        SelectionAnchorIndex = Mathf.Clamp(SelectionAnchorIndex, 0, _text.Length);
    }

    private void ResetCaretBlink()
    {
        _caretVisible = true;
        _caretBlinkAccum = 0;
        _caretActionPauseRemaining = CARET_BLINK_PAUSE_AFTER_ACTION;
    }
    
    private int SelectionStart() => Mathf.Min(SelectionAnchorIndex, CaretIndex);
    private int SelectionEnd()   => Mathf.Max(SelectionAnchorIndex, CaretIndex);

    private string GetSelectedText()
    {
        if (!HasSelection) return "";
        var start = SelectionStart();
        return _text.Substring(start, SelectionEnd() - start);
    }

    private void SelectAll()
    {
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;

        SelectionAnchorIndex = 0;
        CaretIndex = _text.Length;
        ResetCaretBlink();
        QueueRedraw();

        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }

    private void SelectWordAt(int globalPos)
    {
        EnsureShaped();
        if (_lineRids.Count == 0 || string.IsNullOrEmpty(_text))
        {
            SelectionAnchorIndex = CaretIndex;
            QueueRedraw();
            return;
        }

        var (line, col) = LocateCaret(globalPos);
        if (line >= _lineRids.Count) return;
        var rid = _lineRids[line];
        if (!rid.IsValid) return;
        
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;

        var lineStart = _lineStarts[line];
        var breaks = Ts.ShapedTextGetWordBreaks(rid);
        for (var i = 0; i + 1 < breaks.Length; i += 2)
        {
            var start = breaks[i];
            var end = breaks[i + 1];
            if (col < start || col > end) continue;
            SelectionAnchorIndex = lineStart + start;
            CaretIndex = lineStart + end;
            ResetCaretBlink();
            QueueRedraw();

            EmitCaretMovedIfNeeded(oldCaret);
            EmitSelectionChangedIfNeeded(oldStart, oldEnd);
            return;
        }

        SelectionAnchorIndex = CaretIndex;
        QueueRedraw();
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }

    private void DoCopy()
    {
        if (!HasSelection) return;
        DisplayServer.ClipboardSet(GetSelectedText());
    }

    private void DoCut()
    {
        if (!HasSelection) return;
        DisplayServer.ClipboardSet(GetSelectedText());
        RecordSnapshot(EditKind.Other);
        DeleteSelectionInternal();
        AfterEdit();
    }

    private void DoPaste()
    {
        var raw = DisplayServer.ClipboardGet();
        if (string.IsNullOrEmpty(raw)) return;
        var clean = raw.Replace("\r", "").Replace("\n", "");
        if (clean.Length == 0) return;
        clean = ClampInputToLimit(clean);
        if (clean.Length == 0) return;
        
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;

        RecordSnapshot(EditKind.Other);
        if (HasSelection) DeleteSelectionInternal();
        SetTextInternal(_text.Insert(CaretIndex, clean));
        CaretIndex += clean.Length;
        SelectionAnchorIndex = CaretIndex;
        AfterEdit();
        
        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }
    
    private int ToNextGrapheme(int index)
    {
        EnsureShaped();
        if (_lineRids.Count == 0 || string.IsNullOrEmpty(_text)) return _text.Length;

        var (line, col) = LocateCaret(index);
        if (line >= _lineRids.Count) return _text.Length;

        var shapedLen = GetLineShapedLength(line);
        if (col >= shapedLen)
            return line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _text.Length;

        var rid = _lineRids[line];
        if (!rid.IsValid) return Mathf.Min(index + 1, _text.Length);

        var next = (int)Ts.ShapedTextNextGraphemePos(rid, col);
        if (next <= col) return Mathf.Min(index + 1, _text.Length);
        return _lineStarts[line] + Mathf.Min(next, shapedLen);
    }

    private int ToPrevGrapheme(int index)
    {
        EnsureShaped();
        if (_lineRids.Count == 0 || string.IsNullOrEmpty(_text)) return 0;

        var (line, col) = LocateCaret(index);
        if (col <= 0)
        {
            if (line == 0) return 0;
            return _lineStarts[line - 1] + GetLineShapedLength(line - 1);
        }

        var rid = _lineRids[line];
        if (!rid.IsValid) return Mathf.Max(index - 1, 0);

        var clamped = Mathf.Min(col, GetLineShapedLength(line));
        var prev = (int)Ts.ShapedTextPrevGraphemePos(rid, clamped);
        if (prev >= clamped) return Mathf.Max(index - 1, 0);
        return _lineStarts[line] + prev;
    }

    private int NextWordBoundary(int from)
    {
        EnsureShaped();
        if (_lineRids.Count == 0 || string.IsNullOrEmpty(_text)) return _text.Length;

        for (var i = 0; i < _lineRids.Count; i++)
        {
            var lineEnd = i + 1 < _lineStarts.Length ? _lineStarts[i + 1] : _text.Length;
            if (lineEnd <= from) continue;

            var rid = _lineRids[i];
            
            if (!rid.IsValid) return lineEnd;
            
            var breaks = Ts.ShapedTextGetWordBreaks(rid);
            var lineStart = _lineStarts[i];
            for (var b = 0; b + 1 < breaks.Length; b += 2)
            {
                var globalEnd = lineStart + breaks[b + 1];
                if (globalEnd > from) return globalEnd;
            }
            return lineEnd;
        }
        return _text.Length;
    }

    private int PrevWordBoundary(int from)
    {
        EnsureShaped();
        if (_lineRids.Count == 0 || string.IsNullOrEmpty(_text)) return 0;

        for (var i = _lineRids.Count - 1; i >= 0; i--)
        {
            var lineStart = _lineStarts[i];
            if (lineStart >= from) continue;

            var rid = _lineRids[i];
            
            if (!rid.IsValid) return lineStart;
            
            var breaks = Ts.ShapedTextGetWordBreaks(rid);
            var best = -1;
            for (var b = 0; b + 1 < breaks.Length; b += 2)
            {
                var globalStart = lineStart + breaks[b];
                if (globalStart >= from) break;
                best = globalStart;
            }
            return best >= 0 ? best : lineStart;
        }
        return 0;
    }

    private int CharIndexAtLocalPos(Vector2 localPos)
    {
        if (string.IsNullOrEmpty(_text)) return 0;
        EnsureShaped();
        if (_lineRids.Count == 0) return 0;

        var font = GetFont(THEME_FONT_DEFAULT);
        var fontSize = _resolvedFontSize > 0 ? _resolvedFontSize : ResolveFontSize();
        var lineHeight = font.GetHeight(fontSize);
        var advance = lineHeight + _lineSpacing;

        var content = GetContentRect();
        var scrollMode = _scrollable && GetMaxScrollOffsetY() > 0f;
        var totalHeight = lineHeight * _lineRids.Count + _lineSpacing * (_lineRids.Count - 1);

        var topY = scrollMode
            ? content.Position.Y - _scrollOffsetY
            : _verticalAlignment switch
            {
                VerticalAlignment.Top    => content.Position.Y,
                VerticalAlignment.Bottom => content.Position.Y + content.Size.Y - totalHeight,
                VerticalAlignment.Fill   => content.Position.Y,
                _                        => content.Position.Y + (content.Size.Y - totalHeight) * 0.5f
            };

        var lineIdx = advance > 0 ? (int)((localPos.Y - topY) / advance) : 0;
        lineIdx = Mathf.Clamp(lineIdx, 0, _lineRids.Count - 1);

        return HitTestOnLine(lineIdx, localPos.X);
    }
    
    private string ClampInputToLimit(string proposed)
    {
        if (string.IsNullOrEmpty(proposed) || LimitMode == CharacterLimit.None) return proposed;

        var selectionLen = HasSelection ? SelectionEnd() - SelectionStart() : 0;
        var currentLen = _text.Length - selectionLen;

        switch (LimitMode)
        {
            case CharacterLimit.Fixed:
            {
                var budget = MaxLength - currentLen;
                if (budget <= 0) return "";
                return proposed.Length <= budget ? proposed : proposed[..budget];
            }
            case CharacterLimit.Visible:
            {
                if (IsMultilineActive) return proposed;
                var available = GetContentRect().Size.X;
                if (available <= 0) return "";

                var font = GetFont(THEME_FONT_DEFAULT);
                var measureSize = _displayOverflow.HasFlag(OverflowDisplay.ShrinkToFit) ? _minFontSize : ResolveFontSize();
                
                var prefix = HasSelection ? _text.Remove(SelectionStart(), selectionLen).Insert(SelectionStart(), "") : _text;
                var insertAt = HasSelection ? SelectionStart() : CaretIndex;
                
                int lo = 0, hi = proposed.Length;
                while (lo < hi)
                {
                    var mid = (lo + hi + 1) / 2;
                    var candidate = prefix.Insert(insertAt, proposed[..mid]);
                    if (MeasureWidthAtSize(candidate, font, measureSize) <= available) lo = mid;
                    else hi = mid - 1;
                }
                return lo == 0 ? "" : proposed[..lo];
            }
            case CharacterLimit.None:
            default: return proposed;
        }
    }
    
    private void UpdateCaretBlink(double delta)
    {
        if (!HasFocus() || !_editable) return;
 
        if (_caretActionPauseRemaining > 0)
        {
            _caretActionPauseRemaining -= delta;
            return;
        }
 
        _caretBlinkAccum += delta;
        if (_caretBlinkAccum < CARET_BLINK_INTERVAL) return;
 
        _caretBlinkAccum = 0;
        _caretVisible = !_caretVisible;
        QueueRedraw();
    }
    
    private void EmitCaretMovedIfNeeded(int oldIndex)
    {
        if (CaretIndex == oldIndex) return;
        EmitSignal(SignalName.CaretMoved, oldIndex, CaretIndex);
    }

    private void EmitSelectionChangedIfNeeded(int oldStart, int oldEnd)
    {
        var newStart = SelectionStartIndex;
        var newEnd = SelectionEndIndex;
        if (newStart == oldStart && newEnd == oldEnd) return;
        EmitSignal(SignalName.SelectionChanged, newStart, newEnd);
    }
}