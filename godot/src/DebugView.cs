using System.Linq;
using System.Text;
using Godot;

namespace GdFfx;

/// <summary>
/// G0's debug view: boots the game (headless inside Godot: nothing of the
/// original's picture), shows its state live, and passes the keyboard,
/// mouse and pads to it.
///
/// With <c>--selftest</c> on the command line (after <c>--</c>) it checks
/// that the game runs and takes input, and quits with 0 (pass) or 1: it
/// waits for the opening (map 0x15d), presses Start (B, through Godot's
/// input like a real key) and expects the title (map 0x17).
/// </summary>
public partial class DebugView : Control
{
    /// <summary>The opening (the campfire scene with the credits).</summary>
    public const int MapOpening = 0x15d;

    /// <summary>The title.</summary>
    public const int MapTitle = 0x17;

    private FfxGame? _game;
    private Label _label = null!;
    private Label _help = null!;
    private string _error = "";
    private SelfTest? _test;
    private double _lastLog;
    private long _rateUpdates;
    private double _rateSeconds;
    private double _rate;

    public override void _Ready()
    {
        var bg = new ColorRect { Color = new Color(0.07f, 0.08f, 0.11f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        var box = new VBoxContainer();
        box.SetAnchorsPreset(LayoutPreset.FullRect);
        box.OffsetLeft = 24;
        box.OffsetTop = 16;
        AddChild(box);
        var title = new Label { Text = "gd-ffx: ffx-rust inside Godot (G0 debug view)" };
        title.AddThemeFontSizeOverride("font_size", 22);
        box.AddChild(title);
        _label = new Label();
        _label.AddThemeFontOverride("font", new SystemFont { FontNames = ["monospace", "Consolas", "DejaVu Sans Mono"] });
        _label.AddThemeFontSizeOverride("font_size", 16);
        box.AddChild(_label);
        _help = new Label
        {
            Text = "Keys go to the game as on the original's keyboard: B Start, arrows, C/Enter confirm, "
                + "X/Backspace cancel, V menu, Esc PC menu. Pads are XInput pads.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _help.AddThemeColorOverride("font_color", new Color(0.6f, 0.65f, 0.7f));
        box.AddChild(_help);

        if (GameSettings.Flag("selftest"))
            _test = new SelfTest(GameSettings.Number("selftest-timeout", 300));

        try
        {
            _game = new FfxGame();
        }
        catch (System.InvalidOperationException e)
        {
            Fail(e.Message);
            return;
        }
        AddChild(_game.Node);
        SendWindow();
        GetViewport().SizeChanged += SendWindow;
        var gameDir = GameSettings.GameDir();
        var documents = GameSettings.DocumentsDir();
        if (string.IsNullOrEmpty(gameDir))
        {
            Fail("No game folder: set FFX_GAME_DIR, pass --game-dir=PATH after --, "
                + "or set ffx/game/game_dir in the project settings (docs/running.md).");
            return;
        }
        GD.Print($"ffx: booting the game from {gameDir} (documents: {documents})");
        if (!_game.Start(gameDir, documents))
            Fail(_game.LastError);
    }

    private void Fail(string why)
    {
        _error = why;
        GD.PrintErr($"ffx: {why}");
        if (_test != null)
            GetTree().Quit(1);
    }

    private void SendWindow()
    {
        if (_game == null)
            return;
        var size = (Vector2I)GetViewportRect().Size;
        var screen = DisplayServer.ScreenGetSize();
        var origin = DisplayServer.WindowGetPosition();
        _game.SetWindow(size, screen, origin);
    }

    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            case NotificationApplicationFocusIn:
                _game?.SetFocus(true);
                break;
            case NotificationApplicationFocusOut:
                // a headless run has no window to lose
                if (DisplayServer.GetName() != "headless")
                    _game?.SetFocus(false);
                break;
            case NotificationWMMouseExit:
                _game?.MouseExited();
                break;
            case NotificationWMCloseRequest:
            case NotificationPredelete:
                _game?.Stop();
                break;
        }
    }

    public override void _Input(InputEvent e)
    {
        if (_game == null)
            return;
        switch (e)
        {
            case InputEventKey k when _game.KeyInput(k):
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouse:
                _game.MouseInput(e);
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (_game == null)
        {
            _label.Text = _error;
            return;
        }
        var s = _game.State;
        if (s.Seconds - _rateSeconds >= 2.0)
        {
            _rate = (s.Updates - _rateUpdates) / (s.Seconds - _rateSeconds);
            _rateUpdates = s.Updates;
            _rateSeconds = s.Seconds;
        }
        _label.Text = Describe(s, _rate, _error);
        if (_test != null)
        {
            if (s.Seconds - _lastLog >= 5.0)
            {
                _lastLog = s.Seconds;
                GD.Print($"ffx: {OneLine(s, _rate)}");
            }
            var verdict = _test.Step(_game, s, delta);
            if (verdict is { } pass)
            {
                GD.Print(pass ? $"ffx selftest: PASS ({_test.Note})" : $"ffx selftest: FAIL ({_test.Note})");
                GD.Print($"ffx: {OneLine(s, _rate)}");
                _game.Stop();
                GetTree().Quit(pass ? 0 : 1);
                _test = null;
            }
        }
    }

    private static string Hex(int v, int digits) => "0x" + v.ToString($"x{digits}");

    public static string OneLine(GameState s, double rate) =>
        $"{s.Seconds,6:F1} s  updates {s.Updates} ({rate:F1}/s)  game time {s.GameTime:F2} s  "
        + $"map {Hex(s.Map, 4)} (from {Hex(s.PreviousMap, 4)}, entrance {s.Entrance})  "
        + $"story {s.Story}  event frame {s.EventFrame}";

    private static string Describe(GameState s, double rate, string error)
    {
        var b = new StringBuilder();
        if (error.Length > 0)
            b.AppendLine($"error: {error}\n");
        b.AppendLine($"game thread    {(s.Running ? "running" : "not running")}{(s.Booted ? "" : " (booting)")}");
        if (!string.IsNullOrEmpty(s.Outcome))
            b.AppendLine($"ended          {s.Outcome}");
        if (!string.IsNullOrEmpty(s.Stopped))
            b.AppendLine($"stopped        {s.Stopped}");
        b.AppendLine($"wall time      {s.Seconds:F1} s");
        b.AppendLine($"updates        {s.Updates}   ({rate:F1} a second; the game's own pace is 30)");
        b.AppendLine($"frames         {s.Flips}   (presented to the null device)");
        b.AppendLine($"game time      {s.GameTime:F3} s");
        b.AppendLine($"map            {Hex(s.Map, 4)}   {MapName(s.Map)}");
        b.AppendLine($"previous map   {Hex(s.PreviousMap, 4)}   {MapName(s.PreviousMap)}");
        b.AppendLine($"entrance       {s.Entrance}");
        b.AppendLine($"story          {s.Story}");
        b.AppendLine($"event frame    {s.EventFrame}");
        b.AppendLine($"focus          {(s.Focused ? "yes" : "no (the game reads no keys)")}");
        b.AppendLine($"keys held      {string.Join(" ", s.Keys.Select(k => k.ToString("x2")))}");
        foreach (var m in s.MessageBoxes)
            b.AppendLine($"message        {m}");
        return b.ToString();
    }

    private static string MapName(int map) => map switch
    {
        0 => "",
        0x15c => "(the start-up logo)",
        MapOpening => "(the opening)",
        MapTitle => "(the title)",
        _ => "",
    };
}

/// <summary>
/// The self-test's steps: the game boots and updates, the opening comes, a
/// Start press through Godot's input reaches the game and it goes to the title.
/// </summary>
public sealed class SelfTest(double timeout)
{
    private enum Phase { Boot, Opening, Press, Release, Title }

    private Phase _phase = Phase.Boot;
    private double _phaseTime;
    private double _total;
    private int _presses;

    public string Note { get; private set; } = "";

    /// <summary>One frame: null while running, else whether the test passed.</summary>
    public bool? Step(FfxGame game, GameState s, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > timeout)
            return Done(false, $"timed out in phase {_phase}");
        switch (_phase)
        {
            case Phase.Boot when s.Updates >= 30:
                Next(Phase.Opening);
                break;
            // Start skips the opening once it is a few seconds in
            case Phase.Opening when s.Map == DebugView.MapOpening && s.EventFrame > 300:
                Next(Phase.Press);
                break;
            case Phase.Press:
                Key(true);
                _presses++;
                Next(Phase.Release);
                break;
            // hold for a few updates, as a player's tap
            case Phase.Release when _phaseTime > 0.2:
                Key(false);
                Next(Phase.Title);
                break;
            case Phase.Title when s.Map == DebugView.MapTitle:
                return Done(true, $"the opening went to the title after {_presses} Start press(es); "
                    + $"{s.Updates} updates in {s.Seconds:F1} s");
            case Phase.Title when _phaseTime > 5 && _presses < 5:
                Next(Phase.Press);
                break;
        }
        return null;
    }

    private static void Key(bool down)
    {
        // a real key event through Godot's input, as a player's
        var e = new InputEventKey { PhysicalKeycode = Godot.Key.B, Keycode = Godot.Key.B, Pressed = down };
        Input.ParseInputEvent(e);
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx selftest: {_phase} -> {p}");
        _phase = p;
        _phaseTime = 0;
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }
}
