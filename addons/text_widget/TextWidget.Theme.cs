using Godot;
using StyleBox = Godot.StyleBox;
// ReSharper disable InconsistentNaming

namespace Widgets;

public partial class TextWidget
{
    private const string THEME_STYLE_NORMAL          = "normal";
    private const string THEME_STYLE_FOCUS           = "focus";
    private const string THEME_STYLE_READ_ONLY       = "read_only";
    private const string THEME_FONT_DEFAULT          = "font";
    private const string THEME_FONT_SIZE             = "font_size";
    private const string THEME_COLOR_FONT            = "font_color";
    private const string THEME_COLOR_FONT_OUTLINE    = "font_outline_color";
    private const string THEME_COLOR_FONT_SELECTED   = "font_selected_color";
    private const string THEME_COLOR_PLACEHOLDER     = "font_placeholder_color";
    private const string THEME_COLOR_CARET           = "caret_color";
    private const string THEME_COLOR_SELECTION       = "selection_color";
    private const string THEME_CONSTANT_CARET_WIDTH  = "caret_width";
    private const string THEME_CONSTANT_OUTLINE_SIZE = "outline_size";
    private const string THEME_FALLBACK              = "LineEdit";
    
    [ExportGroup("Theme Overrides/Colors")]
    [Export] private Variant _FontColor
    {
        get => _fontColorEnabled ? Variant.From(_fontColor) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) _fontColorEnabled = false;
            else { _fontColorEnabled = true; _fontColor = value.AsColor(); }
            QueueRedraw();
        }
    }
    
    [Export] private Variant _FontOutlineColor
    {
        get => _fontOutlineColorEnabled ? Variant.From(_fontOutlineColor) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) _fontOutlineColorEnabled = false;
            else { _fontOutlineColorEnabled = true; _fontOutlineColor = value.AsColor(); }
            QueueRedraw();
        }
    }
    
    [Export] private Variant _FontSelectedColor
    {
        get => _fontSelectedColorEnabled ? Variant.From(_fontSelectedColor) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) _fontSelectedColorEnabled = false;
            else { _fontSelectedColorEnabled = true; _fontSelectedColor = value.AsColor(); }
            QueueRedraw();
        }
    }
    
    [Export] private Variant _PlaceholderColor
    {
        get => _placeholderColorEnabled ? Variant.From(_placeholderColor) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) _placeholderColorEnabled = false;
            else { _placeholderColorEnabled = true; _placeholderColor = value.AsColor(); }
            QueueRedraw();
        }
    }
    
    [Export] private Variant _ReadOnlyColor
    {
        get => _readOnlyColorEnabled ? Variant.From(_readOnlyColor) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) _readOnlyColorEnabled = false;
            else { _readOnlyColorEnabled = true; _readOnlyColor = value.AsColor(); }
            QueueRedraw();
        }
    }
    
    [Export] private Variant _CaretColor
    {
        get => _caretColorEnabled ? Variant.From(_caretColor) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) _caretColorEnabled = false;
            else { _caretColorEnabled = true; _caretColor = value.AsColor(); }
            QueueRedraw();
        }
    }
    
    [Export] private Variant _SelectionColor
    {
        get => _selectionColorEnabled ? Variant.From(_selectionColor) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) _selectionColorEnabled = false;
            else { _selectionColorEnabled = true; _selectionColor = value.AsColor(); }
            QueueRedraw();
        }
    }
    
    [ExportGroup("Theme Overrides/Constants")]
    [Export] private Variant _OutlineSize
    {
        get => _outlineSizeEnabled ? Variant.From(_outlineSize) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) _outlineSizeEnabled = false;
            else { _outlineSizeEnabled = true; _outlineSize = Mathf.Max(0, value.AsInt32()); }
            QueueRedraw();
        }
    }
    
    [Export] private Variant _CaretWidth
    {
        get => _caretWidthEnabled ? Variant.From(_caretWidth) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) _caretWidthEnabled = false;
            else { _caretWidthEnabled = true; _caretWidth = Mathf.Max(1, value.AsInt32()); }
            QueueRedraw();
        }
    }
    
    [ExportGroup("Theme Overrides/Fonts")]
    [Export] private Variant _Font
    {
        get => _fontEnabled ? Variant.From(_font) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) { _fontEnabled = false; _font = null; }
            else { _fontEnabled = true; _font = value.As<Font>(); }
            InvalidateShaping();
            QueueRedraw();
            UpdateMinimumSize();
        }
    }
    
    [ExportGroup("Theme Overrides/Styles")]
    [Export] private Variant _NormalStyle
    {
        get => _normalStyleEnabled ? Variant.From(_normalStyle) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) { _normalStyleEnabled = false; _normalStyle = null; }
            else { _normalStyleEnabled = true; _normalStyle = value.As<StyleBox>(); }
            InvalidateShaping();
            QueueRedraw();
            UpdateMinimumSize();
        }
    }
    
    [Export] private Variant _ReadOnlyStyle
    {
        get => _readOnlyStyleEnabled ? Variant.From(_readOnlyStyle) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) { _readOnlyStyleEnabled = false; _readOnlyStyle = null; }
            else { _readOnlyStyleEnabled = true; _readOnlyStyle = value.As<StyleBox>(); }
            InvalidateShaping();
            QueueRedraw();
            UpdateMinimumSize();
        }
    }
    
    [Export] private Variant _FocusStyle
    {
        get => _focusStyleEnabled ? Variant.From(_focusStyle) : new Variant();
        set
        {
            if (value.VariantType == Variant.Type.Nil) { _focusStyleEnabled = false; _focusStyle = null; }
            else { _focusStyleEnabled = true; _focusStyle = value.As<StyleBox>(); }
            InvalidateShaping();
            QueueRedraw();
            UpdateMinimumSize();
        }
    }
    
    // Check Boxes
    [Export] private bool _fontColorEnabled;
    [Export] private bool _fontOutlineColorEnabled;
    [Export] private bool _fontSelectedColorEnabled;
    [Export] private bool _placeholderColorEnabled;
    [Export] private bool _readOnlyColorEnabled;
    [Export] private bool _caretColorEnabled;
    [Export] private bool _selectionColorEnabled;
    [Export] private bool _outlineSizeEnabled;
    [Export] private bool _caretWidthEnabled;
    [Export] private bool _fontEnabled;
    [Export] private bool _normalStyleEnabled;
    [Export] private bool _readOnlyStyleEnabled;
    [Export] private bool _focusStyleEnabled;
    
    // Backing Fields
    private Color _fontColor = Colors.White;
    private Color _fontOutlineColor = Colors.Black;
    private Color _fontSelectedColor = Colors.White;
    private Color _placeholderColor = Colors.White;
    private Color _readOnlyColor = Colors.White;
    private Color _caretColor = Colors.White;
    private Color _selectionColor = Colors.White;
    private int _outlineSize;
    private int _caretWidth = 1;
    private Font? _font;
    private StyleBox? _normalStyle;
    private StyleBox? _readOnlyStyle;
    private StyleBox? _focusStyle;
    
    // Helpers
    private bool HasColor(string name) => HasThemeColor(name) || HasThemeColor(name, THEME_FALLBACK);
    private bool HasConstant(string name) => HasThemeConstant(name) || HasThemeConstant(name, THEME_FALLBACK);
    private int GetFontSize(string name) => HasThemeFontSize(name) ? GetThemeFontSize(name) : GetThemeFontSize(name, THEME_FALLBACK);
    private Color GetColor(string name) => HasThemeColor(name) ? GetThemeColor(name) : GetThemeColor(name, THEME_FALLBACK);
    private int GetConstant(string name) => HasThemeConstant(name) ? GetThemeConstant(name) : GetThemeConstant(name, THEME_FALLBACK);

    private Font GetFont(string name)
    {
        if (name == THEME_FONT_DEFAULT && _fontEnabled && _font != null) return _font;
        return HasThemeFont(name) ? GetThemeFont(name) : GetThemeFont(name, THEME_FALLBACK);
    }

    private StyleBox GetStyle(string name)
    {
        if (name == THEME_STYLE_NORMAL    && _normalStyleEnabled   && _normalStyle   != null) return _normalStyle;
        if (name == THEME_STYLE_FOCUS     && _focusStyleEnabled    && _focusStyle    != null) return _focusStyle;
        if (name == THEME_STYLE_READ_ONLY && _readOnlyStyleEnabled && _readOnlyStyle != null) return _readOnlyStyle;
        return HasThemeStylebox(name) ? GetThemeStylebox(name) : GetThemeStylebox(name, THEME_FALLBACK);
    }
    
    private int ResolveFontSize() => _fontSize > 0 ? _fontSize : GetFontSize(THEME_FONT_SIZE);
    
    private void ClampMinFontSizeAgainstEffective()
    {
        var ceiling = ResolveFontSize();
        if (_minFontSize > ceiling) _minFontSize = ceiling;
    }
    
    private StyleBox CurrentBaseStyle() => _editable ? GetStyle(THEME_STYLE_NORMAL) : GetStyle(THEME_STYLE_READ_ONLY);
}