using System.Collections.Generic;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// Stable part IDs on the Godot side (docs/importer.md, "Stable part IDs"): the import writes
/// them as glTF extras, <see cref="Mark"/> keeps them as metadata after
/// <see cref="ClassicLibrary.Load"/>, <see cref="PartIndex"/> finds parts by them.
/// <list type="bullet">
/// <item>every node made from a glTF node: <c>ffx_part</c> = its ID (the scene's glTF root the
/// model's <c>x:/...dae.phyre</c>, a <c>PNode</c>'s node <c>#&lt;node&gt;</c>, an instance's mesh
/// node <c>#&lt;node&gt;/*</c>);</item>
/// <item>an instance's <see cref="MeshInstance3D"/>: <c>ffx_parts</c> = its surfaces' part IDs
/// (<c>#&lt;node&gt;/&lt;k&gt;</c>), surface i the glTF primitive i;</item>
/// <item>each mesh's own surface material (<see cref="Mesh.SurfaceGetMaterial"/>, not the drawn
/// override): <c>ffx_texture_ids</c> and <c>ffx_texture_samplers</c>, the material's textures'
/// IDs (<c>x:/...dds.phyre</c>) and their samplers, in the same order.</item>
/// </list>
/// A remade scene may tag its own nodes with <c>ffx_part</c> the same way (overrides then find
/// them). Overrides add <c>ffx_added</c> (an added node's ID) and <c>ffx_replacement</c>.
/// </summary>
public static class PartIds
{
    public const string Part = "ffx_part";
    public const string Parts = "ffx_parts";
    public const string TextureIds = "ffx_texture_ids";
    public const string TextureSamplers = "ffx_texture_samplers";
    public const string Added = "ffx_added";
    public const string Replacement = "ffx_replacement";

    /// <summary>The level IDs' prefix (<c>x:level/bsil00</c>).</summary>
    public const string LevelPrefix = "x:level/";

    /// <summary>
    /// Sets the import's IDs (<see cref="GltfExtras"/>) on the nodes, meshes and materials
    /// <paramref name="state"/> made into <paramref name="scene"/>. Nodes are found by their glTF
    /// node's unique name (Godot names each scene node after it), not by
    /// <see cref="GltfState.GetSceneNode"/>: <c>generate_scene</c> replaces each
    /// <c>ImporterMeshInstance3D</c> by a <see cref="MeshInstance3D"/> and frees it, but keeps
    /// the freed one in that table.
    /// </summary>
    public static void Mark(GltfState state, GltfExtras x, Node scene)
    {
        var byName = new Dictionary<string, int>();
        var nodes = state.GetNodes();
        for (int i = 0; i < nodes.Count && i < x.NodeIds.Length; i++)
            if (x.NodeIds[i] != null)
            {
                using var g = nodes[i];
                byName.TryAdd(g.ResourceName, i);
            }
        if (byName.Count > 0)
            MarkNodes(scene, x, byName);
        var mats = state.GetMaterials();
        for (int i = 0; i < mats.Count && i < x.Materials.Length; i++)
        {
            var t = x.Materials[i].TextureIds;
            if (t.Count == 0)
                continue;
            using var mat = mats[i];
            var samplers = new string[t.Count];
            var ids = new string[t.Count];
            int k = 0;
            foreach (var (sampler, id) in t)
            {
                samplers[k] = sampler;
                ids[k++] = id;
            }
            SetStrings(mat, TextureIds, ids);
            SetStrings(mat, TextureSamplers, samplers);
        }
    }

    private static void MarkNodes(Node node, GltfExtras x, Dictionary<string, int> byName)
    {
        if (byName.TryGetValue(node.Name, out int i))
        {
            SetString(node, Part, x.NodeIds[i]!);
            int m = i < x.NodeMeshes.Length ? x.NodeMeshes[i] : -1;
            if (node is MeshInstance3D { Mesh: { } mesh } mi && m >= 0 && m < x.PartIds.Length)
            {
                var ids = x.PartIds[m];
                if (ids.Length == mesh.GetSurfaceCount() && System.Array.TrueForAll(ids, s => s != null))
                    SetStrings(mi, Parts, ids!);
            }
        }
        for (int c = 0, k = node.GetChildCount(); c < k; c++)
            MarkNodes(node.GetChild(c), x, byName);
    }

    /// <summary>A string metadata value, its variant disposed (docs/running.md, "Garbage collections").</summary>
    public static void SetString(GodotObject o, string name, string value)
    {
        using var v = Variant.CreateFrom(value);
        o.SetMeta(Variants.Name(name), v);
    }

