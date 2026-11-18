using System.Collections.Generic;
using System.Linq;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R7b's check of a 2D room rebuilt in 3D (<c>--selftest-rebuild</c>, a developer save on a 2D-layer
/// map: 14 <c>ptkl14</c>, 12 <c>slik04</c>, 52 <c>kami01</c>): once the map shows its classic layers,
/// a rebuilt room is made from them (<see cref="RebuiltRoom.Create"/>, as the editor's Rebuild in 3D
/// makes it; or the editor's scene, <c>--rebuild-scene=FILE</c>), saved into a temporary folder,
/// registered for the level and the map loaded again. Then: the remade scene drawn instead of the
/// layers (every layer a plane with its projected picture, the floor), the classic layers hidden,
/// the game's camera on the scene camera the planes were made from (within 0.05 units and 0.05°),
/// the player and the shown characters on the walk mesh and so on the floor proxy, the proxies
/// shown as the game shows the layers, the alignment check on the scene (the floor on the walk
/// mesh: no issue; the floor sunk by 5 units: floating), F7 to the classic layers and back. With a
/// renderer (<c>--shot=F.png</c>, Xvfb) pictures from the game's camera, the views held at one
/// moment meanwhile (<see cref="GameStateView.Frozen"/>): <c>F.png</c> rebuilt,
/// <c>F-classic.png</c> classic, <c>F-again.png</c> rebuilt once more; the mean absolute
/// difference (0..255 per channel) between the rebuilt and the classic picture must stay under
/// <c>--rebuild-max-diff=D</c> (2.5), against the rebuilt picture's own run-to-run difference.
/// </summary>
public sealed class RebuildTest(double timeout, string? shot) : ITest
{
    private enum Phase { Setup, Boot, Create, Rebuilt, ShotRebuilt, ToClassic, ShotClassic, ToRebuilt, ShotAgain, Done }

    private readonly string _dir = ProjectSettings.GlobalizePath("user://selftest-rebuild");
    private readonly string? _scene = GameSettings.Arg("rebuild-scene");
    private Phase _phase = Phase.Setup;
    private double _phaseTime, _total;
    private bool _acted;
    private readonly List<string> _seen = new();
    private string _level = "";
    private Image? _rebuilt, _classic;
    private readonly bool _pictures = shot != null && DisplayServer.GetName() != "headless";

