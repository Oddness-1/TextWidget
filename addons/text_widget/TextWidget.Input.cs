using Godot;

namespace Widgets;

public partial class TextWidget
{
    private ulong _lastClickTimeMs;
    private Vector2 _lastClickPos;
    private int _consecutiveClicks;
    private const ulong MULTI_CLICK_TIMEOUT_MS = 400;
    private const float MULTI_CLICK_POSITION_TOLERANCE_SQ = 16f;
    private bool _selecting;
    private string _altCodeBuffer = "";
    
    private bool HandleMouseButton(InputEventMouseButton mb)
    {
        return mb.ButtonIndex switch
        {
            MouseButton.Left      => HandleLeftMouseButton(mb),
            MouseButton.Right     => HandleRightMouseButton(mb),
            MouseButton.WheelUp   => HandleWheel(mb, scrollDown: false),
            MouseButton.WheelDown => HandleWheel(mb, scrollDown: true),
            _                           => false
        };
    }

    private bool HandleLeftMouseButton(InputEventMouseButton mb)
    {
        if (!mb.Pressed)
        {
            _selecting = false;
            return true;
        }

        GrabFocus();
        var hit = CharIndexAtLocalPos(mb.Position);

        var now = Time.GetTicksMsec();
        var withinTime = now - _lastClickTimeMs <= MULTI_CLICK_TIMEOUT_MS;
        var withinDist = mb.Position.DistanceSquaredTo(_lastClickPos) <= MULTI_CLICK_POSITION_TOLERANCE_SQ;
        _consecutiveClicks = (withinTime && withinDist) ? _consecutiveClicks + 1 : 1;
        _lastClickTimeMs = now;
        _lastClickPos = mb.Position;

        switch (_consecutiveClicks)
        {
            case 1:  MoveCaretTo(hit, extend: mb.ShiftPressed); _selecting = true; break;
            case 2:  SelectWordAt(hit); _selecting = false; break;
            default: SelectAll(); _selecting = false; break;
        }
        return true;
    }

    private bool HandleRightMouseButton(InputEventMouseButton mb)
    {
        if (!mb.Pressed) return false;
        GrabFocus();
        ShowContextMenu(mb.Position);
        return true;
    }

    private bool HandleWheel(InputEventMouseButton mb, bool scrollDown)
    {
        if (!mb.Pressed || GetMaxScrollOffsetY() <= 0f) return false;
        var delta = GetWheelScrollDelta() * mb.Factor;
        ScrollOffsetY += scrollDown ? delta : -delta;
        return true;
    }

    private bool HandleMouseMotion(InputEventMouseMotion mm)
    {
        if (!_selecting) return false;
        MoveCaretTo(CharIndexAtLocalPos(mm.Position), extend: true);
        return true;
    }

    private bool HandleSelectionEnd()
    {
        _selecting = false;
        return true;
    }

    private bool HandleKey(InputEventKey key)
    {
        if (!_editable)
        {
            if (!key.Pressed) return false;
            if (HandleReadOnlyShortcut(key)) return true;
            return HandleNavigationKey(key);
        }

        if (key is { Pressed: false, Keycode: Key.Alt })
        {
            CommitAltCode();
            return true;
        }

        if (!key.Pressed) return false;
        if (HandleAltCode(key)) return true;
        if (_OnCompletionKeyPressed(key)) return true;
        if (HandleReadOnlyShortcut(key)) return true;
        if (HandleEditingShortcut(key)) return true;
        if (HandleNavigationKey(key)) return true;
        if (HandleEditingKey(key)) return true;
        return HandleTextInput(key);
    }

    private bool HandleAltCode(InputEventKey key)
    {
        if (key is { AltPressed: true, CtrlPressed: false, MetaPressed: false } && IsNumpadDigit(key.Keycode))
        {
            _altCodeBuffer += NumpadDigitChar(key.Keycode);
            return true;
        }

        if (key.AltPressed && _altCodeBuffer.Length > 0) _altCodeBuffer = "";
        return false;
    }

