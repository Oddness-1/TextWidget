using Godot;

namespace Widgets;

public partial class TextWidget
{
    private void ResolvePropertyUsageOverride(ref Godot.Collections.Dictionary property)
    {
        var name = property["name"].AsStringName();
        var isMultiline = _displayOverflow.HasFlag(OverflowDisplay.Multiline);
        var isClip = _displayOverflow.HasFlag(OverflowDisplay.Clip);
        var isShrink = _displayOverflow.HasFlag(OverflowDisplay.ShrinkToFit);

        if (name == PropertyName.OverflowMarker && !isClip) property["usage"] = (long)PropertyUsageFlags.NoEditor;
        if (name == PropertyName.MinFontSize && !isShrink) property["usage"] = (long)PropertyUsageFlags.NoEditor;
        if (name == PropertyName.Scrollable && !isMultiline) property["usage"] = (long)PropertyUsageFlags.NoEditor;
        if (name == PropertyName.MinLines && !isMultiline) property["usage"] = (long)PropertyUsageFlags.NoEditor;
        if (name == PropertyName.MaxLines && (!isMultiline || !_scrollable)) property["usage"] = (long)PropertyUsageFlags.NoEditor;
        if (name == PropertyName.LineSpacing && !isMultiline) property["usage"] = (long)PropertyUsageFlags.NoEditor;
        if (name == PropertyName.MaxLength && LimitMode != CharacterLimit.Fixed) property["usage"] = (long)PropertyUsageFlags.NoEditor;

        // Hide all *Enabled backing bools — state is reflected via the Checked flag on the visible property.
        if 
        (
            name == PropertyName._fontColorEnabled         ||
            name == PropertyName._fontOutlineColorEnabled  ||
            name == PropertyName._fontSelectedColorEnabled ||
            name == PropertyName._placeholderColorEnabled  ||
            name == PropertyName._readOnlyColorEnabled     ||
            name == PropertyName._caretColorEnabled        ||
            name == PropertyName._selectionColorEnabled    ||
            name == PropertyName._outlineSizeEnabled       ||
            name == PropertyName._caretWidthEnabled        ||
            name == PropertyName._fontEnabled              ||
            name == PropertyName._normalStyleEnabled       ||
            name == PropertyName._readOnlyStyleEnabled     ||
            name == PropertyName._focusStyleEnabled
        )
            property["usage"] = (long)PropertyUsageFlags.NoEditor;

        // Colors
        ApplyCheckableColor(property, name, PropertyName._FontColor,         _fontColorEnabled);
        ApplyCheckableColor(property, name, PropertyName._FontOutlineColor,  _fontOutlineColorEnabled);
        ApplyCheckableColor(property, name, PropertyName._FontSelectedColor, _fontSelectedColorEnabled);
        ApplyCheckableColor(property, name, PropertyName._PlaceholderColor,  _placeholderColorEnabled);
        ApplyCheckableColor(property, name, PropertyName._ReadOnlyColor,     _readOnlyColorEnabled);
        ApplyCheckableColor(property, name, PropertyName._CaretColor,        _caretColorEnabled);
        ApplyCheckableColor(property, name, PropertyName._SelectionColor,    _selectionColorEnabled);

        // Constants
        ApplyCheckableInt(property, name, PropertyName._OutlineSize, _outlineSizeEnabled, "0,64,1");
        ApplyCheckableInt(property, name, PropertyName._CaretWidth,  _caretWidthEnabled,  "1,16,1");

        // Fonts
        ApplyCheckableResource(property, name, PropertyName._Font, _fontEnabled, "Font");

        // Styles
        ApplyCheckableResource(property, name, PropertyName._NormalStyle,   _normalStyleEnabled,   "StyleBox");
        ApplyCheckableResource(property, name, PropertyName._ReadOnlyStyle, _readOnlyStyleEnabled, "StyleBox");
        ApplyCheckableResource(property, name, PropertyName._FocusStyle,    _focusStyleEnabled,    "StyleBox");
    }

    private static void ApplyCheckableColor(Godot.Collections.Dictionary property, StringName name, StringName target, bool enabled)
    {
        if (name != target) return;
        property["type"] = (long)Variant.Type.Color;
        var usage = (long)(PropertyUsageFlags.Default | PropertyUsageFlags.Checkable);
        if (enabled) usage |= (long)PropertyUsageFlags.Checked;
        property["usage"] = usage;
    }

    private static void ApplyCheckableInt(Godot.Collections.Dictionary property, StringName name, StringName target, bool enabled, string range)
    {
        if (name != target) return;
        property["type"] = (long)Variant.Type.Int;
        property["hint"] = (long)PropertyHint.Range;
        property["hint_string"] = range;
        var usage = (long)(PropertyUsageFlags.Default | PropertyUsageFlags.Checkable);
        if (enabled) usage |= (long)PropertyUsageFlags.Checked;
        property["usage"] = usage;
    }

    private static void ApplyCheckableResource(Godot.Collections.Dictionary property, StringName name, StringName target, bool enabled, string resourceType)
    {
        if (name != target) return;
        property["type"] = (long)Variant.Type.Object;
        property["hint"] = (long)PropertyHint.ResourceType;
        property["hint_string"] = resourceType;
        var usage = (long)(PropertyUsageFlags.Default | PropertyUsageFlags.Checkable);
        if (enabled) usage |= (long)PropertyUsageFlags.Checked;
        property["usage"] = usage;
    }
}