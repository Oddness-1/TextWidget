# TextWidget

A text input control for Godot 4 (C#). One node that covers single-line, multi-line, shrink-to-fit, clip-with-ellipsis, and scrolling, in any combination. Built-in spell-check. Subclassable.

## Why use it over LineEdit and TextEdit

`LineEdit` and `TextEdit` are separate controls with separate APIs. Switching between single-line and multi-line means swapping the node, and behaviours like shrink-to-fit, focus-driven collapse, or spell-check don't exist on either.

`TextWidget` is one control. Behaviour is set by a flags export (`DisplayOverflow`), and unset theme values fall back to `LineEdit`'s theme, so existing project themes still apply.

Things it does that the stock controls don't:

- Collapse to one line when unfocused, expand to multi-line when focused
- Shrink the font to a configurable floor before clipping
- Cap the visible line count with `MaxLines` and scroll within that
- Spell-check with right-click suggestions and a user-dictionary editor
- Windows-style alt-codes on the numpad
- `CaretMoved` and `SelectionChanged` signals

## Requirements

- Godot 4 with .NET support
- .NET 8 SDK or later (C# 12 features: collection expressions, primary constructors)

## Installation

Copy `addons/text_widget/` into your project's `res://addons/` folder and rebuild the C# solution. The widget registers via `[GlobalClass]` and appears in the Add Node dialog with its own icon.

Keep `addons/text_widget/spelling/en_us.bin` where it is; the `Spelling` service expects it there. The user dictionary is created at `user://text_widget_user_dictionary.txt` on first write.

## Quick start

```csharp
var input = new TextWidget
{
    Placeholder = "Type something…",
    DisplayOverflow = TextWidget.OverflowDisplay.Clip,
};
input.Submitted += () => GD.Print($"Submitted: {input.Text}");
input.TextChanged += (oldText, newText) => GD.Print($"{oldText} -> {newText}");
AddChild(input);
```

Multi-line with a scroll cap:

```csharp
var notes = new TextWidget
{
    DisplayOverflow = TextWidget.OverflowDisplay.Multiline | TextWidget.OverflowDisplay.Clip,
    Scrollable = true,
    MinLines = 3,
    MaxLines = 8,
    SpellCheck = true,
};
```

Collapse-when-unfocused:

```csharp
// One line at rest; expands to its full multi-line layout on focus.
var compact = new TextWidget
{
    DisplayOverflow = TextWidget.OverflowDisplay.Multiline | TextWidget.OverflowDisplay.Clip,
    OverflowMarker = "…",
};
```

## Overflow

`DisplayOverflow` is a `[Flags]` enum. The bits compose:

| Flag          | Effect                                                                                              |
|---------------|-----------------------------------------------------------------------------------------------------|
| `Clip`        | Cuts glyphs at the widget edge. When unfocused, appends `OverflowMarker` (default `…`).             |
| `ShrinkToFit` | Shrinks the font from the effective size down to `MinFontSize` until the text fits on one line.   |
| `Multiline`   | Wraps text. With `Clip`, collapses to the first line plus marker when unfocused.                    |

`ShrinkToFit | Clip` shrinks first, then clips anything still over at the floor. `Multiline | Clip` gives the collapse-on-blur pattern.

## Multi-line layout

With `Multiline` set, `MinLines` floors the widget's minimum height. `MaxLines` caps the visible viewport when `Scrollable` is true; content past the cap scrolls with the wheel, `PageUp`/`PageDown`, or by dragging a selection into the edge zone. `LineSpacing` tightens or loosens the gap between lines.

## Spell-check

Setting `SpellCheck = true` underlines tokens that don't match the bundled English dictionary. Right-click a misspelled word for up to five suggestions and an "Add to Dictionary" entry. Right-click anywhere for a "Manage Dictionary…" submenu that opens a modal for browsing and removing user entries.

The dictionary loads lazily on first lookup. Call `Spelling.Load()` ahead of time if you want to avoid a first-keystroke hitch.

Acronyms (all-caps) and single-letter tokens are skipped. The word currently under the caret is hidden from the spell-check pass for 750 ms after the last keystroke so the squiggle doesn't flicker while typing.

## Theming

`TextWidget` reads its theme values from its own type first, then falls back to `LineEdit`. Recognised entries:

| Type     | Names                                                                                                                |
|----------|----------------------------------------------------------------------------------------------------------------------|
| StyleBox | `normal`, `focus`, `read_only`                                                                                       |
| Font     | `font`                                                                                                               |
| FontSize | `font_size`                                                                                                          |
| Color    | `font_color`, `font_outline_color`, `font_selected_color`, `font_placeholder_color`, `caret_color`, `selection_color` |
| Constant | `caret_width`, `outline_size`                                                                                        |

Each value also has a per-instance inspector override with a checkbox, matching the way stock Godot controls handle theme overrides. Tick to override, untick to fall back to the theme.

## Alt-codes

With Num Lock on, hold `Alt` and type digits on the numpad to build a code, then release `Alt` to commit. Codes starting with `0` use CP1252 (Windows-1252); codes without a leading zero use CP437 (DOS). `Alt+0233` produces `é`, `Alt+1` produces `☺`. Invalid codes are dropped silently.

## Undo / redo

`Ctrl+Z` and `Ctrl+Y` (also `Ctrl+Shift+Z` for redo). Consecutive insertions coalesce into a single snapshot until ~2 seconds of inactivity, a word-break character, or a navigation event flushes the run.

## Signals

| Signal                                          | When it fires                                                              |
|-------------------------------------------------|----------------------------------------------------------------------------|
| `Submitted()`                                   | Enter (without Shift)                                                      |
| `TextChanged(string oldText, string newText)`   | `Text` changes by any path: input, paste, undo/redo, or assignment         |
| `CaretMoved(int oldIndex, int newIndex)`        | The caret position changes                                                 |
| `SelectionChanged(int start, int end)`          | The selection range changes                                                |
| `MultilineActiveChanged(bool nowActive)`        | The widget transitions between expanded and collapsed layout               |

## Inspector reference

### Text
- **Text** — content
- **Placeholder** — shown when `Text` is empty
- **Font Size** — pixel size; `0` inherits the themed default

### Layout
- **Horizontal Alignment** — `Left`, `Center`, `Right`
- **Vertical Alignment** — `Top`, `Center`, `Bottom`, `Fill`. Ignored while scrolling, which top-anchors the viewport.

### Behavior
- **Editable** — whether the user can edit
- **Release Focus On Submit** — whether Enter releases focus
- **Limit Mode** — `None`, `Visible` (refuse input that won't fit the viewport), `Fixed` (refuse input past `MaxLength`)
- **Max Length** — character cap for `Fixed`
- **Spell Check** — toggles spell-check marks

### Overflow
- **Display Overflow** — `Clip`, `Shrink To Fit`, `Multiline` (combinable)

#### Clip
- **Overflow Marker** — appended after truncated content when unfocused. Empty string disables it.

#### Shrink To Fit
- **Min Font Size** — shrink floor. Clamped to the effective font size.

#### Multiline
- **Scrollable** — caps the viewport at `Max Lines` and scrolls inside it

##### Dimensions
- **Min Lines** — floor on reserved height in visual lines
- **Max Lines** — viewport cap when scrollable. `0` means unlimited.
- **Line Spacing** — extra pixels between visual lines, can be negative

### Theme Overrides
Per-instance overrides for every theme value, each with a checkbox.

## Public API

### Read-only state
- `bool HasSelection`
- `int CaretIndex` — range `[0, Text.Length]`
- `int SelectionAnchorIndex` — the fixed end of the selection
- `int SelectionStartIndex`, `int SelectionEndIndex` — half-open range
- `bool IsMultilineActive`
- `Color FontColor`, `FontOutlineColor`, `FontSelectedColor`, `PlaceholderColor`, `ReadOnlyColor`, `CaretColor`, `SelectionColor`, `int OutlineSize`, `int CaretWidth` — resolved values (override-or-theme)

### Methods
- `void ReplaceRange(int start, int end, string replacement, int caretOffset = -1)` — replaces a range as a single undo step. `caretOffset = -1` puts the caret at the end of the replacement; `0` puts it at the start. Useful for completion providers and find-and-replace.
- `Vector2 GetCaretScreenPos()` — screen-space position of the caret's bottom-left, suitable as the anchor for a popup placed below the caret.

## Subclassing

Override the protected hooks instead of reaching into private state:

| Hook                                                            | When it runs                                                                |
|-----------------------------------------------------------------|-----------------------------------------------------------------------------|
| `string _GetPlaceholderText()`                                  | Whenever placeholder text is needed                                         |
| `void _OnSubmit()`                                              | On Enter, before the `Submitted` signal                                     |
| `void _OnTextChanged(string oldText, string newText)`           | Before the `TextChanged` signal                                             |
| `void _OnMultilineActiveChanged(bool nowActive)`                | On every layout transition                                                  |
| `void _OnCompletionContextChanged(string text, int caretIndex)` | On text or caret changes that could affect completion                       |
| `bool _OnCompletionKeyPressed(InputEventKey key)`               | Before the base handles a key. Return `true` to consume.                    |
| `IEnumerable<TextMark> _GetTextMarks()`                         | When marks are needed for drawing. Default emits spell-check marks.         |

A `TextMark` is `(start, end, color, tooltip, category)`. Marks render as wavy underlines and contribute their `Tooltip` when the mouse hovers their range. Call `InvalidateTextMarks()` when your mark source changes. `TryGetTextMarkAt(charIndex, out var mark)` looks up the mark at a position.

Adding parse-error squiggles alongside spell-check:

```csharp
public partial class CodeInput : TextWidget
{
    protected override IEnumerable<TextMark> _GetTextMarks() =>
        base._GetTextMarks().Concat(ParseErrorMarks());

    private IEnumerable<TextMark> ParseErrorMarks()
    {
        foreach (var err in _parser.Errors)
            yield return new TextMark(err.Start, err.End, Colors.OrangeRed, err.Message, "parse");
    }
}
```

## Spelling service

The `Spelling` static class is the spell-check backend. It loads on first lookup; nothing needs to be wired up if you're only using `SpellCheck = true`.

```csharp
public static bool   IsLoaded { get; }
public static IReadOnlyCollection<string> UserDictionary { get; }
public static event  Action? UserDictionaryChanged;

public static void   Load();
public static void   Unload();
public static bool   IsCorrect(string word);
public static IReadOnlyList<string> GetSuggestions(string word, int max = 5);
public static bool   AddToUserDictionary(string word);
public static bool   RemoveFromUserDictionary(string word);
public static void   ClearUserDictionary();
```

The user dictionary is plain UTF-8 text at `user://text_widget_user_dictionary.txt`, one word per line. Hand-editing works fine; missing file means an empty dictionary.

## Limitations

- The bundled dictionary is en-US only. The binary format supports other languages, but no other dictionaries ship with the addon.
- Spell-check is token-based and doesn't understand proper nouns beyond what's in the dictionary.
- Alt-code input has only been tested on Windows. Behaviour on macOS and Linux depends on whether the OS and window manager pass `Alt + numpad` events through to Godot. On a Mac without a physical numpad, it can't fire at all.
- The `[Tool]` annotation lets the widget render in the editor, but spell-check is skipped at editor time to keep the inspector responsive.

## License

0BSD. Do whatever you want with it, no attribution required. See `LICENSE`.

## Issues and contributions

https://github.com/Oddness-1/TextWidget
