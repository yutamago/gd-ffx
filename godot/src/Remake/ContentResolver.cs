using Godot;

namespace GdFfx.Remake;

/// <summary>
/// One piece of content to show for a kind and id: a remade scene
/// (<see cref="RemakeRegistry"/>) or the classic import (<see cref="ClassicLibrary"/>).
/// </summary>
public sealed record Content(string Kind, string Id, string Path, bool Remade)
{
    /// <summary>
    /// The scale of a map's node: classic maps are the file times
    /// <see cref="ClassicLibrary.MapScale"/>; a remade map is authored in Godot's space (1).
    /// </summary>
    public float MapScale => Remade ? 1f : ClassicLibrary.MapScale;

    /// <summary>
    /// A new instance (null on failure), tagged with metadata <c>ffx_kind</c>, <c>ffx_id</c>
    /// and <c>ffx_source</c> (<c>remade</c> or <c>classic</c>) and <c>ffx_path</c>.
    /// </summary>
    public Node3D? Load()
    {
        Node3D? node;
        if (Remade)
        {
            var scene = ResourceLoader.Load<PackedScene>(Path);
            node = scene?.Instantiate() as Node3D;
            if (node == null)
                GD.PrintErr($"ffx: remake: {Kind} {Id}: {Path} is not a 3D scene");
        }
        else
            node = ClassicLibrary.Load(Path);
        if (node == null)
            return null;
        node.SetMeta("ffx_kind", Kind);
        node.SetMeta("ffx_id", Id);
        node.SetMeta("ffx_source", Remade ? "remade" : "classic");
        node.SetMeta("ffx_path", Path);
        return node;
    }

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
    /// <summary>
    /// The content for <paramref name="kind"/> and <paramref name="id"/>: the remade scene if
    /// one is registered, else the classic import when it is ready (asking for it otherwise;
    /// null until then, or always without classic content).
    /// </summary>
    public static Content? Resolve(ClassicLibrary? classic, string kind, string id)
    {
        if (RemakeRegistry.Find(kind, id) is { } scene)
            return new Content(kind, id, scene, true);
        if (classic?.Path(kind, id) is { } path)
            return new Content(kind, id, path, false);
        return null;
    }
}
