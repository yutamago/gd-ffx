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
/// it and play halts, the game skipping its field update while its <c>escmenu.swf</c> page is up). The game's page is a
/// Flash screen gd-ffx does not draw, so while it is open gd-ffx shows its home page's entries
/// (the game's own texts, localisation lines 0x217..0x21f) and keeps the keyboard and mouse
/// (<see cref="GameHost.Capture"/>), never moving the game's invisible cursor: Resume (Esc taps
/// the game's Esc, which closes its menu from any page), System setting (gd-ffx's own settings,
/// <see cref="OptionsView"/>: they replace the game's video page; the volume is there too),
/// Special features (the four boosters with their state: one picked closes the menu and taps
/// its key, F1..F4), Change parameters (the game's page 13, natively: below), Quit (asks, then
/// ends gd-ffx). Up/Down choose, Enter (C, Space) confirms, Esc (Backspace, X) goes back. Each
/// entry's help line is the game's (localisation lines 0x218..0x220, its
/// <c>EscMenuHelpTextRefresh</c> table).</para>
///
/// <para><b>Change parameters</b> (<c>FFEscMenu_initializeParameterChangePage</c>): three rows,
/// "Get all items", "Unlock all skills", "MAX gil" (lines 0x2b6, 0x2bd, 0x2c4) each with its
/// "Get" button (0x2c8) and help (0x2b7, 0x2be, 0x2c5). The entry is enabled as the game's page
/// enables it (<c>setEnableChangePara</c>: no movie fading, the scenario flag set, not an
/// Eternal Calm scene; <see cref="FfxGame.EscState"/>). A button is the page's own callback,
/// <c>EscMenuButtonClick([13, row])</c>, which the extension calls in the movie's place
/// (<see cref="FfxGame.EscButton"/>): the game then sets up its confirmation, drawn here with its
/// texts (0x2c9, Yes 0x2ca, No 0x2cb; the cursor starts on No, as nothing can undo it), whose
/// answer goes back as <c>EscMenuPopUpConfirmed</c> (<see cref="FfxGame.EscPopUpAnswer"/>): yes
/// fills the items to 99 (<c>MsDebugFullItem</c>), learns every ability
/// (<c>MsDebugFullCommand</c>, with the game's two achievements) or sets the gil to 999,999,999
/// (<c>MsDebugFullGill</c>). Where the game refuses, its "Function not Available now!".</para>
///
/// <para>F10 opens the options from play too: gd-ffx taps the game's Esc first, so play halts
/// under them, and the options open over the ESC menu (at once when the game's
/// menu does not open within 0.7 s, e.g. during a load).</para>
/// </summary>
public partial class PauseView : Control
{
    private enum Page { Home, Features, Parameters, Confirm, Quit }

    private ColorRect _dim = null!;
    private PanelContainer _window = null!;
    private Label _title = null!, _help = null!, _pause = null!, _pauseKeys = null!;
    private MenuList _list = null!;
    private Page _page = Page.Home;
    private int _cursor;
    private bool _menuOpen;
    private double _resumeAt = -1, _wantOptionsAt = -1, _clock;
    private int _keyAfterClose;
    // the parameter page's call waiting for the game (the extension's call count to reach), the row
    private long _waitDone = -1;
    private double _waitAt;
    private int _row;
    private string _note = "";
    private EscMenuState _esc;
    private bool _optionsFromMenu;
    private OptionsView? _options;

    /// <summary>The rows shown now (the highlighted one marked), the page and the state (for tests).</summary>
    public List<string> Shown => _list.Shown;
    public string PageName => _menuOpen ? _page.ToString() : _pause.Visible ? "Pause" : "";

    /// <summary>Whether the native ESC menu is up (the game's is open).</summary>
    public bool MenuOpen => _menuOpen;

    /// <summary>The game's ESC menu as the extension saw it last (for tests).</summary>
    public EscMenuState Esc => _esc;

