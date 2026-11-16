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
        // --no-overlay: start hidden (screenshots for comparison)
        if (GameSettings.Flag("no-overlay"))
            Visible = false;
        var timeout = GameSettings.Number("selftest-timeout", 300);
        if (GameSettings.Flag("selftest"))
            _test = new SelfTest(timeout);
        else if (GameSettings.Flag("selftest-menu"))
            _test = new MenuTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-besaid"))
            _test = new BesaidTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-remake-map"))
            _test = new Remake.RemakeMapTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-remake"))
            _test = new Remake.RemakeTest(timeout);
        else if (GameSettings.Flag("selftest-battle"))
            _test = new BattleTest(timeout, GameSettings.Arg("shot"));
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
            ? $"audio {s.AudioPulled / 48000.0:F1} s played, {s.AudioQueued} frames queued "
                + $"(mean {s.AudioMeanQueued}), {s.AudioUnderruns} underruns, "
                + $"latency {s.AudioLatencyMs:F0} ms (queue {s.AudioMeanQueuedMs:F0} + output {s.AudioOutputLatencyMs:F0})"
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
            var fx = c.Effects;
            b.AppendLine($"effects        magic {fx.Magic}  records {fx.Running.Count}  music {fx.Music}  shake {fx.ShakeX:F1},{fx.ShakeY:F1}"
                + $"  filters {string.Join(" ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(fx.Filters, f => f.Active), f => $"{f.Slot}:{f.Mode}"))}");
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
    /// <summary>The save's menu parts in one line: gil, time, the front line by name, items.</summary>
    public static string SaveLine(FfxGame game, GdFfx.Api.Snapshot s)
    {
        var sv = s.Save;
        var lang = MessageView.LanguageCode(s.World.Language);
        string Name(int i) => i < sv.Members.Count ? MessageView.Clean(game.DecodeText(sv.Members[i].Name, lang)) : $"#{i}";
        var front = string.Join(", ", sv.Frontline.Where(i => i != 0xff).Select(i => $"{Name(i)} {sv.Members[i].Hp}/{sv.Members[i].MaxHp}"));
        var joined = string.Join(", ", sv.Members.Where(m => m.Joined).Select(m => Name(m.Index)));
        return $"valid {sv.Valid} gil {sv.Gil} time {sv.PlayTime / 3600}:{sv.PlayTime / 60 % 60:00}:{sv.PlayTime % 60:00} front [{front}] "
            + $"joined [{joined}] items {sv.Items.Count} (first {(sv.Items.Count > 0 ? $"0x{sv.Items[0].Id:x4} x{sv.Items[0].Count}" : "-")})";
    }

    /// <summary>The scene's look in one line (the colour scale, the fog, the lights).</summary>
    public static string LookLine(GdFfx.Api.Look l)
    {
        var lights = string.Join("; ", l.Lights.ConvertAll(x => $"dir {x.Direction} colour {x.Colour} level {x.Level:F2} pitch {x.Pitch} yaw {x.Yaw}"));
        return $"valid {l.Valid} scale {l.ColourScale} ambient {l.Ambient} fog {l.FogColour} {l.FogNear}..{l.FogFar} curve {l.FogCurve} limit {l.FogLimit} "
            + $"chr ambient {l.CharacterAmbient} lights [{lights}]";
    }

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
/// the beach to the next map (0x14, <c>bsil0200</c>). With <c>--check-autosave</c>, then
/// the autosave the game writes there, read back as the load list's first row.
/// </summary>
public sealed class BesaidTest(double timeout, string? shot) : ITest
{
    public const int MapBeach = 0x46;
    public const int MapNext = 0x14;

