using System.Collections.Generic;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R3's sky rule in the game (<c>--selftest-sky</c>, any developer save; R7b's follow-up on Luca,
/// saves 22 and 29): the shown map drawn remastered, the meshes <see cref="MapLook.FindSky"/> keeps
/// classic as sky measured: their surface area (render units²) and how much of it is ground
/// (level: a normal within 45° of vertical, either winding, over the map's walk mesh and within 30
/// units of it: where the characters walk). Passes when at most 1 % of the map's ground is kept
/// classic as sky (<c>--sky-max-ground=F</c>, 0..1) and, on Besaid beach (save 7), the sky still has
/// its cloud quarters (four meshes or more). <c>--sky-level=LEVEL</c> measures that map's classic
/// import instead of the shown one (Luca's <c>lchb00</c>, which no developer save is on).
/// </summary>
public sealed class SkyTest(double timeout) : ITest
{
    private enum Phase { Setup, Boot, Remaster, Done }

    private Phase _phase = Phase.Setup;
    private double _phaseTime, _total;

    public string Note { get; private set; } = "";

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (_total > timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)})");
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        switch (_phase)
        {
            case Phase.Setup when GameHost.Instance?.Classic is { Open: true }:
                RenderPaths.Set("remastered");
                Next(Phase.Boot);
                break;
            case Phase.Boot when v.Current != null && view?.MapNode is { Name: { } n } && n == "ClassicMap" && _phaseTime > 4:
                Next(Phase.Remaster);
                break;
            case Phase.Remaster when _phaseTime > 1:
            {
                // --sky-level=LEVEL: that map's classic import loaded on its own (one no developer save is on)
                var level = GameSettings.Arg("sky-level") ?? view!.MapLevel;
                Node3D map;
                MapLook? look;
                if (level != view!.MapLevel)
                {
                    var lib = GameHost.Instance!.Classic!;
                    if (lib.Path("map", level) is not { } path)
                    {
                        if (lib.Failed("map", level))
                            return Done(false, $"{level} did not import");
                        return null; // importing
                    }
                    map = ClassicLibrary.Load(path, $"PS3Data{level}tex/")!;
                    map.Scale = Vector3.One * ClassicLibrary.MapScale;
                    map.Visible = false;
                    view.AddChild(map);
                    look = MapLook.Of(map);
                }
                else
                {
                    if (!view.Remastered)
                        return Done(false, "the map does not draw remastered");
                    map = view.MapNode!;
                    look = MapLook.Find(map);
                }
                if (look == null)
                    return Done(false, "no remastered look for the map");
                if (MapGameData.Read(game, level)?.Main is not { } walk)
                    return Done(false, "no walk mesh");
                var (sky, skyArea, skyUp) = Measure(look.SkyMeshes, walk);
                var all = new HashSet<MeshInstance3D>(ReferenceEqualityComparer.Instance);
                foreach (var mi in look.Meshes)
                    if (GodotObject.IsInstanceValid(mi))
                        all.Add(mi);
                var (_, mapArea, mapUp) = Measure(all, walk);
                float share = skyArea > 0 ? skyUp / skyArea : 0;
                float max = (float)GameSettings.Number("sky-max-ground", 0.01);
                float kept = mapUp > 0 ? skyUp / mapUp : 0;
                var key = PartIds.LevelKey(level);
                var line = System.FormattableString.Invariant(
                    $"{key}: sky {sky} mesh(es) of {all.Count}, {skyArea:F0} units² ({100 * skyArea / System.Math.Max(mapArea, 1):F1} % of the map's), ")
                    + System.FormattableString.Invariant($"{skyUp:F0} of it ground ({100 * share:F1} %); the map's ground area {mapUp:F0} ({100 * skyUp / System.Math.Max(mapUp, 1):F1} % of it kept classic as sky)");
                GD.Print($"ffx selftest: sky: {line}");
                foreach (var mi in look.SkyMeshes)
                {
                    if (!GodotObject.IsInstanceValid(mi) || mi.Mesh == null)
                        continue;
                    var (_, a, g) = Measure([mi], walk);
                    var box = mi.GlobalTransform * mi.Mesh.GetAabb();
                    string? tex = null;
                    for (int k = 0; k < mi.Mesh.GetSurfaceCount() && tex == null; k++)
                        tex = RemasterTextures.ColourId(mi.GetSurfaceOverrideMaterial(k));
                    var (_, _, flat) = MeasureLevel(mi);
                    var worlds = new Dictionary<MeshInstance3D, Transform3D>(ReferenceEqualityComparer.Instance) { [mi] = mi.GlobalTransform };
                    int sectors = MapLook.Sectors([mi], worlds, 1000f);
                    GD.Print(System.FormattableString.Invariant(
                        $"ffx selftest: sky: {mi.Name}: {tex?.GetFile()} area {a:F0}, level {flat:F0} ({100 * flat / System.Math.Max(a, 1):F0} %), on the walk mesh {g:F0}, in {sectors} of 8 sectors round the axis, box {box.Position.Round()} size {box.Size.Round()}, dome kind {MeshExtras.Of(mi.Mesh).DobjKind}"));
                }
                if (kept > max)
                    return Done(false, $"{line}: more than {100 * max:F0} % of the map's ground kept classic as sky");
                if (key == "bsil00" && sky < 4)
                    return Done(false, $"{line}: Besaid's cloud quarters are not sky");
                return Done(true, line);
            }
        }
        return null;
    }

    /// <summary>The meshes' count, world surface area and the part of it that is ground (level, on <paramref name="walk"/>).</summary>
    public static (int Count, float Area, float Up) Measure(IEnumerable<MeshInstance3D> meshes, WalkMesh walk)
    {
        int count = 0;
        float area = 0, up = 0;
        foreach (var mi in meshes)
        {
            if (!GodotObject.IsInstanceValid(mi) || mi.Mesh is not { } mesh)
                continue;
            count++;
            var t = mi.GlobalTransform;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if (mesh is ArrayMesh am && am.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles)
                    continue;
                var arrays = mesh.SurfaceGetArrays(s);
                var pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                int n = idx.Length > 0 ? idx.Length : pos.Length;
                for (int i = 0; i + 2 < n; i += 3)
                {
                    int a = idx.Length > 0 ? idx[i] : i, b = idx.Length > 0 ? idx[i + 1] : i + 1, c = idx.Length > 0 ? idx[i + 2] : i + 2;
                    var pa = t * pos[a];
                    var cross = (t * pos[b] - pa).Cross(t * pos[c] - pa);
                    float twice = cross.Length();
                    if (twice <= 0)
                        continue;
                    area += twice / 2;
                    // either winding: a face is ground when it lies level, whichever way it was wound
                    var mid = (pa + t * pos[b] + t * pos[c]) / 3;
                    if (Mathf.Abs(cross.Y) / twice > 0.7071f && walk.Under(mid, out var h) >= 0 && Mathf.Abs(h - mid.Y) < 30f)
                        up += twice / 2;
                }
            }
        }
        return (count, area, up);
    }

    /// <summary>A mesh's world area and the level part of it (a normal within 45° of vertical, either winding).</summary>
    public static (int Count, float Area, float Level) MeasureLevel(MeshInstance3D mi)
    {
        float area = 0, level = 0;
        if (mi.Mesh is not { } mesh)
            return (0, 0, 0);
        var t = mi.GlobalTransform;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            var pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            int n = idx.Length > 0 ? idx.Length : pos.Length;
            for (int i = 0; i + 2 < n; i += 3)
            {
                int a = idx.Length > 0 ? idx[i] : i, b = idx.Length > 0 ? idx[i + 1] : i + 1, c = idx.Length > 0 ? idx[i + 2] : i + 2;
                var cross = (t * pos[b] - t * pos[a]).Cross(t * pos[c] - t * pos[a]);
                float twice = cross.Length();
                if (twice <= 0)
                    continue;
                area += twice / 2;
                if (Mathf.Abs(cross.Y) / twice > 0.7071f)
                    level += twice / 2;
            }
        }
        return (1, area, level);
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx selftest: sky: {_phase} -> {p} ({_total:F1} s)");
        _phase = p;
        _phaseTime = 0;
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }
}
