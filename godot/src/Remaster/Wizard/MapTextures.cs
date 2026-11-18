using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx.Remaster.Wizard;

/// <summary>
/// How a map draws one colour texture (the wizard's classifier reads it beside the pixels): its
/// surfaces, their area and which way they face (render space: y up), and the classic material's
/// ways (blend, alpha test, additive, scrolling UVs, morphs), whether its parts are the map's sky
/// (<see cref="MapLook.FindSky"/>) or it is drawn by another shader than the map's (water, the
/// refraction sea).
/// </summary>
public sealed class TextureUseInfo
{
    public required string Id { get; init; }
    public int Surfaces { get; set; }

    /// <summary>The surfaces' area (render units²) and its shares facing up (normal y above 0.6), down (below -0.6), and sideways (walls).</summary>
    public float Area { get; set; }
    public float Up { get; set; }
    public float Down { get; set; }
    public float Wall => Area > 0 ? Mathf.Max(0, 1 - Up - Down) : 0;

    /// <summary>Shares of its surfaces: blended (the game's painted layers), alpha tested, additive (glows, effects), scrolling UVs, morphing.</summary>
    public float Blend { get; set; }
    public float AlphaTest { get; set; }
    public float Additive { get; set; }
    public float Scroll { get; set; }
    public float Morph { get; set; }

    /// <summary>Some of its parts are the map's sky.</summary>
    public bool Sky { get; set; }

    /// <summary>A shader other than the map's draws it (water, the refraction sea: they keep their classic shaders).</summary>
    public bool Special { get; set; }

    /// <summary>The highest point of its surfaces (render units) and the map's whole height range, for "far above everything".</summary>
    public float Top { get; set; } = float.MinValue;

    public override string ToString() =>
        $"{Surfaces} surface(s), area {Area:0} (up {Up:0.00}, wall {Wall:0.00}, down {Down:0.00}), blend {Blend:0.00}, alpha test {AlphaTest:0.00}, additive {Additive:0.00}, "
        + $"scroll {Scroll:0.00}, morph {Morph:0.00}{(Sky ? ", sky" : "")}{(Special ? ", special shader" : "")}";
}

/// <summary>The textures a loaded classic map draws (<see cref="Collect"/>), with how.</summary>
public static class MapTextures
{
    /// <summary>
    /// Every colour texture the classic map <paramref name="map"/> (a loaded import, in the tree or
    /// not; its own transform counts) draws, with how (<see cref="TextureUseInfo"/>); and the map's
    /// bounding box in render space (the GI's choice).
    /// </summary>
    public static (Dictionary<string, TextureUseInfo> Uses, Aabb Box) Collect(Node map)
    {
        var meshes = new List<(MeshInstance3D Mesh, Aabb Box, Transform3D World)>();
        void Walk(Node n, Transform3D t)
        {
            if (n is MeshInstance3D { Mesh: { } mesh } mi && mi.Visible)
                meshes.Add((mi, t * mesh.GetAabb(), t));
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                if (n.GetChild(i) is Node c)
                    Walk(c, c is Node3D c3 ? t * c3.Transform : t);
        }
        Walk(map, map is Node3D m3 ? m3.Transform : Transform3D.Identity);
        var sky = MapLook.FindSky(meshes.Select(m => (m.Mesh, m.Box)).ToList());
        var uses = new Dictionary<string, TextureUseInfo>();
        var counts = new Dictionary<string, (int Blend, int Alpha, int Add, int Scroll, int Morph)>();
        Aabb? box = null;
        foreach (var (mi, b, world) in meshes)
        {
            // the map's box without its sky (a dome reaches far beyond the ground)
            if (!sky.Contains(mi))
                box = box is { } a ? a.Merge(b) : b;
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var mat = mi.GetSurfaceOverrideMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
                if (mat is not ShaderMaterial sm)
                    continue;
                var spec = PartIds.GetString(sm, ClassicMaterials.MapShaderMeta);
                if (spec == null)
                {
                    // water and the like: their textures stay classic
                    foreach (var (_, tid) in RemasterTextures.BoundTextures(sm))
                        Get(uses, tid).Special = true;
                    continue;
                }
                if (RemasterTextures.ColourId(sm) is not { } id)
                    continue;
                var u = Get(uses, id);
                u.Surfaces++;
                if (sky.Contains(mi))
                    u.Sky = true;
                var bar = spec.IndexOf('|');
                var modes = bar >= 0 ? spec[..bar] : spec;
                var defines = bar >= 0 ? spec[(bar + 1)..].Split(',') : [];
                var c = counts.GetValueOrDefault(id);
                if (defines.Contains("BLEND")) c.Blend++;
                if (defines.Contains("ALPHA_TEST")) c.Alpha++;
                if (modes.Contains("blend_add")) c.Add++;
                if (defines.Contains("UV_ANIM") || defines.Contains("UV_SCROLL")) c.Scroll++;
                if (defines.Contains("UV_MORPH") || defines.Contains("COLOR_MORPH")) c.Morph++;
                if (defines.Contains("REFRACTION")) u.Special = true;
                counts[id] = c;
                Faces(mi.Mesh, s, world, u);
            }
        }
        foreach (var (id, c) in counts)
        {
            var u = uses[id];
            float n = Mathf.Max(1, u.Surfaces);
            (u.Blend, u.AlphaTest, u.Additive, u.Scroll, u.Morph) = (c.Blend / n, c.Alpha / n, c.Add / n, c.Scroll / n, c.Morph / n);
            if (u.Area > 0)
            {
                u.Up /= u.Area;
                u.Down /= u.Area;
            }
        }
        return (uses, box ?? new Aabb());
    }

    private static TextureUseInfo Get(Dictionary<string, TextureUseInfo> uses, string id)
    {
        if (!uses.TryGetValue(id, out var u))
            uses[id] = u = new TextureUseInfo { Id = id };
        return u;
    }

    /// <summary>Adds the area of <paramref name="mesh"/>'s surface <paramref name="s"/> to <paramref name="u"/>, by which way each triangle faces (Up and Down hold areas until <see cref="Collect"/> divides).</summary>
    private static void Faces(Mesh mesh, int s, Transform3D world, TextureUseInfo u)
    {
        var arrays = mesh.SurfaceGetArrays(s);
        var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var nrm = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        if (v.Length == 0)
            return;
        var basis = world.Basis.Inverse().Transposed();
        int tris = idx.Length > 0 ? idx.Length / 3 : v.Length / 3;
        for (int t = 0; t < tris; t++)
        {
            int i0 = idx.Length > 0 ? idx[t * 3] : t * 3, i1 = idx.Length > 0 ? idx[t * 3 + 1] : t * 3 + 1, i2 = idx.Length > 0 ? idx[t * 3 + 2] : t * 3 + 2;
            if (i0 >= v.Length || i1 >= v.Length || i2 >= v.Length)
                continue;
            var a = world * v[i0];
            var b = world * v[i1];
            var c = world * v[i2];
            var cross = (b - a).Cross(c - a);
            float area = cross.Length() * 0.5f;
            if (area <= 1e-6f)
                continue;
            // the vertex normals say which side faces out (the windings of FFX's models vary); else the winding
            var n = nrm.Length == v.Length ? basis * (nrm[i0] + nrm[i1] + nrm[i2]) : cross;
            float ny = n.LengthSquared() > 1e-12f ? n.Normalized().Y : 0;
            u.Area += area;
            if (ny > 0.6f)
                u.Up += area;
            else if (ny < -0.6f)
                u.Down += area;
            u.Top = Mathf.Max(u.Top, Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y)));
        }
    }
}
