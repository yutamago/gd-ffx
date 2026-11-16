using System.Linq;
using GdFfx.Api;
using Godot;

namespace GdFfx.Remake;

/// <summary>
/// G6's map check (<c>--selftest-remake-map</c>; run with <c>--devsave=7</c>): registers the
/// example remade Besaid beach (<see cref="Scene"/>: sand, sea and palms made of Godot's
/// shapes, no game data) for the beach's level at run time, boots there and checks that the
/// remade map is drawn instead of the classic one, that the game's characters stand on its
/// sand (the player's feet within <see cref="Ground"/> units of it), then walks the player off
/// the beach as <see cref="BesaidTest"/> does: the game does the walking (its own collision),
/// the remade map only draws. With <c>--shot=FILE.png</c> under a renderer, a picture.
/// </summary>
public sealed class RemakeMapTest : ITest
{
    public const string Level = "/map/bsil/bsil00/";
    public const string Scene = "res://remake/examples/stand_in_map.tscn";

    /// <summary>How far the player's feet may be from the example's sand (it is a plane fitted to the characters' places).</summary>
    public const float Ground = 4f;

    /// <summary>The example's sand: y = a x + b z + c in render space (fitted to Besaid's characters).</summary>
    public static float SandY(float x, float z) => -0.0422f * x - 0.0690f * z - 10f;

    private enum Phase { Boot, Beach, Shot, Walk }

    private readonly double _timeout;
    private readonly string? _shot;
    private Phase _phase = Phase.Boot;
    private double _phaseTime;
    private double _total;
    private string _seen = "";
    private Vector3 _start;
    private Vector3 _last;

    public string Note { get; private set; } = "";

    public RemakeMapTest(double timeout, string? shot)
    {
        _timeout = timeout;
        _shot = shot;
        RemakeRegistry.Register("map", Level, Scene, "selftest-remake-map");
        GD.Print($"ffx selftest: map {Level} registered to {Scene}");
    }

    private static Actor? Player(GameStateView v) => v.Current?.Actors.FirstOrDefault(a => a.Player);

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > _timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)}; {_seen})");
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        switch (_phase)
        {
            case Phase.Boot when v.Current is { World.Map: BesaidTest.MapBeach, World.PlayerControl: true }:
                Next(Phase.Beach);
                break;
            case Phase.Beach when view != null && view.MapLevel == v.Current!.World.Level && _phaseTime > 1.0:
            {
                var map = view.GetChildren().OfType<Node3D>().FirstOrDefault(n => n.HasMeta("ffx_kind") && n.GetMeta("ffx_kind").AsString() == "map");
                if (map == null)
                    return Done(false, "no map drawn");
                var source = map.GetMeta("ffx_source").AsString();
                if (source != "remade" || map.GetMeta("ffx_path").AsString() != Scene)
                    return Done(false, $"the map is {source} ({map.GetMeta("ffx_path")}), not the remade {Scene}");
                if (map.Scale != Vector3.One)
                    return Done(false, $"the remade map is scaled {map.Scale} (remade maps are in render space)");
                var p = Player(v);
                if (p == null)
                    return Done(false, "no player");
                var feet = GameStateView.FieldPoint(p.Position);
                var off = feet.Y - SandY(feet.X, feet.Z);
                if (Mathf.Abs(off) > Ground)
                    return Done(false, $"the player stands {off:F1} units off the remade sand");
                if (view.Posed.Models == 0)
                    return Done(false, "no character drawn on the remade map");
                _seen = $"{v.Current.World.Level} drawn by the remade {Scene}; the player {off:F1} units from its sand, "
                    + $"{view.Posed.Models} character model(s) posed";
                Next(_shot != null && DisplayServer.GetName() != "headless" ? Phase.Shot : Phase.Walk);
                if (_phase == Phase.Walk)
                    StartWalk(v);
                break;
            }
            case Phase.Shot when _phaseTime > 1.0:
                (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage().SavePng(_shot!);
                GD.Print($"ffx selftest: picture {_shot}");
                Next(Phase.Walk);
                StartWalk(v);
                break;
            case Phase.Walk when s.Map == BesaidTest.MapNext:
            {
                Keys(false);
                var moved = _start.DistanceTo(_last);
                if (moved < 5f)
                    return Done(false, $"map {BesaidTest.MapNext:x} came without the player moving ({moved:F1} units)");
                return Done(true, $"{_seen}; walked {moved:F0} units over it to map {BesaidTest.MapNext:x} in {_phaseTime:F1} s");
            }
            case Phase.Walk:
                if (v.Current?.World.Map == BesaidTest.MapBeach && Player(v) is { } a)
                    _last = a.Position;
                if (_phaseTime > 60)
                {
                    Keys(false);
                    return Done(false, $"still on map {s.Map:x} after 60 s of walking ({_seen})");
                }
                break;
        }
        return null;
    }

    private void StartWalk(GameStateView v)
    {
        _start = Player(v)?.Position ?? Vector3.Zero;
        _last = _start;
        if (!GameSettings.Flag("no-walk"))
            Keys(true);
    }

    private static void Keys(bool down)
    {
        foreach (var k in new[] { Key.Down, Key.Left })
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