    /// <summary>The page's last note (the game's refusal, a call not answered; for tests).</summary>
    public string Note => _note;

    /// <summary>The title shown now (for tests).</summary>
    public string Title => _title.Text;

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

    /// <summary>A pop-up's text with its line breaks (<see cref="FfxGame.FlashLines"/>).</summary>
    private string Lines(FfxGame? game, Snapshot? s, int id, string english)
    {
        if (game == null || s == null)
            return english;
        var key = (id | 0x10000, s.World.Language);
        if (!_loc.TryGetValue(key, out var t))
        {
            t = game.FlashLines(id, MessageView.LanguageCode(s.World.Language));
            _loc[key] = t.Trim().Length > 0 ? t : english;
        }
        return _loc[key];
    }

    /// <summary>
    /// The home page: the game's entries and help lines (its texts; the help table
    /// <c>0x00c33dc0</c> gives page 1's rows 0x218..0x220), and gd-ffx's own help where it does
    /// something else than the game's page (System setting: gd-ffx's settings).
    /// </summary>
    private static readonly (int Loc, string English, int HelpLoc, string Help)[] Home =
    [
        (0x217, "Resume", 0x218, "Resume game"),
        (0x219, "System setting", 0, "gd-ffx's settings: graphics, sound volume, game speed, the FPS overlay (also F10)."),
        (0x21b, "Special features", 0, "The PC boosters: Turbo, Supercharge, enemy encounters, auto battle (also F1 to F4 in play)."),
        (0x21d, "Change parameters", 0x21e, "Change game parameters"),
        (0x21f, "Quit", 0, "Quit gd-ffx."),
    ];

    /// <summary>The Change parameters page's rows: label and help (page 13 of the help table).</summary>
    private static readonly (int Loc, string English, int HelpLoc, string Help)[] Parameters =
    [
        (0x2b6, "Get all items", 0x2b7, "Get 99 of all common items, not including key items."),
        (0x2bd, "Unlock all skills", 0x2be, "Unlock all the character's skills."),
        (0x2c4, "MAX gil", 0x2c5, "Get max gil."),
    ];

    /// <summary><c>FFEscMenu_onParameterButtonClicked</c>'s refusal (<c>0x00b4c52c</c>, English in every language), read from the game.</summary>
    private string NotAvailable(FfxGame? game)
    {
        if (_notAvailable == null && game != null)
        {
            var b = game.Peek(0x00b4c52c, 40);
            int n = System.Array.IndexOf(b, (byte)0);
            _notAvailable = n > 0 ? System.Text.Encoding.ASCII.GetString(b, 0, n) : "";
        }
        return _notAvailable is { Length: > 0 } t ? t : "Not available now.";
    }