    private enum Phase { Boot, Beach, Classic, Shot, Walk, AutoSave }

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
                _seen = $"{v.Current.World.Level} shown, {view.Posed.Models} character model(s) posed ({view.Posed.PosedBones} bones), "
                    + $"{view.ObjectsPlaced} object mesh(es) placed by the game";
                if (view.ObjectsPlaced == 0)
                    return Done(false, $"no map object placed by the game ({v.Current.Objects.Count} in the snapshot)");
                GD.Print($"ffx selftest: look {SelfTest.LookLine(v.Current.Look)}");
                GD.Print($"ffx selftest: save {SelfTest.SaveLine(game, v.Current)}");
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
                _seen = $"{_seen}; walked {moved:F0} units off the beach to map {MapNext:x} in {_phaseTime:F1} s";
                if (!GameSettings.Flag("check-autosave"))
                    return Done(true, _seen);
                Next(Phase.AutoSave);
                break;
            }
            // --check-autosave: the game writes ffx_000 on entering the map; the load list
            // reads it back as its first row, the header as the game's own state
            case Phase.AutoSave:
            {
                var list = SaveFiles.List(GameSettings.DocumentsDir(), saving: false);
                if (list.Count == 0 || list[0] is not { Slot: 0 } auto || v.Current == null)
                {
                    if (_phaseTime > 30)
                        return Done(false, $"no autosave 30 s after map {MapNext:x} ({_seen})");
                    break;
                }
                var sv = v.Current.Save;
                if (auto.Gil != sv.Gil || auto.Party[0] != sv.Frontline[0] || auto.Party[1] != sv.Frontline[1])
                    return Done(false, $"the autosave (gil {auto.Gil}, party {auto.Party[0]},{auto.Party[1]}) is not the game's (gil {sv.Gil}, {sv.Frontline[0]},{sv.Frontline[1]})");
                if (auto.Name(0).Length == 0 || !auto.Name(0).SequenceEqual(sv.Members[0].Name))
                    return Done(false, "the autosave's first name is not the game's");
                var row = BattleMenuView.SaveRow(game, auto, MessageView.LanguageCode(v.Current.World.Language));
                return Done(true, $"{_seen}; the autosave's row: {row}");
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

/// <summary>
/// G3/G4's battle check (<c>--selftest-battle --devsave=7 --battle=3,0,0</c>):
/// from Besaid the deck's battle starts; its classic battle map and the
/// characters (party, monsters) come up posed, and the native command menu
/// shows Tidus's commands named from the kernel tables with the game's cursor,
/// and the HUD the party's HP and MP and the turn order; then Tidus attacks
/// (Enter tapped) until a damage number pops; the battle's effects play on their
/// targets, effect 50001800 by the example remade effect (registered at the start)
/// (with <c>--shot=FILE.png</c> under a renderer, a picture).
/// </summary>
public sealed class BattleTest : ITest
{
    private readonly double timeout;
    private readonly string? shot;

    public BattleTest(double timeout, string? shot)
    {
        this.timeout = timeout;
        this.shot = shot;
        Remake.RemakeRegistry.Register("effect", RemadeId, ExampleEffect, "selftest-battle");
        GD.Print($"ffx selftest: effect {RemadeId} registered to {ExampleEffect}");
    }

    private double _total;
    private double _seen = -1;
    private string _first = "";
    /// <summary>The effect this battle starts first (seen in the deck's battle 3,0,0), remade by the example.</summary>
    public const string RemadeId = "50001800";
    private double _tap;

    /// <summary>The example remade effect (a ring; docs/remake.md).</summary>
    public const string ExampleEffect = "res://remake/examples/stand_in_effect.tscn";
    private bool _down;

    public string Note { get; private set; } = "";

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        var menu = root?.FindChild("BattleMenuView", true, false) as BattleMenuView;
        var view3d = root?.FindChild("StateView3D", true, false) as StateView3D;
        var hud = root?.FindChild("BattleHudView", true, false) as BattleHudView;
        var c = v.Current;
        if (_total > timeout)
            return Done(false, $"timed out: battle {c?.World.Battle}, level {c?.World.Level}, map shown {view3d?.MapLevel}, "
                + $"models {view3d?.Posed.Models}, menu [{string.Join(", ", menu?.Shown ?? [])}]");
        if (c == null || menu == null || view3d == null || hud == null || !c.World.Battle || menu.Shown.Count < 2
            || view3d.MapLevel != c.World.Level || view3d.Posed.Models < 2 || hud.Shown.Count == 0 || hud.Order.Length == 0)
            return null;
        if (_seen < 0)
            _seen = _total;
        if (_total - _seen < 0.3)
            return null;
        if (_first.Length == 0)
        {
            if (shot != null && DisplayServer.GetName() != "headless")
            {
                root!.GetViewport().GetTexture().GetImage().SavePng(shot);
                GD.Print($"ffx selftest: picture {shot}");
            }
            _first = $"battle {c.World.Level}: {view3d.Posed.Models} models posed ({view3d.Posed.PosedBones} bones); "
                + $"the command menu: {string.Join(" | ", menu.Shown)}; the HUD: {string.Join(" | ", hud.Shown)}; next: {hud.Order}";
            GD.Print($"ffx selftest: {_first}; attacking");
        }
        var effects = root?.FindChild("EffectView", true, false) as EffectView;
        // Attack: confirm (Enter) tapped until a damage number pops
        if (hud.Popped > 0)
        {
            Tap(false);
            return Done(true, $"{_first}; {hud.Popped} damage number(s) popped; {effects?.Started ?? 0} effect(s) started "
                + $"({effects?.RemadeStarted ?? 0} remade as {RemadeId})");
        }
        _tap += delta;
        if (_tap > 0.25)
        {
            _tap = 0;
            Tap(!_down);
        }
        return null;
    }

    private void Tap(bool down)
    {
        _down = down;
        Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Godot.Key.Enter, Keycode = Godot.Key.Enter, Pressed = down });
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }
}

