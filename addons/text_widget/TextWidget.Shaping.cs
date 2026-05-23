using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Widgets;

public partial class TextWidget
{
    private static TextServer Ts => TextServerManager.GetPrimaryInterface();
    private bool _shapingDirty = true;
    private int _resolvedFontSize;
    private readonly List<Rid> _lineRids = [];
    private int[] _lineStarts = [0];
    private int[] _lineEnds = [0];
    private float _preferredCaretX = -1f;
    private Rid _markerRid;
    private float _markerWidth;
    private bool _showMarker;
    private int _markerLineIndex;
    private float _markerLineX;
    
    private bool IsShapingWidthSensitive() => _displayOverflow.HasFlag(OverflowDisplay.Clip) || _displayOverflow.HasFlag(OverflowDisplay.ShrinkToFit) || _displayOverflow.HasFlag(OverflowDisplay.Multiline);
    private bool IsShapingFocusSensitive() => _displayOverflow.HasFlag(OverflowDisplay.Clip) || _displayOverflow.HasFlag(OverflowDisplay.Multiline);
    private void InvalidateShaping() => _shapingDirty = true;
    
    private void OnThemeChanged()
    {
        ClampMinFontSizeAgainstEffective();
        InvalidateShaping();
        QueueRedraw();
        UpdateMinimumSize();
    }
 
    private void OnResized()
    {
        if (!IsShapingWidthSensitive()) return;
        InvalidateShaping();
        QueueRedraw();
    }
 
    private void OnFocusChanged()
    {
        _altCodeBuffer = "";
        
        if (IsShapingFocusSensitive())
        {
            InvalidateShaping();
            UpdateMinimumSize();
        }
 
        ResetCaretBlink();
        QueueRedraw();
        RecomputeMultilineActive();
    }

