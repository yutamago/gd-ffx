using System.Collections.Generic;
using GdFfx.Api;
using Godot;

namespace GdFfx;

/// <summary>
/// The pause screen and the ESC menu, natively (G5).
///
/// <para><b>The pause</b> (Start, B: <c>World.paused</c>, the game's <c>g_PauseOn</c>; the PC
/// shows <c>pausemenu.swf</c>'s "pause"): the picture dimmed with "PAUSE" and the keys. The
/// game keeps the input (Start resumes); F10 opens the options over it.</para>
///
/// <para><b>The ESC menu</b> (<c>Menus.esc_menu</c>, the game's <c>g_FFEscMenu</c>: Esc opens
/// it and the game stands still, as its <c>escmenu.swf</c> page is up). The game's page is a
/// Flash screen gd-ffx does not draw, so while it is open gd-ffx shows its home page's entries
/// (the game's own texts, localisation lines 0x217..0x21f) and keeps the keyboard and mouse
/// (<see cref="GameHost.Capture"/>), never moving the game's invisible cursor: Resume (Esc taps
/// the game's Esc, which closes its menu from any page), System setting (gd-ffx's own settings,
/// <see cref="OptionsView"/>: they replace the game's video page; the volume is there too),
/// Special features (the four boosters with their state: one picked closes the menu and taps
/// its key, F1..F4), Change parameters (not offered: the game's page only), Quit (asks, then
/// ends gd-ffx). Up/Down choose, Enter (C, Space) confirms, Esc (Backspace, X) goes back.</para>
///
/// <para>F10 opens the options from play too: gd-ffx taps the game's Esc first, so the game
/// stands still under them, and the options open over the ESC menu (at once when the game's
/// menu does not open within 0.7 s, e.g. during a load).</para>
/// </summary>
public partial class PauseView : Control
{
    private enum Page { Home, Features, Quit }

    private ColorRect _dim = null!;
    private PanelContainer _window = null!;
    private Label _title = null!, _help = null!, _pause = null!, _pauseKeys = null!;
    private MenuList _list = null!;
    private Page _page = Page.Home;
    private int _cursor;
    private bool _menuOpen;
    private double _resumeAt = -1, _wantOptionsAt = -1, _clock;
    private int _keyAfterClose;
    private bool _optionsFromMenu;
    private OptionsView? _options;

    /// <summary>The rows shown now (the highlighted one marked), the page and the state (for tests).</summary>
    public List<string> Shown => _list.Shown;
    public string PageName => _menuOpen ? _page.ToString() : _pause.Visible ? "Pause" : "";

