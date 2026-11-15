using System.Linq;
using System.Text;
using GdFfx.Api;
using Godot;

namespace GdFfx;

/// <summary>
/// The debug overlay (F1 shows or hides it): the game's live state from
/// <see cref="GameHost"/> and the game-state API's newest snapshot.
///
/// With <c>--selftest</c> on the command line (after <c>--</c>) it runs
/// <see cref="SelfTest"/> and quits with 0 (pass) or 1.
/// </summary>
public partial class DebugView : Control
{
    /// <summary>The opening (the campfire scene with the credits).</summary>
    public const int MapOpening = 0x15d;

    /// <summary>The title.</summary>
    public const int MapTitle = 0x17;

    private Label _label = null!;
    private ITest? _test;
    private double _lastLog;
    private long _rateUpdates;
    private double _rateSeconds;
    private double _rate;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.04f, 0.06f, 0.78f),
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        panel.Position = new Vector2(12, 12);
        AddChild(panel);
        var box = new VBoxContainer();
        panel.AddChild(box);
        var title = new Label { Text = "gd-ffx: ffx-rust inside Godot (F1: hide)" };
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        _label = new Label();
        _label.AddThemeFontOverride("font", new SystemFont { FontNames = ["monospace", "Consolas", "DejaVu Sans Mono"] });
        _label.AddThemeFontSizeOverride("font_size", 13);
        box.AddChild(_label);
        var help = new Label
        {
            Text = "Keys as on the original: B Start, arrows, C/Enter confirm, X/Backspace cancel, V menu, Esc PC menu.",
        };
        help.AddThemeColorOverride("font_color", new Color(0.6f, 0.65f, 0.7f));
        help.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(help);
        var timeout = GameSettings.Number("selftest-timeout", 300);
        if (GameSettings.Flag("selftest"))
            _test = new SelfTest(timeout);
        else if (GameSettings.Flag("selftest-besaid"))
            _test = new BesaidTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-remake"))
            _test = new Remake.RemakeTest(timeout);
        else if (GameSettings.Flag("selftest-message"))
            _test = new MessageTest(timeout, GameSettings.Arg("shot"));
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F1 })
            Visible = !Visible;
    }

    public override void _Process(double delta)
    {
        var host = GameHost.Instance;
        if (host?.Game == null)
        {
            _label.Text = host?.Error ?? "no GameHost autoload";
            if (_test != null && host != null && host.Error.Length > 0)
                GetTree().Quit(1);
            return;
        }
        var s = host.Game.State;
        if (s.Seconds - _rateSeconds >= 2.0)
        {
            _rate = (s.Updates - _rateUpdates) / (s.Seconds - _rateSeconds);
            _rateUpdates = s.Updates;
            _rateSeconds = s.Seconds;
        }
        if (Visible)
            _label.Text = Describe(s, host.View, _rate, host.Error);
        if (_test == null)
            return;
        if (s.Seconds - _lastLog >= 5.0)
        {
            _lastLog = s.Seconds;
            GD.Print($"ffx: {OneLine(s, _rate)}  {SnapshotLine(host.View)}  {AudioLine(s)}");
        }
        if (_test.Step(host.Game, s, host.View, delta) is { } pass)
        {
            GD.Print(pass ? $"ffx selftest: PASS ({_test.Note})" : $"ffx selftest: FAIL ({_test.Note})");
            GD.Print($"ffx: {OneLine(s, _rate)}  {AudioLine(s)}");
            host.Game.Stop();
            GetTree().Quit(pass ? 0 : 1);
            _test = null;
        }
    }

    private static string Hex(int v, int digits) => "0x" + v.ToString($"x{digits}");

    public static string OneLine(GameState s, double rate) =>
        $"{s.Seconds,6:F1} s  updates {s.Updates} ({rate:F1}/s)  game time {s.GameTime:F2} s  "
        + $"map {Hex(s.Map, 4)} (from {Hex(s.PreviousMap, 4)}, entrance {s.Entrance})  "
        + $"story {s.Story}  event frame {s.EventFrame}";

    public static string AudioLine(GameState s) =>
        s.AudioRunning
            ? $"audio {s.AudioPulled / 48000.0:F1} s played, {s.AudioQueued} frames queued, "
                + $"{s.AudioUnderruns} underruns, {s.AudioSkips} skips"
            : "audio off";

    public static string SnapshotLine(GameStateView v)
    {
        var c = v.Current;
        if (c == null)
            return "no snapshot yet";
        var shown = c.Actors.Count(a => a.Hidden == 0);
        return $"snapshot {c.Update} ({v.Decoded} decoded): event {c.World.Event}, "
            + $"{c.Actors.Count} actors ({shown} shown), camera {(c.Camera.Valid ? "yes" : "no")}";
    }

    private static string Describe(GameState s, GameStateView v, double rate, string error)
    {
        var b = new StringBuilder();
        if (error.Length > 0)
            b.AppendLine($"error: {error}\n");
        b.AppendLine($"game thread    {(s.Running ? "running" : "not running")}{(s.Booted ? "" : " (booting)")}");
        if (!string.IsNullOrEmpty(s.Outcome))
            b.AppendLine($"ended          {s.Outcome}");
        b.AppendLine($"wall time      {s.Seconds:F1} s");
        b.AppendLine($"updates        {s.Updates}   ({rate:F1} a second; the game's pace is 30)");
        b.AppendLine($"game time      {s.GameTime:F3} s");
        b.AppendLine($"map            {Hex(s.Map, 4)}   {MapName(s.Map)}  (from {Hex(s.PreviousMap, 4)})");
        b.AppendLine($"story          {s.Story}");
        b.AppendLine($"sound          {AudioLine(s)}");
        b.AppendLine($"focus          {(s.Focused ? "yes" : "no (the game reads no keys)")}");
        b.AppendLine($"keys held      {string.Join(" ", s.Keys.Select(k => k.ToString("x2")))}");
        foreach (var m in s.MessageBoxes)
            b.AppendLine($"message        {m}");
        var c = v.Current;
        if (c != null)
        {
            var w = c.World;
            b.AppendLine();
            b.AppendLine($"state API      snapshot {c.Update}, interpolation {v.Alpha:F2}{(v.Cut ? " (cut)" : "")}, {v.Cuts} cuts{(v.Error.Length > 0 ? ", " + v.Error : "")}");
            b.AppendLine($"event          {w.Event}  frame {w.EventFrame}{(w.Battle ? "  BATTLE" : "")}"
                + $"{(w.PlayerControl ? "  control" : "")}{(w.Paused ? "  paused" : "")}{(w.Fading ? "  fading" : "")}");
            b.AppendLine($"pad            held {c.Input.Held:x4} pressed {c.Input.Pressed:x4} stick {c.Input.StickX},{c.Input.StickY}");
            if (c.Camera.Valid)
            {
                var t = GameStateView.CameraTransform(c.Camera.World);
                b.AppendLine($"camera         at {t.Origin:F1}  fov {Mathf.RadToDeg(c.Camera.FovY):F1} deg");
            }
            foreach (var a in c.Actors.Where(a => a.Hidden == 0).Take(12))
                b.AppendLine($"  {a.Slot,2} {a.Model,-6} {Hex(a.Id, 4)} motion {a.Motion:x8} @{a.MotionFrame,7:F1}  bones {a.Bones.Count,3}"
                    + $"{(a.Player ? "  player" : "")}");
        }
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
/// The self-test: the game boots and updates; the game-state API's
/// snapshots come and decode; at the campfire (the opening, its event a
/// few seconds in) the party is shown, posed by their skeletons, and the
/// camera is there; then a Start press through Godot's input reaches the
/// game and it goes to the title; unless muted, the game's sound has reached
/// Godot's player by then.
/// </summary>
public interface ITest
{
    string Note { get; }
    bool? Step(FfxGame game, GameState s, GameStateView v, double delta);
}

public sealed class SelfTest(double timeout) : ITest
{
    private enum Phase { Boot, Opening, Fire, Press, Release, Title }

    private Phase _phase = Phase.Boot;
    private double _phaseTime;
    private double _total;
    private int _presses;
    private string _fire = "";

    public string Note { get; private set; } = "";

    /// <summary>One frame: null while running, else whether the test passed.</summary>
    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)})");
        if (v.Error.Length > 0)
            return Done(false, $"snapshot: {v.Error}");
        switch (_phase)
        {
            case Phase.Boot when s.Updates >= 30 && v.Current != null:
                Next(Phase.Opening);
                break;
            // the party sits at the fire from the event's frame 900 or so on
            case Phase.Opening when s.Map == DebugView.MapOpening && s.EventFrame > 900:
                Next(Phase.Fire);
                break;
            case Phase.Fire:
            {
                var c = v.Current!;
                var party = c.Actors.Where(a => a.Kind == 0 && a.Hidden == 0).ToList();
                var posed = v.Actors().Count(f => f.Shown && f.Actor.Kind == 0 && f.Bones.Length > 50);
                if (c.World.Map != DebugView.MapOpening || !c.Camera.Valid || party.Count < 3 || posed < 3)
                    return Done(false, $"at the fire: {DebugView.SnapshotLine(v)}, party {party.Count}, posed {posed}");
                _fire = $"at the fire: {party.Count} party members posed by their skeletons ({string.Join(", ", party.Select(a => a.Model))}), "
                    + $"camera fov {Mathf.RadToDeg(c.Camera.FovY):F0} deg";
                Next(Phase.Press);
                break;
            }
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
            // the game's sound reached Godot's player (unless muted)
            case Phase.Title when s.Map == DebugView.MapTitle && GameSettings.AudioEnabled() && s.AudioPulled == 0:
                return Done(false, $"at the title, but Godot's player got no sound ({DebugView.AudioLine(s)})");
            case Phase.Title when s.Map == DebugView.MapTitle:
                return Done(true,$"{_fire}; the opening went to the title after {_presses} Start press(es); "
                    + $"{s.Updates} updates in {s.Seconds:F1} s, {v.Decoded} snapshots decoded; {DebugView.AudioLine(s)}");
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
        Godot.Input.ParseInputEvent(e);
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

/// <summary>
/// G2's check (<c>--selftest-besaid</c>; run with <c>--devsave=7</c>): developer
/// save 7 puts the game on Besaid beach with the control; the classic map
/// of its level and the player's classic model come up, posed by the game's
/// skeleton (with <c>--shot=FILE.png</c> and a renderer, a picture of it);
/// then the player walks down and left, as the original's replay does, off
/// the beach to the next map (0x14, <c>bsil0200</c>).
/// </summary>
public sealed class BesaidTest(double timeout, string? shot) : ITest
{
    public const int MapBeach = 0x46;
    public const int MapNext = 0x14;

    private enum Phase { Boot, Beach, Classic, Shot, Walk }

    private Phase _phase = Phase.Boot;
    private double _phaseTime;
    private double _total;
    private string _seen = "";
    private Vector3 _start;
    private Vector3 _last;

    public string Note { get; private set; } = "";

    /// <summary>The player's field position (zero when none).</summary>
    private static Vector3 Player(GameStateView v)
    {
        if (v.Current != null)
            foreach (var a in v.Current.Actors)
                if (a.Player)
                    return a.Position;
        return Vector3.Zero;
    }

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)})");
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        switch (_phase)
        {
            case Phase.Boot when v.Current is { World.Map: MapBeach, World.PlayerControl: true }:
                Next(Phase.Beach);
                break;
            case Phase.Beach when view != null && view.MapLevel == v.Current!.World.Level && view.Posed.Models > 0
                && view.Posed.PosedBones > 40:
                _seen = $"{v.Current.World.Level} shown, {view.Posed.Models} character model(s) posed ({view.Posed.PosedBones} bones)";
                Next(shot != null && DisplayServer.GetName() != "headless" ? Phase.Shot : Phase.Classic);
                break;
            case Phase.Shot when _phaseTime > 1.0:
            {
                var img = (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage();
                img.SavePng(shot!);
                GD.Print($"ffx selftest: picture {shot}");
                Next(Phase.Classic);
                break;
            }
            case Phase.Classic:
                _start = Player(v);
                if (!GameSettings.Flag("no-walk"))
                    Keys(true);
                Next(Phase.Walk);
                break;
            case Phase.Walk when s.Map == MapNext:
            {
                Keys(false);
                var moved = _start.DistanceTo(_last);
                if (moved < 5f)
                    return Done(false, $"map {MapNext:x} came without the player moving ({moved:F1} units)");
                return Done(true, $"{_seen}; walked {moved:F0} units off the beach to map {MapNext:x} in {_phaseTime:F1} s");
            }
            case Phase.Walk:
                if (v.Current?.World.Map == MapBeach)
                    _last = Player(v);
                if (_phaseTime > 60)
                {
                    Keys(false);
                    return Done(false, $"still on map {s.Map:x} after 60 s of walking ({_seen})");
                }
                break;
        }
        return null;
    }

    private static void Keys(bool down)
    {
        foreach (var k in new[] { Godot.Key.Down, Godot.Key.Left })
            Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = down });
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

