#if TOOLS
using System.Collections.Generic;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The project's override layer of each map as the editor edits it (docs/remake.md, "Overrides";
/// docs/remaster-editor.md): <c>&lt;dir&gt;/&lt;level&gt;/overrides.tres</c> and
/// <c>additions.tscn</c> beside it, the folder <c>res://remaster/maps</c> (or
/// <c>--remaster-maps=DIR</c>, the editor check's temporary folder). One resource a map is held
/// in memory while the editor runs, so the dock's status and the map's part edits are written
/// into the same file. The packs' layers are not edited here.
/// </summary>
public static class MapStore
{
    private static readonly Dictionary<string, MapOverrides> Held = new();

    /// <summary>The folder of the project's per-map files.</summary>
    public static string Dir => GameSettings.Arg("remaster-maps") is { Length: > 0 } d ? d.TrimEnd('/') : RemasterOverrides.BaseDir;

    public static string PathOf(string key) => $"{Dir}/{key}/{RemasterOverrides.FileName}";

    public static string AdditionsPathOf(string key) => $"{Dir}/{key}/{RemasterOverrides.AdditionsName}";

    /// <summary><paramref name="key"/>'s overrides (null: the project has none).</summary>
    public static MapOverrides? Find(string key)
    {
        if (Held.TryGetValue(key, out var r))
            return r;
        var path = PathOf(key);
        if (!ResourceLoader.Exists(path))
            return null;
        // read afresh: the file may have changed outside the editor
        if (ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace) is not MapOverrides res)
        {
            GD.PrintErr($"ffx remaster: {path} is not a MapOverrides resource");
            return null;
        }
        Held[key] = res;
        return res;
    }

    /// <summary><paramref name="key"/>'s overrides, a new empty one when the project has none (not written until <see cref="Write"/>).</summary>
    public static MapOverrides GetOrNew(string key)
    {
        if (Find(key) is { } r)
            return r;
        var res = new MapOverrides { Level = key };
        Held[key] = res;
        return res;
    }

    /// <summary>The levels whose project layer exists on disk or is held with content.</summary>
    public static SortedSet<string> Levels()
    {
        var keys = new SortedSet<string>();
        if (DirAccess.DirExistsAbsolute(Dir))
            foreach (var k in DirAccess.GetDirectoriesAt(Dir))
                if (ResourceLoader.Exists($"{Dir}/{k}/{RemasterOverrides.FileName}"))
                    keys.Add(k.ToLowerInvariant());
        foreach (var (k, r) in Held)
            if (!IsEmpty(r))
                keys.Add(k);
        return keys;
    }

    /// <summary>Whether <paramref name="r"/> says nothing (no parts, no additions, original, classic, no notes).</summary>
    public static bool IsEmpty(MapOverrides r) =>
        r.Parts.Count == 0 && r.Additions == null && r.Status == MapStatus.Original
        && r.RenderPath == RenderPath.Classic && string.IsNullOrWhiteSpace(r.Notes);

    /// <summary>Whether <paramref name="key"/> has an additions scene beside its overrides.</summary>
    public static bool HasAdditions(string key) => ResourceLoader.Exists(AdditionsPathOf(key));

    /// <summary>
    /// Writes <paramref name="key"/>'s held resource (text, stable order: the parts sorted by ID,
    /// fixed resource IDs) and, unless <paramref name="keepAdditions"/>, <paramref name="additions"/>
    /// (null: none, the file is removed). A layer that says nothing removes both files. False on
    /// failure (logged).
    /// </summary>
    public static bool Write(string key, PackedScene? additions, bool keepAdditions = false)
    {
        var res = GetOrNew(key);
        var path = PathOf(key);
        var addPath = AdditionsPathOf(key);
        var dir = path.GetBaseDir();
        var parts = new List<PartOverride>(res.Parts);
        parts.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        res.Parts = new Godot.Collections.Array<PartOverride>(parts);
        res.Level = key;
        res.Additions = null;
        bool hasAdditions = keepAdditions ? HasAdditions(key) : additions != null;
        if (IsEmpty(res) && !hasAdditions)
        {
            Remove(path);
            Remove(addPath);
            if (DirAccess.DirExistsAbsolute(dir) && DirAccess.GetFilesAt(dir).Length == 0 && DirAccess.GetDirectoriesAt(dir).Length == 0)
                DirAccess.RemoveAbsolute(dir);
            Changed(key, path, addPath);
            GD.Print($"ffx remaster: {key}: nothing to override; {path} removed");
            return true;
        }
        DirAccess.MakeDirRecursiveAbsolute(dir);
        if (!keepAdditions)
        {
            if (additions != null)
            {
                additions.TakeOverPath(addPath);
                if (ResourceSaver.Save(additions, addPath) != Error.Ok)
                {
                    GD.PrintErr($"ffx remaster: cannot write {addPath}");
                    return false;
                }
                WithoutNodeIds(addPath);
            }
            else
                Remove(addPath);
        }
        StableIds(res, path);
        res.TakeOverPath(path);
        if (ResourceSaver.Save(res, path) != Error.Ok)
        {
            GD.PrintErr($"ffx remaster: cannot write {path}");
            return false;
        }
        Changed(key, path, addPath);
        GD.Print($"ffx remaster: {key}: wrote {path} ({res.Parts.Count} part(s), status {res.Status})"
            + (!keepAdditions && additions != null ? $" and {addPath}" : ""));
        return true;
    }

    /// <summary>
    /// Stable IDs in the text file at <paramref name="path"/>, so a save that changes nothing
    /// changes no byte: the IDs the file already had (Godot keeps an external resource's ID on
    /// that resource, per file that refers to it; a part's on the part), else fixed ones: the
    /// scripts <c>1</c> and <c>2</c>, each replacement scene <c>s_&lt;hash of its path&gt;</c>,
    /// each part <c>p_&lt;hash of its ID&gt;</c>.
    /// </summary>
    private static void StableIds(MapOverrides res, string path)
    {
        static void Ext(Resource? r, string path, string id)
        {
            if (r != null && r.GetIdForPath(path).Length == 0)
                r.SetIdForPath(path, id);
        }
        Ext(res.GetScript().As<Script>(), path, "1");
        var seen = new HashSet<string>();
        foreach (var p in res.Parts)
        {
            Ext(p.GetScript().As<Script>(), path, "2");
            var id = p.ResourceSceneUniqueId is { Length: > 0 } had ? had : $"p_{Hash(p.Id)}";
            while (!seen.Add(id))
                id += "x";
            p.ResourceSceneUniqueId = id;
            if (p.Scene is { ResourcePath: { Length: > 0 } s })
                Ext(p.Scene, path, $"s_{Hash(s)}");
        }
    }

    /// <summary>
    /// Drops the <c>unique_id</c> Godot writes on each node of a scene: the additions are copies
    /// made at each save, so the IDs would be new each time (a diff on every save). A scene
    /// without them loads the same.
    /// </summary>
    private static void WithoutNodeIds(string path)
    {
        var file = GameSettings.Globalize(path);
        var text = System.IO.File.ReadAllText(file);
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, @"^(\[node [^\n]*?) unique_id=-?\d+", "$1",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        if (stripped != text)
            System.IO.File.WriteAllText(file, stripped);
    }

    /// <summary>FNV-1a of <paramref name="s"/>, 8 hex digits.</summary>
    public static string Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
        {
            h ^= c;
            h *= 16777619;
        }
        return h.ToString("x8");
    }

    private static void Remove(string path)
    {
        if (FileAccess.FileExists(path))
            DirAccess.RemoveAbsolute(path);
    }

    /// <summary>Tells the editor's file system and the game's merged overrides that the files changed.</summary>
    private static void Changed(string key, params string[] paths)
    {
        RemasterOverrides.Invalidate(key);
        var fs = EditorInterface.Singleton.GetResourceFilesystem();
        foreach (var p in paths)
            if (p.StartsWith("res://", System.StringComparison.Ordinal))
                fs.UpdateFile(p);
    }
}
#endif
