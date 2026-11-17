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
    /// The extension's game object the editor opened the archive with (the remaster plugin,
    /// docs/remaster-editor.md: no game booted, no autoload running); null in the game.
    /// </summary>
    public static FfxGame? EditorGame { get; set; }

    /// <summary>
    /// The game object to ask for classic content (textures, material variants): the running
    /// game's, else the editor's (<see cref="EditorGame"/>).
    /// </summary>
    public static FfxGame? ClassicGame => Instance?.Game ?? EditorGame;

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
        // the collector's blocking full collections are frame hitches; background ones are not
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        // --background-window: behind every other window (checks on the GPU from WSL)
        BackgroundWindow.Apply();
    }

    /// <summary>Whether the start-up opening plays natively (<see cref="OpeningView"/>): set at <see cref="Start"/>.</summary>
    public bool NativeOpening { get; private set; }

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
        // --language=CODE: the game's language written into the sandbox's GameSetting.ini first
        if (GameSettings.Arg("language") is { Length: > 0 } language
            && GameSettings.SetGameLanguage(documents, language) is { } langError)
            GD.PrintErr($"ffx: --language={language}: {langError}");
        // the game's speed (the options' or --speed=), from the boot on
        if (UserSettings.Current.Speed != 1)
            Game!.SetSpeed(UserSettings.Current.Speed);
        // the start-up opening (openingscreen.swf) natively when booting to the title: not with a
        // developer save or --skip-opening (the checks), where it ends on its first frame
        NativeOpening = devsave <= 0 && GameSettings.Int("devsave", 0) <= 0 && !GameSettings.Flag("skip-opening");
        Game!.SetNativeOpening(NativeOpening);
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

    /// <summary>The window size last sent to the game.</summary>
    private Vector2I _windowSent = new(-1, -1);

    private void SendWindow()
    {
        if (Game == null)
            return;
        // the window's own size, as the game's WM_SIZE has it (the root viewport's visible rect
        // is the 1280 x 720 of the stretch mode, project.godot); headless has none
        var size = DisplayServer.WindowGetSize();
        if (size.X <= 0 || size.Y <= 0)
            size = (Vector2I)GetViewport().GetVisibleRect().Size;
        _windowSent = DisplayServer.WindowGetSize();
        Game.SetWindow(size, DisplayServer.ScreenGetSize(), DisplayServer.WindowGetPosition());
    }

    /// <summary>A frame this long (seconds) is already a hitch (a load): a collection added to it is not seen.</summary>
    private const double LostFrame = 0.05;
    /// <summary>The garbage (bytes allocated since the last collection) worth collecting then.</summary>
    private const long LostFrameGarbage = 4 << 20;
    private int _gen0Seen = -1;
    private long _allocatedAtCollection;

    /// <summary>The collections done in frames already lost (<see cref="CollectWhenLost"/>).</summary>
    public static int LostFrameCollections { get; private set; }

    /// <summary>
    /// Young collections right after a frame that was already a hitch, when there is garbage: a
    /// load's leftovers (the importers' Godot dictionaries and variants, finalizable) and its
    /// survivors otherwise make the first collections while playing long ones (docs/running.md,
    /// "Benchmark").
    /// </summary>
    private void CollectWhenLost(double delta)
    {
        var gen0 = System.GC.CollectionCount(0);
        if (gen0 != _gen0Seen)
        {
            _gen0Seen = gen0;
            _allocatedAtCollection = System.GC.GetTotalAllocatedBytes();
            return;
        }
        if (delta < LostFrame || System.GC.GetTotalAllocatedBytes() - _allocatedAtCollection < LostFrameGarbage)
            return;
        // twice: the load's survivors (its scene, kept) go on to gen2 now; left in gen1, the next
        // young collection while playing would copy them there (2 to 3 MiB: 5 to 9 ms)
        System.GC.Collect(1, System.GCCollectionMode.Forced, blocking: true, compacting: false);
        System.GC.Collect(1, System.GCCollectionMode.Forced, blocking: true, compacting: false);
        LostFrameCollections++;
    }

    private int _devsave;
    /// <summary>The requests (developer save, battle) not answered yet.</summary>
    private int _requests;
    private (int Field, int Group, int Formation)? _battle;

    public override void _Process(double delta)
    {
        CollectWhenLost(delta);
        Taps(delta);
        // the worker threads' texture asks (answered "none" without a game)
        TextureFetcher.Poll(Game);
        if (Game == null)
            return;
        // a window made wider than 16:9 keeps the viewport's size (the bars grow): its own size is watched
        if (DisplayServer.WindowGetSize() != _windowSent)
            SendWindow();
        var t0 = Perf.Start();
        View.Refresh(Game);
        Perf.End(Perf.Section.Snapshots, t0);
        var t1 = Perf.Start();
        Classic?.Poll();
        // the Flash screens' movies and calls (the tips, the save list, the autosave prompt)
        FlashScreens.Poll(Game, _clock);
        // the developer save once an event runs (the opening is the first)
        if (_devsave > 0 && View.Current is { } s && s.World.Event.Length > 0 && s.Update > 30)
        {
            if (Game.RequestDevSave(_devsave))
                _requests++;
            GD.Print($"ffx: developer save {_devsave} asked");
            _devsave = 0;
        }
        if (_battle is { } b && _devsave == 0 && View.Current is { World.PlayerControl: true, World.Battle: false } f2
            && f2.Update > 60)
        {
            // --party=HEX (8 bytes, e.g. 01000405ff020306): the battle party first, as ffx-rust's
            // cloth-probe --party does (a back-line character into the battle)
            if (GameSettings.Arg("party") is { Length: 16 } hex)
            {
                try
                {
                    var party = System.Convert.FromHexString(hex);
                    GD.Print(Game.SetBattleParty(party) ? $"ffx: battle party {hex}" : "ffx: --party not written");
                }
                catch (System.FormatException)
                {
                    GD.PrintErr("ffx: --party=HEX (8 bytes)");
                }
            }
            if (Game.RequestBattle(b.Field, b.Group, b.Formation))
                _requests++;
            GD.Print($"ffx: battle {b.Field},{b.Group},{b.Formation} asked");
            _battle = null;
        }
        // asked only while a request is open (a string array each call: finalizable, see ClassicLibrary.Poll)
        if (_requests > 0)
            foreach (var r in Game.RequestResults())
            {
                GD.Print($"ffx: request: {r}");
                _requests--;
            }
        Perf.End(Perf.Section.Host, t1);
    }

    public override void _ExitTree()
    {
        // the background loads end before the engine does
        TextureFetcher.Close();
        Remake.ContentResolver.WaitPending(10000);
        EffectDrawView.WaitPending(10000);
        MapLayers.WaitPending(10000);
        // Godot objects C# let go of (materials, textures, meshes) are released by their
        // wrappers' finalizers: run them while the servers are still up. With little garbage a
        // frame there may have been no full collection since they were dropped, and finalized
        // at shutdown they leak their RIDs ("RenderingServer is null" at exit).
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();
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

    // ---- input -----------------------------------------------------------------------------

    /// <summary>gd-ffx's own keys (never the game's): the FPS overlay, the options, fullscreen, the debug overlay; and <see cref="RemasterKey"/>.</summary>
    public const Key FpsKey = Key.F9, OptionsKey = Key.F10, FullscreenKey = Key.F11, DebugKey = Key.F12;

    /// <summary>The remaster's compare key: classic and remastered look in turn (<see cref="Remaster.RenderPaths.Toggle"/>; StateView3D takes it).</summary>
    public const Key RemasterKey = Key.F7;

    /// <summary>
    /// A screen of gd-ffx's own that takes the keyboard and mouse while it is open (the ESC
    /// menu, the options): the game gets none of it, and the keys it held are let go.
    /// </summary>
    public bool Captured => _captors.Count > 0;

    private readonly System.Collections.Generic.HashSet<object> _captors = new();

    /// <summary>Takes (or gives back) the input for <paramref name="who"/> (<see cref="Captured"/>).</summary>
    public void Capture(object who, bool on)
    {
        bool was = Captured;
        if (on)
            _captors.Add(who);
        else
            _captors.Remove(who);
        if (!was && Captured)
            ReleaseHeld();
    }

    /// <summary>The scan codes the game holds down from Godot's keys (let go when a screen captures).</summary>
    private readonly System.Collections.Generic.HashSet<int> _held = new();

    private void ReleaseHeld()
    {
        if (Game == null)
            return;
        foreach (var scan in _held)
            Game.ScanInput(scan, false);
        _held.Clear();
    }

    /// <summary>A key tap for the game: pressed, held, let go (<see cref="Tap"/>).</summary>
    private readonly System.Collections.Generic.List<(int Scan, double Release)> _taps = new();
    private double _clock;

    /// <summary>The DirectInput scan codes of the keys gd-ffx taps for the game.</summary>
    public const int ScanEsc = 0x01, ScanF1 = 0x3b, ScanF2 = 0x3c, ScanF3 = 0x3d, ScanF4 = 0x3e;

    /// <summary>
    /// Taps key <paramref name="scan"/> for the game (pressed now, let go after
    /// <paramref name="hold"/> seconds: the game reads its keyboard once an update, 30 a
    /// second, so a press and release in one Godot frame would be missed). Not while the key is
    /// being tapped already.
    /// </summary>
    public void Tap(int scan, double hold = 0.1)
    {
        if (Game == null || _taps.Exists(t => t.Scan == scan))
            return;
        Game.ScanInput(scan, true);
        _taps.Add((scan, _clock + hold));
    }

    private void Taps(double delta)
    {
        _clock += delta;
        for (int i = _taps.Count - 1; i >= 0; i--)
            if (_clock >= _taps[i].Release)
            {
                Game?.ScanInput(_taps[i].Scan, false);
                _taps.RemoveAt(i);
            }
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } fk && fk.PhysicalKeycode == FullscreenKey)
        {
            var u = UserSettings.Current;
            u.WindowMode = u.WindowMode == "windowed" ? "fullscreen" : "windowed";
            u.Apply();
            u.Save();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (Game == null || !InputToGame)
            return;
        switch (e)
        {
            // gd-ffx's own keys stay Godot's (the views take them); F1 to F4 are the game's boosters
            case InputEventKey k when k.PhysicalKeycode is FpsKey or OptionsKey or FullscreenKey or DebugKey or RemasterKey:
                break;
            // a screen of gd-ffx's own has the input: the game gets none
            case InputEventKey or InputEventMouse when Captured:
                break;
            case InputEventKey k when Game.KeyInput(k):
                if (!k.Echo && ScanCodes.Of(k.PhysicalKeycode, k.Location) is int scan)
                {
                    if (k.Pressed)
                        _held.Add(scan);
                    else
                        _held.Remove(scan);
                }
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouse:
                Game.MouseInput(e);
                break;
        }
    }
}

