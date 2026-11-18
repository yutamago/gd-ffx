#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GdFfx.Remake;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>One benchmark run's budget record (tools/budget.sh, tools/compare/budget.py), as the dashboard shows it.</summary>
public sealed record BudgetRecord(string Key, string Preset, string Path, string Gi, string Adapter, bool Software, int Width, int Height,
    double FrameMs, double GpuMs, double DrawCalls, double TextureMib, double VideoMib, double? GiMs, bool Ok, string Over, string When)
{
    public string Short => System.FormattableString.Invariant(
        $"{Preset} {Path}{(Gi != "None" ? " " + Gi : "")}: {FrameMs:0.0} ms{(GpuMs > 0 ? System.FormattableString.Invariant($" (GPU {GpuMs:0.0})") : "")}, {DrawCalls:0} draws, {TextureMib:0} MiB tex{(GiMs is { } g ? System.FormattableString.Invariant($", GI {g:0.0} ms") : "")}")
        + (Software ? " [llvmpipe]" : Ok ? "" : $" OVER {Over}");
}

/// <summary>A map's row in the status dashboard: its status and what the project changes on it, its last renders, its budgets.</summary>
public sealed class MapStatusRow
{
    public required MapEntry Entry { get; init; }
    public required DockState Dock { get; init; }
    public int OverrideParts { get; init; }
    public bool Additions { get; init; }
    public bool Lighting { get; init; }
    public bool Rebuilt { get; init; }
    public bool Remade { get; init; }
    /// <summary>The map's textures (where-used; -1 while the index is not ready), the replaced ones, the ones with a material.</summary>
    public int Textures { get; init; } = -1;
    public int Replaced { get; init; }
    public int Materials { get; init; }
    /// <summary>Remade effects of the map's field programs (<c>&lt;key&gt;-fp&lt;n&gt;</c>).</summary>
    public int Effects { get; init; }
    public RenderRecord? Render { get; init; }
    public List<BudgetRecord> Budgets { get; init; } = new();

    public MapStatus Status => Dock.Effective;

    public bool Is2D => Entry.Model3D == null;

    /// <summary>Whether the project changes anything on the map.</summary>
    public bool Changed => Dock.Changed || Lighting || Rebuilt || Replaced > 0 || Materials > 0 || Effects > 0;

    public string ChangesText => string.Join(", ", new[]
    {
        OverrideParts > 0 ? $"overrides {OverrideParts}" : null, Additions ? "additions" : null,
        Replaced > 0 ? $"replaced textures {Replaced}" : null, Materials > 0 ? $"materials {Materials}" : null,
        Lighting ? "lighting" : null, Rebuilt ? "rebuilt room" : Remade ? "remade scene" : null,
        Effects > 0 ? $"effects {Effects}" : null, Dock.RenderPath == RenderPath.Remastered ? "PBR" : null,
    }.Where(s => s != null));

    /// <summary>The budgets' verdict: "" (none measured), "ok", "over", "llvmpipe" (software only).</summary>
    public string BudgetVerdict => Budgets.Count == 0 ? "" : Budgets.Any(b => !b.Software && !b.Ok) ? "over" : Budgets.All(b => b.Software) ? "llvmpipe" : "ok";

    public string RenderText => Render == null ? "" : Render.State != "done" ? Render.State + (Render.Reason.Length > 0 ? $": {Render.Reason}" : "")
        : Render.Kind == "3d" ? System.FormattableString.Invariant($"{(Render.ColourOk ? "colour ok" : "colour OFF")} dL {Render.DL:+0.000;-0.000;0.000} x{Render.Chroma:0.00} {Render.Hue:0.0}°")
        : Render.Kind == "2d-rebuilt" ? System.FormattableString.Invariant($"rebuilt, diff {Render.Diff:0.00}/255") : "classic layers";
}

