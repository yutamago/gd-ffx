using Godot;

namespace GdFfx;

/// <summary>
/// The game, for the whole Godot project (an autoload: <c>/root/GameHost</c>).
/// Boots ffx-rust from the player's game folder (<see cref="GameSettings"/>),
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

    public override void _Ready()
    {
        try
        {
            Game = new FfxGame();
        }
        catch (System.InvalidOperationException e)
        {
            Fail(e.Message);
            return;
        }
        AddChild(Game.Node);
        // the player first: the game's output runs only with one attached
        if (GameSettings.AudioEnabled() && ClassDB.ClassExists("FfxAudioOut"))
        {
            Audio = (Node)ClassDB.Instantiate("FfxAudioOut");
            Audio.Name = "FfxAudioOut";
            AddChild(Audio);
        }
        SendWindow();
        GetViewport().SizeChanged += SendWindow;
        if (GameSettings.Flag("no-game"))
            return;
        var gameDir = GameSettings.GameDir();
        var documents = GameSettings.DocumentsDir();
        if (string.IsNullOrEmpty(gameDir))
        {
            Fail("No game folder: set FFX_GAME_DIR, pass --game-dir=PATH after --, "
                + "or set ffx/game/game_dir in the project settings (docs/running.md).");
            return;
        }
        GD.Print($"ffx: booting the game from {gameDir} (documents: {documents})");
        if (!Game.Start(gameDir, documents))
        {
            Fail(Game.LastError);
            return;
        }
        if (!GameSettings.Flag("no-classic"))
        {
            Classic = new ClassicLibrary(Game, gameDir, GameSettings.ClassicDir());
            if (!Classic.Open)
                GD.PrintErr($"ffx: no classic content: {Game.LastError}");
        }
        // --devsave=N: start at developer save N once the game runs an event
        _devsave = GameSettings.Int("devsave", 0);
    }

    private void Fail(string why)
    {
        Error = why;
        GD.PrintErr($"ffx: {why}");
    }

    private void SendWindow()
    {
        if (Game == null)
            return;
        var size = (Vector2I)GetViewport().GetVisibleRect().Size;
        Game.SetWindow(size, DisplayServer.ScreenGetSize(), DisplayServer.WindowGetPosition());
    }

    private int _devsave;

    public override void _Process(double delta)
    {
        if (Game == null)
            return;
        View.Refresh(Game);
        Classic?.Poll();
        // the developer save once an event runs (the opening is the first)
        if (_devsave > 0 && View.Current is { } s && s.World.Event.Length > 0 && s.Update > 30)
        {
            Game.RequestDevSave(_devsave);
            GD.Print($"ffx: developer save {_devsave} asked");
            _devsave = 0;
        }
        foreach (var r in Game.RequestResults())
            GD.Print($"ffx: request: {r}");
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
