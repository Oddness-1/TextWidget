using System.Collections.Generic;
using Godot;

namespace Widgets;

public partial class TextWidget
{
    private enum EditKind { Insert, Delete, Other }
    
    private readonly List<EditSnapshot> _undoStack = [];
    private readonly List<EditSnapshot> _redoStack = [];
    private const int UNDO_STACK_CAP = 200;
    private const ulong UNDO_SAFETY_FLUSH_MS = 2000;
    
    private void RecordSnapshot(EditKind kind)
    {
        var now = Time.GetTicksMsec();
        if (kind != EditKind.Other && _undoStack.Count > 0)
        {
            var top = _undoStack[^1];
            if (top.Kind == kind && now - top.TimeMs <= UNDO_SAFETY_FLUSH_MS)
            {
                _undoStack[^1] = new EditSnapshot(top.Text, top.CaretIndex, top.SelectionAnchor, top.Kind, now);
                _redoStack.Clear();
                return;
            }
        }

        _undoStack.Add(new EditSnapshot(_text, CaretIndex, SelectionAnchorIndex, kind, now));
        if (_undoStack.Count > UNDO_STACK_CAP) _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    private static bool IsWordBreakChar(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) return false;
            if (c == '_') return false;
        }
        return true;
    }

    private void FlushCoalescing()
    {
        if (_undoStack.Count == 0) return;
        var top = _undoStack[^1];
        if (top.Kind == EditKind.Other) return;
        _undoStack[^1] = new EditSnapshot(top.Text, top.CaretIndex, top.SelectionAnchor, EditKind.Other, top.TimeMs);
    }

    private void DoUndo()
    {
        if (_undoStack.Count == 0) return;
        
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;
        
        var snap = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        _redoStack.Add(new EditSnapshot(_text, CaretIndex, SelectionAnchorIndex, EditKind.Other, Time.GetTicksMsec()));

        SetTextInternal(snap.Text);
        CaretIndex = snap.CaretIndex;
        SelectionAnchorIndex = snap.SelectionAnchor;
        ClampCaret();
        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
        AfterEdit();
    }

    private void DoRedo()
    {
        if (_redoStack.Count == 0) return;
        
        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;
        
        var snap = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        _undoStack.Add(new EditSnapshot(_text, CaretIndex, SelectionAnchorIndex, EditKind.Other, Time.GetTicksMsec()));
        if (_undoStack.Count > UNDO_STACK_CAP) _undoStack.RemoveAt(0);

        SetTextInternal(snap.Text);
        CaretIndex = snap.CaretIndex;
        SelectionAnchorIndex = snap.SelectionAnchor;
        ClampCaret();
        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
        AfterEdit();
    }
        

    private void ClearHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
    
    private readonly struct EditSnapshot(string text, int caret, int anchor, EditKind kind, ulong timeMs)
    {
        public readonly string Text = text;
        public readonly int CaretIndex = caret;
        public readonly int SelectionAnchor = anchor;
        public readonly EditKind Kind = kind;
        public readonly ulong TimeMs = timeMs;
    }
}