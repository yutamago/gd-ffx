using System.Collections.Generic;
using System.Text.Json;

namespace GdFfx.Characters;

/// <summary>
/// The game's characters (docs/remaster-editor.md, "Characters"): every character model of the
/// player's archive (<c>c001</c>...) with its kind, the game's names where it gives one (the
/// party's, monsters'), whether it has motions, and its attachment points (point id to element:
/// 9 the head, 13 the jaw, 4 and 5 the hands' weapon points on the party). From
/// <see cref="FfxGame.ClassicCharacters"/>.
/// </summary>
public sealed class CharacterCatalogue
{
    public sealed record Entry(string Name, string Kind, bool Motions, IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<int, int> Points)
    {
        /// <summary>The name in <paramref name="language"/>, else English, else any, else empty.</summary>
        public string Title(string language = "us") =>
            Names.TryGetValue(language, out var n) ? n : Names.TryGetValue("us", out n) ? n : Names.Count > 0 ? First(Names) : "";

        private static string First(IReadOnlyDictionary<string, string> d)
        {
            foreach (var v in d.Values)
                return v;
            return "";
        }
    }

    public List<Entry> Entries { get; } = new();

    public Entry? Find(string name) => Entries.Find(e => e.Name == name);

    /// <summary>The catalogue from the extension (null without the archive or when it does not read).</summary>
    public static CharacterCatalogue? Read(FfxGame game, params string[] languages) =>
        Parse(game.ClassicCharacters(languages));

    public static CharacterCatalogue? Parse(string json)
    {
        if (json.Length == 0)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var cat = new CharacterCatalogue();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var names = new Dictionary<string, string>();
                if (e.TryGetProperty("names", out var ns))
                    foreach (var p in ns.EnumerateObject())
                        names[p.Name] = p.Value.GetString() ?? "";
                var points = new Dictionary<int, int>();
                if (e.TryGetProperty("points", out var ps))
                    foreach (var p in ps.EnumerateArray())
                        points.TryAdd(p.GetProperty("id").GetInt32(), p.GetProperty("element").GetInt32());
                cat.Entries.Add(new Entry(e.GetProperty("name").GetString() ?? "", e.GetProperty("kind").GetString() ?? "",
                    e.GetProperty("motions").GetBoolean(), names, points));
            }
            return cat;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
