using System.Collections.Generic;
using System.Linq;
using GdFfx.Api;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R5's check of the game's data in the running game (<c>--selftest-alignment</c>; run with
/// <c>--devsave=7</c>): Besaid beach's game data from the archive (<see cref="MapGameData"/>:
/// the walk mesh, entrances, triggers, the exit to the next map) against the game: the player
/// stands on the walk mesh (the units and axes line up); the capture for the editor
/// (<see cref="MapCapture"/>, written into the check's own folder) with the birds where the game
/// flies them, the camera, the characters; then the alignment check (<see cref="Alignment"/>) on
/// R1a's example additions and replacement (registered as in <c>--selftest-overrides</c>) and on
/// three test pieces made at run time: a patch of the walk mesh itself (lines up: no issue), the
/// same patch 5 units low (floating) and a box 8 units over the ground (blocked); and on the
/// example remade beach (<c>res://remake/examples/stand_in_map.tscn</c>, counted only).
/// </summary>
public sealed class AlignmentTest : ITest
{
    public const string Level = OverridesTest.Level;
    public const string StandInMap = "res://remake/examples/stand_in_map.tscn";
    private const string CaptureDir = "user://selftest-alignment";

    private enum Phase { Boot, Beach, Capture, Walk }

    private readonly double _timeout;
    private Phase _phase = Phase.Boot;
    private double _phaseTime;
    private double _total;
    private readonly List<string> _seen = new();

    public string Note { get; private set; } = "";

    public AlignmentTest(double timeout)
    {
        _timeout = timeout;
        if (GameSettings.Arg("remaster-captures") is not { Length: > 0 })
        {
            Clean();
            MapCapture.DirOverride = CaptureDir;
        }
        RemasterOverrides.Register(Level, OverridesTest.Example, "selftest");
        GD.Print($"ffx selftest: overrides {OverridesTest.Example} registered for {Level}; captures into {MapCapture.Dir}");
    }

    private static void Clean()
    {
        var dir = GameSettings.Globalize(CaptureDir);
        if (!DirAccess.DirExistsAbsolute(dir))
            return;
        foreach (var f in DirAccess.GetFilesAt(dir))
            DirAccess.RemoveAbsolute($"{dir}/{f}");
        DirAccess.RemoveAbsolute(dir);
    }

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > _timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)}; {string.Join("; ", _seen)})");
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        switch (_phase)
        {
            case Phase.Boot when v.Current is { World.Map: BesaidTest.MapBeach, World.PlayerControl: true }:
                Next(Phase.Beach);
                break;
            case Phase.Beach when view != null && view.MapLevel == v.Current!.World.Level && view.MapNode != null && _phaseTime > 1.0:
                if (CheckData(game, v, view) is { } dataFail)
                    return Done(false, dataFail);
                Next(Phase.Capture);
                break;
            case Phase.Capture when FileAccess.FileExists(MapCapture.PathOf(Level)):
            {
                if (CheckCapture() is { } captureFail)
                    return Done(false, captureFail);
                if (CheckAlignment(game, v, view!) is { } fail2)
                    return Done(false, fail2);
                _walkFrom = GameStateView.FieldPoint(v.Current!.Actors.First(a => a.Player).Position);
                Keys(true);
                Next(Phase.Walk);
                break;
            }
            case Phase.Walk when _phaseTime > 1.2:
            {
                // the facing convention: the player turns the way they walk
                Keys(false);
                var p = v.Current!.Actors.First(a => a.Player);
                var moved = GameStateView.FieldPoint(p.Position) - _walkFrom;
                moved.Y = 0;
                if (moved.Length() < 5)
                    return Done(false, $"the player walked {moved.Length():F1} units in 1.2 s");
                var facing = MapGameData.Facing(p.Yaw);
                var angle = Mathf.RadToDeg(moved.Normalized().AngleTo(facing));
                _seen.Add($"walked {moved.Length():F0} units: the facing from the game's yaw is {angle:F0}° off the walk");
                if (angle > 30)
                    return Done(false, $"the facing from the yaw is {angle:F0}° off the way the player walks: {string.Join("; ", _seen)}");
                return Done(true, string.Join("; ", _seen));
            }
            case Phase.Capture when _phaseTime > 15:
                return Done(false, $"no capture in {MapCapture.PathOf(Level)} after 15 s");
        }
        return null;
    }

    private MapGameData? _data;

    private string? CheckData(FfxGame game, GameStateView v, StateView3D view)
    {
        var t0 = Time.GetTicksUsec();
        _data = MapGameData.Read(game, Level);
        var us = Time.GetTicksUsec() - t0;
        if (_data?.Main is not { } walk)
            return "no game data or no walk mesh for Besaid beach";
        var triggers = _data.Workers().Count(w => w.IsTrigger);
        var lines = _data.Workers().Count(w => w.Line != null);
        var entrances = _data.Events.Sum(e => e.Entrances.Count);
        var actors = _data.Workers().Count(w => w.Type == 1 && w.Position != null);
        if (walk.Count < 500 || walk.Vertices.Length < 300)
            return $"the walk mesh has {walk.Count} triangles over {walk.Vertices.Length} vertices (Besaid beach: 839, 518)";
        if (entrances < 3 || triggers < 10 || lines < 10 || actors < 5)
            return $"too little game data: {entrances} entrances, {triggers} triggers ({lines} lines), {actors} actors placed";
        var exit = _data.Workers().FirstOrDefault(w => w.Exits.Any(e => e.Map == BesaidTest.MapNext) && w.Line != null);
        if (exit == null)
            return $"no line trigger leading to map {BesaidTest.MapNext:x}";
        _seen.Add($"game data in {us / 1000.0:F1} ms: walk mesh {walk.Count} triangles, {entrances} entrances, {triggers} triggers, "
            + $"{actors} actors placed, the exit to {BesaidTest.MapNext:x} is worker {exit.Event}/{exit.Index}");
        // the player on the walk mesh: the units and the axes line up
        var player = v.Current!.Actors.FirstOrDefault(a => a.Player);
        if (player == null)
            return "no player";
        var feet = GameStateView.FieldPoint(player.Position);
        if (walk.Under(feet, out var h) < 0)
            return $"the player ({feet}) is over no walk-mesh triangle";
        if (Mathf.Abs(h - feet.Y) > 1.5f)
            return $"the player's feet are {feet.Y - h:F2} units off the walk mesh";
        // every shown character over the walk mesh, and on it
        int over = 0, on = 0, shown = 0;
        foreach (var a in v.Current.Actors)
        {
            if (a.Hidden != 0 || a.Kind is 4 or 5)
                continue;
            shown++;
            var p = GameStateView.FieldPoint(a.Position);
            if (walk.Under(p, out var y) >= 0)
            {
                over++;
                if (Mathf.Abs(y - p.Y) <= 1.5f)
                    on++;
            }
        }
        _seen.Add($"the player {feet.Y - h:F2} units from the walk mesh; {on} of {shown} shown characters on it ({over} over it)");
        return null;
    }

    private string? CheckCapture()
    {
        var c = MapCapture.Read(Level);
        if (c == null)
            return "the capture does not read back";
        if (!c.CameraValid || c.FovY <= 0)
            return "the capture has no camera";
        if (!c.Characters.Any(a => a.Player))
            return "the capture has no player";
        var model = PartIds.ModelIdOf(Level)!;
        var bird = c.Parts.FirstOrDefault(p => p.Id == PartIds.Full(OverridesTest.MovedBird, model));
        if (bird == null)
            return $"the capture has no {OverridesTest.MovedBird} (parts: {c.Parts.Count})";
        // the file places the birds at the origin: the game flies them far from it
        if (bird.World.Origin.Length() < 50)
            return $"the captured bird is at {bird.World.Origin}, the file's place";
        _seen.Add($"captured {c.Parts.Count} placed parts (the bird {OverridesTest.MovedBird} at {bird.World.Origin.Round()}), "
            + $"{c.Morphs.Count} morph values, {c.Characters.Count} characters, the camera (fov {Mathf.RadToDeg(c.FovY):F1}°)");
        return null;
    }

    private string? CheckAlignment(FfxGame game, GameStateView v, StateView3D view)
    {
        var walk = _data!.Main!;
        var space = view.GlobalTransform;
        // R1a's example: its additions and its replacement, as the game draws them
        var example = new List<Surface>();
        if (view.Overrides is { } o)
        {
            foreach (var (id, n) in o.AddedNodes)
                Alignment.Collect(n, space, example, id);
            foreach (var (id, n) in o.Replacements)
                Alignment.Collect(n, space, example, id);
        }
        var r = Alignment.Check(walk, example);
        _seen.Add($"R1a's example: {r.Summary()}");
        // the test pieces, beside the player
        var feet = GameStateView.FieldPoint(v.Current!.Actors.First(a => a.Player).Position);
        var holder = new Node3D { Name = "AlignmentTest" };
        view.AddChild(holder);
        var aligned = Alignment.Patch(walk, feet, 60f, 0f);
        var sunken = Alignment.Patch(walk, feet + new Vector3(150, 0, 0), 60f, -5f);
        if (aligned == null || sunken == null)
        {
            holder.QueueFree();
            return "no walk mesh around the player for the test patches";
        }
        holder.AddChild(new MeshInstance3D { Name = "AlignedPatch", Mesh = aligned });
        holder.AddChild(new MeshInstance3D { Name = "SunkenPatch", Mesh = sunken });
        // the box over a walkable triangle about 100 units from the player, away from the patches
        int boxTri = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < walk.Count; i++)
        {
            var m = walk.Centre(i);
            if (!WalkMesh.Walkable(walk.Kind(i)) || m.X > feet.X - 40)
                continue;
            var d = Mathf.Abs(new Vector2(m.X - feet.X, m.Z - feet.Z).Length() - 100f);
            if (d < bestD)
                (bestD, boxTri) = (d, i);
        }
        if (boxTri < 0)
        {
            holder.QueueFree();
            return "no walkable triangle for the misplaced box";
        }
        var boxAt = walk.Centre(boxTri);
        holder.AddChild(new MeshInstance3D
        {
            Name = "MisplacedBox",
            Mesh = new BoxMesh { Size = new Vector3(30, 4, 30) },
            Position = boxAt + Vector3.Up * (8f + 2f),
        });
        var test = new List<Surface>(example);
        for (int i = 0; i < holder.GetChildCount(); i++)
            Alignment.Collect(holder.GetChild(i), space, test);
        var t = Alignment.Check(walk, test);
        holder.QueueFree();
        var by = t.BySource();
        int Of(string name) => by.Where(kv => kv.Key.StartsWith(name)).Sum(kv => kv.Value);
        int alignedIssues = Of("AlignedPatch"), sunkenIssues = Of("SunkenPatch"), boxIssues = Of("MisplacedBox");
        var sunkenKinds = t.Issues.Where(i => i.Source.StartsWith("SunkenPatch")).Select(i => i.Kind).Distinct().ToList();
        var boxKinds = t.Issues.Where(i => i.Source.StartsWith("MisplacedBox")).Select(i => i.Kind).Distinct().ToList();
        _seen.Add($"test pieces: aligned patch {alignedIssues} issue(s), sunken patch {sunkenIssues} ({string.Join(",", sunkenKinds)}), "
            + $"misplaced box {boxIssues} ({string.Join(",", boxKinds)}); {t.Summary()}");
        if (alignedIssues != 0)
            return $"the patch of the walk mesh itself has {alignedIssues} issue(s): {string.Join("; ", _seen)}";
        if (sunkenIssues == 0 || sunkenKinds.Any(k => k != AlignmentKind.Floating))
            return $"the sunken patch is not reported as floating: {string.Join("; ", _seen)}";
        if (boxIssues == 0 || boxKinds.Any(k => k != AlignmentKind.Blocked))
            return $"the misplaced box is not reported as blocking: {string.Join("; ", _seen)}";
        // the example remade beach (not drawn: collected and freed in this frame)
        if (ResourceLoader.Load(StandInMap) is PackedScene ps && ps.Instantiate() is Node3D standIn)
        {
            view.AddChild(standIn);
            var surfaces = new List<Surface>();
            Alignment.Collect(standIn, space, surfaces);
            view.RemoveChild(standIn);
            standIn.Free();
            var sr = Alignment.Check(walk, surfaces);
            _seen.Add($"the example remade beach: {sr.Summary()}");
        }
        return null;
    }

    private Vector3 _walkFrom;

    private static void Keys(bool down)
    {
        Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Up, Keycode = Key.Up, Pressed = down });
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
        if (MapCapture.DirOverride == CaptureDir)
            Clean();
        return pass;
    }
}
