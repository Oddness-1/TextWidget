using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Godot;
using FileAccess = Godot.FileAccess;
// ReSharper disable CommentTypo
// ReSharper disable StringLiteralTypo

namespace Widgets;

/// <summary>
/// Global spell-checking service backed by a compact on-disk dictionary plus a per-user dictionary
/// for additions. Static and side-effect-free from the consumer's perspective — <see cref="IsCorrect"/>
/// lazy-loads on first call and stays loaded until <see cref="Unload"/> is invoked. <see cref="Load"/>
/// is an explicit warm-up that callers can invoke proactively (e.g. when an editor opens) to avoid
/// the load cost on first use.
/// </summary>
/// <remarks>
/// The main dictionary (<c>en_us.bin</c>) lives alongside this class in the <c>text_widget</c> addon
/// and is shared by every consumer in the project. The user dictionary lives at
/// <c>user://text_widget_user_dictionary.txt</c> — one word per line, UTF-8 — and is intended to be
/// edited primarily through this API (though hand-editing in a text editor is supported).
/// Both dictionaries are consulted by <see cref="IsCorrect"/> and <see cref="GetSuggestions"/>.
/// </remarks>
public static class Spelling
{
    private const string DICTIONARY_PATH = "res://addons/text_widget/spelling/en_us.bin";
    private const string USER_DICTIONARY_PATH = "user://text_widget_user_dictionary.txt";
    private const string USER_DICTIONARY_TEMP_PATH = "user://text_widget_user_dictionary.txt.tmp";

    // Binary format constants. See build_dict.py for the authoritative spec.
    private const int HEADER_SIZE = 24;
    private static readonly byte[] EXPECTED_MAGIC = "TWDC"u8.ToArray();
    private const ushort EXPECTED_VERSION = 1;

    // Suggestion tuning. Max edit distance of 2 catches real typos without surfacing nonsense
    // (cat→dog is distance 3 but unrelated). Default max results of 5 matches what spell-checker
    // context menus typically show.
    private const int MAX_EDIT_DISTANCE = 2;

    // Main dictionary state. Null when unloaded; non-null and fully initialized when loaded.
    private static byte[]? _data;
    private static int _indexCount;
    private static int _indexStride;
    private static int _dataOffset;

    // User dictionary state. Stored case-flexibly (we apply the same case-folding chain at lookup
    // time, so adding "Mikkelsen" also passes "mikkelsen" and "MIKKELSEN"). The set holds whatever
    // casing the user provided — usually the natural form for display in the management UI.
    private static HashSet<string>? _userDictionary;
    private static readonly HashSet<string> USER_DICTIONARY_LOWER_INDEX = [];

    /// <summary>
    /// Fired whenever the user dictionary changes (add, remove, clear, or reload from disk).
    /// Subscribe from a management UI to refresh the displayed list. Fires after the change is
    /// persisted, so reading <see cref="UserDictionary"/> in the handler returns the new state.
    /// </summary>
    public static event Action? UserDictionaryChanged;

    /// <summary>
    /// Whether the main dictionary is currently resident in memory. <see cref="IsCorrect"/> and
    /// other lookup methods autoload on first use, so checking this isn't usually necessary — it
    /// exists for callers that want to gate behavior (e.g. skip showing a "loading" indicator) or
    /// assert invariants in tests.
    /// </summary>
    public static bool IsLoaded => _data != null;

    /// <summary>
    /// The current contents of the user dictionary as a snapshot. Returns an empty collection if
    /// the dictionary has not been loaded. Order is not meaningful — sort in the consumer if needed.
    /// </summary>
    public static IReadOnlyCollection<string> UserDictionary => _userDictionary == null ? [] : _userDictionary.ToArray();

