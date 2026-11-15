using Godot;

namespace GdFfx;

/// <summary>
/// The game, for the whole Godot project (an autoload: <c>/root/GameHost</c>).
/// Boots ffx-rust from the player's game folder (<see cref="GameSettings"/>),
/// passes it the window's events and the keyboard, mouse and pads, and
/// refreshes <see cref="View"/> (the game-state API's snapshots, interpolated)
/// before the scenes' frame.
/// </summary>
public partial class GameHost : Node
{
    /// <summary>The one host (the autoload).</summary>
    public static GameHost? Instance { get; private set; }

    /// <summary>The game (null when the extension is missing).</summary>
    public FfxGame? Game { get; private set; }

    /// <summary>The snapshots, interpolated for this frame.</summary>
    public GameStateView View { get; } = new();

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
            Fail(Game.LastError);
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

    public override void _Process(double delta)
    {
        if (Game != null)
            View.Refresh(Game);
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
