using Godot;

namespace Widgets;

public partial class TextWidget
{
    private const float SCROLLBAR_WIDTH = 3f;
    private const float SCROLLBAR_MIN_THUMB_HEIGHT = 16f;
    private const float DRAG_AUTO_SCROLL_EDGE_ZONE = 24f;
    private const float DRAG_AUTO_SCROLL_PIXELS_PER_FRAME = 8f;
    
    private float ScrollOffsetX
    {
        get => _scrollOffsetX;
        set
        {
            var clamped = Mathf.Clamp(value, 0f, GetMaxScrollOffsetX());
            if (Mathf.IsEqualApprox(_scrollOffsetX, clamped)) return;
            _scrollOffsetX = clamped;
            QueueRedraw();
        }
    }
    
    private float ScrollOffsetY
    {
        get => _scrollOffsetY;
        set
        {
            var clamped = Mathf.Clamp(value, 0f, GetMaxScrollOffsetY());
            if (Mathf.IsEqualApprox(_scrollOffsetY, clamped)) return;
            _scrollOffsetY = clamped;
            QueueRedraw();
        }
    }
    
    private float _scrollOffsetX;
    private float _scrollOffsetY;
    
    private float GetMaxScrollOffsetY()
    {
        if (!_scrollable || !_displayOverflow.HasFlag(OverflowDisplay.Multiline)) return 0f;
        if (_maxLines <= 0) return 0f;
        EnsureShaped();

        var font = GetFont(THEME_FONT_DEFAULT);
        var fontSize = _resolvedFontSize > 0 ? _resolvedFontSize : ResolveFontSize();
        var lineHeight = font.GetHeight(fontSize);
        var advance = lineHeight + _lineSpacing;

        var visibleLines = _maxLines;
        var totalLines = _lineRids.Count;
        if (totalLines <= visibleLines) return 0f;

        return (totalLines - visibleLines) * advance;
    }
    
    private float GetMaxScrollOffsetX()
    {
        if (IsMultilineActive) return 0f;
        EnsureShaped();
        if (_lineRids.Count == 0) return 0f;
        var rid = _lineRids[0];
        if (!rid.IsValid) return 0f;
        var contentWidth = (float)Ts.ShapedTextGetWidth(rid);
        var available = GetContentRect().Size.X;
        return Mathf.Max(0f, contentWidth - available);
    }

    private void ScrollCaretIntoViewX()
    {
        if (GetMaxScrollOffsetX() <= 0f) return;
        var (line, col) = LocateCaret(CaretIndex);
        if (line != 0) return;
        var caretX = GetCaretRelativeXOnLine(0, col);
        var available = GetContentRect().Size.X;

        if (caretX < _scrollOffsetX) ScrollOffsetX = caretX;
        else if (caretX > _scrollOffsetX + available) ScrollOffsetX = caretX - available;
    }

    private void ResetScroll()
    {
        if (_scrollOffsetY == 0f && _scrollOffsetX == 0f) return;
        _scrollOffsetY = 0f;
        _scrollOffsetX = 0f;
        QueueRedraw();
    }

    private void ClampScrollToContentY()
    {
        var max = GetMaxScrollOffsetY();
        if (_scrollOffsetY > max) ScrollOffsetY = max;
    }
    
    private void ClampScrollToContentX()
    {
        var max = GetMaxScrollOffsetX();
        if (_scrollOffsetX > max) ScrollOffsetX = max;
    }

    private float GetWheelScrollDelta()
    {
        var font = GetFont(THEME_FONT_DEFAULT);
        var fontSize = _resolvedFontSize > 0 ? _resolvedFontSize : ResolveFontSize();
        return font.GetHeight(fontSize) + _lineSpacing;
    }

    private void ScrollCaretIntoViewY()
    {
        if (GetMaxScrollOffsetY() <= 0f) return;
        EnsureShaped();
        var (line, _) = LocateCaret(CaretIndex);

        var font = GetFont(THEME_FONT_DEFAULT);
        var fontSize = _resolvedFontSize > 0 ? _resolvedFontSize : ResolveFontSize();
        var lineHeight = font.GetHeight(fontSize);
        var advance = lineHeight + _lineSpacing;

        var caretTop = line * advance;
        var caretBottom = caretTop + lineHeight;

        var viewportTop = _scrollOffsetY;
        var viewportBottom = _scrollOffsetY + _maxLines * advance;

        if (caretTop < viewportTop)
        {
            ScrollOffsetY = caretTop;
        }
        else if (caretBottom > viewportBottom)
        {
            ScrollOffsetY = caretBottom - _maxLines * advance;
        }
    }
    