    /// <summary>A string array metadata value, its variant disposed.</summary>
    public static void SetStrings(GodotObject o, string name, string[] value)
    {
        using var v = Variant.CreateFrom(value);
        o.SetMeta(Variants.Name(name), v);
    }

    /// <summary>A string metadata value (null: none).</summary>
    public static string? GetString(GodotObject o, string name)
    {
        if (!o.HasMeta(Variants.Name(name)))
            return null;
        using var v = o.GetMeta(Variants.Name(name));
        return v.VariantType == Variant.Type.String || v.VariantType == Variant.Type.StringName ? v.AsString() : null;
    }

    /// <summary>A string array metadata value (null: none).</summary>
    public static string[]? GetStrings(GodotObject o, string name)
    {
        if (!o.HasMeta(Variants.Name(name)))
            return null;
        using var v = o.GetMeta(Variants.Name(name));
        return v.VariantType == Variant.Type.PackedStringArray ? v.AsStringArray() : null;
    }

    /// <summary>A node's part ID (<c>ffx_part</c>; null: none).</summary>
    public static string? Of(Node n) => GetString(n, Part);

    /// <summary>
    /// The ID of the classic 3D model of <paramref name="level"/> (<c>/map/bsil/bsil00/</c>,
    /// <c>/btlmap/bika/bika00_a/</c> or the key <c>bsil00</c>), as the import writes it; null
    /// when it names no map. A relative ID (<c>#&lt;node&gt;/&lt;k&gt;</c>) is relative to it.
    /// </summary>
    public static string? ModelIdOf(string level)
    {
        var parts = level.Trim().Trim('/').ToLowerInvariant().Split('/', System.StringSplitOptions.RemoveEmptyEntries);
        string root, map;
        if (parts.Length >= 3 && parts[0] is "map" or "btlmap")
            (root, map) = (parts[0], parts[2]);
        else if (parts.Length == 1 && parts[0].Length >= 6)
            (root, map) = (parts[0].Contains('_') ? "btlmap" : "map", parts[0]);
        else
            return null;
        if (map.Length < 4)
            return null;
        return $"x:/ffx_data/gamedata/ps3data/{root}/{map[..4]}/{map}/mdl/d3d11/{map}.dae.phyre";
    }

    /// <summary><paramref name="id"/> made absolute: one starting with <c>#</c> is relative to <paramref name="model"/>.</summary>
    public static string Full(string id, string? model) =>
        id.StartsWith('#') && model != null ? model + id : id;

    /// <summary>gd-ffx's key of a level (<c>/map/bsil/bsil00/</c> and <c>bsil00</c> are <c>bsil00</c>).</summary>
    public static string LevelKey(string level)
    {
        var t = level.Trim().Trim('/');
        int slash = t.LastIndexOf('/');
        return (slash >= 0 ? t[(slash + 1)..] : t).ToLowerInvariant();
    }

    /// <summary>The ID of a node added to <paramref name="level"/> by layer <paramref name="layer"/>: <c>x:level/&lt;key&gt;/+&lt;layer&gt;/&lt;name&gt;</c>.</summary>
    public static string AddedId(string level, string layer, string name) =>
        $"{LevelPrefix}{LevelKey(level)}/+{layer}/{name}";
}

/// <summary>
/// A map scene's parts by ID: nodes tagged <c>ffx_part</c> (or <c>ffx_added</c>) and the
/// surfaces of meshes tagged <c>ffx_parts</c>. Built once per map (a walk of every node).
/// </summary>
public sealed class PartIndex
{
    public readonly Dictionary<string, Node3D> Nodes = new();
    public readonly Dictionary<string, (MeshInstance3D Mesh, int Surface)> Surfaces = new();

    public static PartIndex Build(Node root)
    {
        var index = new PartIndex();
        index.Add(root);
        return index;
    }

    /// <summary>Indexes <paramref name="n"/> and everything under it.</summary>
    public void Add(Node n)
    {
        if (n is Node3D n3)
        {
            if ((PartIds.Of(n3) ?? PartIds.GetString(n3, PartIds.Added)) is { } id)
                Nodes.TryAdd(id, n3);
            if (n3 is MeshInstance3D mi && PartIds.GetStrings(mi, PartIds.Parts) is { } parts)
                for (int s = 0; s < parts.Length; s++)
                    Surfaces.TryAdd(parts[s], (mi, s));
        }
        // by index: GetChildren makes a Godot array (finalizable) a node
        for (int i = 0, k = n.GetChildCount(); i < k; i++)
            Add(n.GetChild(i));
    }
}
