using System.Collections.Generic;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// Where a map's remastered lighting comes from (docs/remaster-editor.md, "Lighting"), in the
/// overrides' order (docs/remake.md, "Layers"): the project's
/// <c>res://remaster/maps/&lt;key&gt;/lighting.tres</c> (layer <c>base</c>), each mounted pack's
/// <c>res://mods/&lt;pack&gt;/remaster/maps/&lt;key&gt;/lighting.tres</c> in load order, then files
/// registered at run time (<see cref="Register"/>; the checks). The last layer that has a file
/// wins, the whole file (lighting is tuned as one). None: <see cref="Defaults"/>.
/// <c>--no-overrides</c> turns them off. Main thread (loads resources); cached per level.
/// </summary>
public static class LightingLayers
{
    public const string FileName = "lighting.tres";

    private static readonly Dictionary<string, List<(string Layer, string Path)>> Registered = new();
    private static readonly Dictionary<string, (MapLighting Lighting, string Layer)> Cache = new();
    private static readonly HashSet<string> Logged = new();

    /// <summary>The defaults (a map without a file): every value from the game's look as <see cref="MapLighting"/>'s defaults convert it.</summary>
    public static readonly MapLighting Defaults = new();

    /// <summary>Bumped whenever a map's lighting may have changed (the rig applies it again).</summary>
    public static int Version { get; private set; }

    /// <summary>Adds the file at <paramref name="path"/> as the last layer of <paramref name="level"/>.</summary>
    public static void Register(string level, string path, string layer = "runtime")
    {
        var key = PartIds.LevelKey(level);
        if (!Registered.TryGetValue(key, out var list))
            Registered[key] = list = new();
        list.RemoveAll(e => e.Layer == layer);
        list.Add((layer, path));
        Invalidate(key);
    }

    /// <summary>Drops a registered layer.</summary>
    public static void Unregister(string level, string layer)
    {
        var key = PartIds.LevelKey(level);
        if (Registered.TryGetValue(key, out var list))
            list.RemoveAll(e => e.Layer == layer);
        Invalidate(key);
    }

    /// <summary>Forgets the cached file of <paramref name="level"/> (null: all).</summary>
    public static void Invalidate(string? level = null)
    {
        if (level == null)
            Cache.Clear();
        else
            Cache.Remove(PartIds.LevelKey(level));
        Version++;
    }

    /// <summary>The files that may hold <paramref name="level"/>'s lighting, in order.</summary>
    public static IEnumerable<(string Layer, string Path)> Sources(string level)
    {
        var key = PartIds.LevelKey(level);
        yield return ("base", $"{RemasterOverrides.BaseDir}/{key}/{FileName}");
        foreach (var p in ModLoader.Packs)
            yield return (p.Name, $"res://mods/{p.Name}/remaster/maps/{key}/{FileName}");
        if (Registered.TryGetValue(key, out var list))
            foreach (var e in list)
                yield return e;
    }

    /// <summary>Whether <paramref name="level"/> has a lighting file in any layer.</summary>
    public static bool Exists(string level)
    {
        if (RemasterOverrides.Disabled)
            return false;
        foreach (var (_, path) in Sources(level))
            if (ResourceLoader.Exists(path))
                return true;
        return false;
    }

    /// <summary><paramref name="level"/>'s lighting (its last layer's file, else <see cref="Defaults"/>) and the layer it comes from ("" for the defaults).</summary>
    public static (MapLighting Lighting, string Layer) For(string level)
    {
        if (RemasterOverrides.Disabled || level.Length == 0)
            return (Defaults, "");
        var key = PartIds.LevelKey(level);
        if (Cache.TryGetValue(key, out var hit))
            return hit;
        (MapLighting, string) found = (Defaults, "");
        foreach (var (layer, path) in Sources(level))
        {
            if (!ResourceLoader.Exists(path))
                continue;
            // a fresh copy: a file read again with Godot's cache keeps the values it no longer says
            if (ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) is MapLighting l)
                found = (l, layer);
            else if (Logged.Add(path))
                GD.PrintErr($"ffx: lighting: {path} is not a MapLighting resource");
        }
        if (found.Item2.Length > 0)
            GD.Print($"ffx: lighting: {key} from layer {found.Item2} ({found.Item1.Volumes.Count} volume(s))");
        Cache[key] = found;
        return found;
    }
}
