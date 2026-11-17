using System.Collections.Generic;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Remaster;

/// <summary>A part's edits merged over every layer (<see cref="RemasterOverrides"/>): each field the last layer's that sets it.</summary>
public sealed class MergedPart
{
    public required string Id { get; init; }
    public RemoveOp Remove { get; set; }
    public VisibilityOp Visibility { get; set; }
    public TransformOp TransformMode { get; set; }
    public Transform3D Transform { get; set; } = Transform3D.Identity;
    public ReplaceOp Replace { get; set; }
    public PackedScene? Scene { get; set; }

    /// <summary>The layers that edit it, in order.</summary>
    public List<string> Layers { get; } = new();

    public bool Removed => Remove == RemoveOp.Remove;
    public bool Hidden => Visibility == VisibilityOp.Hidden;
    public bool Moved => TransformMode is TransformOp.Offset or TransformOp.Absolute;
    public bool Replaced => Replace == ReplaceOp.Scene && Scene != null;
}

/// <summary>One layer of a map's overrides: its name (<c>base</c>, a pack's, a registered one's), its file and its added nodes' scene.</summary>
public sealed record OverrideLayer(string Name, string Path, MapOverrides Resource, PackedScene? Additions);

/// <summary>A map's overrides over all its layers (main thread to make; read-only after, so worker threads may apply it).</summary>
public sealed class MergedOverrides
{
    public required string Key { get; init; }

    /// <summary>The ID a relative part ID (<c>#...</c>) is relative to: the map's classic 3D model.</summary>
    public string? Model { get; init; }

    public required IReadOnlyList<OverrideLayer> Layers { get; init; }

    /// <summary>By full part ID.</summary>
    public required IReadOnlyDictionary<string, MergedPart> Parts { get; init; }

    /// <summary>The last layer's status, render path and notes.</summary>
    public MapStatus Status => Layers.Count > 0 ? Layers[^1].Resource.Status : MapStatus.Original;
    public RenderPath RenderPath => Layers.Count > 0 ? Layers[^1].Resource.RenderPath : RenderPath.Classic;
    public string Notes => Layers.Count > 0 ? Layers[^1].Resource.Notes : "";
}

/// <summary>
/// Where a map's overrides come from and how they layer (docs/remake.md, "Overrides"):
/// <list type="number">
/// <item><c>res://remaster/maps/&lt;key&gt;/overrides.tres</c>, the project's (layer <c>base</c>);</item>
/// <item>each mounted pack's <c>res://mods/&lt;pack&gt;/remaster/maps/&lt;key&gt;/overrides.tres</c>,
/// in load order (layer = the pack's name);</item>
/// <item>files registered at run time (<see cref="Register"/>; the checks).</item>
/// </list>
/// A later layer wins per part ID and per field; added nodes of every layer are added (each
/// with its own ID, <c>x:level/&lt;key&gt;/+&lt;layer&gt;/&lt;name&gt;</c>, which a later layer
/// may edit or remove). <c>--no-overrides</c> turns them all off. Merged once per level
/// (<see cref="Invalidate"/> to read again).
/// </summary>
public static class RemasterOverrides
{
    public const string BaseDir = "res://remaster/maps";
    public const string FileName = "overrides.tres";
    public const string AdditionsName = "additions.tscn";

    private static readonly Dictionary<string, List<(string Layer, string Path)>> Registered = new();
    private static readonly Dictionary<string, MergedOverrides?> Cache = new();
    private static readonly HashSet<string> Logged = new();

    public static bool Disabled => GameSettings.Flag("no-overrides");

    /// <summary>Adds the overrides file at <paramref name="path"/> as the last layer of <paramref name="level"/> (named <paramref name="layer"/>).</summary>
    public static void Register(string level, string path, string layer = "runtime")
    {
        var key = PartIds.LevelKey(level);
        if (!Registered.TryGetValue(key, out var list))
            Registered[key] = list = new();
        list.RemoveAll(e => e.Layer == layer);
        list.Add((layer, path));
        Invalidate(key);
    }

    /// <summary>Drops the cached merge of <paramref name="level"/> (null: all), so the next <see cref="For"/> reads the files again.</summary>
    public static void Invalidate(string? level = null)
    {
        if (level == null)
            Cache.Clear();
        else
            Cache.Remove(PartIds.LevelKey(level));
    }