    private string? _notAvailable;

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
            case Page.Parameters:
            {
                var get = Loc(game, s, 0x2c8, "Get");
                foreach (var (loc, english, _, _) in Parameters)
                {
                    rows.Add($"{Loc(game, s, loc, english)}\t{get}");
                    enabled.Add(_waitDone < 0);
                }
                var row = Parameters[System.Math.Clamp(_cursor, 0, Parameters.Length - 1)];
                help = _note.Length > 0 ? _note : _waitDone >= 0 ? "…" : Loc(game, s, row.HelpLoc, row.Help);
                break;
            }
            case Page.Confirm:
                rows.Add(Loc(game, s, 0x2ca, "Yes"));
                rows.Add(Loc(game, s, 0x2cb, "No"));
                enabled.AddRange([_waitDone < 0, _waitDone < 0]);
                help = _note;
                break;
            default:
                foreach (var (loc, english, _, _) in Home)
                {
                    rows.Add(Loc(game, s, loc, english));
                    enabled.Add(loc != 0x21d || _esc.Parameters);
                }
                var h = Home[System.Math.Clamp(_cursor, 0, Home.Length - 1)];
                help = h.HelpLoc != 0 ? Loc(game, s, h.HelpLoc, h.Help) : h.Help;
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
        if (esc && host?.Game is { } g)
            _esc = g.EscState();
        if (esc && !_menuOpen)
        {
            _menuOpen = true;
            _page = Page.Home;
            _cursor = 0;
            _resumeAt = -1;
            _waitDone = -1;
            _note = "";
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
        if (_menuOpen && _waitDone >= 0)
            Answered(host?.Game);
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
                Page.Parameters => Loc(host?.Game, s, 0x21d, "Change parameters"),
                // the game's pop-up: its message over its two buttons
                Page.Confirm => Lines(host?.Game, s, 0x2c9, "This operation is irreversible.\nDo you want to use this function?"),
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
        int rows = _page switch { Page.Features => 4, Page.Parameters => Parameters.Length, Page.Confirm or Page.Quit => 2, _ => Home.Length };
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
                else if (_page == Page.Confirm)
                    Answer(host?.Game, false);
                else if (_waitDone < 0)
                {
                    _cursor = _page switch { Page.Features => 2, Page.Parameters => 3, _ => 4 };
                    _page = Page.Home;
                    _note = "";
                }
                break;
        }
    }

    /// <summary><c>EscMenuButtonClick</c>'s page id of Change parameters (the game's help table and <c>FFEscMenu_onParameterButton_0068f610</c>).</summary>
    public const int EscParameterPage = 13;

    /// <summary>The confirmation's answer, the game's <c>EscMenuPopUpConfirmed</c>.</summary>
    private void Answer(FfxGame? game, bool yes)
    {
        if (game == null || !game.EscPopUpAnswer(yes))
            return;
        GD.Print($"ffx: ESC menu: Change parameters row {_row}: {(yes ? "yes" : "no")} (EscMenuPopUpConfirmed {(yes ? 1 : 0)})");
        _waitDone = _esc.Done + 1;
        _waitAt = _clock;
        _confirmed = yes;
    }

    private bool _confirmed;

    /// <summary>A call's answer: the game ran it (the extension's count reached), or 2 s passed.</summary>
    private void Answered(FfxGame? game)
    {
        if (_esc.Done >= _waitDone)
        {
            _waitDone = -1;
            if (_esc.Failed)
                _note = "The game's menu did not take the call.";
            else if (_page == Page.Parameters)
            {
                if (_esc.PopUp && _esc.Row == _row)
                {
                    _page = Page.Confirm;
                    _cursor = 1;
                }
                else
                    _note = NotAvailable(game);
            }
            else if (_page == Page.Confirm)
            {
                _page = Page.Parameters;
                _cursor = _row;
                if (_confirmed)
                    GD.Print($"ffx: ESC menu: Change parameters row {_row} done");
            }
        }
        else if (_clock - _waitAt > 2)
        {
            _waitDone = -1;
            _note = "The game did not answer.";
            if (_page == Page.Confirm)
            {
                _page = Page.Parameters;
                _cursor = _row;
            }
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
                    case 0x21d when _esc.Parameters:
                        _page = Page.Parameters;
                        _cursor = 0;
                        _note = "";
                        break;
                    case 0x21f:
                        _page = Page.Quit;
                        _cursor = 0;
                        break;
                }
                break;
            case Page.Parameters when _waitDone < 0:
                // the page's button: the game's callback; its answer (a confirmation or a refusal) shows next
                _row = _cursor;
                _note = "";
                if (host?.Game is { } g && g.EscButton(EscParameterPage, _row))
                {
                    _waitDone = _esc.Done + 1;
                    _waitAt = _clock;
                    GD.Print($"ffx: ESC menu: Change parameters row {_row} (EscMenuButtonClick [13, {_row}])");
                }
                break;
            case Page.Confirm when _waitDone < 0:
                Answer(host?.Game, _cursor == 0);
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
