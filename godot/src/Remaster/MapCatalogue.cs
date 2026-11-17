using System.Collections.Generic;
using System.Text.Json;
using GdFfx.Remake;

namespace GdFfx.Remaster;

/// <summary>One event (field map number) of a catalogue level.</summary>
public sealed record MapEvent(string Name, string Id, int Map, int MapIndex, int Entrances, int? PlaceIndex,
    IReadOnlyDictionary<string, string> Place);

/// <summary>An area: its codes (<c>bsil</c>, <c>bsyt</c>), the first its <see cref="Code"/>, and its titles by language.</summary>
public sealed record MapArea(string Code, IReadOnlyList<string> Codes, IReadOnlyDictionary<string, string> Title);

/// <summary>
/// A map's remaster state: derived from the project (whether it has overrides, a remade scene)
/// and set by hand (<see cref="Status"/>, notes, render path, in its overrides).
/// </summary>
public sealed record MapState(bool HasOverrides, bool HasRemade, MapStatus Status, RenderPath RenderPath, string Notes,
    IReadOnlyList<string> Layers)
{
    /// <summary>
    /// The status to show: the hand-set one, except that a map with overrides or a remade scene
    /// is at least <see cref="MapStatus.InProgress"/>.
    /// </summary>
    public MapStatus Effective =>
        Status == MapStatus.Original && (HasOverrides || HasRemade) ? MapStatus.InProgress : Status;
}

/// <summary>
/// One level of the catalogue (docs/importer.md, "The map catalogue"): a field level with its
/// events or a battle stage. <see cref="Key"/> (<c>bsil00</c>) is gd-ffx's map key,
/// <see cref="Level"/> the game's level name (<c>/map/bsil/bsil00/</c>).
/// </summary>
public sealed record MapEntry(string Kind, string Key, string Id, string Level, string Code, string Area,
    string? Model3D, string? Model2D, string? ModelId, IReadOnlyDictionary<string, string> Place, IReadOnlyList<MapEvent> Events)
{
    /// <summary>The place in <paramref name="language"/>, else English, else the key.</summary>
    public string Title(string language = "us") =>
        Place.TryGetValue(language, out var p) ? p : Place.TryGetValue("us", out var us) ? us
            : Kind == "battle" ? $"Battle stage {Key}" : Key;

    /// <summary>
    /// Its remaster state now: whether it has overrides (any layer) or a remade scene
    /// (<see cref="RemakeRegistry"/>), and the hand-set status, render path and notes of its
    /// overrides' last layer (<see cref="MapStatus.Original"/> without overrides).
    /// </summary>
    public MapState State()
    {
        var merged = RemasterOverrides.For(Level);
        var layers = new List<string>();
        if (merged != null)
            foreach (var l in merged.Layers)
                layers.Add(l.Name);
        return new MapState(merged != null, RemakeRegistry.Find("map", Level) != null,
            merged?.Status ?? MapStatus.Original, merged?.RenderPath ?? RenderPath.Classic, merged?.Notes ?? "", layers);
    }
}

/// <summary>
/// The map catalogue from the player's archive (<see cref="FfxGame.ClassicMapCatalogue"/>):
/// every field level and battle stage with its events, map numbers, place names in the game's
/// languages and area; each entry's <see cref="MapEntry.State"/> says what the project has for
/// it. Nothing of it is stored: it is read from the player's copy.
/// </summary>
public sealed class MapCatalogue
{
    public required IReadOnlyList<string> Languages { get; init; }
    public required IReadOnlyList<MapEntry> Levels { get; init; }
    public required IReadOnlyList<MapArea> Areas { get; init; }

    /// <summary>Events in the game's event list (with a map or not).</summary>
    public int EventCount { get; init; }

    /// <summary>Events without a map.</summary>
    public int Skipped { get; init; }

    private readonly Dictionary<string, MapEntry> _byKey = new();

    /// <summary>The level of a key, level name or event name (<c>bsil00</c>, <c>/map/bsil/bsil00/</c>, <c>bsil0000</c>); null: none.</summary>
    public MapEntry? Find(string name)
    {
        if (_byKey.Count == 0)
            foreach (var l in Levels)
            {
                _byKey.TryAdd(l.Key, l);
                foreach (var e in l.Events)
                    _byKey.TryAdd(e.Name, l);
            }
        return _byKey.GetValueOrDefault(PartIds.LevelKey(name));
    }

    /// <summary>The level of map number <paramref name="map"/> (<c>World.map</c>); null: none.</summary>
    public MapEntry? FindMap(int map)
    {
        foreach (var l in Levels)
            foreach (var e in l.Events)
                if (e.Map == map)
                    return l;
        return null;
    }

    /// <summary>The catalogue of the open archive (main thread; null without one).</summary>
    public static MapCatalogue? Read(FfxGame game, params string[] languages)
    {
        var json = game.ClassicMapCatalogue(languages);
        return json.Length == 0 ? null : Parse(json);
    }

    /// <summary>The catalogue from <c>classic_map_catalogue</c>'s JSON.</summary>
    public static MapCatalogue Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        static string S(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        static string? N(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int I(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
        static Dictionary<string, string> Texts(JsonElement e, string name)
        {
            var d = new Dictionary<string, string>();
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
                foreach (var p in v.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                        d[p.Name] = p.Value.GetString()!;
            return d;
        }
        static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
        var levels = new List<MapEntry>();
        foreach (var l in Items(root, "levels"))
        {
            var events = new List<MapEvent>();
            foreach (var e in Items(l, "events"))
                events.Add(new MapEvent(S(e, "name"), S(e, "id"), I(e, "map"), I(e, "map_index"), I(e, "entrances"),
                    I(e, "place_index") is >= 0 and var pi ? pi : null, Texts(e, "place")));
            levels.Add(new MapEntry(S(l, "kind"), S(l, "key"), S(l, "id"), S(l, "level"), S(l, "code"), S(l, "area"),
                N(l, "model_3d"), N(l, "model_2d"), N(l, "model_id"), Texts(l, "place"), events));
        }
        var areas = new List<MapArea>();
        foreach (var a in Items(root, "areas"))
        {
            var codes = new List<string>();
            foreach (var c in Items(a, "codes"))
                if (c.ValueKind == JsonValueKind.String)
                    codes.Add(c.GetString()!);
            areas.Add(new MapArea(S(a, "code"), codes, Texts(a, "title")));
        }
        var langs = new List<string>();
        foreach (var l in Items(root, "languages"))
            if (l.ValueKind == JsonValueKind.String)
                langs.Add(l.GetString()!);
        int skipped = 0;
        foreach (var _ in Items(root, "skipped"))
            skipped++;
        return new MapCatalogue
        {
            Languages = langs, Levels = levels, Areas = areas, EventCount = I(root, "events"), Skipped = skipped,
        };
    }
}
