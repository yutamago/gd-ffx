#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The Materials tab's <b>In map</b> preview (docs/remaster-materials.md, "The preview"; phase 5): a
/// copy of a map that uses the texture (the open map, else one loaded from the classic import),
/// for the preview's own world. Every mesh of the map's geometry is copied (the edit scene's own
/// nodes, the game's data, hidden parts left out), its surfaces drawn remastered (the game's lit
/// shader, <see cref="RemasterLook.Lit"/>) on both sides of the compare. A mesh with surfaces that
/// draw the texture is copied twice: on the material's layer (1) those surfaces take the live
/// material, on the compare's layer (2) the compare's (the classic look, or the raw texture lit).
/// </summary>
public sealed class InMapScene
{
    /// <summary>At most this many meshes are copied (a map's geometry; the first ones).</summary>
    public const int MaxMeshes = 6000;

    /// <summary>The copy's root (put in the preview's world).</summary>
    public Node3D Root { get; } = new() { Name = "InMap" };

    /// <summary>The level the copy is of.</summary>
    public string Level { get; }

    /// <summary>The surfaces that draw the texture, on the material's layer: (copy, surface, its classic material).</summary>
    public readonly List<(MeshInstance3D Mesh, int Surface, Material? Classic)> Live = new();

    /// <summary>The same surfaces on the compare's layer.</summary>
    public readonly List<(MeshInstance3D Mesh, int Surface, Material? Classic)> Left = new();

    /// <summary>
    /// The box the camera frames: around the area-weighted centre of the surface that draws the
    /// texture over the largest area, as wide as the square of that area (a beach's sand framed on
    /// the beach, not the island); and the whole copy's.
    /// </summary>
    public Aabb Focus { get; private set; }

    public Aabb Bounds { get; private set; }

    /// <summary>Meshes copied.</summary>
    public int Meshes { get; private set; }

    private InMapScene(string level) => Level = level;

    /// <summary>
    /// The copy of <paramref name="map"/> (a classic map's root: the edit scene's ClassicMap, or a
    /// loaded classic import) for texture <paramref name="id"/>; null when no surface draws it.
    /// </summary>
    public static InMapScene? Build(Node3D map, string id, string level)
    {
        id = id.ToLowerInvariant();
        var scene = new InMapScene(level);
        var look = MapLook.Find(map);
        var pieces = new List<(Vector3 Centre, float Area)>();
        Aabb? bounds = null;
        void Walk(Node n, Transform3D parent)
        {
            if (n.HasMeta(MapEditRoot.EditorNode) || n is Node3D { Visible: false } || scene.Meshes >= MaxMeshes)
                return;
            var place = n is Node3D n3 ? parent * n3.Transform : parent;
            if (n is MeshInstance3D { Mesh: { } mesh } mi)
            {
                int count = mesh.GetSurfaceCount();
                var classic = new Material?[count];
                var uses = new bool[count];
                bool any = false;
                for (int s = 0; s < count; s++)
                {
                    var active = mi.GetActiveMaterial(s);
                    // the classic material (the open map may be drawn remastered: its look keeps the classic one)
                    classic[s] = look?.ClassicOf(mi, s) ?? active;
                    uses[s] = RemasterTextures.ColourId(classic[s]) == id
                        || active != null && PartIds.GetString(active, RemasterLook.LitTextureMeta) == id;
                    any |= uses[s];
                }
                var box = place * mesh.GetAabb();
                bounds = bounds is { } b ? b.Merge(box) : box;
                MeshInstance3D Copy(uint layers)
                {
                    var c = new MeshInstance3D { Mesh = mesh, Transform = place, Layers = layers, CastShadow = mi.CastShadow };
                    for (int s = 0; s < count; s++)
                        c.SetSurfaceOverrideMaterial(s, RemasterLook.Lit(classic[s]) ?? classic[s]);
                    scene.Root.AddChild(c);
                    scene.Meshes++;
                    return c;
                }
                if (!any)
                    Copy(1 | 2);
                else
                {
                    var right = Copy(1);
                    var left = Copy(2);
                    for (int s = 0; s < count; s++)
                    {
                        if (!uses[s])
                            continue;
                        scene.Live.Add((right, s, classic[s]));
                        scene.Left.Add((left, s, classic[s]));
                        pieces.Add(SurfaceArea(mesh, s, place));
                    }
                }
            }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i), place);
        }
        // the map's own scale and place left out (as the Object view): only its inside matters
        Walk(map, map.Transform.AffineInverse());
        if (scene.Live.Count == 0)
        {
            scene.Root.Free();
            return null;
        }
        scene.Bounds = bounds!.Value;
        var largest = pieces.MaxBy(p => p.Area);
        float side = Mathf.Max(1e-3f, Mathf.Sqrt(largest.Area));
        scene.Focus = new Aabb(largest.Centre - Vector3.One * side * 0.5f, Vector3.One * side);
        return scene;
    }

    /// <summary>One surface's area (placed) and its area-weighted centre.</summary>
    private static (Vector3 Centre, float Area) SurfaceArea(Mesh mesh, int s, Transform3D place)
    {
        var arrays = mesh.SurfaceGetArrays(s);
        var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var idx = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        int n = idx?.Length ?? v.Length;
        float area = 0;
        var sum = Vector3.Zero;
        for (int i = 0; i + 2 < n; i += 3)
        {
            var a = place * v[idx?[i] ?? i];
            var b = place * v[idx?[i + 1] ?? i + 1];
            var c = place * v[idx?[i + 2] ?? i + 2];
            float t = (b - a).Cross(c - a).Length() * 0.5f;
            area += t;
            sum += (a + b + c) / 3 * t;
        }
        return area > 0 ? (sum / area, area) : (place * mesh.GetAabb().GetCenter(), 0);
    }

    /// <summary>The live surfaces' materials (<paramref name="right"/>: from each classic material) and the compare's (<paramref name="left"/>).</summary>
    public void Apply(System.Func<Material?, Material?> right, System.Func<Material?, Material?> left)
    {
        foreach (var (m, s, c) in Live)
            m.SetSurfaceOverrideMaterial(s, right(c));
        foreach (var (m, s, c) in Left)
            m.SetSurfaceOverrideMaterial(s, left(c));
    }
}
#endif
