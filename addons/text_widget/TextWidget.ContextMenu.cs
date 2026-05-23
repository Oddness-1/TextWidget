using System;
using System.Collections.Generic;
using Godot;

namespace Widgets;

public partial class TextWidget
{
    private PopupMenu? _contextMenu, _spellingSubmenu;
    private static UserDictionaryEditor? _sharedDictionaryEditor;
    private const int MENU_ID_SUGGESTION_BASE = 100;
    private List<string>? _menuSuggestions;
    private TextMark? _menuTargetMark;
    
    private void EnsureContextMenu()
    {
        if (_contextMenu != null! && IsInstanceValid(_contextMenu)) return;
        _contextMenu = new PopupMenu();
        _contextMenu.IdPressed += OnContextMenuIdPressed;
        AddChild(_contextMenu);

        _spellingSubmenu = new PopupMenu { Name = "SpellingSubmenu" };
        _spellingSubmenu.IdPressed += OnContextMenuIdPressed;
        _contextMenu.AddChild(_spellingSubmenu);
    }

    private void ShowContextMenu(Vector2 localPos)
    {
        EnsureContextMenu();

        _contextMenu!.Clear();
        _spellingSubmenu!.Clear();
        _menuSuggestions = null;
        _menuTargetMark = null;

        var charIndex = CharIndexAtLocalPos(localPos);
        var hitSpellMark = TryGetTextMarkAt(charIndex, out var hitMark) && hitMark.Category == "spell";

        if (hitSpellMark)
        {
            var token = _text[hitMark.Start..hitMark.End];
            var suggestions = Spelling.GetSuggestions(token);
            if (suggestions.Count > 0)
            {
                for (var i = 0; i < suggestions.Count; i++)
                    _contextMenu.AddItem(suggestions[i], MENU_ID_SUGGESTION_BASE + i);
                _contextMenu.AddSeparator();
                _menuSuggestions = [..suggestions];
            }
            _spellingSubmenu.AddItem($"Add \"{token}\" to Dictionary", (int)MenuId.AddToDictionary);
            _menuTargetMark = hitMark;
        }

        _contextMenu.AddItem("Cut",        (int)MenuId.Cut);
        _contextMenu.AddItem("Copy",       (int)MenuId.Copy);
        _contextMenu.AddItem("Paste",      (int)MenuId.Paste);
        _contextMenu.AddItem("Delete",     (int)MenuId.Delete);
        _contextMenu.AddItem("Select All", (int)MenuId.SelectAll);
        _contextMenu.AddItem("Undo",       (int)MenuId.Undo);
        _contextMenu.AddItem("Redo",       (int)MenuId.Redo);
        _contextMenu.AddSeparator();
        
        if (SpellCheck && !Engine.IsEditorHint()) _spellingSubmenu.AddItem("Manage Dictionary…", (int)MenuId.ManageDictionary);

        if (_spellingSubmenu.ItemCount > 0)
        {
            _contextMenu.AddSubmenuNodeItem("Settings", _spellingSubmenu);
            _contextMenu.AddSeparator();
        }

        UpdateContextMenuEnabled();
        var screenPos = GetScreenPosition() + localPos;
        _contextMenu.Position = (Vector2I)screenPos;
        _contextMenu.ResetSize();
        _contextMenu.Popup();
    }
    
    private void DisposeContextMenu()
    {
        if (_contextMenu == null || !IsInstanceValid(_contextMenu)) return;
        _contextMenu.QueueFree();
        _contextMenu = null;
    }

    private void UpdateContextMenuEnabled()
    {
        var hasSel = HasSelection;
        var clip = DisplayServer.ClipboardGet();
        var hasClip = !string.IsNullOrEmpty(clip);

        SetMenuDisabled(MenuId.Cut,       !_editable || !hasSel);
        SetMenuDisabled(MenuId.Copy,      !hasSel);
        SetMenuDisabled(MenuId.Paste,     !_editable || !hasClip);
        SetMenuDisabled(MenuId.Delete,    !_editable || !hasSel);
        SetMenuDisabled(MenuId.SelectAll, string.IsNullOrEmpty(_text));
        SetMenuDisabled(MenuId.Undo,      !_editable || _undoStack.Count == 0);
        SetMenuDisabled(MenuId.Redo,      !_editable || _redoStack.Count == 0);
    }

    private void SetMenuDisabled(MenuId id, bool disabled)
    {
        var idx = _contextMenu?.GetItemIndex((int)id) ?? -1;
        if (idx >= 0) _contextMenu?.SetItemDisabled(idx, disabled);
    }

    private void OnContextMenuIdPressed(long id)
    {
        if (_menuSuggestions != null && id >= MENU_ID_SUGGESTION_BASE && id < MENU_ID_SUGGESTION_BASE + _menuSuggestions.Count)
        {
            if (_menuTargetMark is not { } mark) return;
            var suggestion = _menuSuggestions[(int)(id - MENU_ID_SUGGESTION_BASE)];
            ReplaceRange(mark.Start, mark.End, suggestion);
            return;
        }

        switch ((MenuId)id)
        {
            case MenuId.Cut:               DoCut();              break;
            case MenuId.Copy:              DoCopy();             break;
            case MenuId.Paste:             DoPaste();            break;
            case MenuId.SelectAll:         SelectAll();          break;
            case MenuId.Undo:              DoUndo();             break;
            case MenuId.Redo:              DoRedo();             break;
            case MenuId.ManageDictionary:  ShowDictionaryEditor(); break;
            case MenuId.AddToDictionary:
            {
                if (_menuTargetMark is { } mark)
                {
                    var token = _text[mark.Start..mark.End];
                    Spelling.AddToUserDictionary(token);
                    InvalidateTextMarks();
                }
                break;
            }
            case MenuId.Delete:
            {
                if (HasSelection)
                {
                    RecordSnapshot(EditKind.Other);
                    DeleteSelectionInternal();
                    AfterEdit();
                }
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(id), id, null);
        }
    }
    
    private void ShowDictionaryEditor()
    {
        if (_sharedDictionaryEditor == null || !IsInstanceValid(_sharedDictionaryEditor))
        {
            _sharedDictionaryEditor = new UserDictionaryEditor();
            GetTree().Root.AddChild(_sharedDictionaryEditor);
        }
        _sharedDictionaryEditor.PopupCentered();
    }

    private enum MenuId
    {
        Cut = 0,
        Copy = 1,
        Paste = 2,
        Delete = 3,
        SelectAll = 4,
        Undo = 5,
        Redo = 6,
        AddToDictionary = 7,
        ManageDictionary = 8
    }
}