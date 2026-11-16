using Godot;

namespace GdFfx;

/// <summary>
/// The game, for the whole Godot project (an autoload: <c>/root/GameHost</c>).
/// Boots ffx-rust from the player's game folder (<see cref="GameSettings"/>) when <see cref="Start"/> is called (by the launcher, docs/launcher.md),
/// passes it the window's events and the keyboard, mouse and pads, plays its
/// sound (<c>FfxAudioOut</c>, unless muted), and
/// refreshes <see cref="View"/> (the game-state API's snapshots, interpolated)
/// before the scenes' frame.
/// </summary>
public partial class GameHost : Node
{
    /// <summary>The one host (the autoload).</summary>
    public static GameHost? Instance { get; private set; }

    /// <summary>The game (null when the extension is missing).</summary>
    public FfxGame? Game { get; private set; }

    /// <summary>
    /// The extension's <c>FfxAudioOut</c>, playing the game's own mix (null when muted:
    /// <see cref="GameSettings.AudioEnabled"/>).
    /// </summary>
    public Node? Audio { get; private set; }

    /// <summary>The snapshots, interpolated for this frame.</summary>
    public GameStateView View { get; } = new();

    /// <summary>The classic content (null before the boot).</summary>
    public ClassicLibrary? Classic { get; private set; }

    /// <summary>Why the game is not running, if it is not.</summary>
    public string Error { get; private set; } = "";

    /// <summary>Whether input goes to the game (scenes may take it, e.g. a free camera).</summary>
    public bool InputToGame { get; set; } = true;

    public override void _EnterTree()
    {
        Instance = this;
        // before every scene's _Process: the view is this frame's
        ProcessPriority = -100;
    }

    /// <summary>Whether <see cref="Start"/> succeeded (the game booted, or <c>--no-game</c>).</summary>
    public bool Started { get; private set; }

    private bool _tried;

    public override void _Ready()
    {
        // the launcher (the project's main scene) calls Start; any other scene run directly
        // (scenes/main.tscn from the editor) gets the game once it is ready
        CallDeferred(MethodName.StartUnlessLauncher);
    }

    private void StartUnlessLauncher()
    {
        if (!_tried && GetTree().CurrentScene is not Launcher.Launcher)
            Start();
    }

    /// <summary>
    /// Mounts the mod packs (<see cref="Remake.ModLoader.MountAll"/>) and boots the game: from
    /// <paramref name="gameDir"/> (the launcher's folder) or <see cref="GameSettings.GameDir"/>
    /// (<c>user://</c> and <c>res://</c> made absolute), at developer save
    /// <paramref name="devsave"/> or <c>--devsave=N</c>. Call it before <c>scenes/main.tscn</c>
    /// loads, so packs can replace its files. With <c>--no-game</c> the game is not booted.
    /// False when the game could not start: <see cref="Error"/> says why, <see cref="Game"/> is
    /// null again and Start may be called again (another folder). Once it succeeded, later
    /// calls do nothing.
    /// </summary>
    public bool Start(string? gameDir = null, int devsave = 0)
    {
        if (Started)
            return true;
        _tried = true;
        Error = "";
        Remake.ModLoader.MountAll();
        if (GameSettings.Flag("no-game"))
        {
            GD.Print("ffx: started without the game (--no-game)");
            Started = true;
            if (ClassDB.ClassExists("FfxGame"))
                Attach();
            return true;
        }
        if (string.IsNullOrEmpty(gameDir))
            gameDir = GameSettings.GameDir();
        gameDir = GameSettings.Globalize(gameDir);
        var documents = GameSettings.DocumentsDir();
        if (string.IsNullOrEmpty(gameDir))
            return Fail("No game folder: choose one in the launcher, set FFX_GAME_DIR, pass --game-dir=PATH after --, "
                + "or set ffx/game/game_dir in the project settings (docs/running.md).");
        try
        {
            Attach();
        }
        catch (System.InvalidOperationException e)
        {
            return Fail(e.Message);
        }
        GD.Print($"ffx: booting the game from {gameDir} (documents: {documents})");
        if (!Game!.Start(gameDir, documents))
            return Fail(Game.LastError);
        Started = true;
        if (!GameSettings.Flag("no-classic"))
        {
            Classic = new ClassicLibrary(Game, gameDir, GameSettings.ClassicDir());
            if (!Classic.Open)
                GD.PrintErr($"ffx: no classic content: {Game.LastError}");
        }
        // --devsave=N: start at developer save N once the game runs an event
        _devsave = devsave > 0 ? devsave : GameSettings.Int("devsave", 0);
        // --battle=FIELD,GROUP,FORMATION: a battle once the field has the control
        var battle = GameSettings.Arg("battle");
        if (!string.IsNullOrEmpty(battle))
        {
            var p = battle.Split(',');
            if (p.Length == 3 && int.TryParse(p[0], out var f) && int.TryParse(p[1], out var g)
                && int.TryParse(p[2], out var fo))
                _battle = (f, g, fo);
            else
                GD.PrintErr("ffx: --battle=FIELD,GROUP,FORMATION");
        }
        return true;
    }