    public string Note { get; private set; } = "";

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)}; {string.Join("; ", _seen)})");
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        switch (_phase)
        {
            case Phase.Setup when GameHost.Instance?.Classic is { Open: true }:
                RenderPaths.Set("map");
                Next(Phase.Boot);
                break;
            case Phase.Boot when view != null && v.Current is { World.PlayerControl: true } && view.MapLevel.Length > 0
                && view.Layers.Level == view.MapLevel && view.Layers.Model != null && view.Layers.State.Layers > 0 && _phaseTime > 2:
                _level = view.MapLevel;
                // a map with a 3D model beside its layers (slik04): the rebuilt room stands in for both
                _seen.Add($"{PartIds.LevelKey(_level)}: {view.Layers.State.Layers} classic layers, {view.Layers.State.Shown} shown (scene {view.Layers.State.Scene})"
                    + (view.MapNode != null ? ", and a classic 3D model" : ""));
                Next(Phase.Create);
                break;
            case Phase.Create:
            {
                if (Create(view!) is { } f)
                    return Done(false, f);
                view!.ReloadMap();
                Next(Phase.Rebuilt);
                break;
            }
            case Phase.Rebuilt when view!.Room is { Built: true } && view.RebuiltShown && _phaseTime > 1.5:
            {
                if (CheckRebuilt(game, view, v) is { } f)
                    return Done(false, f);
                Next(_pictures ? Phase.ShotRebuilt : Phase.ToClassic);
                break;
            }
            case Phase.ShotRebuilt when !_acted:
                // the same moment in every picture (the ship's sway, the characters' steps)
                _acted = true;
                GameStateView.Frozen = true;
                break;
            case Phase.ShotRebuilt when _phaseTime > 0.5:
                _rebuilt = Shot(shot!);
                Next(Phase.ToClassic);
                break;
            case Phase.ToClassic when !_acted:
                _acted = true;
                PressToggle();
                break;
            case Phase.ToClassic when _phaseTime > 0.5:
            {
                var visible = view!.Layers.State.Shown;
                if (view.RebuiltShown || view.MapNode?.Visible == true || !LayersVisible(view))
                    return Done(false, $"F7: still rebuilt (rebuilt {view.RebuiltShown}, scene visible {view.MapNode?.Visible}, layers visible {LayersVisible(view)})");
                _seen.Add($"F7: the classic layers ({visible} shown), the rebuilt scene hidden");
                Next(_pictures ? Phase.ShotClassic : Phase.ToRebuilt);
                break;
            }
            case Phase.ShotClassic when _phaseTime > 0.5:
                _classic = Shot(shot![..^4] + "-classic.png");
                Next(Phase.ToRebuilt);
                break;
            case Phase.ToRebuilt when !_acted:
                _acted = true;
                PressToggle();
                break;
            case Phase.ToRebuilt when _phaseTime > 0.5:
                if (!view!.RebuiltShown || view.MapNode?.Visible != true || LayersVisible(view))
                    return Done(false, "F7 again: not rebuilt");
                _seen.Add("F7 again: rebuilt");
                Next(_pictures ? Phase.ShotAgain : Phase.Done);
                break;
            case Phase.ShotAgain when _phaseTime > 0.5:
            {
                var again = Shot(shot![..^4] + "-again.png");
                GameStateView.Frozen = false;
                double diff = MeanDiff(_rebuilt!, _classic!), noise = MeanDiff(_rebuilt!, again);
                double max = GameSettings.Number("rebuild-max-diff", 2.5);
                var line = System.FormattableString.Invariant($"the picture from the game's camera at one moment: rebuilt against classic {diff:F2}/255 (mean absolute), rebuilt against rebuilt {noise:F2}");
                GD.Print($"ffx selftest: rebuild: {line}");
                if (diff > max)
                    return Done(false, $"{line}: above {max}");
                _seen.Add(line);
                Next(Phase.Done);
                break;
            }
            case Phase.Done:
                return Done(true, string.Join("; ", _seen));
        }
        return null;
    }

    private static bool LayersVisible(StateView3D view) =>
        view.Layers.FindChildren("*", nameof(MeshInstance3D), true, false).Any(n => n is MeshInstance3D { Visible: true } mi && mi.IsVisibleInTree());

    /// <summary>The rebuilt scene (made from the layers, or the editor's) saved and registered.</summary>
    private string? Create(StateView3D view)
    {
        DirAccess.MakeDirRecursiveAbsolute(_dir);
        var path = $"{_dir}/{PartIds.LevelKey(_level)}.tscn";
        if (_scene != null)
        {
            if (!FileAccess.FileExists(_scene))
                return $"--rebuild-scene={_scene}: no such file";
            path = _scene;
            _seen.Add($"the editor's rebuilt scene {_scene}");
        }
        else
        {
            var root = RebuiltRoom.Create(_level, view.Layers.Model!);
            var packed = new PackedScene();
            var err = packed.Pack(root);
            root.Free();
            if (err != Error.Ok || ResourceSaver.Save(packed, path) != Error.Ok)
                return $"cannot write {path}";
            var text = FileAccess.GetFileAsString(path);
            // references only: no mesh, picture or material in the file
            if (text.Contains("ArrayMesh") || text.Contains("Texture") || text.Contains("ShaderMaterial"))
                return $"{path} holds more than references";
            _seen.Add($"made {path} ({text.Length} bytes, references only)");
        }
        RemakeRegistry.Register("map", _level, path, "selftest-rebuild");
        return null;
    }

    private string? CheckRebuilt(FfxGame game, StateView3D view, GameStateView v)
    {
        var room = view.Room!;
        int layers = view.Layers.State.Layers;
        int planes = room.Projected.Count(p => p.Mesh is RoomProxy { Kind: RoomProxyKind.Layer });
        int floors = room.Projected.Count(p => p.Mesh is RoomProxy { Kind: RoomProxyKind.Floor });
        if (planes != layers || floors < 1)
            return $"{planes} layer plane(s) for {layers} layers, {floors} floor(s) ({room.Summary})";
        if (LayersVisible(view))
            return "the classic layers still drawn under the rebuilt scene";
        if (view.MapNode?.Visible != true || view.MapNode.GetMeta("ffx_source").AsString() != "remade")
            return "the rebuilt scene is not the map drawn";
        // the proxies shown as the game shows the layers
        int planesShown = room.Projected.Count(p => p.Mesh is RoomProxy { Kind: RoomProxyKind.Layer, Visible: true });
        if (planesShown != view.Layers.State.Shown)
            return $"{planesShown} layer proxies shown, the game shows {view.Layers.State.Shown} layers";
        _seen.Add($"rebuilt: {room.Summary}; {planesShown} layer planes shown as the game shows its layers (and the floor); the classic layers hidden");
        // the game's camera on the scene camera the planes were made from
        int scene = view.Layers.State.Scene;
        if (!room.Cameras.TryGetValue(scene, out var cam))
            return $"no camera for 2D scene {scene}";
        var game3d = view.GetViewport().GetCamera3D();
        if (game3d == null)
            return "no camera";
        float dist = game3d.GlobalPosition.DistanceTo(cam.Origin);
        float angle = Mathf.RadToDeg((-game3d.GlobalBasis.Z).AngleTo(-cam.Basis.Z));
        float fov = Mathf.Abs(game3d.Fov - room.Fovs[scene]);
        if (dist > 0.05f || angle > 0.05f || fov > 0.05f)
            return $"the game's camera is {dist:F3} units, {angle:F3}° and {fov:F3}° (field of view) off the scene camera";
        _seen.Add(System.FormattableString.Invariant($"the game's camera on scene {scene}'s: {dist:F3} units, {angle:F3}°, field of view {fov:F3}° off"));
        // the characters on the walk mesh, so on the floor proxy
        if (room.Data?.Main is not { } walk)
            return "no walk mesh";
        var player = v.Current!.Actors.FirstOrDefault(a => a.Player);
        if (player == null)
            return "no player";
        var feet = GameStateView.FieldPoint(player.Position);
        if (walk.Under(feet, out var h) < 0 || Mathf.Abs(h - feet.Y) > 1.5f)
            return $"the player ({feet}) is not on the walk mesh";
        int on = 0, shown = 0;
        foreach (var a in v.Current.Actors)
        {
            if (a.Hidden != 0 || a.Kind is 4 or 5)
                continue;
            shown++;
            var p = GameStateView.FieldPoint(a.Position);
            if (walk.Under(p, out var y) >= 0 && Mathf.Abs(y - p.Y) <= 1.5f)
                on++;
        }
        _seen.Add($"the player {feet.Y - h:F2} units from the floor; {on} of {shown} shown characters on it");
        // the alignment check on the rebuilt scene: the floor on the walk mesh
        var floor = room.Projected.First(p => p.Mesh is RoomProxy { Kind: RoomProxyKind.Floor }).Mesh;
        var r = AlignWith(room, walk);
        if (r.Issues.Count != 0 || r.Covered == 0)
            return $"the alignment check on the rebuilt scene: {r.Summary()}";
        var was = floor.Position;
        floor.Position = was + Vector3.Down * 5f;
        var sunk = AlignWith(room, walk);
        floor.Position = was;
        if (sunk.Count(AlignmentKind.Floating) == 0)
            return $"the floor sunk by 5 units is not flagged: {sunk.Summary()}";
        _seen.Add($"alignment: {r.Summary()}; the floor sunk 5 units: {sunk.Count(AlignmentKind.Floating)} floating");
        return null;
    }

    private static AlignmentReport AlignWith(RebuiltRoom room, WalkMesh walk)
    {
        var surfaces = new List<Surface>();
        Alignment.Collect(room, Transform3D.Identity, surfaces, null,
            n => n.HasMeta("ffx_alignment_skip") || n is Light3D or ReflectionProbe or Camera3D or FogVolume);
        return Alignment.Check(walk, surfaces);
    }

    /// <summary>The mean absolute difference of two pictures (0..255 per channel, over RGB).</summary>
    public static double MeanDiff(Image a, Image b)
    {
        if (a.GetSize() != b.GetSize())
            return 255;
        var pa = a.GetData();
        var pb = b.GetData();
        int step = a.GetFormat() == Image.Format.Rgba8 ? 4 : 3;
        long sum = 0;
        long n = 0;
        for (int i = 0; i + 2 < pa.Length && i + 2 < pb.Length; i += step)
        {
            sum += System.Math.Abs(pa[i] - pb[i]) + System.Math.Abs(pa[i + 1] - pb[i + 1]) + System.Math.Abs(pa[i + 2] - pb[i + 2]);
            n += 3;
        }
        return n > 0 ? (double)sum / n : 255;
    }

    private static Image Shot(string file)
    {
        var img = (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage();
        img.SavePng(file);
        GD.Print($"ffx selftest: picture {file}");
        return img;
    }

    private static void PressToggle()
    {
        Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = GameHost.RemasterKey, Keycode = GameHost.RemasterKey, Pressed = true });
        Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = GameHost.RemasterKey, Keycode = GameHost.RemasterKey, Pressed = false });
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx selftest: rebuild: {_phase} -> {p} ({_total:F1} s)");
        _phase = p;
        _phaseTime = 0;
        _acted = false;
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        GameStateView.Frozen = false;
        if (_scene == null)
            DirAccess.RemoveAbsolute($"{_dir}/{PartIds.LevelKey(_level)}.tscn");
        RenderPaths.Set(null);
        return pass;
    }
}
