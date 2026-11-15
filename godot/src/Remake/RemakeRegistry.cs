using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace GdFfx.Remake;

/// <summary>
/// Remade content by the game's own ids (docs/remake.md). A remade item is a
/// Godot scene registered for a kind and an id:
/// <list type="bullet">
/// <item><c>map</c>: the level the game's renderer loaded (<c>World.level</c>,
/// e.g. <c>/map/bsil/bsil00/</c>), or its last part (<c>bsil00</c>);</item>
/// <item><c>chr</c>: the model name (<c>Actor.model</c>, e.g. <c>c001</c>);</item>
/// <item>later <c>effect</c> and <c>ui</c>.</item>
/// </list>
/// Explicit entries come from <c>res://remake/remake.json</c>, then from each
/// mod pack's <c>res://mods/&lt;pack&gt;/remake.json</c> in load order, then from
/// <see cref="Register"/> at run time; a later entry wins. Without an explicit
/// entry the convention applies: <c>res://remake/&lt;kind&gt;/&lt;name&gt;.tscn</c>
/// (or <c>.scn</c>). Anything else stays classic.
/// </summary>
public static class RemakeRegistry
{
    /// <summary>The base registry, shipped with gd-ffx (may be absent).</summary>
    public const string BaseJson = "res://remake/remake.json";

    /// <summary>An entry: the scene, or "" when the id is pinned to classic; where it came from.</summary>
    public readonly record struct Entry(string Scene, string Source);

    private static readonly Dictionary<(string Kind, string Id), Entry> Explicit = new();
    private static readonly Dictionary<(string Kind, string Id), string?> Cache = new();

    /// <summary>Explicit entries (kind, id, scene; "" = classic), for logs and the overlay.</summary>
    public static IReadOnlyDictionary<(string Kind, string Id), Entry> Entries => Explicit;

    /// <summary>
    /// Registers <paramref name="scene"/> (a <c>res://</c> path) for <paramref name="kind"/>
    /// and <paramref name="id"/>, over any earlier entry. An empty scene pins the id to
    /// classic (a pack can undo another's replacement).
    /// </summary>
    public static void Register(string kind, string id, string scene, string source = "runtime")
    {
        Explicit[(kind, Normalize(kind, id))] = new Entry(scene, source);
        Cache.Clear();
    }

    /// <summary>Forgets the convention's lookups (files under <c>res://</c> changed: a pack was mounted).</summary>
    internal static void Invalidate() => Cache.Clear();

    /// <summary>Removes the explicit entry for an id (the convention applies again).</summary>
    public static void Unregister(string kind, string id)
    {
        Explicit.Remove((kind, Normalize(kind, id)));
        Cache.Clear();
    }

    /// <summary>
    /// The remade scene for <paramref name="kind"/> and <paramref name="id"/>, or null when
    /// the id stays classic.
    /// </summary>
    public static string? Find(string kind, string id)
    {
        var key = (kind, Normalize(kind, id));
        if (Cache.TryGetValue(key, out var hit))
            return hit;
        string? scene = null;
        if (Explicit.TryGetValue(key, out var e))
            scene = e.Scene.Length > 0 ? e.Scene : null;
        else
        {
            foreach (var ext in new[] { ".tscn", ".scn" })
            {
                var path = $"res://remake/{kind}/{key.Item2}{ext}";
                if (ResourceLoader.Exists(path))
                {
                    scene = path;
                    break;
                }
            }
        }
        Cache[key] = scene;
        return scene;
    }

    /// <summary>
    /// The id as the registry keys it: a map level <c>/map/bsil/bsil00/</c> becomes
    /// <c>bsil00</c> (map names are unique; the convention's file name), others as given.
    /// </summary>
    public static string Normalize(string kind, string id)
    {
        if (kind != "map")
            return id;
        var parts = id.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : id;
    }

    /// <summary>
    /// Reads a registry file (<c>{"chr": {"c001": "res://..."}, "map": {"bsil00": "res://..."}}</c>;
    /// a scene of <c>""</c> or <c>null</c> pins the id to classic); the entries read, -1 when it
    /// cannot be read. A missing file reads as 0 entries.
    /// </summary>
    public static int LoadJson(string path, string source)
    {
        if (!FileAccess.FileExists(path))
            return 0;
        var text = FileAccess.GetFileAsString(path);
        try
        {
            return ApplyJson(text, source);
        }
        catch (JsonException e)
        {
            GD.PrintErr($"ffx: remake: {path}: {e.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Applies a registry's text (see <see cref="LoadJson"/>); the entries read. All or
    /// nothing: a bad entry throws before any is registered.
    /// </summary>
    public static int ApplyJson(string text, string source)
    {
        var entries = new List<(string Kind, string Id, string Scene)>();
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("the registry is not an object of kinds");
        foreach (var kind in doc.RootElement.EnumerateObject())
        {
            if (kind.Value.ValueKind != JsonValueKind.Object)
                throw new JsonException($"\"{kind.Name}\" is not an object of ids");
            foreach (var id in kind.Value.EnumerateObject())
            {
                var scene = id.Value.ValueKind switch
                {
                    JsonValueKind.String => id.Value.GetString()!,
                    JsonValueKind.Null => "",
                    _ => throw new JsonException($"{kind.Name} {id.Name}: not a scene path"),
                };
                entries.Add((kind.Name, id.Name, scene));
            }
        }
        foreach (var (kind, id, scene) in entries)
            Register(kind, id, scene, source);
        return entries.Count;
    }
}