    /// <summary>
    /// Loads the main and user dictionaries into memory. Idempotent — calling when already loaded
    /// is a no-op. Reads the binary at <c>res://addons/text_widget/spelling/en_us.bin</c> via
    /// Godot's resource API (so it works in both editor and exported builds) and parses the header.
    /// Also loads <c>user://text_widget_user_dictionary.txt</c> if it exists; absence is not an
    /// error (a fresh install just has no user additions yet).
    /// Typical cost: ~50ms cold, ~1.6 MB resident. Callers can invoke proactively to avoid the
    /// hit on the first lookup, but it is not required — lookups autoload on demand.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the main dictionary file is missing, the magic bytes don't match, or the
    /// version is unsupported. These are bugs in deployment (file not shipped, file replaced with
    /// a future format) rather than runtime conditions a caller can recover from. A missing or
    /// malformed user dictionary is non-fatal — it's treated as empty and a fresh one is written
    /// on the next mutation.
    /// </exception>
    public static void Load()
    {
        if (_data != null) return;

        using var file = FileAccess.Open(DICTIONARY_PATH, FileAccess.ModeFlags.Read);
        if (file == null)
        {
            throw new InvalidOperationException(
                $"Spelling: dictionary file not found at '{DICTIONARY_PATH}'. " +
                $"Error: {FileAccess.GetOpenError()}");
        }

        var length = (long)file.GetLength();
        if (length < HEADER_SIZE)
        {
            throw new InvalidOperationException(
                $"Spelling: dictionary file is too small ({length} bytes) to contain a valid header.");
        }

        var bytes = file.GetBuffer(length);

        // Validate magic.
        if (EXPECTED_MAGIC.Where((t, i) => bytes[i] != t).Any()) 
            throw new InvalidOperationException("Spelling: dictionary file has bad magic. Expected 'TWDC'.");

        var span = bytes.AsSpan();
        var version = BinaryPrimitives.ReadUInt16LittleEndian(span[4..6]);
        if (version != EXPECTED_VERSION)
            throw new InvalidOperationException($"Spelling: dictionary file version {version} is not supported (expected {EXPECTED_VERSION}).");

        _indexCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[12..16]);
        _indexStride = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[16..20]);
        _dataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[20..24]);

        // Sanity-check the parsed header against the file we got.
        if (_indexCount <= 0 || _indexStride <= 0 || _dataOffset < HEADER_SIZE + _indexCount * 4 || _dataOffset > bytes.Length)
        {
            throw new InvalidOperationException(
                $"Spelling: dictionary file header is malformed (indexCount={_indexCount}, " +
                $"indexStride={_indexStride}, dataOffset={_dataOffset}, fileLength={bytes.Length}).");
        }

        _data = bytes;
        LoadUserDictionary();
    }

    /// <summary>
    /// Releases the in-memory dictionaries. Safe to call at any time — subsequent lookups will
    /// transparently reload. Intended for consumers that load the dictionary only when needed
    /// (e.g. an in-game editor) and want to reclaim the ~1.6 MB when done. Calling while
    /// another consumer is actively spell-checking is technically safe but wasteful; coordinate
    /// at the application level. The user dictionary on disk is preserved — only the in-memory
    /// copy is dropped.
    /// </summary>
    public static void Unload()
    {
        _data = null;
        _indexCount = 0;
        _indexStride = 0;
        _dataOffset = 0;
        _userDictionary = null;
        USER_DICTIONARY_LOWER_INDEX.Clear();
    }

    /// <summary>
    /// Returns whether <paramref name="word"/> is spelled correctly. The word is matched against
    /// the user dictionary first (cheap exact-and-case-folded lookup), then the main dictionary
    /// using the same case-folding chain: exact match, then Title-Case, then lowercase. Returns
    /// <c>true</c> if any variant in either dictionary matches. Empty strings are treated as
    /// correct (nothing to check).
    /// </summary>
    /// <remarks>
    /// Lazy-loads the dictionary on first call. Subsequent calls skip the load check.
    /// Lookup cost is microseconds: binary-search the offset index, then linear-scan a bucket
    /// of at most <c>indexStride</c> (64) entries.
    /// </remarks>
    public static bool IsCorrect(string word)
    {
        if (string.IsNullOrEmpty(word)) return true;

        EnsureLoaded();
        
        if (UserDictionaryContains(word)) return true;
        
        if (ContainsExact(word)) return true;
        
        var titleCase = ToTitleCase(word);
        if (!ReferenceEquals(titleCase, word) && ContainsExact(titleCase)) return true;

        var lower = word.ToLowerInvariant();
        return lower != word && ContainsExact(lower);
    }

    /// <summary>
    /// Returns up to <paramref name="max"/> suggested corrections for <paramref name="word"/>,
    /// ranked by edit distance (closest first), then by same-length preference, then alphabetically.
    /// Returns an empty list if the word is correctly spelled, empty/null, or no candidates exist
    /// within the maximum edit distance of 2. Suggestions are drawn from both the main and user
    /// dictionaries.
    /// </summary>
    /// <remarks>
    /// Uses Damerau-Levenshtein distance, which counts insertions, deletions, substitutions, and
    /// adjacent transpositions (so <c>teh</c>/<c>the</c> is distance 1, not 2). Suggestions are
    /// drawn from the lowercase dictionary; if the input is capitalized, the casing is restored
    /// (e.g. suggestions for <c>Recieve</c> include <c>Receive</c>, not <c>receive</c>).
    /// Cost is one pass over the dictionary (~50–100ms for ~170k words), filtered aggressively
    /// by length — only words within ±2 characters of the input are scored. Acceptable for a
    /// right-click context menu; not for per-keystroke use.
    /// </remarks>
    public static IReadOnlyList<string> GetSuggestions(string word, int max = 5)
    {
        if (string.IsNullOrEmpty(word) || max <= 0) return [];

        EnsureLoaded();
        if (_data == null || IsCorrect(word)) return [];
        var inputLower = word.ToLowerInvariant();
        var casing = DetectCasing(word);
        var inputBytes = System.Text.Encoding.UTF8.GetBytes(inputLower);
        var inputLen = inputBytes.Length;
        var prev2 = new int[inputLen + 1];
        var prev1 = new int[inputLen + 1];
        var curr = new int[inputLen + 1];
        var candidates = new List<(int distance, string word)>(max * 4);
        var data = _data.AsSpan();
        var pos = _dataOffset;
        var endPos = data.Length;
        
        while (pos < endPos)
        {
            var newlineAt = IndexOfByte(data, (byte)'\n', pos, endPos);
            if (newlineAt < 0) break;

            var candidateBytes = data[pos..newlineAt];
            pos = newlineAt + 1;

            var lenDiff = candidateBytes.Length - inputBytes.Length;
            if (lenDiff is < -MAX_EDIT_DISTANCE or > MAX_EDIT_DISTANCE) continue;

            var distance = DamerauLevenshtein(inputBytes, candidateBytes, prev2, prev1, curr, MAX_EDIT_DISTANCE);
            if (distance is < 0 or > MAX_EDIT_DISTANCE) continue;

            candidates.Add((distance, System.Text.Encoding.UTF8.GetString(candidateBytes)));
        }
        
        if (_userDictionary != null)
        {
            foreach (var userWord in _userDictionary)
            {
                var userLower = userWord.ToLowerInvariant();
                var userBytes = System.Text.Encoding.UTF8.GetBytes(userLower);
                var lenDiff = userBytes.Length - inputBytes.Length;
                if (lenDiff is < -MAX_EDIT_DISTANCE or > MAX_EDIT_DISTANCE) continue;

                var distance = DamerauLevenshtein(inputBytes, userBytes, prev2, prev1, curr, MAX_EDIT_DISTANCE);
                if (distance is < 0 or > MAX_EDIT_DISTANCE) continue;

                candidates.Add((distance, userLower));
            }
        }

        if (candidates.Count == 0) return [];
        
        candidates.Sort((a, b) =>
        {
            var d = a.distance.CompareTo(b.distance);
            if (d != 0) return d;
            var aLenDiff = Math.Abs(a.word.Length - inputLen);
            var bLenDiff = Math.Abs(b.word.Length - inputLen);
            var l = aLenDiff.CompareTo(bLenDiff);
            return l != 0 ? l : string.CompareOrdinal(a.word, b.word);
        });

        var take = Math.Min(max, candidates.Count);
        var results = new string[take];
        for (var i = 0; i < take; i++) results[i] = ApplyCasing(candidates[i].word, casing);
        return results;
    }

    /// <summary>
    /// Adds <paramref name="word"/> to the user dictionary and persists the change to disk.
    /// Leading and trailing whitespace is trimmed; internal whitespace is rejected (the dictionary
    /// works in terms of individual tokens, not phrases). Returns <c>true</c> if the word was
    /// added; <c>false</c> if the input was rejected (empty after trimming, contained internal
    /// whitespace) or already present (in either dictionary). Fires <see cref="UserDictionaryChanged"/>
    /// on successful add.
    /// </summary>
    public static bool AddToUserDictionary(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;

        var trimmed = word.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed.Any(char.IsWhiteSpace)) { return false; }

        EnsureLoaded();
        
        if (IsCorrect(trimmed)) return false;

        _userDictionary ??= [];
        if (!_userDictionary.Add(trimmed)) return false;
        USER_DICTIONARY_LOWER_INDEX.Add(trimmed.ToLowerInvariant());

        SaveUserDictionary();
        UserDictionaryChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Removes <paramref name="word"/> from the user dictionary. Comparison is exact (matching the
    /// form returned by <see cref="UserDictionary"/>) — to remove an entry, pass the exact string
    /// you got back from the snapshot. Returns <c>true</c> if removed; <c>false</c> if the word
    /// was not present. Fires <see cref="UserDictionaryChanged"/> on successful remove.
    /// </summary>
    public static bool RemoveFromUserDictionary(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        EnsureLoaded();
        if (_userDictionary == null) return false;
        if (!_userDictionary.Remove(word)) return false;
        
        RebuildLowerIndex();

        SaveUserDictionary();
        UserDictionaryChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Removes every entry from the user dictionary. Useful for a "Reset User Dictionary" action
    /// in a management UI. Persists the empty state to disk (the file is overwritten with zero
    /// content, not deleted). Fires <see cref="UserDictionaryChanged"/> if any entries were
    /// actually removed.
    /// </summary>
    public static void ClearUserDictionary()
    {
        EnsureLoaded();
        if (_userDictionary == null || _userDictionary.Count == 0) return;

        _userDictionary.Clear();
        USER_DICTIONARY_LOWER_INDEX.Clear();
        SaveUserDictionary();
        UserDictionaryChanged?.Invoke();
    }

    private static void LoadUserDictionary()
    {
        _userDictionary = [];
        USER_DICTIONARY_LOWER_INDEX.Clear();

        if (!FileAccess.FileExists(USER_DICTIONARY_PATH)) return;

        using var file = FileAccess.Open(USER_DICTIONARY_PATH, FileAccess.ModeFlags.Read);
        if (file == null)
        {
            GD.PushWarning(
                $"Spelling: user dictionary exists but could not be opened. Error: {FileAccess.GetOpenError()}. " +
                $"Treating as empty.");
            return;
        }

        while (!file.EofReached())
        {
            var line = file.GetLine();
            if (string.IsNullOrEmpty(line)) continue;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var hasWhitespace = trimmed.Any(char.IsWhiteSpace);
            if (hasWhitespace) continue;

            _userDictionary.Add(trimmed);
            USER_DICTIONARY_LOWER_INDEX.Add(trimmed.ToLowerInvariant());
        }
    }

    private static void SaveUserDictionary()
    {
        if (_userDictionary == null) return;

        using (var tmp = FileAccess.Open(USER_DICTIONARY_TEMP_PATH, FileAccess.ModeFlags.Write))
        {
            if (tmp == null)
            {
                GD.PushError($"Spelling: could not open user dictionary temp file for writing. " + $"Error: {FileAccess.GetOpenError()}. Changes not persisted.");
                return;
            }
            
            foreach (var word in _userDictionary.OrderBy(static w => w, StringComparer.Ordinal))
            {
                tmp.StoreLine(word);
            }
        }

        var err = DirAccess.RenameAbsolute(
            ProjectSettings.GlobalizePath(USER_DICTIONARY_TEMP_PATH),
            ProjectSettings.GlobalizePath(USER_DICTIONARY_PATH));

        if (err == Error.Ok) return;
        GD.PushError($"Spelling: could not rename temp user dictionary into place. Error: {err}");
    }

    private static void RebuildLowerIndex()
    {
        USER_DICTIONARY_LOWER_INDEX.Clear();
        if (_userDictionary == null) return;
        foreach (var w in _userDictionary) USER_DICTIONARY_LOWER_INDEX.Add(w.ToLowerInvariant());
    }

    private static bool UserDictionaryContains(string word)
    {
        if (_userDictionary == null || _userDictionary.Count == 0) return false;
        return _userDictionary.Contains(word) || USER_DICTIONARY_LOWER_INDEX.Contains(word.ToLowerInvariant());
    }
    

    private static void EnsureLoaded()
    {
        if (_data == null) Load();
    }

    private static int DamerauLevenshtein(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int[] prev2, int[] prev1, int[] curr, int maxDistance)
    {
        var aLen = a.Length;
        var bLen = b.Length;
        if (aLen == 0) return bLen;
        if (bLen == 0) return aLen;

        for (var i = 0; i <= aLen; i++) prev1[i] = i;

        for (var j = 1; j <= bLen; j++)
        {
            curr[0] = j;
            var rowMin = curr[0];
            var bj = b[j - 1];
            var bjPrev = j >= 2 ? b[j - 2] : (byte)0;

            for (var i = 1; i <= aLen; i++)
            {
                var ai = a[i - 1];
                var cost = ai == bj ? 0 : 1;

                var insert = curr[i - 1] + 1;
                var delete = prev1[i] + 1;
                var replace = prev1[i - 1] + cost;
                var best = Math.Min(Math.Min(insert, delete), replace);

                if (i >= 2 && j >= 2 && ai == bjPrev && a[i - 2] == bj)
                {
                    var transpose = prev2[i - 2] + 1;
                    if (transpose < best) best = transpose;
                }

                curr[i] = best;
                if (best < rowMin) rowMin = best;
            }

            if (rowMin > maxDistance) return -1;

            (prev2, prev1, curr) = (prev1, curr, prev2);
        }

        return prev1[aLen];
    }

    private static bool ContainsExact(string word)
    {
        if (_data == null) return false;

        var target = System.Text.Encoding.UTF8.GetBytes(word);
        var data = _data.AsSpan();

        var lo = 0;
        var hi = _indexCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var firstWord = ReadWordAt(data, GetIndexEntry(data, mid));
            if (CompareBytes(firstWord, target) <= 0) lo = mid;
            else hi = mid - 1;
        }

        var bucketStart = GetIndexEntry(data, lo);
        var bucketEnd = lo + 1 < _indexCount ? GetIndexEntry(data, lo + 1) : data.Length;

        var pos = bucketStart;
        while (pos < bucketEnd)
        {
            var newlineAt = IndexOfByte(data, (byte)'\n', pos, bucketEnd);
            if (newlineAt < 0) break;

            var word_ = data.Slice(pos, newlineAt - pos);
            var cmp = CompareBytes(word_, target);
            switch (cmp)
            {
                case 0:
                    return true;
                case > 0:
                    return false;
                default:
                    pos = newlineAt + 1;
                    break;
            }
        }

        return false;
    }

    private static int GetIndexEntry(ReadOnlySpan<byte> data, int entryIndex)
    {
        var offset = HEADER_SIZE + entryIndex * 4;
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    private static ReadOnlySpan<byte> ReadWordAt(ReadOnlySpan<byte> data, int byteOffset)
    {
        var end = IndexOfByte(data, (byte)'\n', byteOffset, data.Length);
        if (end < 0) end = data.Length;
        return data[byteOffset..end];
    }

    private static int IndexOfByte(ReadOnlySpan<byte> data, byte value, int start, int end)
    {
        for (var i = start; i < end; i++) if (data[i] == value) return i;
        return -1;
    }

    private static int CompareBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceCompareTo(b);

    private static string ToTitleCase(string word)
    {
        if (word.Length == 0) return word;
        var first = word[0];
        var upperFirst = char.ToUpperInvariant(first);
        if (first == upperFirst && AllLowerAfter(word, 1)) return word;

        return string.Create(word.Length, word, static (span, src) =>
        {
            span[0] = char.ToUpperInvariant(src[0]);
            for (var i = 1; i < src.Length; i++) span[i] = char.ToLowerInvariant(src[i]);
        });
    }

    private static bool AllLowerAfter(string word, int startIndex)
    {
        for (var i = startIndex; i < word.Length; i++)
            if (word[i] != char.ToLowerInvariant(word[i])) return false;
        return true;
    }

    private enum Casing { Lower, Title, Upper, Mixed }

    private static Casing DetectCasing(string word)
    {
        if (word.Length == 0) return Casing.Lower;

        var allUpper = true;
        var allLower = true;
        foreach (var c in word)
        {
            if (c != char.ToUpperInvariant(c)) allUpper = false;
            if (c != char.ToLowerInvariant(c)) allLower = false;
        }

        if (allUpper && word.Length > 1) return Casing.Upper;
        if (allLower) return Casing.Lower;
        if (char.IsUpper(word[0]) && AllLowerAfter(word, 1)) return Casing.Title;
        return Casing.Mixed;
    }

    private static string ApplyCasing(string lowercase, Casing casing) => casing switch
    {
        Casing.Upper => lowercase.ToUpperInvariant(),
        Casing.Title => ToTitleCase(lowercase),
        _ => lowercase
    };
}