    /// <summary>Whether the native ESC menu is up (the game's is open).</summary>
    public bool MenuOpen => _menuOpen;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _dim = new ColorRect { Color = new Color(0, 0, 0, 0.5f), MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_dim);
        _pause = Panels.Text("PAUSE", 54, MenuList.LitColour, MessageView.UiFont);
        _pause.AddThemeColorOverride("font_outline_color", Colors.Black);
        _pause.AddThemeConstantOverride("outline_size", 8);
        _pause.Visible = false;
        AddChild(_pause);
        _pauseKeys = Panels.Text("Start (B): resume    Esc: PC menu    F10: options", 18, MenuList.TextColour);
        _pauseKeys.Visible = false;
        AddChild(_pauseKeys);
        _window = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _window.AddThemeStyleboxOverride("panel", Panels.Window(0.92f, 22, 12));
        AddChild(_window);
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 8);
        _window.AddChild(box);
        _title = Panels.Text("", 18, new Color(0.75f, 0.8f, 0.95f), MessageView.UiFont);
        box.AddChild(_title);
        _list = new MenuList(24, MessageView.UiFont);
        box.AddChild(_list);
        _help = Panels.Text("", 15, MenuList.DimColour, MessageView.UiFont);
        _help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _help.CustomMinimumSize = new Vector2(420, 0);
        box.AddChild(_help);
        CallDeferred(MethodName.FindOptions);
    }

    private void FindOptions()
    {
        _options = GetParent()?.GetNodeOrNull<OptionsView>("OptionsView");
        if (_options != null)
            _options.Closed += () =>
            {
                // back on the ESC menu's page
                _optionsFromMenu = false;
            };
    }

    private string Loc(FfxGame? game, Snapshot? s, int id, string english)
    {
        if (game == null || s == null)
            return english;
        var key = (id, s.World.Language);
        if (!_loc.TryGetValue(key, out var t))
        {
            t = game.FlashText(id, MessageView.LanguageCode(s.World.Language));
            _loc[key] = t.Length > 0 ? t : english;
        }
        return _loc[key];
    }

    private readonly Dictionary<(int, byte), string> _loc = new();

    /// <summary>The home page: the game's entries (its texts) and what each does here.</summary>
    private static readonly (int Loc, string English, string Help)[] Home =
    [
        (0x217, "Resume", "Back to the game."),
        (0x219, "System setting", "gd-ffx's settings: graphics, sound volume, game speed, the FPS overlay (also F10)."),
        (0x21b, "Special features", "The PC boosters: Turbo, Supercharge, enemy encounters, auto battle (also F1 to F4 in play)."),
        (0x21d, "Change parameters", "Not offered by gd-ffx yet: the game's own page is a Flash screen gd-ffx does not draw."),
        (0x21f, "Quit", "Quit gd-ffx."),
    ];

    /// <summary>The rows of a page with whether each can be chosen.</summary>
    private (List<string> Rows, List<bool> Enabled, string Help) Rows(FfxGame? game, Snapshot? s)
    {
        var rows = new List<string>();
        var enabled = new List<bool>();
        string help;
        switch (_page)
        {
            case Page.Features:
            {
                var b = s?.Boosters;
                rows.Add($"F1 Turbo: {(b?.SpeedLevel) switch { 1 => "x2", 2 => "x4", _ => "Normal" }}");
                rows.Add($"F2 Supercharge: {(b?.Powerup == true ? "On" : "Off")}");
                rows.Add($"F3 Enemy encounters: {(b?.Encounters) switch { 0 => "None", 2 => "Frequent", _ => "Normal" }}");
                rows.Add($"F4 Auto battle: {(b?.AutoBattle == true ? "On" : "Off")}");
                enabled.AddRange([true, true, true, true]);
                help = "Enter: back to the game with the next mode (the key is pressed for you). Esc: back.";
                break;
            }
            case Page.Quit:
                rows.Add(Loc(game, s, 0x215, "Cancel"));
                rows.Add(Loc(game, s, 0x21f, "Quit"));
                enabled.AddRange([true, true]);
                help = "Quit gd-ffx? Unsaved progress is lost.";
                break;
            default:
                foreach (var (loc, english, _) in Home)
                {
                    rows.Add(Loc(game, s, loc, english));
                    enabled.Add(loc != 0x21d);
                }
                help = Home[System.Math.Clamp(_cursor, 0, Home.Length - 1)].Help;
                break;
        }
        return (rows, enabled, help);
    }

    /// <summary>Opens the options over the ESC menu (or alone when the game's menu does not open).</summary>
    public void RequestOptions()
    {
        var host = GameHost.Instance;
        var s = host?.View.Current;
        if (_options == null)
            return;
        if (_options.IsOpen)
            return;
        // the game's own pause, or no game: the options alone
        if (_menuOpen || host?.Game == null || s == null || s.World.Paused)
        {
            _optionsFromMenu = _menuOpen;
            _options.Open();
            return;
        }
        // stop the game under them: its ESC menu first
        host.Tap(GameHost.ScanEsc);
        _wantOptionsAt = _clock;
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        var host = GameHost.Instance;
        var s = host?.View.Current;
        bool esc = s?.Menus.EscMenu == true && host?.Game != null;
        if (esc && !_menuOpen)
        {
            _menuOpen = true;
            _page = Page.Home;
            _cursor = 0;
            _resumeAt = -1;
            host!.Capture(this, true);
            GD.Print("ffx: the game's ESC menu is open: the native ESC menu shown");
            if (_wantOptionsAt >= 0)
            {
                _wantOptionsAt = -1;
                _optionsFromMenu = true;
                _options?.Open();
            }
        }
        else if (!esc && _menuOpen)
        {
            _menuOpen = false;
            host?.Capture(this, false);
            if (_optionsFromMenu)
                _options?.Close();
            GD.Print("ffx: the game's ESC menu closed");
            // a booster picked on the Special features page: its key once the game runs again
            if (_keyAfterClose != 0)
            {
                host?.Tap(_keyAfterClose);
                _keyAfterClose = 0;
            }
        }
        // the options asked for, but the game's menu did not open (a load, a movie): alone
        if (_wantOptionsAt >= 0 && _clock - _wantOptionsAt > 0.7)
        {
            _wantOptionsAt = -1;
            _optionsFromMenu = false;
            _options?.Open();
        }
        // Resume asked, the menu still open after a while: tap again
        if (_menuOpen && _resumeAt >= 0 && _clock - _resumeAt > 1.5)
        {
            _resumeAt = _clock;
            host?.Tap(GameHost.ScanEsc);
        }
        bool paused = !_menuOpen && s?.World.Paused == true && host?.Game != null;
        _pause.Visible = paused && _options?.IsOpen != true;
        _pauseKeys.Visible = _pause.Visible;
        _window.Visible = _menuOpen && _options?.IsOpen != true;
        _dim.Visible = _pause.Visible || _window.Visible;
        var view = GetViewportRect().Size;
        if (_pause.Visible)
        {
            _pause.Size = _pause.GetCombinedMinimumSize();
            _pause.Position = new Vector2((view.X - _pause.Size.X) / 2, view.Y * 0.38f);
            _pauseKeys.Size = _pauseKeys.GetCombinedMinimumSize();
            _pauseKeys.Position = new Vector2((view.X - _pauseKeys.Size.X) / 2, view.Y * 0.38f + _pause.Size.Y + 8);
        }
        if (_window.Visible)
        {
            var (rows, enabled, help) = Rows(host?.Game, s);
            _cursor = System.Math.Clamp(_cursor, 0, rows.Count - 1);
            Ui.SetText(_title, _page switch
            {
                Page.Features => Loc(host?.Game, s, 0x21b, "Special features"),
                Page.Quit => Loc(host?.Game, s, 0x21f, "Quit"),
                _ => "PC menu (Esc)",
            });
            _list.SetRows(rows, _cursor, enabled);
            Ui.SetText(_help, help);
            _window.Size = _window.GetCombinedMinimumSize();
            _window.Position = (view - _window.Size) / 2;
        }
    }

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k)
            return;
        if (!k.Echo && k.PhysicalKeycode == GameHost.OptionsKey)
        {
            if (_options?.IsOpen == true)
                _options.Close();
            else
                RequestOptions();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!_menuOpen || _options?.IsOpen == true)
            return;
        GetViewport().SetInputAsHandled();
        var host = GameHost.Instance;
        int rows = _page switch { Page.Features => 4, Page.Quit => 2, _ => Home.Length };
        switch (k.PhysicalKeycode)
        {
            case Key.Up:
                _cursor = (_cursor + rows - 1) % rows;
                break;
            case Key.Down:
                _cursor = (_cursor + 1) % rows;
                break;
            case Key.Enter or Key.KpEnter or Key.C or Key.Space when !k.Echo:
                Confirm(host);
                break;
            case Key.Escape or Key.Backspace or Key.X when !k.Echo:
                if (_page == Page.Home)
                    Resume(host);
                else
                {
                    _cursor = _page == Page.Features ? 2 : 4;
                    _page = Page.Home;
                }
                break;
        }
    }

    private void Resume(GameHost? host)
    {
        if (_resumeAt >= 0)
            return;
        _resumeAt = _clock;
        host?.Tap(GameHost.ScanEsc);
    }

    private void Confirm(GameHost? host)
    {
        switch (_page)
        {
            case Page.Home:
                switch (Home[_cursor].Loc)
                {
                    case 0x217:
                        Resume(host);
                        break;
                    case 0x219:
                        _optionsFromMenu = true;
                        _options?.Open();
                        break;
                    case 0x21b:
                        _page = Page.Features;
                        _cursor = 0;
                        break;
                    case 0x21f:
                        _page = Page.Quit;
                        _cursor = 0;
                        break;
                }
                break;
            case Page.Features:
                _keyAfterClose = GameHost.ScanF1 + _cursor;
                _page = Page.Home;
                Resume(host);
                break;
            case Page.Quit:
                if (_cursor == 1)
                {
                    GD.Print("ffx: quit from the ESC menu");
                    GetTree().Root.PropagateNotification((int)NotificationWMCloseRequest);
                    GetTree().Quit();
                }
                else
                {
                    _page = Page.Home;
                    _cursor = 4;
                }
                break;
        }
    }
}
