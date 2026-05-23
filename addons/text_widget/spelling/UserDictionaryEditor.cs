using System;
using System.Linq;
using Godot;

namespace Widgets;

/// <summary>
/// A modal dialog for managing the user's spell-check dictionary. Owned and shown by
/// TextWidget via its "Manage Dictionary…" context-menu item. Subscribes to
/// <see cref="Spelling.UserDictionaryChanged"/> so the list refreshes when entries are
/// added or removed from anywhere — including the right-click "Add to Dictionary" path
/// in any TextWidget instance.
/// </summary>
[Tool, GlobalClass]
public partial class UserDictionaryEditor : Window
{
    [Export] public string AddPlaceholder { get; set; } = "Add word…";
    [Export] public string EmptyMessage { get; set; } = "Dictionary is empty.";
    [Export] public string RejectionMessage { get; set; } = "Could not add \"{0}\".";
    [Export(PropertyHint.Range, "0.5,10.0,0.1")] public float StatusMessageDuration { get; set; } = 3.0f;

    private const int MIN_FONT_SIZE = 8;
    private const int MAX_FONT_SIZE = 32;
    private const float FONT_SIZE_FACTOR = 0.05f;

    private Button _addButton = null!;
    private LineEdit _addInput = null!;
    private Label _statusLabel = null!;
    private ScrollContainer _scroll = null!;
    private VBoxContainer _list = null!;
    private Label _emptyLabel = null!;
    private Timer _statusTimer = null!;

    public override void _Ready()
    {
        Name = "TWDictionaryEditor";
        Title = "User Dictionary";
        Exclusive = true;
        Transient = true;
        Unresizable = false;
        MinSize = new Vector2I(280, 320);
        Size = new Vector2I(420, 560);
        CloseRequested += Hide;
        SizeChanged += OnDialogSizeChanged;

        BuildLayout();
        Spelling.UserDictionaryChanged += OnDictionaryChanged;
        Rebuild();
        ApplyFontSizes();
    }
    
    public override void _Input(InputEvent @event)
    {
        Control? focusOwner = null;
        var shouldRelease = false;
        var shouldClose = false;

        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
            {
                focusOwner = GetViewport().GuiGetFocusOwner();
                if (focusOwner != null)
                {
                    var mousePos = GetViewport().GetMousePosition();
                    shouldRelease = !focusOwner.GetGlobalRect().HasPoint(mousePos);
                }
                break;
            }

            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
            {
                focusOwner = GetViewport().GuiGetFocusOwner();
                shouldClose = Visible && focusOwner == null;
                shouldRelease = focusOwner != null;
                break;
            }
        }

        if (shouldRelease && focusOwner != null) focusOwner.ReleaseFocus();
        if (shouldClose) Hide();
    }

    public override void _ExitTree() => Spelling.UserDictionaryChanged -= OnDictionaryChanged;

    private void BuildLayout()
    {
        var margin = new MarginContainer { Name = "TWDictionary_Margin", AnchorRight = 1f, AnchorBottom = 1f };
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        AddChild(margin);

        var root = new VBoxContainer
        {
            Name = "TWDictionary_Root",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        root.AddThemeConstantOverride("separation", 8);
        margin.AddChild(root);

        var addRow = new HBoxContainer { Name = "TWDictionary_AddRow", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        addRow.AddThemeConstantOverride("separation", 6);
        root.AddChild(addRow);

        _addInput = new LineEdit
        {
            Name = "TWDictionary_AddInput",
            PlaceholderText = AddPlaceholder,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _addInput.TextSubmitted += _ => TryAddFromInput();
        addRow.AddChild(_addInput);

        _addButton = new Button { Name = "TWDictionary_AddButton", Text = "Add" };
        _addButton.Pressed += TryAddFromInput;
        addRow.AddChild(_addButton);

        _statusLabel = new Label
        {
            Name = "TWDictionary_StatusLabel",
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        root.AddChild(_statusLabel);

        _statusTimer = new Timer { Name = "TWDictionary_StatusTimer", OneShot = true };
        _statusTimer.Timeout += () => _statusLabel.Visible = false;
        AddChild(_statusTimer);

        _emptyLabel = new Label
        {
            Name = "TWDictionary_EmptyLabel",
            Text = EmptyMessage,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Visible = false
        };
        root.AddChild(_emptyLabel);

        _scroll = new ScrollContainer
        {
            Name = "TWDictionary_Scroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        root.AddChild(_scroll);

        _list = new VBoxContainer { Name = "TWDictionary_List", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 4);
        _scroll.AddChild(_list);
    }

    private void TryAddFromInput()
    {
        var word = _addInput.Text.Trim();
        if (word.Length == 0) return;

        if (Spelling.AddToUserDictionary(word))
        {
            _addInput.Text = "";
        }
        else
        {
            ShowStatus(string.Format(RejectionMessage, word));
        }
        _addInput.GrabFocus();
    }

    private void Rebuild()
    {
        foreach (var child in _list.GetChildren()) child.QueueFree();

        var words = Spelling.UserDictionary.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
        _emptyLabel.Visible = words.Count == 0;
        _scroll.Visible = words.Count > 0;

        foreach (var word in words) _list.AddChild(BuildRow(word));

        ApplyFontSizes();  // newly-created rows need the current font size applied
    }

    private static HBoxContainer BuildRow(string word)
    {
        var row = new HBoxContainer { Name = $"TWDictionary_Row_{word}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        var label = new Label
        {
            Name = "TWDictionary_RowLabel",
            Text = word,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.AddChild(label);

        var remove = new Button { Name = "TWDictionary_RowRemove", Text = "×", TooltipText = $"Remove \"{word}\" from dictionary" };
        remove.Pressed += () => Spelling.RemoveFromUserDictionary(word);
        row.AddChild(remove);

        return row;
    }

    private void OnDictionaryChanged() => Rebuild();

    private void ShowStatus(string message)
    {
        _statusLabel.Text = message;
        _statusLabel.Visible = true;
        _statusTimer.Start(StatusMessageDuration);
    }
    
    private int ComputeFontSize()
    {
        var height = Size.Y;
        var raw = Mathf.RoundToInt(height * FONT_SIZE_FACTOR);
        return Mathf.Clamp(raw, MIN_FONT_SIZE, MAX_FONT_SIZE);
    }

    private void ApplyFontSizes()
    {
        var size = ComputeFontSize();

        _addInput.AddThemeFontSizeOverride("font_size", size);
        _addButton.AddThemeFontSizeOverride("font_size", size);
        _statusLabel.AddThemeFontSizeOverride("font_size", size);
        _emptyLabel.AddThemeFontSizeOverride("font_size", size);
        
        foreach (var rowNode in _list.GetChildren())
        {
            if (rowNode is not HBoxContainer row) continue;
            foreach (var cellNode in row.GetChildren())
            {
                if (cellNode is Control control) control.AddThemeFontSizeOverride("font_size", size);
            }
        }
    }

    private void OnDialogSizeChanged() => ApplyFontSizes();
}