/// <summary>Godot's physical keys as DirectInput scan codes, as the extension maps them (<c>rust/ffx-gd/src/keys.rs</c>), for the keys gd-ffx lets go of.</summary>
public static class ScanCodes
{
    /// <summary>The scan code of <paramref name="key"/>, or null for a key not tracked (then it is not let go early).</summary>
    public static int? Of(Key key, KeyLocation location)
    {
        bool right = location == KeyLocation.Right;
        if (key >= Key.A && key <= Key.Z)
            return new byte[] { 0x1e, 0x30, 0x2e, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31,
                0x18, 0x19, 0x10, 0x13, 0x1f, 0x14, 0x16, 0x2f, 0x11, 0x2d, 0x15, 0x2c }[key - Key.A];
        if (key >= Key.F1 && key <= Key.F10)
            return 0x3b + (int)(key - Key.F1);
        return key switch
        {
            Key.Escape => 0x01,
            Key.Backspace => 0x0e,
            Key.Tab => 0x0f,
            Key.Enter => 0x1c,
            Key.Space => 0x39,
            Key.Ctrl => right ? 0x9d : 0x1d,
            Key.Shift => right ? 0x36 : 0x2a,
            Key.Alt => right ? 0xb8 : 0x38,
            Key.Up => 0xc8,
            Key.Down => 0xd0,
            Key.Left => 0xcb,
            Key.Right => 0xcd,
            Key.Pageup => 0xc9,
            Key.Pagedown => 0xd1,
            Key.Home => 0xc7,
            Key.End => 0xcf,
            Key.KpEnter => 0x9c,
            _ => null,
        };
    }
}
