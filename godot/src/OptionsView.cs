using Godot;

namespace GdFfx;

/// <summary>
/// The in-game options overlay (G5): gd-ffx's settings (<see cref="OptionsPanel"/>) over a
/// dimmed picture. Opened with F10 or from the ESC menu's System setting
/// (<see cref="PauseView"/>); while it is open it has the keyboard and mouse (the game gets
/// none: <see cref="GameHost.Capture"/>). Esc, Backspace or F10 close it (an open list first
/// closes itself).
/// </summary>
public partial class OptionsView : Control
{
    private OptionsPanel _panel = null!;
    private Label _title = null!;

    /// <summary>The settings' controls (for tests).</summary>
    public OptionsPanel Panel => _panel;

    /// <summary>Whether it is open.</summary>
    public bool IsOpen => Visible;

    /// <summary>Raised when it closes.</summary>
    public event System.Action? Closed;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var window = new PanelContainer();
        window.AddThemeStyleboxOverride("panel", Panels.Window(0.94f, 22, 14));
        center.AddChild(window);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        window.AddChild(box);
        _title = Panels.Text("gd-ffx options", 24, MenuList.LitColour);
        box.AddChild(_title);
        _panel = new OptionsPanel { InGame = true };
        box.AddChild(_panel);
        box.AddChild(Panels.Text("Changes apply at once and are saved. Up/Down, Enter: change. Esc or F10: back.", 14, MenuList.DimColour));
    }

    public void Open()
    {
        if (Visible)
            return;
        Visible = true;
        _panel.Fill();
        GameHost.Instance?.Capture(this, true);
        _panel.First?.GrabFocus();
        GD.Print("ffx: options open");
    }

    public void Close()
    {
        if (!Visible)
            return;
        Visible = false;
        GetViewport().GuiReleaseFocus();
        GameHost.Instance?.Capture(this, false);
        GD.Print("ffx: options closed");
        Closed?.Invoke();
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Echo: false } k)
            return;
        if (k.PhysicalKeycode is Key.Escape or Key.Backspace or GameHost.OptionsKey)
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