    private bool HandleReadOnlyShortcut(InputEventKey key)
    {
        if (key is not { CtrlPressed: true, AltPressed: false }) return false;
        switch (key.Keycode)
        {
            case Key.A: SelectAll(); return true;
            case Key.C: DoCopy();    return true;
            default:    return false;
        }
    }

    private bool HandleEditingShortcut(InputEventKey key)
    {
        if (key is not { CtrlPressed: true, AltPressed: false }) return false;
        switch (key.Keycode)
        {
            case Key.X: DoCut();     return true;
            case Key.V: DoPaste();   return true;
            case Key.Z: if (key.ShiftPressed) DoRedo(); else DoUndo(); return true;
            case Key.Y: DoRedo();    return true;
            default:    return false;
        }
    }

    private bool HandleNavigationKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Left:     HandleHorizontalArrow(toLeft: true,  key.CtrlPressed, key.ShiftPressed); return true;
            case Key.Right:    HandleHorizontalArrow(toLeft: false, key.CtrlPressed, key.ShiftPressed); return true;
            case Key.Up:       HandleVerticalArrow(up: true,  key.ShiftPressed); return true;
            case Key.Down:     HandleVerticalArrow(up: false, key.ShiftPressed); return true;
            case Key.Home:     HandleHomeKey(key.CtrlPressed, key.ShiftPressed); return true;
            case Key.End:      HandleEndKey(key.CtrlPressed, key.ShiftPressed);  return true;
            case Key.Pageup:   return HandlePageScroll(up: true);
            case Key.Pagedown: return HandlePageScroll(up: false);
            default:           return false;
        }
    }

    private void HandleHomeKey(bool ctrl, bool shift)
    {
        if (ctrl) { MoveCaretTo(0, extend: shift); return; }
        var (line, _) = LocateCaret(CaretIndex);
        MoveCaretTo(_lineStarts[line], extend: shift);
    }

    private void HandleEndKey(bool ctrl, bool shift)
    {
        if (ctrl) { MoveCaretTo(_text.Length, extend: shift); return; }
        var (line, _) = LocateCaret(CaretIndex);
        MoveCaretTo(_lineStarts[line] + GetLineShapedLength(line), extend: shift);
    }

    private bool HandlePageScroll(bool up)
    {
        if (GetMaxScrollOffsetY() <= 0f) return false;
        var viewportLines = Mathf.Max(1, _maxLines - 1);  // overlap by one for context
        var delta = viewportLines * GetWheelScrollDelta();
        ScrollOffsetY += up ? -delta : delta;
        return true;
    }

    private bool HandleEditingKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Enter:
            case Key.KpEnter:
                return HandleEnterKey(key.ShiftPressed);

            case Key.Backspace:
                if (key.CtrlPressed) DeleteWordBackward(); else DeleteBackward();
                return true;

            case Key.Delete:
                if (key.CtrlPressed) DeleteWordForward(); else DeleteForward();
                return true;

            default:
                return false;
        }
    }

    private bool HandleEnterKey(bool shift)
    {
        if (shift)
        {
            if (_displayOverflow.HasFlag(OverflowDisplay.Multiline)) InsertText("\n");
            return true;
        }
        _OnSubmit();
        EmitSignal(SignalName.Submitted);
        if (ReleaseFocusOnSubmit) ReleaseFocus();
        return true;
    }

    private bool HandleTextInput(InputEventKey key)
    {
        if (key.Unicode is < 32 or 127) return false;
        InsertText(char.ConvertFromUtf32((int)key.Unicode));
        return true;
    }

    private void HandleHorizontalArrow(bool toLeft, bool ctrl, bool shift)
    {
        if (HasSelection && !shift && !ctrl)
        {
            var edge = toLeft ? SelectionStart() : SelectionEnd();
            MoveCaretTo(edge, extend: false);
            return;
        }

        int target;
        if (ctrl) target = toLeft ? PrevWordBoundary(CaretIndex) : NextWordBoundary(CaretIndex);
        else      target = toLeft ? ToPrevGrapheme(CaretIndex) : ToNextGrapheme(CaretIndex);

        MoveCaretTo(target, extend: shift);
    }

    private void HandleVerticalArrow(bool up, bool shift)
    {
        EnsureShaped();
        if (_lineRids.Count == 0) return;

        var oldCaret = CaretIndex;
        var oldStart = SelectionStartIndex;
        var oldEnd = SelectionEndIndex;

        var (line, _) = LocateCaret(CaretIndex);

        switch (up)
        {
            case true when line == 0:
            {
                MoveCaretTo(0, extend: shift);
                return;  // MoveCaretTo already emitted
            }
            case false when line == _lineRids.Count - 1:
            {
                MoveCaretTo(_text.Length, extend: shift);
                return;  // MoveCaretTo already emitted
            }
        }

        if (_preferredCaretX < 0)
        {
            var (curLine, curCol) = LocateCaret(CaretIndex);
            _preferredCaretX = GetLineDrawOriginX(curLine) + GetCaretRelativeXOnLine(curLine, curCol);
        }

        var target = HitTestOnLine(up ? line - 1 : line + 1, _preferredCaretX);

        CaretIndex = Mathf.Clamp(target, 0, _text.Length);
        if (!shift) SelectionAnchorIndex = CaretIndex;
        ResetCaretBlink();
        ScrollCaretIntoViewY();
        QueueRedraw();
        FlushCoalescing();

        EmitCaretMovedIfNeeded(oldCaret);
        EmitSelectionChangedIfNeeded(oldStart, oldEnd);
    }

    private void HandleDragScrollPoll()
    {
        var maxY = GetMaxScrollOffsetY();
        var maxX = GetMaxScrollOffsetX();
        if (maxY <= 0f && maxX <= 0f) return;

        var mousePos = GetLocalMousePosition();
        var content = GetContentRect();
        var caretMoved = false;

        if (maxY > 0f)
        {
            var topEdge = content.Position.Y;
            var bottomEdge = content.Position.Y + content.Size.Y;

            if (mousePos.Y < topEdge + DRAG_AUTO_SCROLL_EDGE_ZONE)
            {
                var intensity = (topEdge + DRAG_AUTO_SCROLL_EDGE_ZONE - mousePos.Y) / DRAG_AUTO_SCROLL_EDGE_ZONE;
                ScrollOffsetY -= DRAG_AUTO_SCROLL_PIXELS_PER_FRAME * intensity;
                caretMoved = true;
            }
            else if (mousePos.Y > bottomEdge - DRAG_AUTO_SCROLL_EDGE_ZONE)
            {
                var intensity = (mousePos.Y - (bottomEdge - DRAG_AUTO_SCROLL_EDGE_ZONE)) / DRAG_AUTO_SCROLL_EDGE_ZONE;
                ScrollOffsetY += DRAG_AUTO_SCROLL_PIXELS_PER_FRAME * intensity;
                caretMoved = true;
            }
        }

        if (maxX > 0f)
        {
            var leftEdge = content.Position.X;
            var rightEdge = content.Position.X + content.Size.X;

            if (mousePos.X < leftEdge + DRAG_AUTO_SCROLL_EDGE_ZONE)
            {
                var intensity = (leftEdge + DRAG_AUTO_SCROLL_EDGE_ZONE - mousePos.X) / DRAG_AUTO_SCROLL_EDGE_ZONE;
                ScrollOffsetX -= DRAG_AUTO_SCROLL_PIXELS_PER_FRAME * intensity;
                caretMoved = true;
            }
            else if (mousePos.X > rightEdge - DRAG_AUTO_SCROLL_EDGE_ZONE)
            {
                var intensity = (mousePos.X - (rightEdge - DRAG_AUTO_SCROLL_EDGE_ZONE)) / DRAG_AUTO_SCROLL_EDGE_ZONE;
                ScrollOffsetX += DRAG_AUTO_SCROLL_PIXELS_PER_FRAME * intensity;
                caretMoved = true;
            }
        }

        if (caretMoved) MoveCaretTo(CharIndexAtLocalPos(mousePos), extend: true);
    }
}