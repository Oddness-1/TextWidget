using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Widgets;

public partial class TextWidget
{
    private List<TextMark>? _cachedTextMarks;
    private bool _textMarksDirty = true;
    private static readonly Color SPELL_CHECK_COLOR = new(0.95f, 0.25f, 0.25f);
    private ulong _lastEditTimeMs;
    private bool _editDebounceActive;
    private const ulong SPELL_CHECK_DEBOUNCE_MS = 750;
    
    private void EnsureTextMarks()
    {
        if (!_textMarksDirty && _cachedTextMarks != null) return;
        _cachedTextMarks = _GetTextMarks().ToList();
        _textMarksDirty = false;
    }

    private IEnumerable<TextMark> GetSpellCheckMarks()
    {
        var text = _text;
        if (string.IsNullOrEmpty(text)) yield break;

        var len = text.Length;
        var i = 0;
        while (i < len)
        {
            if (!char.IsLetter(text[i])) { i++; continue; }

            var tokenStart = i;
            while (i < len && (char.IsLetter(text[i]) || text[i] == '\'')) i++;
            var tokenEnd = i;

            while (tokenStart < tokenEnd && text[tokenStart] == '\'') tokenStart++;
            while (tokenEnd > tokenStart && text[tokenEnd - 1] == '\'') tokenEnd--;

            var tokenLen = tokenEnd - tokenStart;
            if (tokenLen < 2) continue;

            // Skip acronyms
            var allCaps = true;
            for (var k = tokenStart; k < tokenEnd; k++)
            {
                if (!char.IsLetter(text[k]) || char.IsUpper(text[k])) continue;
                allCaps = false; break;
            }
            if (allCaps) continue;

            var token = text[tokenStart..tokenEnd];
            if (Spelling.IsCorrect(token)) continue;

            yield return new TextMark(tokenStart, tokenEnd, SPELL_CHECK_COLOR, null, "spell");
        }
    }
    
    private bool TryGetActiveEditRange(out int start, out int end)
    {
        start = 0;
        end = 0;
        if (!_editDebounceActive || string.IsNullOrEmpty(_text)) return false;

        var text = _text;
        var len = text.Length;
        var s = Mathf.Clamp(CaretIndex, 0, len);
        var e = s;

        while (s > 0 && (char.IsLetter(text[s - 1]) || text[s - 1] == '\'')) s--;
        while (e < len && (char.IsLetter(text[e]) || text[e] == '\'')) e++;

        while (s < e && text[s] == '\'') s++;
        while (e > s && text[e - 1] == '\'') e--;

        if (e <= s) return false;
        start = s;
        end = e;
        return true;
    }
    
    private bool IsMarkSuppressed(TextMark mark)
    {
        if (mark.Category != "spell") return false;
        if (!TryGetActiveEditRange(out var activeStart, out var activeEnd)) return false;
        return mark.Start < activeEnd && mark.End > activeStart;
    }
}