/// <summary>The project's progress (the dashboard's summary, the report's head).</summary>
public sealed record StatusSummary(int Maps, Dictionary<MapStatus, int> ByStatus, int Changed, int TexturesReplaced, int TexturesWithMaterials,
    int TexturesInIndex, int MapsWithLighting, int RebuiltRooms, int RemadeEffects, int RemadeCharacters, int Sounds, int Movies, int UiArt,
    int Rendered, int ColourOff, int BudgetsMeasured, int BudgetsOver)
{
    public override string ToString() =>
        $"{Maps} maps: " + string.Join(", ", ByStatus.OrderBy(k => k.Key).Select(kv => $"{kv.Key} {kv.Value}"))
        + $"; {Changed} changed by the project; textures: {TexturesWithMaterials} with materials, {TexturesReplaced} replaced (of {TexturesInIndex} in the project's index); "
        + $"{MapsWithLighting} with lighting, {RebuiltRooms} rebuilt rooms, {RemadeEffects} remade effects, {RemadeCharacters} remade characters, "
        + $"{Sounds} sounds, {Movies} movies, {UiArt} UI art; renders {Rendered} ({ColourOff} with the colour check off); budgets {BudgetsMeasured} maps ({BudgetsOver} over)";
}

/// <summary>
/// The status dashboard's data (R8; docs/remaster-editor.md, "The status dashboard"): every map of the
/// catalogue with its status and what the project changes (overrides, additions, textures and
/// materials by the where-used index, lighting, a rebuilt room or remade scene, remade effects of
/// its field programs), its last renders (<see cref="RenderStore"/>) and budgets
/// (<see cref="BudgetsDir"/>); a summary over the project. The same rows make the HTML report.
/// </summary>
public static class RemasterStatus
{
    /// <summary>Where the budgets' tables are read: <c>--remaster-budgets=DIR</c>, else <c>user://remaster/budgets</c> (tools/budget.sh writes there).</summary>
    public static string BudgetsDir => GameSettings.Arg("remaster-budgets") is { Length: > 0 } d ? d.TrimEnd('/') : "user://remaster/budgets";

    public static MapStatusRow Of(RemasterPlugin plugin, MapEntry e, RenderState? renders = null, Dictionary<string, List<BudgetRecord>>? budgets = null)
    {
        var res = MapStore.Find(e.Key);
        int tex = -1, replaced = 0, materials = 0;
        if (plugin.Uses.Ready && plugin.Uses.ByLevel.TryGetValue(e.Key, out var ids))
        {
            tex = ids.Count;
            foreach (var id in ids)
                if (TextureStore.Find(id) is { } t)
                {
                    if (t.Replacement.Length > 0)
                        replaced++;
                    if (t.Material.Length > 0)
                        materials++;
                }
        }
        else if (plugin.Uses.Ready)
            tex = 0;
        var prefix = e.Key + "-fp";
        int effects = EffectIds().Count(id => id.StartsWith(prefix, System.StringComparison.Ordinal));
        var rebuiltPath = RemasterPlugin.RoomScenePath(e.Key);
        var remade = RemakeRegistry.Find("map", e.Level);
        return new MapStatusRow
        {
            Entry = e,
            Dock = DockState.Of(e),
            OverrideParts = res?.Parts.Count ?? 0,
            Additions = MapStore.HasAdditions(e.Key),
            Lighting = FileAccess.FileExists(LightingEdit.PathOf(e.Key)),
            Rebuilt = FileAccess.FileExists(rebuiltPath) || remade != null && e.Model3D == null,
            Remade = remade != null,
            Textures = tex,
            Replaced = replaced,
            Materials = materials,
            Effects = effects,
            Render = (renders ?? RenderStore.Load()).Maps.GetValueOrDefault(e.Key),
            Budgets = (budgets ?? Budgets()).GetValueOrDefault(e.Key) ?? new(),
        };
    }

    /// <summary>Every map of the catalogue (the renders and budgets read once).</summary>
    public static List<MapStatusRow> All(RemasterPlugin plugin)
    {
        var cat = plugin.Session.Catalogue;
        if (cat == null)
            return new();
        var renders = RenderStore.Load();
        var budgets = Budgets();
        _effects = null;
        return cat.Levels.Select(e => Of(plugin, e, renders, budgets)).ToList();
    }

    private static List<string>? _effects;

    /// <summary>The ids of remade effects: the editor's folder's scenes and the registry's entries.</summary>
    public static List<string> EffectIds()
    {
        if (_effects != null)
            return _effects;
        var ids = new SortedSet<string>();
        var dir = MediaStore.EffectScenesDir;
        if (DirAccess.DirExistsAbsolute(GameSettings.Globalize(dir)))
            foreach (var f in DirAccess.GetFilesAt(dir))
                if (f.EndsWith(".tscn", System.StringComparison.Ordinal))
                    ids.Add(f[..^5]);
        foreach (var (k, v) in RemakeRegistry.Entries)
            if (k.Kind == "effect" && v.Scene.Length > 0)
                ids.Add(k.Id);
        return _effects = ids.ToList();
    }