    private float ComputeMultilineMinHeight(Font font, int themedSize)
    {
        EnsureShaped();
        var resolvedSize = _resolvedFontSize > 0 ? _resolvedFontSize : themedSize;
        var resolvedLineHeight = font.GetHeight(resolvedSize);
        var count = Mathf.Max(_minLines, _lineRids.Count);
        
        if (_scrollable && _maxLines > 0) count = Mathf.Min(count, _maxLines);
        return resolvedLineHeight * count + _lineSpacing * (count - 1);
    }
    
    private Rect2 GetContentRect()
    {
        var style = CurrentBaseStyle();
        return new Rect2(style.GetOffset(), Size - style.GetMinimumSize());
    }

    private void UpdateClipContents() => ClipContents = _displayOverflow.HasFlag(OverflowDisplay.Clip) || _scrollable;

    private float GetLineDrawOriginX(int lineIndex)
    {
        var content = GetContentRect();
        if (lineIndex >= _lineRids.Count) return content.Position.X;
        var rid = _lineRids[lineIndex];
        if (!rid.IsValid) return content.Position.X;

        var lineWidth = (float)Ts.ShapedTextGetWidth(rid);
        if (_showMarker && _markerLineIndex == lineIndex) lineWidth += _markerWidth;
        var available = content.Size.X;

        var baseX = lineWidth >= available
            ? content.Position.X
            : _horizontalAlignment switch
            {
                HorizontalAlignment.Center => content.Position.X + (available - lineWidth) * 0.5f,
                HorizontalAlignment.Right  => content.Position.X + (available - lineWidth),
                _                          => content.Position.X
            };
        if (!IsMultilineActive && GetMaxScrollOffsetX() > 0f) baseX -= _scrollOffsetX;
        return baseX;
    }

    private float GetLineBaselineY(int lineIndex)
    {
        var content = GetContentRect();
        var font = GetFont(THEME_FONT_DEFAULT);
        var fontSize = _resolvedFontSize > 0 ? _resolvedFontSize : ResolveFontSize();
        var ascent = font.GetAscent(fontSize);
        var lineHeight = font.GetHeight(fontSize);
        var advance = lineHeight + _lineSpacing;
        var lineCount = Mathf.Max(1, _lineRids.Count);
        var scrollMode = _scrollable && GetMaxScrollOffsetY() > 0f;
        var totalHeight = lineHeight * lineCount + _lineSpacing * (lineCount - 1);

        var topY = scrollMode
            ? content.Position.Y - _scrollOffsetY
            : _verticalAlignment switch
            {
                VerticalAlignment.Top    => content.Position.Y,
                VerticalAlignment.Bottom => content.Position.Y + content.Size.Y - totalHeight,
                VerticalAlignment.Fill   => content.Position.Y,
                _                        => content.Position.Y + (content.Size.Y - totalHeight) * 0.5f
            };

        return topY + ascent + advance * lineIndex;
    }

    private float GetCaretRelativeXOnLine(int lineIndex, int colInOwnedSpace)
    {
        if (lineIndex >= _lineRids.Count) return 0f;
        var rid = _lineRids[lineIndex];
        if (!rid.IsValid) return 0f;

        var shapedLen = GetLineShapedLength(lineIndex);
        if (shapedLen == 0) return 0f;

        if (colInOwnedSpace >= shapedLen) return (float)Ts.ShapedTextGetWidth(rid);

        var caretInfo = Ts.ShapedTextGetCarets(rid, colInOwnedSpace);
        var leadingRect = (Rect2)caretInfo["leading_rect"];
        return leadingRect.Position.X;
    }

    private int HitTestOnLine(int lineIndex, float localX)
    {
        if (lineIndex < 0 || lineIndex >= _lineRids.Count) return 0;
        var rid = _lineRids[lineIndex];
        if (!rid.IsValid) return _lineStarts[lineIndex];

        var lineOriginX = GetLineDrawOriginX(lineIndex);
        var lineWidth = (float)Ts.ShapedTextGetWidth(rid);
        var relX = localX - lineOriginX;

        if (relX <= 0) return _lineStarts[lineIndex];
        if (relX >= lineWidth) return _lineEnds[lineIndex];

        var hit = (int)Ts.ShapedTextHitTestPosition(rid, relX);
        return _lineStarts[lineIndex] + Mathf.Clamp(hit, 0, GetLineShapedLength(lineIndex));
    }
    
    private bool ComputeIsMultilineActive()
    {
        if (!_displayOverflow.HasFlag(OverflowDisplay.Multiline)) return false;
        if (!_displayOverflow.HasFlag(OverflowDisplay.Clip)) return true;
        return HasFocus();
    }

    private void RecomputeMultilineActive()
    {
        var now = ComputeIsMultilineActive();
        if (now == IsMultilineActive) return;
        IsMultilineActive = now;
        _OnMultilineActiveChanged(now);
        EmitSignalMultilineActiveChanged(now);
    }
}