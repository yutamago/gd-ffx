using System.Linq;
using System.Text;
using GdFfx.Api;
using Godot;

namespace GdFfx;

/// <summary>
/// The debug overlay (F12 shows or hides it; F1 to F4 are the game's boosters): the game's live state from
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
    private GameState _state;
    private double _stateAt = double.NegativeInfinity;

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
        var title = new Label { Text = "gd-ffx: ffx-rust inside Godot (F12: hide)" };
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        _label = new Label();
        _label.AddThemeFontOverride("font", new SystemFont { FontNames = ["monospace", "Consolas", "DejaVu Sans Mono"] });
        _label.AddThemeFontSizeOverride("font_size", 13);
        box.AddChild(_label);
        var help = new Label
        {
            Text = "Keys as on the original: B Start, arrows, C/Enter confirm, X/Backspace cancel, V menu, Esc PC menu, F1-F4 boosters.\n"
                + "gd-ffx: F9 FPS overlay, F10 options, F11 fullscreen, F12 this overlay.",
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
        else if (GameSettings.Flag("bench"))
            _test = new BenchTest(timeout);
        else if (GameSettings.Flag("selftest-map"))
            _test = new MapTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-besaid"))
            _test = new BesaidTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-saved-overrides"))
            _test = new Remaster.SavedOverridesTest(timeout);
        else if (GameSettings.Flag("selftest-lighting"))
            _test = new Remaster.LightingTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-materialize"))
            _test = new Remaster.Materialize.MaterializeTest(timeout);
        else if (GameSettings.Flag("selftest-materials"))
            _test = new Remaster.MaterialsTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-alignment"))
            _test = new Remaster.AlignmentTest(timeout);
        else if (GameSettings.Flag("selftest-overrides"))
            _test = new Remaster.OverridesTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-remake-map"))
            _test = new Remake.RemakeMapTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-character"))
            _test = new Characters.CharacterTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-remake"))
            _test = new Remake.RemakeTest(timeout);
        else if (GameSettings.Flag("selftest-battle"))
            _test = new BattleTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-message"))
            _test = new MessageTest(timeout, GameSettings.Arg("shot"));
        else if (GameSettings.Flag("selftest-load"))
            _test = new LoadTest(timeout, GameSettings.Arg("shot-dir"));
        else if (GameSettings.Flag("selftest-save"))
            _test = new SaveTest(timeout, GameSettings.Arg("shot-dir"));
        else if (GameSettings.Flag("selftest-ux"))
            _test = new UxTest(timeout, GameSettings.Arg("shot-dir"));
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: GameHost.DebugKey })
            Visible = !Visible;
    }

    public override void _Process(double delta)
    {
        var t0 = Perf.Start();
        Tick(delta);
        Perf.End(Perf.Section.Debug, t0);
    }

    private void Tick(double delta)
    {
        var host = GameHost.Instance;
        if (host?.Game == null)
        {
            _label.Text = host?.Error ?? "no GameHost autoload";
            if (_test != null && host != null && host.Error.Length > 0)
                GetTree().Quit(1);
            return;
        }
        // the state (a dictionary built by the extension) and the overlay's text, four times a
        // second; the checks read it every frame, but the benchmark does not need to
        var now = Time.GetTicksMsec() / 1000.0;
        bool fresh = now - _stateAt >= 0.25 || _test is not (null or BenchTest);
        if (fresh)
        {
            _state = host.Game.State;
            _stateAt = now;
        }
        var s = _state;
        if (s.Seconds - _rateSeconds >= 2.0)
        {
            _rate = (s.Updates - _rateUpdates) / (s.Seconds - _rateSeconds);
            _rateUpdates = s.Updates;
            _rateSeconds = s.Seconds;
        }
        if (Visible && fresh)
            _label.Text = Describe(s, host.View, _rate, host.Error);
        if (_test == null)
            return;
        if (s.Seconds - _lastLog >= 5.0)
        {
            _lastLog = s.Seconds;
            GD.Print($"ffx: {OneLine(s, _rate)}  {SnapshotLine(host.View)}  {AudioLine(s)}");
        }
        if (_test.Step(host.Game, s, host.View, delta) is { } passed)
        {
            var pass = passed;
            var note = _test.Note;
            // --check-flash: every Flash movie the game opened has a native screen
            if (GameSettings.Flag("check-flash"))
            {
                var (ok, flash) = FlashScreens.Check(host.Game);
                GD.Print($"ffx flash: {flash}");
                pass &= ok;
                note += $"; {flash}";
            }
            GD.Print(pass ? $"ffx selftest: PASS ({note})" : $"ffx selftest: FAIL ({note})");
            GD.Print($"ffx: {OneLine(s, _rate)}  {AudioLine(s)}");
            if (host.View.Skipped.Count > 0)
                GD.Print($"ffx: characters the game culled or skipped (not drawn here since API 10): {string.Join(" ", host.View.Skipped)}");
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
        var shown = c.Actors.Count(GameStateView.Drawn);
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
            foreach (var a in c.Actors.Where(GameStateView.Drawn).Take(12))
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
        return $"valid {l.Valid} glow {l.GlowNone}..{l.GlowFull} gain {l.GlowGain} spread {l.GlowSpread} scale {l.ColourScale} ambient {l.Ambient} fog {l.FogColour} {l.FogNear}..{l.FogFar} curve {l.FogCurve} limit {l.FogLimit} "
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
                var posed = v.Actors().Count(f => f.Shown && f.Actor.Kind == 0 && f.Actor.Bones.Count > 50);
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
            {
                // the start-up opening (openingscreen.swf) played natively and ended the game's
                // start-up loop through its Finish (unless --skip-opening)
                var opening = OpeningCheck(game);
                return Done(opening.Pass, $"{opening.Note}; {_fire}; the opening went to the title after {_presses} Start press(es); "
                    + $"{s.Updates} updates in {s.Seconds:F1} s, {v.Decoded} snapshots decoded; {DebugView.AudioLine(s)}");
            }
            case Phase.Title when _phaseTime > 5 && _presses < 5:
                Next(Phase.Press);
                break;
        }
        return null;
    }

    /// <summary>
    /// The native start-up opening's check: the game waited in its start-up loop, the view
    /// played the movie's frames (the logo, then the autosave notice), the extension called
    /// <c>Finish</c> on the view's ask (not on its own time-out) and the loop went on.
    /// </summary>
    public static (bool Pass, string Note) OpeningCheck(FfxGame game)
    {
        if (GameHost.Instance?.NativeOpening != true)
            return (true, "start-up opening skipped");
        var st = game.OpeningState();
        var view = OpeningView.Instance;
        bool pass = st.Waiting && st.FinishSent && st.Ended && !st.TimedOut && view is { LogoShown: true, NoticeShown: true };
        return (pass, $"start-up opening: {(view?.LogoShown == true ? "logo" : "NO logo")}, {(view?.NoticeShown == true ? "autosave notice" : "NO notice")} "
            + $"(frame {view?.Frame}), the game {(st.Waiting ? "waited" : "did NOT wait")}, Finish {(st.FinishSent ? (st.TimedOut ? "sent on the time-out" : "sent") : "NOT sent")}, "
            + $"{(st.Ended ? "the start-up loop went on" : "the loop did NOT end")}");
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
/// the beach to the next map (0x14, <c>bsil0200</c>). The map change is held until the new
/// map and its characters are ready (docs/architecture.md, "Map transitions"): no frame shows
/// characters over another map than theirs or a stand-in, the hold ends with the next map
/// shown, and its timing is reported (<c>--transition-shots=DIR</c> under a renderer: a picture
/// every 50 ms from the map change request to half a second after). With
/// <c>--check-autosave</c>, then the autosave the game writes there, read back as the load
/// list's first row.
/// </summary>
public sealed class BesaidTest(double timeout, string? shot) : ITest
{
    public const int MapBeach = 0x46;
    public const int MapNext = 0x14;

    private enum Phase { Boot, Beach, Classic, Shot, Walk, Fade, AutoSave }

    private Phase _phase = Phase.Boot;
    private double _phaseTime;
    // the screen filters seen while walking (ScreenFilterView): modes, the highest alpha, frames at 1
    private readonly System.Collections.Generic.HashSet<int> _fadeModes = new();
    private float _fadeMost;
    private int _fadeBlack, _fadeEvents;

    private void WatchFilters()
    {
        if ((Engine.GetMainLoop() as SceneTree)?.Root.FindChild("ScreenFilterView", true, false) is not ScreenFilterView filter)
            return;
        foreach (var (mode, alpha) in filter.Shown)
            if (alpha > 0)
            {
                _fadeModes.Add(mode);
                _fadeMost = System.Math.Max(_fadeMost, alpha);
                if (alpha >= 1f)
                    _fadeBlack++;
            }
    }
    private double _total;
    private string _seen = "";
    private Vector3 _start;
    // the map change's frames: (leaked, stand-ins, held) at the walk's start; pictures taken
    private (int Leaked, int StandIns, int Held) _framesAtWalk;
    private int _transitionShots;
    private double _transitionShotAt = -1, _releasedAt = -1;

    /// <summary><c>--transition-shots=DIR</c>: a picture every 50 ms from the map change request until half a second after the hold.</summary>
    private void TransitionShots(StateView3D? view, GameStateView v)
    {
        if (GameSettings.Arg("transition-shots") is not { Length: > 0 } dir || DisplayServer.GetName() == "headless" || view == null)
            return;
        if (_transitionShotAt < 0 && v.Current?.World.MapChange != true && !view.InTransition)
            return;
        if (!view.InTransition && v.Current?.World.MapChange != true && _transitionShotAt >= 0 && _releasedAt < 0)
            _releasedAt = _total;
        if (_releasedAt >= 0 && _total - _releasedAt > 0.5 || _transitionShots >= 200)
            return;
        if (_transitionShotAt >= 0 && _total - _transitionShotAt < 0.05)
            return;
        _transitionShotAt = _total;
        System.IO.Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, $"transition-{_transitionShots++:000}{(view.Holding ? "-held" : view.InTransition ? "-changing" : "")}.png");
        (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage().SavePng(file);
    }
    // an autosave counts only when written by this run (the sandbox keeps earlier runs' files)
    private readonly System.DateTime _began = System.DateTime.UtcNow;
    private Vector3 _last;
    private int _shots;
    private const double ShotEvery = 0.1;

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
            // (and Tidus's cloth placed and moving for a few updates, or 5 s)
            case Phase.Beach when view != null && view.MapLevel == v.Current!.World.Level && view.Posed.Models > 0
                && view.Posed.PosedBones > 40 && (!ClothView.Enabled || view.Cloth.MovingUpdates >= 3 || _phaseTime > 5):
                _seen = $"{v.Current.World.Level} shown, {view.Posed.Models} character model(s) posed ({view.Posed.PosedBones} bones), "
                    + $"{view.ObjectsPlaced} object mesh(es) placed by the game ({view.ObjectsUnmatched} without a mesh)";
                if (view.ObjectsPlaced == 0)
                    return Done(false, $"no map object placed by the game ({v.Current.Objects.Count} in the snapshot)");
                // the beach's morphing meshes (the shore, the birds: docs/importer.md, "Morphing meshes")
                _seen += $", {view.Morphed.Meshes} morphing mesh(es) ({view.Morphed.Moved} with a weight)";
                if (view.Morphed.Meshes == 0)
                    return Done(false, "no morphing map mesh");
                // the game's own effects (the save sphere's orb, the shore's strips), unless FFX_NO_VFX
                if (System.Environment.GetEnvironmentVariable("FFX_NO_VFX") == null)
                {
                    var fx = view.GetNodeOrNull<EffectDrawView>("EffectDrawView");
                    if (fx == null || fx.Shown.Draws == 0 || fx.Shown.Textured == 0)
                        return Done(false, $"no effect draws shown ({fx?.Shown})");
                    _seen += $", {fx.Shown.Draws} effect draws ({fx.Shown.Textured} textured, {fx.Textures.Count} textures)";
                    var dv = view.GetTree().Root.FindChild("DistortionView", true, false) as DistortionView;
                    _seen += dv?.Shown.On == true ? $", the screen distortion on ({dv.Shown.Vertices} vertices, copy alpha {dv.Shown.Alpha:F2})" : ", no screen distortion";
                }
                // the game's cloth (Tidus's hair and clothes: 104 simulated vertices on 5 segments),
                // unless FFX_NO_CLOTH: placed on his model, every surface matched, moving relative to him
                if (ClothView.Enabled)
                {
                    var cl = view.Cloth.Shown;
                    _seen += $", cloth: {cl} ({view.Cloth.MovingUpdates} update(s) with moving cloth, {view.Cloth.Late} of {view.Cloth.Updates} frame(s) after their snapshot)";
                    if (cl.Weighted == 0 || cl.Unmatched > 0)
                        return Done(false, $"the game's cloth not drawn: {cl}");
                    if (view.Cloth.MovingUpdates == 0)
                        return Done(false, $"the game's cloth does not move: {cl}");
                }
                GD.Print($"ffx selftest: beach {_seen}");
                GD.Print($"ffx selftest: look {SelfTest.LookLine(v.Current.Look)}");
                GD.Print($"ffx selftest: save {SelfTest.SaveLine(game, v.Current)}");
                Next(shot != null && DisplayServer.GetName() != "headless" ? Phase.Shot : Phase.Classic);
                break;
            case Phase.Shot when _phaseTime > 1.0 + _shots * ShotEvery:
            {
                // --shots=N: N pictures ShotEvery apart (the second as NAME-1.png, ...)
                var img = (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage();
                var file = _shots == 0 ? shot! : shot![..^4] + $"-{_shots}.png";
                if (v.Current is { } cur)
                    GD.Print("ffx selftest: morph values " + string.Join(" ", cur.Objects.Where(o => o.Dobj && o.Morph != 0).Select(o => $"{o.Id}:{o.Morph:F2}@{(o.Meshes.Count > 0 ? GameStateView.CameraTransform(o.Meshes[0].World).Origin : Vector3.Zero)}")));
                img.SavePng(file);
                GD.Print($"ffx selftest: picture {file}");
                if (++_shots >= (int.TryParse(GameSettings.Arg("shots"), out var n) ? System.Math.Max(1, n) : 1))
                    Next(Phase.Classic);
                break;
            }
            case Phase.Classic:
                _start = Player(v);
                _framesAtWalk = view?.Frames ?? default;
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
                _fadeEvents = EffectDrawView.FadeFrames;
                Next(Phase.Fade);
                break;
            }
            // the screen filters for 4 s more (the map change's fades), and until the map change is shown
            case Phase.Fade:
            {
                WatchFilters();
                TransitionShots(view, v);
                int at = view?.Transitions.FindIndex(t => t.From == "/map/bsil/bsil00/" && t.To != t.From) ?? -1;
                if (_phaseTime < 4 || (at < 0 && _phaseTime < 20))
                    break;
                if (at < 0)
                    return Done(false, $"no map change shown 20 s after map {MapNext:x} ({_seen})");
                var tr = view!.Transitions[at];
                var f = view.Frames;
                var (leaked, standIns, held) = (f.Leaked - _framesAtWalk.Leaked, f.StandIns - _framesAtWalk.StandIns, f.Held - _framesAtWalk.Held);
                _seen += $"; the map change {tr}; frames held {held}, with characters over another map {leaked}, with stand-ins {standIns}"
                    + $"; loaded ahead: {string.Join(" ", view.Prefetch.Candidates)}";
                if (leaked > 0 || standIns > 0)
                    return Done(false, $"{leaked} frame(s) showed characters over another map than theirs, {standIns} stand-ins ({_seen})");
                if (held == 0 && !GameSettings.Flag("no-hold"))
                    return Done(false, $"the map change was not held ({_seen})");
                if (tr.TimedOut)
                    return Done(false, $"the map change's hold gave up waiting ({_seen})");
                _seen += $"; the filters: modes {string.Join(",", _fadeModes.Order())}, highest alpha {_fadeMost:F3}, "
                    + $"{_fadeBlack} frames fully black; {EffectDrawView.FadeFrames - _fadeEvents} frames with a screen fade event";
                // a fade to black is fully black once it holds (Sg_DrawFilter's mode 3)
                if (_fadeModes.Contains(3) && _fadeMost < 1f)
                    return Done(false, $"the fade to black stopped at alpha {_fadeMost:F3} ({_seen})");
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
                if (list.Count == 0 || list[0] is not { Slot: 0 } auto || auto.Written < _began || v.Current == null)
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
                WatchFilters();
                TransitionShots(view, v);
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
/// G5's sweep check (<c>--selftest-map</c>, with <c>--devsave=N</c>; <c>tools/sweep-devsaves.sh</c>
/// runs it on every developer save): once the save's scene runs (not the title), its map or
/// battle map is shown with the classic content, character models posed by the game, and it
/// stays up for 3 seconds; reports the level, models, bones, objects, effect draws, the
/// distortion, the characters the game draws and whether it is a battle (with <c>--shot=FILE.png</c>
/// and a renderer, a picture; with <c>--walk=KEYS:SECONDS[,KEYS:SECONDS...]</c>, after walking,
/// and with <c>--shots=N</c> N - 1 more during the walk).
/// </summary>
public sealed class MapTest(double timeout, string? shot) : ITest
{
    private double _total;
    private double _shown = -1;
    private string _level = "";

    public string Note { get; private set; } = "";

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        var c = v.Current;
        if (_total > timeout)
            return Done(false, $"timed out: level {c?.World.Level}, map shown {view?.MapLevel}, models {view?.Posed.Models} ({DebugView.SnapshotLine(v)})");
        if (c == null || view == null || c.World.Level.Length == 0 || c.World.Level.Contains("/titl/") || _total < 5)
            return null;
        if (view.MapLevel != c.World.Level || view.Posed.Models == 0)
        {
            _shown = -1;
            // a walk stops while the next map comes and settles (Walk takes it up again)
            Release();
            return null;
        }
        if (_level != c.World.Level)
        {
            _level = c.World.Level;
            _shown = _total;
        }
        if (_shown < 0)
            _shown = _total;
        if (_total - _shown < 3)
            return null;
        // --walk=KEYS:SECONDS[,KEYS:SECONDS...] (keys joined by +, e.g. up+left:6,up:2): hold
        // each in turn, then 2 s still
        if (Walk(delta) is { } walking && walking)
            return null;
        var fx = view.GetNodeOrNull<EffectDrawView>("EffectDrawView");
        var dv = view.GetTree().Root.FindChild("DistortionView", true, false) as DistortionView;
        // --shots=N without a walk: N - 1 pictures 0.1 s apart first (NAME-1.png, ...), then NAME.png
        if (shot != null && DisplayServer.GetName() != "headless" && GameSettings.Arg("walk") == null
            && int.TryParse(GameSettings.Arg("shots"), out var shots) && _stillShots < shots - 1)
        {
            if (_total - _stillShotAt >= 0.1)
            {
                _stillShotAt = _total;
                var file = shot[..^4] + $"-{++_stillShots}.png";
                view.GetTree().Root.GetViewport().GetTexture().GetImage().SavePng(file);
                GD.Print($"ffx selftest: picture {file}");
            }
            return null;
        }
        if (shot != null && DisplayServer.GetName() != "headless")
        {
            view.GetTree().Root.GetViewport().GetTexture().GetImage().SavePng(shot);
            GD.Print($"ffx selftest: picture {shot}");
        }
        return Done(true, $"{(c.World.Battle ? "battle" : "field")} {c.World.Level} (map {c.World.Map:x}): {view.Posed.Models} models "
            + $"({view.Posed.PosedBones} bones), {view.ObjectsPlaced} objects ({view.ObjectsUnmatched} without a mesh), {view.Morphed.Meshes} morphing ({view.Morphed.Moved} with a weight), {fx?.Shown.Draws ?? 0} effect draws "
            + $"({fx?.Shown.Textured ?? 0} textured, {fx?.MissingDraws ?? 0} without their texture, {EffectDrawView.UnsetSeen} with unset vertex colours so far), distortion {(dv?.Shown.On == true ? "on" : "off")}{Layers(view)}, {Characters(c)}, "
            + $"global time {view.GlobalTime:F2} s{BattleTest.ClothNote()}, at {_total:F0} s");
    }

    /// <summary>
    /// The characters the game draws (<c>Actor.drawn</c>), and those it culls or skips although
    /// their hide byte, alpha and matrix would show them (by model): hidden here since API 10.
    /// </summary>
    public static string Characters(Snapshot c)
    {
        var skipped = c.Actors.Where(a => !a.Drawn && a.Hidden == 0 && a.Alpha > 0 && !GameStateView.ZeroSize(a.World)).ToList();
        var note = $"characters drawn {c.Actors.Count(GameStateView.Drawn)}/{c.Actors.Count}";
        return skipped.Count == 0 ? note
            : note + $" ({skipped.Count} culled or skipped by the game: {string.Join(" ", skipped.Select(a => $"{a.Model}#{a.Slot}(flags2 {a.Flags2:x})"))})";
    }

    private Key[] _heldKeys = [];
    private int _walkShots, _stillShots;
    private double _stillShotAt = -1;
    /// <summary>The walk's time: it runs only on a shown map, after its 3 s (not across map changes).</summary>
    private double _walkT;

    /// <summary>The <c>--walk</c> legs: keys and seconds each (empty: no walk or not read).</summary>
    private static (Key[] Keys, string Names, double Seconds)[] Legs()
    {
        if (GameSettings.Arg("walk") is not { } arg)
            return [];
        var legs = new System.Collections.Generic.List<(Key[], string, double)>();
        foreach (var leg in arg.Split(','))
        {
            if (leg.Split(':') is not [var names, var time]
                || !double.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return [];
            var keys = names.Split('+').Select(n => n.ToLowerInvariant() switch
            {
                "up" => Key.Up, "down" => Key.Down, "left" => Key.Left, "right" => Key.Right, _ => Key.None,
            }).Where(k => k != Key.None).ToArray();
            legs.Add((keys, names, seconds));
        }
        return [.. legs];
    }

    /// <summary>Holds <paramref name="keys"/> (releasing the others held).</summary>
    private void Hold(Key[] keys)
    {
        if (keys.SequenceEqual(_heldKeys))
            return;
        foreach (var k in _heldKeys)
            if (!keys.Contains(k))
                Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = false });
        foreach (var k in keys)
            if (!_heldKeys.Contains(k))
                Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = true });
        _heldKeys = keys;
    }

    private void Release() => Hold([]);

    /// <summary>The player character's field position (", player at (x, y, z)"), for following a walk in the log.</summary>
    private static string PlayerAt(Snapshot? c)
    {
        if (c == null)
            return "";
        foreach (var a in c.Actors)
            if (a.Player)
                return $", player at ({a.Position.X:F0}, {a.Position.Y:F0}, {a.Position.Z:F0})";
        return "";
    }

    /// <summary>
    /// <c>--walk=KEYS:SECONDS[,KEYS:SECONDS...]</c>: each leg's keys (arrow names, joined by
    /// <c>+</c>) held for its seconds in turn, then released and 2 s more; true while walking,
    /// null without the option. Its clock runs only while called (a shown map, 3 s after it came):
    /// a map change pauses it, the keys released until the next map has settled.
    /// </summary>
    private bool? Walk(double delta)
    {
        var legs = Legs();
        if (legs.Length == 0)
            return null;
        double t = _walkT, total = legs.Sum(l => l.Seconds);
        _walkT += delta;
        // --shots=N with a shot: N - 1 pictures during the walk, evenly spaced to its end
        // (NAME-1.png, ...), the last one (NAME.png) after it
        if (shot != null && DisplayServer.GetName() != "headless" && int.TryParse(GameSettings.Arg("shots"), out var n) && n > 1
            && _walkShots < n - 1 && t >= (_walkShots + 1) * total / (n - 1))
        {
            _walkShots++;
            var file = shot[..^4] + $"-{_walkShots}.png";
            (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage().SavePng(file);
            var cur = GameHost.Instance?.View.Current;
            var p = GameProjection.Last;
            GD.Print($"ffx selftest: picture {file} (walking, {t:F2} s, {cur?.World.Level}{PlayerAt(cur)}, camera offset ({p.Offset.X:F3}, {p.Offset.Y:F3}))");
        }
        double end = 0;
        int leg = -1;
        for (int i = 0; i < legs.Length; i++)
        {
            end += legs[i].Seconds;
            if (t < end)
            {
                leg = i;
                break;
            }
        }
        var keys = leg >= 0 ? legs[leg].Keys : [];
        if (!keys.SequenceEqual(_heldKeys))
        {
            if (leg >= 0)
                GD.Print($"ffx selftest: walking {legs[leg].Names} for {legs[leg].Seconds:F1} s (at {t:F2} s, {GameHost.Instance?.View.Current?.World.Level}{PlayerAt(GameHost.Instance?.View.Current)})");
            else
                GD.Print($"ffx selftest: walked {string.Join(",", legs.Select(l => $"{l.Names}:{l.Seconds:F1}"))} ({GameHost.Instance?.View.Current?.World.Level}{PlayerAt(GameHost.Instance?.View.Current)})");
            Hold(keys);
        }
        return t < total + 2;
    }

    /// <summary>The 2D layers, when the map has any: loaded, shown, the 2D scene and the pan (read from the game, or "not read").</summary>
    private static string Layers(StateView3D view)
    {
        var l = view.Layers.State;
        var p = GameProjection.Last;
        var camera = $", camera aspect {p.Aspect:F3}{(p.Squeezed ? " squeezed" : "")} offset ({p.Offset.X:F3}, {p.Offset.Y:F3}), {GameProjection.Mismatched} mismatched";
        return l.Layers == 0 ? camera : $", 2D layers {l.Shown}/{l.Layers} (scene {l.Scene}, pan {(l.Live ? l.Pan.ToString("F3") : "not read")})" + camera;
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
/// (with <c>--shot=FILE.png</c> under a renderer, a picture). With <c>--to-results</c> it
/// attacks on until the battle is won and reports the results screen (<see cref="BattleEndView"/>).
/// The HUD pops the game's own damage numbers (API 12's <c>Effects.numbers</c>): the check
/// collects them from the snapshots itself, checks each HP number against the HP its battler
/// lost in that update (all it had when the number is more: a killing blow's overkill) and every
/// HP change against a number, and fails when the HUD popped other numbers than the game's, or
/// (with <c>--to-results</c>) no killing blow with overkill popped.
/// </summary>
public sealed class BattleTest : ITest
{
    /// <summary>The characters' cloth as the last rebuild placed it (<see cref="ClothView"/>), for the checks' notes.</summary>
    public static string ClothNote()
    {
        if (!ClothView.Enabled)
            return ", cloth off";
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        return view == null ? "" : $", cloth: {view.Cloth.Shown} ({view.Cloth.MovingUpdates} update(s) with moving cloth, "
            + $"{view.Cloth.Late} of {view.Cloth.Updates} frame(s) after their snapshot)";
    }

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
    private string _target = "", _btLine = "";
    private bool _targetClosed;
    private int _targetShot, _stillShots;
    private double _stillShotAt = -1;
    /// <summary>The effect this battle starts first (seen in the deck's battle 3,0,0), remade by the example.</summary>
    public const string RemadeId = "50001800";
    private double _tap;

    /// <summary>The example remade effect (a ring; docs/remake.md).</summary>
    public const string ExampleEffect = "res://remake/examples/stand_in_effect.tscn";
    private bool _down;

    public string Note { get; private set; } = "";

    // the game's damage numbers seen in the snapshots (each should pop), checked against the HP
    // the battlers lost: the hits, the monsters' among them, the kills, those with overkill
    private readonly System.Collections.Generic.Dictionary<int, int> _hp = new();
    private readonly System.Collections.Generic.HashSet<BattleHudView.NumberKey> _gameKeys = new();
    private readonly System.Collections.Generic.List<(BattleHudView.NumberKey Key, string Text)> _gameNumbers = new();
    private int _hits, _monsterHits, _kills, _overkills, _checked;
    private string _wrong = "";
    private uint _hitsOf;

    private void CountHits(GdFfx.Api.Snapshot? c)
    {
        if (c == null || !c.World.Battle || c.Update == _hitsOf)
            return;
        // the HP checks need the update before (headless Godot sees every update)
        bool next = c.Update == _hitsOf + 1;
        _hitsOf = c.Update;
        var had = new System.Collections.Generic.Dictionary<int, int>(_hp);
        var now = new System.Collections.Generic.Dictionary<int, int>();
        foreach (var b in c.Battlers)
            now[b.Index] = b.Hp;
        // the game's new numbers: an HP loss or heal (no word) is what the battler lost in this
        // update (all it had when the number is more: a killing blow's overkill)
        var numbered = new System.Collections.Generic.HashSet<int>();
        foreach (var n in c.Effects.Numbers)
        {
            var key = BattleHudView.KeyOf(c, n);
            if (!_gameKeys.Add(key))
                continue;
            if (BattleHudView.ActorOf(c, n.Battler) == null)
                continue;
            var word = BattleHudView.WordText(n.Word);
            _gameNumbers.Add((key, $"{(n.Digits > 0 ? n.Value.ToString() : "")}{(n.Digits > 0 && word.Length > 0 ? " " : "")}{word} on 0x{n.Battler:x}"));
            if (n.Word != 0 || n.Digits == 0 || n.Colour > 1)
                continue;
            _hits++;
            if (n.Battler >= BattleHudView.FirstMonster)
                _monsterHits++;
            numbered.Add(n.Battler);
            if (!next || n.Age != 0 || !had.TryGetValue(n.Battler, out var was))
                continue;
            int left = now.TryGetValue(n.Battler, out var hp) ? hp : 0;
            if (left == 0)
                _kills++;
            if (n.Colour == 0)
            {
                if (was - left != System.Math.Min(n.Value, was))
                    _wrong = $"the game's number {n.Value} on 0x{n.Battler:x}, which went from {was} to {left} HP";
                if (n.Value > was)
                    _overkills++;
            }
            else if (left - was > n.Value)
                _wrong = $"the game's heal {n.Value} on 0x{n.Battler:x}, which went from {was} to {left} HP";
            _checked++;
        }
        // every HP change has its number (a battler that left the list: with HP left, a blow)
        if (next)
            foreach (var (index, was) in had)
            {
                int left = now.TryGetValue(index, out var hp) ? hp : 0;
                if (was != left && !numbered.Contains(index) && BattleHudView.ActorOf(c, index) != null)
                    _wrong = $"0x{index:x} went from {was} to {left} HP without a number of the game's";
            }
        _hp.Clear();
        foreach (var (index, hp) in now)
            _hp[index] = hp;
    }

    /// <summary>The game's numbers in the order they came (for the report).</summary>
    private string GameNumbers() => string.Join(", ", _gameNumbers.Select(n => n.Text));

    /// <summary>
    /// The HUD's pops against the game's numbers (null when they agree): every number of the
    /// game's popped, nothing else, and every HP change with its number.
    /// </summary>
    private string? PopsMismatch(BattleHudView? hud)
    {
        if (hud == null)
            return "no battle HUD";
        if (_wrong.Length > 0)
            return _wrong;
        var popped = new System.Collections.Generic.HashSet<BattleHudView.NumberKey>(hud.PoppedNumbers);
        // a number of the last two updates may not have reached the HUD yet
        foreach (var (key, text) in _gameNumbers)
            if (!popped.Contains(key) && _hitsOf - key.Born > 2)
                return $"the game's number {text} (update {key.Born}) not popped: {hud.Popped} popped of the game's {_gameNumbers.Count} [{GameNumbers()}]";
        foreach (var key in hud.PoppedNumbers)
            if (!_gameKeys.Contains(key) && _hitsOf - key.Born > 2)
                return $"popped {key}, which is not a number of the game's [{GameNumbers()}]";
        return null;
    }

    /// <summary>What the HUD popped against the game's numbers (for the report).</summary>
    private string Pops(BattleHudView? hud) =>
        $"{hud?.Popped} damage number(s) popped, the game's {_gameNumbers.Count} [{GameNumbers()}]; {_hits} hits, "
        + $"{_checked} checked against the HP lost, {_kills} kills, {_overkills} with overkill ({hud?.PoppedKills} popped)";

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        CountHits(v.Current);
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        var menu = root?.FindChild("BattleMenuView", true, false) as BattleMenuView;
        var view3d = root?.FindChild("StateView3D", true, false) as StateView3D;
        var hud = root?.FindChild("BattleHudView", true, false) as BattleHudView;
        var c = v.Current;
        if (_total > timeout)
            return Done(false, $"timed out: battle {c?.World.Battle}, level {c?.World.Level}, map shown {view3d?.MapLevel}, "
                + $"models {view3d?.Posed.Models}, menu [{string.Join(", ", menu?.Shown ?? [])}]");
        if (_attacked.Length > 0 && c != null)
        {
            if (PopsMismatch(hud) is { } off)
                return Done(false, $"{off}; {_attacked}");
            return Results(c, root, delta);
        }
        if (_first.Length > 0 && c != null && menu != null)
            Target(c, menu, root);
        if (c == null || menu == null || view3d == null || hud == null || !c.World.Battle || (menu.Shown.Count < 2 && _first.Length == 0)
            || view3d.MapLevel != c.World.Level || view3d.Posed.Models < 2 || hud.Shown.Count == 0 || hud.Order.Length == 0)
            return null;
        if (_seen < 0)
            _seen = _total;
        if (_total - _seen < 0.3)
            return null;
        // --shots=N: N - 1 pictures 0.1 s apart first (NAME-1.png, ...), then NAME.png
        if (_first.Length == 0 && shot != null && DisplayServer.GetName() != "headless"
            && int.TryParse(GameSettings.Arg("shots"), out var shots) && _stillShots < shots - 1)
        {
            if (_total - _stillShotAt >= 0.1)
            {
                _stillShotAt = _total;
                var file = shot[..^4] + $"-{++_stillShots}.png";
                root!.GetViewport().GetTexture().GetImage().SavePng(file);
                GD.Print($"ffx selftest: picture {file}");
            }
            return null;
        }
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
        if (PopsMismatch(hud) is { } wrong)
            return Done(false, $"{wrong}; {_first}");
        // the target chosen (the selection seen, then closed by the confirm), and the attack's
        // damage number (a monster's HP or its kill)
        if (_target.Length > 0 && _targetClosed && _monsterHits > 0)
        {
            var attacked = $"{_first}; {_target}; {Pops(hud)}; {effects?.Started ?? 0} effect(s) started "
                + $"({effects?.RemadeStarted ?? 0} remade as {RemadeId}); effect draws by pass [{string.Join(", ", EffectDrawView.PassDraws)}], "
                + $"{EffectDrawView.FadeFrames} frame(s) with a screen fade{ClothNote()}";
            if (!GameSettings.Flag("to-results"))
            {
                Tap(false);
                return Done(true, attacked);
            }
            _attacked = attacked;
            GD.Print($"ffx selftest: {attacked}; attacking until the results");
        }
        _tap += delta;
        if (_tap > 0.25)
        {
            _tap = 0;
            Tap(!_down);
        }
        return null;
    }

    /// <summary>
    /// The first confirm (Attack) opens the target selection: the native menu names the target
    /// and hands point at it (API 11's <c>Menus.battle_target</c>); traced on each change.
    /// </summary>
    private void Target(GdFfx.Api.Snapshot c, BattleMenuView menu, Window? root)
    {
        var bt = c.Menus.BattleTarget;
        var popped = (root?.FindChild("BattleHudView", true, false) as BattleHudView)?.Popped ?? -1;
        var btLine = $"popped {popped}; open {bt.Open} kind {bt.Kind} chr 0x{bt.Character:x} mask 0x{bt.Mask:x} cursor {bt.Cursor}/{bt.Count} side {bt.Side} hands {bt.Hands.Count}; depth {c.Menus.BattleDepth}; shown [{menu.TargetShown}] hands drawn {menu.HandsShown}";
        if (btLine != _btLine)
        {
            _btLine = btLine;
            GD.Print($"ffx selftest: battle target: {btLine}");
        }
        if (_target.Length > 0 && !bt.Open)
            _targetClosed = true;
        // the picture a few frames after the selection is first seen (the frame read is the last one drawn)
        if (_targetShot > 0 && --_targetShot == 0 && shot != null)
        {
            var file = shot[..^4] + "-target.png";
            root!.GetViewport().GetTexture().GetImage().SavePng(file);
            GD.Print($"ffx selftest: picture {file}");
        }
        if (_target.Length > 0 || menu.TargetShown.Length == 0 || !bt.Open || menu.HandsShown == 0)
            return;
        _target = $"the target selection: {menu.TargetShown} (character 0x{bt.Character:x}, mask 0x{bt.Mask:x}, side {bt.Side}); menu [{string.Join(" | ", menu.Shown)}]";
        GD.Print($"ffx selftest: {_target}");
        if (shot != null && DisplayServer.GetName() != "headless")
            _targetShot = 4;
    }

    private string _attacked = "";
    private double _resultsAt = -1;
    private uint _firstAp, _firstGilLeft, _firstState;
    private bool _stateChanged;
    private uint _firstGil = uint.MaxValue;
    private readonly System.Collections.Generic.List<uint> _states = new();
    private string _lastView = "", _apView = "", _gilView = "";
    private double _gilAt = -1;
    private string _resultsFirst = "";

    private static uint TotalAp(GdFfx.Api.BattleEnd e)
    {
        uint t = 0;
        for (int i = 0; i < 18; i++)
            t += BattleEndView.Ap(e, i);
        return t;
    }

    /// <summary>
    /// <c>--to-results</c>: confirm (Enter) tapped on until the battle is won and the results
    /// screen runs (as ffx-rust's test wins the deck's battle with Circle); the AP count down
    /// by themselves, then confirm is tapped once a second until the gil is paid (at most 20 s): the rows, the AP counted down, the gil and the spoils as
    /// <see cref="BattleEndView"/> shows them.
    /// </summary>
    private bool? Results(GdFfx.Api.Snapshot c, Window? root, double delta)
    {
        var view = root?.FindChild("BattleEndView", true, false) as BattleEndView;
        var e = c.Menus.BattleEnd;
        if (_resultsAt < 0)
        {
            if (view == null || !BattleEndView.Open(c) || view.Shown.Count == 0)
            {
                _tap += delta;
                if (_tap > 0.25)
                {
                    _tap = 0;
                    Tap(!_down);
                }
                return null;
            }
            Tap(false);
            _resultsAt = _total;
            _firstAp = TotalAp(e);
            _firstGilLeft = e.GilLeft;
            _firstState = e.State;
            _resultsFirst = $"state 0x{e.State:x}: [{string.Join(" | ", view.Shown)}]; {view.Gil}; {view.Spoils}";
            GD.Print($"ffx selftest: the results screen: {_resultsFirst}");
            return null;
        }
        _stateChanged |= e.State != _firstState;
        if (_firstGil == uint.MaxValue)
            _firstGil = c.Save.Gil;
        if (_states.Count == 0 || _states[^1] != e.State)
            _states.Add(e.State);
        if (BattleEndView.Open(c))
        {
            if (TotalAp(e) == 0 && _apView.Length == 0)
                _apView = $"state 0x{e.State:x}: [{string.Join(" | ", view?.Shown ?? [])}]";
            _lastView = $"state 0x{e.State:x}: [{string.Join(" | ", view?.Shown ?? [])}]; {view?.Gil}; {view?.Spoils}";
            if (e.GilWon > 0 && e.GilLeft == 0 && c.Save.Gil >= _firstGil + e.GilWon && _gilAt < 0)
            {
                _gilAt = _total;
                _gilView = _lastView;
            }
        }
        double on = _total - _resultsAt;
        // the AP count needs no input; then the screen waits for confirm (state 0xe) before the
        // gil and the spoils: confirm tapped once a second from 4 s on, until the gil is paid
        bool paid = _gilAt >= 0 && _total - _gilAt > 1;
        if (BattleEndView.Open(c) && !paid && on < 20)
        {
            if (shot != null && on >= 1.5 && on - delta < 1.5 && DisplayServer.GetName() != "headless")
            {
                root!.GetViewport().GetTexture().GetImage().SavePng(shot);
                GD.Print($"ffx selftest: picture {shot}");
            }
            if (TotalAp(e) == 0 && on > 4)
            {
                bool want = (on - 4) % 1.0 < 0.25;
                if (want != _down)
                    Tap(want);
            }
            return null;
        }
        if (_down)
            Tap(false);
        uint ap = TotalAp(e);
        var hud = root?.FindChild("BattleHudView", true, false) as BattleHudView;
        var mismatch = PopsMismatch(hud);
        bool pass = _apView.Length > 0 && _gilAt >= 0 && _stateChanged && _overkills > 0 && hud?.PoppedKills > 0 && mismatch == null;
        return Done(pass, $"{_attacked}; the battle's damage numbers: {Pops(hud)}{(mismatch != null ? $"; {mismatch}" : "")}; "
            + $"the results screen: {_resultsFirst}; the AP counted: {_apView}; the gil paid: {_gilView} "
            + $"(states {string.Join(" ", _states.Select(x => $"0x{x:x}"))}; AP left {_firstAp} -> {ap}, gil left {_firstGilLeft} -> 0, party gil {_firstGil} -> {c.Save.Gil}, "
            + $"most AP shown {view?.MostAp}, level-up flash frames {view?.Flashes}, state changed {_stateChanged})");
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

    /// <summary>
    /// The joined party's weapons named as the game names gear (<c>MsGetSaveWeaponName</c>: API
    /// 13's <c>PartyMember.weapon_gear</c>, <see cref="FfxGame.GearRecord"/>, then <see cref="FfxGame.WeaponName"/>):
    /// the self-check's look at the gear names the results screen shows.
    /// </summary>
    private static string Weapons(GdFfx.Api.Snapshot s)
    {
        var game = GameHost.Instance?.Game;
        if (game == null)
            return "weapons: no game";
        var lang = MessageView.LanguageCode(s.World.Language);
        var names = new System.Collections.Generic.List<string>();
        for (int i = 0; i < s.Save.Members.Count && i < 7; i++)
        {
            var m = s.Save.Members[i];
            if (!m.Joined || m.Weapon == 0xff)
                continue;
            var record = FfxGame.GearRecord(m.WeaponGear);
            var n = record < 0 ? "" : MessageView.Clean(game.WeaponName(record, lang));
            names.Add($"{FieldMenuView.MemberName(game, s, i)} {(n.Length > 0 ? n : $"slot {m.Weapon}: no name")}");
        }
        return $"weapons: [{string.Join(" | ", names)}]";
    }

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
            _main = $"the menu: [{string.Join(" | ", options.Shown)}]; the party: [{string.Join(" | ", panel.Shown)}]; {panel.Footer}; {Weapons(c)}";
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
            return Done(grid.Shown.Marked > 0 && grid.CursorName.Length > 0,
                $"{_main}; the sphere grid: {grid.Shown.Nodes} nodes, {grid.Shown.Links} links, {grid.Shown.Marked} marked for character {c.SphereGrid.Current}, "
                + $"the cursor on node {c.SphereGrid.Cursor} ({grid.CursorName})");
        }
        if (screen == "config")
            return Config(c, panel, delta);
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

    private double _key;
    private bool _keyDown;
    private double _configAt = -1;

    /// <summary>
    /// The config screen: down the main menu's options to the config entry (option id 8,
    /// <c>TkMenuMainOpenOption</c> opens slot 0xa, as ffx-rust's test picks it), confirm, then
    /// the native panel's rows; one Down moves the game's cursor to the next row.
    /// </summary>
    private bool? Config(Snapshot c, FieldMenuView panel, double delta)
    {
        void Press(Key key, bool d) => Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = d });
        _key += delta;
        _probe += delta;
        var cfg = c.Menus.Config;
        if (_configAt < 0 && !(cfg.Open && panel.Config.Count > 0))
        {
            if (_probe > 20)
                return Done(false, $"the config screen did not come (mask {c.Menus.MainMask:x}, cursor {c.Menus.MainCursor}); {_main}");
            if (_key < 0.3)
                return null;
            _key = 0;
            if (_keyDown)
            {
                Press(Key.Down, false);
                Press(Key.Enter, false);
                _keyDown = false;
                return null;
            }
            if ((c.Menus.MainMask & 2) == 0)
                return null;
            int target = c.Menus.MainOptionList.FindIndex(o => o.Id == 8);
            if (target < 0)
                return Done(false, $"no config entry (option id 8) in the main menu: [{string.Join(", ", c.Menus.MainOptionList.Select(o => $"0x{o.Id:x}"))}]");
            Press(c.Menus.MainCursor == target ? Key.Enter : Key.Down, true);
            _keyDown = true;
            return null;
        }
        if (_configAt < 0)
        {
            _configAt = _probe;
            _first = $"cursor {cfg.Cursor}: [{string.Join(" | ", panel.Config)}]";
        }
        double t = _probe - _configAt, tw = t - delta;
        if (tw < 1.0 && t >= 1.0) Press(Key.Down, true);
        if (tw < 1.3 && t >= 1.3) Press(Key.Down, false);
        if (t < 2.5)
            return null;
        if (shot != null && DisplayServer.GetName() != "headless")
            ((Engine.GetMainLoop() as SceneTree)?.Root)!.GetViewport().GetTexture().GetImage().SavePng(shot);
        var at = panel.Config.FindIndex(r => r.StartsWith("▶"));
        return Done(cfg.Rows.Count > 1 && at == cfg.Cursor && panel.Config.Count == cfg.Rows.Count,
            $"{_main}; the config screen ({cfg.Rows.Count} rows, PC row {cfg.PcRow}), {_first}; one down: cursor {cfg.Cursor} on [{(at >= 0 ? panel.Config[at] : "")}]");
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