    /// <summary>The files that may hold <paramref name="level"/>'s layers, in order (whether or not they exist).</summary>
    public static IEnumerable<(string Layer, string Path)> Sources(string level)
    {
        var key = PartIds.LevelKey(level);
        yield return ("base", $"{BaseDir}/{key}/{FileName}");
        foreach (var p in ModLoader.Packs)
            yield return (p.Name, $"res://mods/{p.Name}/remaster/maps/{key}/{FileName}");
        if (Registered.TryGetValue(key, out var list))
            foreach (var e in list)
                yield return e;
    }

    /// <summary>Whether any layer of <paramref name="level"/> exists (without loading it).</summary>
    public static bool Exists(string level)
    {
        if (Disabled)
            return false;
        foreach (var (_, path) in Sources(level))
            if (ResourceLoader.Exists(path))
                return true;
        return false;
    }

    /// <summary>
    /// The levels (keys) that have overrides: the project's and the packs' folders, and the
    /// registered ones.
    /// </summary>
    public static SortedSet<string> Levels()
    {
        var keys = new SortedSet<string>();
        if (Disabled)
            return keys;
        var dirs = new List<string> { BaseDir };
        foreach (var p in ModLoader.Packs)
            dirs.Add($"res://mods/{p.Name}/remaster/maps");
        foreach (var d in dirs)
            if (DirAccess.DirExistsAbsolute(d))
                foreach (var k in DirAccess.GetDirectoriesAt(d))
                    if (ResourceLoader.Exists($"{d}/{k}/{FileName}"))
                        keys.Add(k.ToLowerInvariant());
        foreach (var (k, list) in Registered)
            if (list.Count > 0)
                keys.Add(k);
        return keys;
    }

    /// <summary>
    /// <paramref name="level"/>'s overrides merged over its layers (main thread: loads
    /// resources), cached; null when it has none (or <c>--no-overrides</c>).
    /// </summary>
    public static MergedOverrides? For(string level)
    {
        if (Disabled)
            return null;
        var key = PartIds.LevelKey(level);
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        var merged = Merge(level, key);
        Cache[key] = merged;
        return merged;
    }

    private static MergedOverrides? Merge(string level, string key)
    {
        var model = PartIds.ModelIdOf(level.Contains('/') ? level : key);
        var layers = new List<OverrideLayer>();
        foreach (var (name, path) in Sources(level))
        {
            if (!ResourceLoader.Exists(path))
                continue;
            if (ResourceLoader.Load(path) is not MapOverrides res)
            {
                if (Logged.Add(path))
                    GD.PrintErr($"ffx: overrides: {path} is not a MapOverrides resource");
                continue;
            }
            var additions = res.Additions;
            var dir = path.GetBaseDir();
            if (additions == null && ResourceLoader.Exists($"{dir}/{AdditionsName}"))
                additions = ResourceLoader.Load($"{dir}/{AdditionsName}") as PackedScene;
            layers.Add(new OverrideLayer(name, path, res, additions));
        }
        if (layers.Count == 0)
            return null;
        var parts = new Dictionary<string, MergedPart>();
        foreach (var layer in layers)
            foreach (var p in layer.Resource.Parts)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Id))
                    continue;
                var id = PartIds.Full(p.Id.Trim(), model);
                if (!parts.TryGetValue(id, out var m))
                    parts[id] = m = new MergedPart { Id = id };
                m.Layers.Add(layer.Name);
                if (p.Remove != RemoveOp.Unset)
                    m.Remove = p.Remove;
                if (p.Visibility != VisibilityOp.Unset)
                    m.Visibility = p.Visibility;
                if (p.TransformMode != TransformOp.Unset)
                {
                    m.TransformMode = p.TransformMode;
                    m.Transform = p.Transform;
                }
                if (p.Replace != ReplaceOp.Unset)
                {
                    m.Replace = p.Replace;
                    m.Scene = p.Scene;
                }
            }
        var names = string.Join(", ", layers.ConvertAll(l => $"{l.Name} ({l.Path})"));
        GD.Print($"ffx: overrides: {key}: {parts.Count} part(s) over {layers.Count} layer(s): {names}");
        return new MergedOverrides { Key = key, Model = model, Layers = layers, Parts = parts };
    }
}