    private void EnsureShaped()
    {
        if (!_shapingDirty) return;

        FreeLineRids();
        InvalidateMarker();
        _showMarker = false;
        _markerLineIndex = 0;
        _markerLineX = 0f;

        var font = GetFont(THEME_FONT_DEFAULT);
        var isPlaceholder = string.IsNullOrEmpty(_text);
        var displayText = isPlaceholder ? _GetPlaceholderText() : _text;

        _resolvedFontSize = ResolveFontSize();

        // Always have at least one Rid, even for empty content.
        if (font == null! || string.IsNullOrEmpty(displayText))
        {
            _lineRids.Add(Ts.CreateShapedText());
            _lineStarts = [0];
            _lineEnds = [0];
            _shapingDirty = false;
            return;
        }

        var hasClip = _displayOverflow.HasFlag(OverflowDisplay.Clip);
        var isMultiline = _displayOverflow.HasFlag(OverflowDisplay.Multiline);
        var collapseToSingle = isMultiline && !IsMultilineActive;
        var availableWidth = GetContentRect().Size.X;

        if (_displayOverflow.HasFlag(OverflowDisplay.ShrinkToFit) && availableWidth > 0)
        {
            _resolvedFontSize = displayText.Contains('\n')
                ? _minFontSize
                : FindShrinkFitFontSize(displayText, font, availableWidth);
        }

        var starts = new List<int>();
        var ends = new List<int>();

        if (collapseToSingle)
        {
            var firstNl = displayText.IndexOf('\n');
            var hasHiddenContent = firstNl >= 0;
            var firstLineLen = firstNl < 0 ? displayText.Length : firstNl;
            var firstLine = displayText[..firstLineLen];

            var rid = Ts.CreateShapedText();
            if (firstLine.Length > 0) Ts.ShapedTextAddString(rid, firstLine, font.GetRids(), _resolvedFontSize);
            _lineRids.Add(rid);
            starts.Add(0);
            ends.Add(firstLineLen);

            // Marker shown if marker is non-empty AND (content is hidden below OR first line itself overflows).
            if (_overflowMarker.Length > 0 && availableWidth > 0)
            {
                EnsureMarkerShaped(font);
                var firstLineWidth = (float)Ts.ShapedTextGetWidth(rid);
                var firstLineOverflows = firstLineWidth > availableWidth;
                if (hasHiddenContent || firstLineOverflows)
                {
                    var trimTarget = availableWidth - _markerWidth;
                    if (firstLineWidth > trimTarget && trimTarget > 0)
                    {
                        Ts.ShapedTextOverrunTrimToWidth(rid, trimTarget,
                            TextServer.TextOverrunFlag.Trim | TextServer.TextOverrunFlag.TrimWordOnly);
                    }
                    _showMarker = true;
                    _markerLineIndex = 0;
                    _markerLineX = (float)Ts.ShapedTextGetWidth(rid);
                }
            }

            _lineStarts = [..starts];
            _lineEnds = [..ends];
            _shapingDirty = false;
            return;
        }

        // Full pass: each \n-separated logical line, with wrap when Multiline + overflow.
        var logicalStart = 0;
        while (logicalStart <= displayText.Length)
        {
            var nlIdx = displayText.IndexOf('\n', logicalStart);
            var logicalEnd = nlIdx < 0 ? displayText.Length : nlIdx;
            var logicalLine = displayText[logicalStart..logicalEnd];

            if (logicalLine.Length == 0)
            {
                _lineRids.Add(Ts.CreateShapedText());
                starts.Add(logicalStart);
                ends.Add(logicalStart);
            }
            else if (isMultiline && availableWidth > 0)
            {
                var fullRid = Ts.CreateShapedText();
                Ts.ShapedTextAddString(fullRid, logicalLine, font.GetRids(), _resolvedFontSize);
                var fullWidth = (float)Ts.ShapedTextGetWidth(fullRid);

                if (fullWidth <= availableWidth)
                {
                    _lineRids.Add(fullRid);
                    starts.Add(logicalStart);
                    ends.Add(logicalEnd);
                }
                else
                {
                    var breaks = Ts.ShapedTextGetLineBreaks
                    (
                        fullRid,
                        availableWidth,
                        breakFlags: TextServer.LineBreakFlag.WordBound | TextServer.LineBreakFlag.Adaptive | TextServer.LineBreakFlag.TrimStartEdgeSpaces | TextServer.LineBreakFlag.TrimEndEdgeSpaces
                    );
                    Ts.FreeRid(fullRid);

                    if (breaks.Length < 2)
                    {
                        var fallback = Ts.CreateShapedText();
                        Ts.ShapedTextAddString(fallback, logicalLine, font.GetRids(), _resolvedFontSize);
                        _lineRids.Add(fallback);
                        starts.Add(logicalStart);
                        ends.Add(logicalEnd);
                    }
                    else
                    {
                        for (var bi = 0; bi + 1 < breaks.Length; bi += 2)
                        {
                            var segStart = breaks[bi];
                            var segEnd = breaks[bi + 1];
                            var segment = logicalLine[segStart..segEnd];
                            var segRid = Ts.CreateShapedText();
                            if (segment.Length > 0) Ts.ShapedTextAddString(segRid, segment, font.GetRids(), _resolvedFontSize);
                            _lineRids.Add(segRid);
                            starts.Add(logicalStart + segStart);
                            ends.Add(logicalStart + segEnd);
                        }
                    }
                }
            }
            else
            {
                var rid = Ts.CreateShapedText();
                Ts.ShapedTextAddString(rid, logicalLine, font.GetRids(), _resolvedFontSize);
                _lineRids.Add(rid);
                starts.Add(logicalStart);
                ends.Add(logicalEnd);
            }

            if (nlIdx < 0) break;
            logicalStart = nlIdx + 1;
        }

        _lineStarts = [..starts];
        _lineEnds = [..ends];

        // Single-line marker (when !Multiline + Clip + non-empty marker + unfocused, and the line overflows).
        if (hasClip && _overflowMarker.Length > 0 && !HasFocus() && !isMultiline
            && _lineRids.Count > 0 && availableWidth > 0)
        {
            var firstRid = _lineRids[0];
            var firstWidth = (float)Ts.ShapedTextGetWidth(firstRid);
            if (firstWidth > availableWidth)
            {
                EnsureMarkerShaped(font);
                var trimTarget = availableWidth - _markerWidth;
                if (trimTarget > 0)
                {
                    Ts.ShapedTextOverrunTrimToWidth(firstRid, trimTarget,
                        TextServer.TextOverrunFlag.Trim | TextServer.TextOverrunFlag.TrimWordOnly);
                    _showMarker = true;
                    _markerLineIndex = 0;
                    _markerLineX = (float)Ts.ShapedTextGetWidth(firstRid);
                }
            }
        }

        _shapingDirty = false;
    }
    