/// <summary>
/// G4's field menu check (<c>--selftest-menu --devsave=7</c>): on Besaid beach with the
/// control, the menu key (V) opens the game's main menu; the native panel then shows the
/// party by the names the player gave, with HP and MP, and the gil and play time
/// (<see cref="FieldMenuView"/>), and the options list (<see cref="BattleMenuView"/>); then
/// Items (one down, confirm) opens the game's item screen and the native list of the
/// inventory, named from the kernel tables.
/// With <c>--shot=FILE.png</c> under a renderer, a picture.
/// </summary>
public sealed class MenuTest(double timeout, string? shot) : ITest
{
    private double _total;
    private double _tap;
    private bool _down;
    private double _open = -1;
    private double _probe;
    private string _main = "";
    private double _listAt = -1;
    private string _first = "";

    public string Note { get; private set; } = "";

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        var panel = root?.FindChild("FieldMenuView", true, false) as FieldMenuView;
        var options = root?.FindChild("BattleMenuView", true, false) as BattleMenuView;
        var c = v.Current;
        if (_total > timeout)
            return Done(false, $"timed out: map {c?.World.Map:x}, menu mask {c?.Menus.MainMask:x}, panel [{string.Join(" | ", panel?.Shown ?? [])}]");
        if (c == null || panel == null || options == null || c.World.Map != BesaidTest.MapBeach
            || _open < 0 && !c.World.PlayerControl && (c.Menus.MainMask & 2) == 0)
            return null;
        if (_open < 0 && ((c.Menus.MainMask & 2) == 0 || panel.Shown.Count == 0))
        {
            // tap the menu key until the game opens its menu
            _tap += delta;
            if (_tap > 0.3)
            {
                _tap = 0;
                Tap(!_down);
            }
            return null;
        }
        Tap(false);
        if (_open < 0)
            _open = _total;
        if (_total - _open < 0.5)
            return null;
        if (_main.Length == 0)
        {
            if (!panel.Footer.Contains("Gil"))
                return Done(false, $"no gil line ({panel.Footer})");
            _main = $"the menu: [{string.Join(" | ", options.Shown)}]; the party: [{string.Join(" | ", panel.Shown)}]; {panel.Footer}";
            if (shot != null && DisplayServer.GetName() != "headless")
                root!.GetViewport().GetTexture().GetImage().SavePng(shot);
        }
        // then into a screen: Items (one down, confirm), or Status with --menu-screen=status
        // (four down, confirm, confirm the first character)
        var screen = GameSettings.Arg("menu-screen") ?? "items";
        if (screen == "sphere-grid")
        {
            // the first option, confirm, confirm the first character: the game's grid and the native view
            _probe += delta;
            double w = _probe - delta;
            void P(Key key, bool d) => Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = d });
            foreach (var t in new[] { 1.0, 2.5 })
            {
                if (w < t && _probe >= t) P(Key.Enter, true);
                if (w < t + 0.3 && _probe >= t + 0.3) P(Key.Enter, false);
            }
            var grid = root?.FindChild("SphereGridView", true, false) as SphereGridView;
            if (grid == null || grid.Shown.Nodes == 0 || _probe < 6)
                return _probe > 20 ? Done(false, $"no sphere grid drawn (mask {c.Menus.MainMask:x}, open {c.SphereGrid.Open})") : null;
            if (shot != null && DisplayServer.GetName() != "headless")
                root!.GetViewport().GetTexture().GetImage().SavePng(shot);
            return Done(grid.Shown.Marked > 0,
                $"{_main}; the sphere grid: {grid.Shown.Nodes} nodes, {grid.Shown.Links} links, {grid.Shown.Marked} marked for character {c.SphereGrid.Current}");
        }
        bool status = screen is "status" or "abilities" or "overdrive";
        _probe += delta;
        void Press(Key key, bool d) => Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = d });
        double was = _probe - delta;
        int downs = screen switch { "abilities" => 2, "overdrive" => 3, "status" => 4, _ => 1 };
        for (int i = 0; i < downs; i++)
        {
            double t = 1.0 + 0.6 * i;
            if (was < t && _probe >= t) Press(Key.Down, true);
            if (was < t + 0.3 && _probe >= t + 0.3) Press(Key.Down, false);
        }
        double e = 1.5 + 0.6 * downs;
        // the status screen: the second character (one down in the character choice)
        bool second = screen == "status";
        if (second && was < e + 0.7 && _probe >= e + 0.7) Press(Key.Down, true);
        if (second && was < e + 0.9 && _probe >= e + 0.9) Press(Key.Down, false);
        foreach (var t in status ? new[] { e, e + 1.4 } : new[] { e })
        {
            if (was < t && _probe >= t) Press(Key.Enter, true);
            if (was < t + 0.3 && _probe >= t + 0.3) Press(Key.Enter, false);
        }
        if (status)
        {
            if (FieldMenuView.OpenScreen(c.Menus.MainMask) != screen || panel.Status.Count == 0)
                return _probe > 15
                    ? Done(false, $"the {screen} screen did not come (mask {c.Menus.MainMask:x}, {FieldMenuView.OpenScreen(c.Menus.MainMask)}); {_main}")
                    : null;
            if (second && (c.Menus.CurrentPlayer != c.Save.Frontline[1] || !panel.Status[0].StartsWith($"HP: {c.Save.Members[c.Save.Frontline[1]].Hp}/")))
                return Done(false, $"the status screen shows character {c.Menus.CurrentPlayer}, not the second ({c.Save.Frontline[1]}): [{string.Join(" | ", panel.Status)}]");
            return Done(true, $"{_main}; the {screen} screen (character {c.Menus.CurrentPlayer}): [{string.Join(" | ", panel.Status)}]");
        }
        if ((c.Menus.MainMask & FieldMenuView.ItemsMask) == 0 || panel.Items.Count == 0)
        {
            if (_probe > 10)
                return Done(false, $"the item screen did not come (mask {c.Menus.MainMask:x}, cursor {c.Menus.MainCursor}); {_main}");
            return null;
        }
        // in the list: the cursor starts on slot 0; one down moves it to slot 1 (measured)
        if (_listAt < 0)
            _listAt = _probe;
        double l = _probe - _listAt, lw = l - delta;
        if (lw < 0.8 && l >= 0.8)
            _first = panel.ItemAtCursor;
        if (lw < 1.0 && l >= 1.0) Press(Key.Down, true);
        if (lw < 1.3 && l >= 1.3) Press(Key.Down, false);
        if (l < 2.5)
            return null;
        var cur = FieldMenuView.ItemList(c)?.Cursor ?? -1;
        if (cur != 1 || _first.Length == 0 || panel.ItemAtCursor.Length == 0 || panel.ItemAtCursor == _first)
            return Done(false, $"the item cursor: [{_first}] then [{panel.ItemAtCursor}] (slot {cur}, not 1)");
        return Done(true, $"{_main}; the cursor on [{_first}], one down on [{panel.ItemAtCursor}]; the items: [{string.Join(" | ", panel.Items.Take(4))}{(panel.Items.Count > 4 ? " | ..." : "")}] ({panel.Items.Count})");
    }

    private void Tap(bool down)
    {
        if (_down == down)
            return;
        _down = down;
        Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.V, Keycode = Key.V, Pressed = down });
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }
}
