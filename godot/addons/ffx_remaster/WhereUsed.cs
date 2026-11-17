#if TOOLS
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>One map's use of a texture: the level key and the part IDs drawn with it (relative to the map's model).</summary>
public sealed record TextureUse(string Level, IReadOnlyList<string> Parts);

/// <summary>
/// The where-used index (docs/remaster-editor.md, "Where used"): which maps and parts draw each
/// texture, over every map of the catalogue with a 3D model, read from the player's archive
/// (<see cref="FfxGame.ClassicTextureUses"/>: parsing only) a few maps a frame, so the editor
/// stays responsive, then cached in <c>user://remaster/texture-uses.json</c> (never in git),
/// keyed by the archive (the game folder, <c>FFX_Data.vbf</c>'s size and time) and the format:
/// a game update or a new format builds it again. Characters, menus and effects are not in it.
/// </summary>
public sealed class WhereUsed
{
    public const int Format = 1;
    public const string CachePath = "user://remaster/texture-uses.json";

    private readonly Dictionary<string, List<TextureUse>> _byTexture = new();
    private readonly Dictionary<string, List<string>> _byLevel = new();
    private readonly Queue<string> _todo = new();
    private JsonObject _levels = new();
    private string _key = "";
    private EditorClassic? _session;

    /// <summary>Levels read, of all.</summary>
    public (int Done, int Total) Progress { get; private set; }

    public bool Ready { get; private set; }

    public bool Building => _todo.Count > 0;

    /// <summary>How long the last build took (ms; 0: from the cache).</summary>
    public long BuildMs { get; private set; }

    private ulong _started;

    /// <summary>The textures known, by ID.</summary>
    public IReadOnlyDictionary<string, List<TextureUse>> ByTexture => _byTexture;

    /// <summary>Each level's textures (by key).</summary>
    public IReadOnlyDictionary<string, List<string>> ByLevel => _byLevel;

    /// <summary><paramref name="id"/>'s uses (empty: none known).</summary>
    public IReadOnlyList<TextureUse> Uses(string id) => _byTexture.TryGetValue(id.ToLowerInvariant(), out var l) ? l : [];

    /// <summary>The cache key of the open archive.</summary>
    private static string KeyOf(EditorClassic s)
    {
        var vbf = System.IO.Path.Combine(s.GameDir, "FFX_Data.vbf");
        var info = new System.IO.FileInfo(vbf);
        return info.Exists ? $"{Format}|{s.GameDir}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : $"{Format}|{s.GameDir}";
    }

    /// <summary>Loads the cache for the open archive, or starts building (<see cref="Step"/>).</summary>
    public void Start(EditorClassic s, bool rebuild = false)
    {
        _session = s;
        _byTexture.Clear();
        _byLevel.Clear();
        _todo.Clear();
        Ready = false;
        BuildMs = 0;
        if (!s.IsOpen || s.Catalogue == null)
            return;
        _key = KeyOf(s);
        if (!rebuild && Load())
        {
            Ready = true;
            GD.Print($"ffx remaster: where-used from {CachePath}: {_byTexture.Count} textures in {_byLevel.Count} maps");
            return;
        }
        _levels = new JsonObject();
        foreach (var e in s.Catalogue.Levels)
            if (e.Model3D != null)
                _todo.Enqueue(e.Level);
        Progress = (0, _todo.Count);
        _started = Time.GetTicksMsec();
        GD.Print($"ffx remaster: building the where-used index over {_todo.Count} maps");
    }

    /// <summary>Reads maps for up to <paramref name="budgetMs"/> (once a frame while <see cref="Building"/>); true when it finished now.</summary>
    public bool Step(double budgetMs = 30)
    {
        if (_todo.Count == 0 || _session?.Game is not { } game)
            return false;
        var t0 = Time.GetTicksUsec();
        while (_todo.Count > 0 && (Time.GetTicksUsec() - t0) / 1000.0 < budgetMs)
        {
            var level = _todo.Dequeue();
            var json = game.ClassicTextureUses(level);
            var key = PartIds.LevelKey(level);
            if (json.Length > 0 && JsonNode.Parse(json) is JsonObject o)
            {
                _levels[key] = o;
                Add(key, o);
            }
            Progress = (Progress.Done + 1, Progress.Total);
        }
        if (_todo.Count > 0)
            return false;
        BuildMs = (long)(Time.GetTicksMsec() - _started);
        Save();
        Ready = true;
        GD.Print($"ffx remaster: where-used built: {_byTexture.Count} textures in {_byLevel.Count} maps ({BuildMs} ms); cached in {CachePath}");
        return true;
    }

    private void Add(string level, JsonObject o)
    {
        var ids = new List<string>();
        if (o["textures"] is not JsonArray textures)
            return;
        foreach (var t in textures)
        {
            if (t?["id"]?.GetValue<string>() is not { } id)
                continue;
            var parts = new List<string>();
            if (t["parts"] is JsonArray ps)
                foreach (var p in ps)
                    if (p?.GetValue<string>() is { } s)
                        parts.Add(s);
            if (!_byTexture.TryGetValue(id, out var uses))
                _byTexture[id] = uses = new();
            uses.Add(new TextureUse(level, parts));
            ids.Add(id);
        }
        _byLevel[level] = ids;
    }

    private bool Load()
    {
        if (!FileAccess.FileExists(CachePath))
            return false;
        try
        {
            if (JsonNode.Parse(FileAccess.GetFileAsString(CachePath)) is not JsonObject root
                || root["key"]?.GetValue<string>() != _key || root["levels"] is not JsonObject levels)
                return false;
            _levels = levels;
            foreach (var (k, v) in levels)
                if (v is JsonObject o)
                    Add(k, o);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void Save()
    {
        DirAccess.MakeDirRecursiveAbsolute(CachePath.GetBaseDir());
        var root = new JsonObject { ["key"] = _key, ["levels"] = _levels.DeepClone() };
        using var f = FileAccess.Open(CachePath, FileAccess.ModeFlags.Write);
        f?.StoreString(root.ToJsonString());
    }
}
#endif