    private void EnsureShapedWithHotReloadGuard()
    {
        // Hot-reload edge case: when the script reloads, _lineRids comes back empty even though
        // _text has content. Force a re-shape rather than rendering an empty widget.
        if (_lineRids.Count == 0 && !string.IsNullOrEmpty(_text)) _shapingDirty = true;
        EnsureShaped();
    }

    private void FreeLineRids()
    {
        foreach (var rid in _lineRids.Where(rid => rid.IsValid)) Ts.FreeRid(rid);
        _lineRids.Clear();
    }

    private void InvalidateMarker()
    {
        if (_markerRid.IsValid) Ts.FreeRid(_markerRid);
        _markerRid = default;
        _markerWidth = 0f;
    }

    private void EnsureMarkerShaped(Font font)
    {
        if (_markerRid.IsValid) return;
        if (string.IsNullOrEmpty(_overflowMarker)) return;
        _markerRid = Ts.CreateShapedText();
        Ts.ShapedTextAddString(_markerRid, _overflowMarker, font.GetRids(), _resolvedFontSize);
        _markerWidth = (float)Ts.ShapedTextGetWidth(_markerRid);
    }

    private (int line, int col) LocateCaret(int globalIndex)
    {
        for (var i = _lineStarts.Length - 1; i >= 0; i--)
            if (globalIndex >= _lineStarts[i]) return (i, globalIndex - _lineStarts[i]);
        return (0, 0);
    }

    private int GetLineOwnedLength(int lineIndex)
    {
        if (lineIndex + 1 < _lineStarts.Length) return _lineStarts[lineIndex + 1] - _lineStarts[lineIndex];
        return _text.Length - _lineStarts[lineIndex];
    }

    private int GetLineShapedLength(int lineIndex) => _lineEnds[lineIndex] - _lineStarts[lineIndex];

    private int FindShrinkFitFontSize(string text, Font font, float availableWidth)
    {
        var effective = ResolveFontSize();
        var hi = effective;
        var lo = Mathf.Min(_minFontSize, effective);

        if (MeasureWidthAtSize(text, font, hi) <= availableWidth) return hi;
        if (MeasureWidthAtSize(text, font, lo) > availableWidth) return lo;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (MeasureWidthAtSize(text, font, mid) <= availableWidth) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private static float MeasureWidthAtSize(string text, Font font, int fontSize)
    {
        var maxWidth = 0f;
        var start = 0;
        while (start <= text.Length)
        {
            var nlIdx = text.IndexOf('\n', start);
            var end = nlIdx < 0 ? text.Length : nlIdx;
            if (end > start)
            {
                var rid = Ts.CreateShapedText();
                Ts.ShapedTextAddString(rid, text[start..end], font.GetRids(), fontSize);
                var w = (float)Ts.ShapedTextGetWidth(rid);
                Ts.FreeRid(rid);
                if (w > maxWidth) maxWidth = w;
            }
            if (nlIdx < 0) break;
            start = nlIdx + 1;
        }
        return maxWidth;
    }
}