    public static StatusSummary Summarise(IReadOnlyList<MapStatusRow> rows)
    {
        var by = System.Enum.GetValues<MapStatus>().ToDictionary(s => s, s => rows.Count(r => r.Status == s));
        var set = TextureStore.Set.Textures.Where(t => t != null).ToList();
        int chars = 0;
        var cdir = CharacterStore.Dir;
        if (DirAccess.DirExistsAbsolute(GameSettings.Globalize(cdir)))
            chars = DirAccess.GetFilesAt(cdir).Count(f => f.EndsWith(".tscn", System.StringComparison.Ordinal));
        int ui = Entries(RemasterPlugin.UiArtFile, "RemasterUiArt.cs");
        return new StatusSummary(rows.Count, by, rows.Count(r => r.Changed), set.Count(t => t.Replacement.Length > 0), set.Count(t => t.Material.Length > 0),
            set.Count, rows.Count(r => r.Lighting), rows.Count(r => r.Rebuilt), EffectIds().Count, chars,
            Entries(MediaStore.AudioFile, "RemasterSound.cs"), Entries(MediaStore.MoviesFile, "RemasterMovie.cs"), ui,
            rows.Count(r => r.Render is { State: "done" }), rows.Count(r => r.Render is { State: "done", ColourOk: false }),
            rows.Count(r => r.Budgets.Count > 0), rows.Count(r => r.BudgetVerdict == "over"));
    }

    /// <summary>
    /// The entries of a set's text file whose script is <paramref name="script"/> (counted in the
    /// text: loading the set again here, beside the docks that hold it, left a disposed C# instance
    /// that took the editor down at exit).
    /// </summary>
    public static int Entries(string path, string script)
    {
        if (!FileAccess.FileExists(path))
            return 0;
        var text = FileAccess.GetFileAsString(path);
        var m = System.Text.RegularExpressions.Regex.Match(text, $@"\[ext_resource type=""Script""[^\]]*path=""[^""]*/{System.Text.RegularExpressions.Regex.Escape(script)}"" id=""([^""]+)""");
        return m.Success ? System.Text.RegularExpressions.Regex.Matches(text, $@"^script = ExtResource\(""{System.Text.RegularExpressions.Regex.Escape(m.Groups[1].Value)}""\)", System.Text.RegularExpressions.RegexOptions.Multiline).Count : 0;
    }

    /// <summary>The budgets' records by map key, from every table in <see cref="BudgetsDir"/>.</summary>
    public static Dictionary<string, List<BudgetRecord>> Budgets()
    {
        var by = new Dictionary<string, List<BudgetRecord>>();
        var dir = GameSettings.Globalize(BudgetsDir);
        if (!System.IO.Directory.Exists(dir))
            return by;
        foreach (var file in System.IO.Directory.GetFiles(dir, "*.json").Order())
        {
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("records", out var recs))
                    continue;
                foreach (var r in recs.EnumerateArray())
                {
                    string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    double D(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                    var over = r.TryGetProperty("verdicts", out var vs) ? string.Join(",", vs.EnumerateObject().Where(p => p.Value.GetString() == "over").Select(p => p.Name)) : "";
                    var b = new BudgetRecord(S("key"), S("preset"), S("path"), S("gi"), S("adapter"),
                        r.TryGetProperty("software", out var sw) && sw.ValueKind == JsonValueKind.True, (int)D("width"), (int)D("height"),
                        D("frame_ms"), D("gpu_ms"), D("draw_calls"), D("texture_mib"), D("video_mib"),
                        r.TryGetProperty("gi_ms", out var gi) && gi.ValueKind == JsonValueKind.Number ? gi.GetDouble() : null,
                        !r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False, over, S("when"));
                    if (b.Key.Length == 0)
                        continue;
                    if (!by.TryGetValue(b.Key, out var list))
                        by[b.Key] = list = new();
                    list.Add(b);
                }
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"ffx remaster: budgets: {file}: {ex.Message}");
            }
        }
        return by;
    }
}
#endif
