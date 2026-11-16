using System.Collections.Generic;
using Godot;

namespace GdFfx.Remake;

/// <summary>
/// One piece of content to show for a kind and id, loaded: a remade scene
/// (<see cref="RemakeRegistry"/>) or the classic import (<see cref="ClassicLibrary"/>).
/// <see cref="Node"/> is the instance (null when a classic import failed to load); it carries
/// the metadata <c>ffx_kind</c>, <c>ffx_id</c>, <c>ffx_source</c> (<c>remade</c> or
/// <c>classic</c>) and <c>ffx_path</c>. A remade scene comes wrapped in a node of its own, so
/// the caller may set the wrapper's transform (the character's turn, the map's scale) and the
/// scene keeps its root's.
/// </summary>
public sealed record Content(string Kind, string Id, string Path, bool Remade, Node3D? Node)
{
    /// <summary>
    /// The scale of a map's node: classic maps are the file times
    /// <see cref="ClassicLibrary.MapScale"/>; a remade map is authored in the game's render
    /// space (1).
    /// </summary>
    public float MapScale => Remade ? 1f : ClassicLibrary.MapScale;

    /// <summary>
    /// Whether the game poses this character's skeleton: classic always; a remade one unless
    /// its root has the metadata <c>ffx_posed</c> = false (docs/remake.md).
    /// </summary>
    public static bool Posed(Node node) =>
        !node.HasMeta("ffx_posed") || node.GetMeta("ffx_posed").AsBool();
}

/// <summary>
/// The single place where content is chosen: the remade scene registered for an id,
/// else the classic one (imported on demand).
/// </summary>
public static class ContentResolver
{
    /// <summary>Remade scenes that failed to load (kind, id, path): classic is used instead.</summary>
    private static readonly HashSet<(string Kind, string Id, string Path)> Failed = new();

    /// <summary>
    /// The content for <paramref name="kind"/> and <paramref name="id"/>, loaded: the remade
    /// scene if one is registered and loads, else the classic import when it is ready (asking
    /// for it otherwise; null until then, or always without classic content). A remade scene
    /// that fails to load is logged once and falls back to classic.
    /// </summary>
    public static Content? Resolve(ClassicLibrary? classic, string kind, string id)
    {
        if (RemakeRegistry.Find(kind, id) is { } scene && !Failed.Contains((kind, id, scene)))
        {
            if (LoadRemade(kind, id, scene) is { } node)
                return new Content(kind, id, scene, true, Tag(node, kind, id, scene, true));
            Failed.Add((kind, id, scene));
            GD.PrintErr($"ffx: remake: {kind} {id}: {scene} did not load; classic instead");
        }
        if (classic?.Path(kind, id) is { } path)
        {
            // a map's own textures (its cube maps) are in PS3Data/<level>tex/
            var texDir = kind == "map" && id.StartsWith('/') && id.EndsWith('/') ? $"PS3Data{id}tex/" : null;
            var node = ClassicLibrary.Load(path, texDir);
            return new Content(kind, id, path, false, node == null ? null : Tag(node, kind, id, path, false));
        }
        return null;
    }

    /// <summary>The remade scene's instance in a wrapper node (null when it is not a 3D scene).</summary>
    private static Node3D? LoadRemade(string kind, string id, string path)
    {
        if (ResourceLoader.Load(path) is not PackedScene packed)
        {
            GD.PrintErr($"ffx: remake: {kind} {id}: {path} is not a scene");
            return null;
        }
        var inst = packed.Instantiate();
        if (inst is not Node3D root)
        {
            GD.PrintErr($"ffx: remake: {kind} {id}: {path}'s root is {inst?.GetClass() ?? "nothing"}, not a Node3D");
            inst?.Free();
            return null;
        }
        var wrapper = new Node3D { Name = "Remade" };
        wrapper.AddChild(root);
        if (root.HasMeta("ffx_posed"))
            wrapper.SetMeta("ffx_posed", root.GetMeta("ffx_posed"));
        return wrapper;
    }

    private static Node3D Tag(Node3D node, string kind, string id, string path, bool remade)
    {
        node.SetMeta("ffx_kind", kind);
        node.SetMeta("ffx_id", id);
        node.SetMeta("ffx_source", remade ? "remade" : "classic");
        node.SetMeta("ffx_path", path);
        return node;
    }
}