    /// <summary>The game's node and its sound player (the player first: the game's output runs only with one attached).</summary>
    private void Attach()
    {
        Game = new FfxGame();
        AddChild(Game.Node);
        if (GameSettings.AudioEnabled() && ClassDB.ClassExists("FfxAudioOut"))
        {
            Audio = (Node)ClassDB.Instantiate("FfxAudioOut");
            Audio.Name = "FfxAudioOut";
            AddChild(Audio);
        }
        SendWindow();
        GetViewport().SizeChanged += SendWindow;
    }

    /// <summary>A failed start: the reason kept, the game's node and player let go (a later Start makes new ones).</summary>
    private bool Fail(string why)
    {
        Error = why;
        GD.PrintErr($"ffx: {why}");
        if (Game != null)
        {
            GetViewport().SizeChanged -= SendWindow;
            Game.Node.QueueFree();
            Game = null;
        }
        Audio?.QueueFree();
        Audio = null;
        return false;
    }

    private void SendWindow()
    {
        if (Game == null)
            return;
        var size = (Vector2I)GetViewport().GetVisibleRect().Size;
        Game.SetWindow(size, DisplayServer.ScreenGetSize(), DisplayServer.WindowGetPosition());
    }

    private int _devsave;
    private (int Field, int Group, int Formation)? _battle;

    public override void _Process(double delta)
    {
        if (Game == null)
            return;
        var t0 = Perf.Start();
        View.Refresh(Game);
        Perf.End(Perf.Section.Snapshots, t0);
        var t1 = Perf.Start();
        Classic?.Poll();
        // the developer save once an event runs (the opening is the first)
        if (_devsave > 0 && View.Current is { } s && s.World.Event.Length > 0 && s.Update > 30)
        {
            Game.RequestDevSave(_devsave);
            GD.Print($"ffx: developer save {_devsave} asked");
            _devsave = 0;
        }
        if (_battle is { } b && _devsave == 0 && View.Current is { World.PlayerControl: true, World.Battle: false } f2
            && f2.Update > 60)
        {
            Game.RequestBattle(b.Field, b.Group, b.Formation);
            GD.Print($"ffx: battle {b.Field},{b.Group},{b.Formation} asked");
            _battle = null;
        }
        foreach (var r in Game.RequestResults())
            GD.Print($"ffx: request: {r}");
        Perf.End(Perf.Section.Host, t1);
    }

    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            case NotificationApplicationFocusIn:
                Game?.SetFocus(true);
                break;
            case NotificationApplicationFocusOut:
                // a headless run has no window to lose
                if (DisplayServer.GetName() != "headless")
                    Game?.SetFocus(false);
                break;
            case NotificationWMMouseExit:
                Game?.MouseExited();
                break;
            case NotificationWMCloseRequest:
            case NotificationPredelete:
                Game?.Stop();
                break;
        }
    }

    public override void _Input(InputEvent e)
    {
        if (Game == null || !InputToGame)
            return;
        switch (e)
        {
            case InputEventKey k when k.PhysicalKeycode == Key.F1:
                // the debug overlay's key stays Godot's
                break;
            case InputEventKey k when Game.KeyInput(k):
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouse:
                Game.MouseInput(e);
                break;
        }
    }
}