/// <summary>
/// G4's check (<c>--selftest-message</c>; run with <c>--devsave=6</c>, the arrival
/// off Besaid): the cut scene's first subtitle opens a message window, and the
/// native message box shows its text decoded with the game's character set (with
/// <c>--shot=FILE.png</c> and a renderer, a picture of it).
/// </summary>
public sealed class MessageTest(double timeout, string? shot) : ITest
{
    private double _total;
    private double _seen = -1;

    public string Note { get; private set; } = "";

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > timeout)
            return Done(false, $"timed out ({DebugView.SnapshotLine(v)})");
        var mv = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("MessageView", true, false) as MessageView;
        if (mv == null || v.Current?.World.Story != 111)
            return null;
        foreach (var text in mv.Shown)
        {
            if (text.Length < 2)
                continue;
            if (_seen < 0)
                _seen = _total;
            // a few frames to lay it out, then the picture
            if (_total - _seen < 0.15)
                return null;
            if (shot != null && DisplayServer.GetName() != "headless")
            {
                (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage().SavePng(shot);
                GD.Print($"ffx selftest: picture {shot}");
            }
            return Done(true, $"the subtitle shown natively: \"{text.Replace('\n', ' ')}\" (language {v.Current.World.Language})");
        }
        return null;
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }
}
