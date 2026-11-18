using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GdFfx.Api;
using Godot;

namespace GdFfx.Remaster.Wizard;

/// <summary>
/// The wizard classifier's accuracy (<c>--selftest-wizard-classifier</c>, with the game's archive:
/// <c>--devsave=7</c>; docs/remaster-plan.md, "R4 in detail"): every texture of the hand-labelled
/// set (<see cref="LabelsPath"/>: texture IDs and kinds, labelled by eye from thumbnails; no
/// images) classified from its classic pixels and how its map draws it (each map's classic import
/// loaded), against its label: the accuracy overall, per kind (recall and precision), the
/// confusion matrix, and the same for the held-out areas (<see cref="HeldOut"/>: not looked at while
/// the rules were tuned). <c>--classifier-dump=FILE</c> writes each texture's features and uses
/// (JSON), <c>--classifier-features=FILE</c> classifies from such a file without the game (tuning).
/// Fails under <see cref="MinAccuracy"/> overall.
/// </summary>
public sealed class ClassifierCheck(double timeout) : ITest
{
    public const string LabelsPath = "res://remaster/classifier-labels.tsv";

    /// <summary>The areas the rules were tuned on (the prototypes are their medians).</summary>
    public static readonly string[] Tuned = ["bsil", "lchb", "mcfr", "maca", "bvyt"];

    /// <summary>Zanarkand: held out at first, then looked at once (it showed that "wall" says nothing; the rule went).</summary>
    public static readonly string[] Seen = ["znkd"];

    /// <summary>The areas labelled only after the rules were frozen: the honest numbers.</summary>
    public static readonly string[] HeldOut = ["klyt", "mihn", "djyt", "guad", "kami", "mtgz"];

    /// <summary>The check's floor: the classifier must beat this overall (it is reported, whatever it is).</summary>
    public const float MinAccuracy = 0.6f;

    public sealed record Label(TextureKind Kind, string Map, string Id, string Note);

    private enum Phase { Wait, Import, Classify }

    private Phase _phase = Phase.Wait;
    private double _t;
    private List<Label> _labels = new();
    private readonly Dictionary<string, Dictionary<string, TextureUseInfo>> _uses = new();
    private readonly Queue<string> _maps = new();

    public string Note { get; private set; } = "";

    /// <summary>The labelled set (kind, map, texture ID, note), from <paramref name="path"/>.</summary>
    public static List<Label> ReadLabels(string path = LabelsPath)
    {
        var list = new List<Label>();
        if (!FileAccess.FileExists(path))
            return list;
        foreach (var raw in FileAccess.GetFileAsString(path).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var cols = line.Split('\t');
            if (cols.Length < 3 || TextureClassifier.Parse(cols[0]) is not { } k)
            {
                GD.PrintErr($"ffx selftest: classifier labels: cannot read \"{line}\"");
                continue;
            }
            list.Add(new Label(k, cols[1].Trim(), cols[2].Trim().ToLowerInvariant(), cols.Length > 3 ? cols[3].Trim().TrimStart('#').Trim() : ""));
        }
        return list;
    }

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _t += delta;
        if (_t > timeout)
            return Done(false, $"timed out in {_phase} ({_maps.Count} map(s) to import)");
        switch (_phase)
        {
            case Phase.Wait:
                if (GameSettings.Arg("classifier-features") is { Length: > 0 } file)
                    return FromFile(file);
                if (GameHost.Instance?.Classic is not { Open: true })
                    return null;
                _labels = ReadLabels();
                if (_labels.Count == 0)
                    return Done(false, $"no labels in {LabelsPath}");
                foreach (var m in _labels.Select(l => l.Map).Distinct())
                    _maps.Enqueue(m);
                _phase = Phase.Import;
                break;
            case Phase.Import:
            {
                var classic = GameHost.Instance!.Classic!;
                classic.Poll();
                // one map at a time: imported (the classic cache), loaded, read, freed
                if (_maps.Count == 0)
                {
                    _phase = Phase.Classify;
                    break;
                }
                var key = _maps.Peek();
                var level = LevelOf(key);
                if (classic.Path("map", level) is not { } path)
                {
                    if (classic.Failed("map", level))
                    {
                        GD.PrintErr($"ffx selftest: classifier: {level} did not import; its textures are classified without their uses");
                        _maps.Dequeue();
                    }
                    return null;
                }
                _maps.Dequeue();
                if (ClassicLibrary.Load(path, $"PS3Data{level}tex/") is { } map)
                {
                    ClassicMaterials.FixLayers(map);
                    map.Scale = Vector3.One * ClassicLibrary.MapScale;
                    _uses[key] = MapTextures.Collect(map).Uses;
                    map.Free();
                }
                break;
            }
            case Phase.Classify:
            {
                var rows = new List<(Label Label, TextureFeatures F, TextureUseInfo? Use)>();
                foreach (var l in _labels)
                {
                    if (RemasterTextures.ClassicImage(l.Id) is not { } img)
                        return Done(false, $"no classic pixels for {l.Id}");
                    var use = _uses.TryGetValue(l.Map, out var u) ? u.GetValueOrDefault(l.Id) : null;
                    rows.Add((l, TextureFeatures.Of(PbrBuilder.From(img)), use));
                }
                if (GameSettings.Arg("classifier-dump") is { Length: > 0 } dump)
                    Dump(dump, rows);
                return Report(rows);
            }
        }
        return null;
    }

    /// <summary>The game's level name of a map key (<c>bsil00</c> → <c>/map/bsil/bsil00/</c>).</summary>
    public static string LevelOf(string key) => $"/map/{key[..4]}/{key}/";

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false, WriteIndented = false };

    private static void Dump(string file, List<(Label Label, TextureFeatures F, TextureUseInfo? Use)> rows)
    {
        var arr = new JsonArray();
        foreach (var (l, f, u) in rows)
            arr.Add(new JsonObject
            {
                ["kind"] = TextureClassifier.Name(l.Kind),
                ["map"] = l.Map,
                ["id"] = l.Id,
                ["note"] = l.Note,
                ["features"] = JsonSerializer.SerializeToNode(f, Json),
                ["use"] = u == null ? null : new JsonObject
                {
                    ["Surfaces"] = u.Surfaces, ["Area"] = u.Area, ["Up"] = u.Up, ["Down"] = u.Down, ["Blend"] = u.Blend, ["AlphaTest"] = u.AlphaTest,
                    ["Additive"] = u.Additive, ["Scroll"] = u.Scroll, ["Morph"] = u.Morph, ["Sky"] = u.Sky, ["Special"] = u.Special,
                },
            });
        System.IO.File.WriteAllText(file, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"ffx selftest: classifier: features of {rows.Count} textures in {file}");
    }

    /// <summary>The rows of a <c>--classifier-dump</c> file classified again (no game: for tuning the rules).</summary>
    public bool? FromFile(string file)
    {
        var rows = new List<(Label Label, TextureFeatures F, TextureUseInfo? Use)>();
        foreach (var n in JsonNode.Parse(System.IO.File.ReadAllText(file))!.AsArray())
        {
            var o = n!.AsObject();
            var f = o["features"].Deserialize<TextureFeatures>(Json)!;
            TextureUseInfo? u = null;
            if (o["use"] is JsonObject uo)
                u = new TextureUseInfo
                {
                    Id = o["id"]!.GetValue<string>(), Surfaces = uo["Surfaces"]!.GetValue<int>(), Area = uo["Area"]!.GetValue<float>(),
                    Up = uo["Up"]!.GetValue<float>(), Down = uo["Down"]!.GetValue<float>(), Blend = uo["Blend"]!.GetValue<float>(),
                    AlphaTest = uo["AlphaTest"]!.GetValue<float>(), Additive = uo["Additive"]!.GetValue<float>(), Scroll = uo["Scroll"]!.GetValue<float>(),
                    Morph = uo["Morph"]!.GetValue<float>(), Sky = uo["Sky"]!.GetValue<bool>(), Special = uo["Special"]!.GetValue<bool>(),
                };
            // the labels as the file says now (relabelled since the dump: the file's kinds win)
            var id = o["id"]!.GetValue<string>();
            var kind = ReadLabels().FirstOrDefault(l => l.Id == id)?.Kind ?? TextureClassifier.Parse(o["kind"]!.GetValue<string>()) ?? TextureKind.Keep;
            rows.Add((new Label(kind, o["map"]!.GetValue<string>(), id, o["note"]?.GetValue<string>() ?? ""), f, u));
        }
        return Report(rows);
    }

    /// <summary>The accuracy of <paramref name="rows"/> (overall, per kind, the confusion matrix; the held-out areas apart).</summary>
    public bool? Report(List<(Label Label, TextureFeatures F, TextureUseInfo? Use)> rows)
    {
        var results = rows.Select(r => (r.Label, C: TextureClassifier.Classify(r.Label.Id, r.F, r.Use, r.Label.Map), r.F, r.Use)).ToList();
        bool verbose = GameSettings.Flag("classifier-verbose");
        foreach (var (l, c, f, u) in results)
            if (verbose || c.Kind != l.Kind)
                GD.Print($"ffx selftest: classifier {(c.Kind == l.Kind ? "ok  " : "MISS")} {l.Map} {RemasterTextures.ShortName(l.Id)}: label {TextureClassifier.Name(l.Kind)}, "
                    + $"chose {c.Explain()}{(verbose ? $" | {f} | {u}" : "")}");
        var all = Summary("all", results.Select(r => (r.Label.Kind, r.C.Kind)).ToList());
        List<(TextureKind, TextureKind)> Of(string[] areas) => results.Where(r => areas.Contains(r.Label.Map[..4])).Select(r => (r.Label.Kind, r.C.Kind)).ToList();
        var t = Summary($"tuned on ({string.Join(", ", Tuned)})", Of(Tuned));
        var z = Of(Seen) is { Count: > 0 } zs ? "; " + Summary($"seen once ({string.Join(", ", Seen)})", zs) : "";
        var h = Of(HeldOut) is { Count: > 0 } hs ? Summary($"held out ({string.Join(", ", HeldOut)})", hs) : "";
        float acc = results.Count(r => r.C.Kind == r.Label.Kind) / (float)System.Math.Max(1, results.Count);
        // the profile that would be made instead, where it matters: a kept texture made, or a material kept
        int keptWrongly = results.Count(r => r.C.Kind == TextureKind.Keep && r.Label.Kind != TextureKind.Keep);
        int madeWrongly = results.Count(r => r.C.Kind != TextureKind.Keep && r.Label.Kind == TextureKind.Keep);
        // leave one area out: each area scored with prototypes fitted to the other areas' labels (the honest number)
        var loao = new List<(TextureKind, TextureKind)>();
        foreach (var area in rows.Select(r => r.Label.Map[..4]).Distinct())
        {
            var protos = TextureClassifier.Fit(rows.Where(r => r.Label.Map[..4] != area).Select(r => (r.Label.Kind, r.F)));
            foreach (var r in rows.Where(r => r.Label.Map[..4] == area))
                loao.Add((r.Label.Kind, TextureClassifier.Classify(r.Label.Id, r.F, r.Use, r.Label.Map, protos).Kind));
        }
        var lo = Summary("leave one area out", loao);
        var note = $"{all}; {lo}; {t}{z}{(h.Length > 0 ? "; " + h : "")}; material/classic mistakes: {madeWrongly} kept textures given a material, {keptWrongly} materials kept classic";
        return Done(acc >= MinAccuracy, acc >= MinAccuracy ? note : $"accuracy {acc:P0} under {MinAccuracy:P0}: {note}");
    }

    /// <summary>One set's accuracy, per kind (recall/precision), and its confusion matrix printed.</summary>
    private static string Summary(string name, List<(TextureKind Want, TextureKind Got)> pairs)
    {
        var kinds = TextureClassifier.Kinds;
        int ok = pairs.Count(p => p.Want == p.Got);
        var sb = new StringBuilder();
        sb.Append($"{name}: {ok}/{pairs.Count} ({(pairs.Count > 0 ? ok * 100.0 / pairs.Count : 0):0}%)");
        var per = new List<string>();
        foreach (var k in kinds)
        {
            int n = pairs.Count(p => p.Want == k);
            int got = pairs.Count(p => p.Got == k);
            if (n == 0 && got == 0)
                continue;
            int hit = pairs.Count(p => p.Want == k && p.Got == k);
            per.Add($"{TextureClassifier.Name(k)} {hit}/{n} (precision {(got > 0 ? $"{hit}/{got}" : "-")})");
        }
        sb.Append(" [" + string.Join(", ", per) + "]");
        // the matrix: rows the labels, columns what was chosen
        var head = "label \\ chose " + string.Join(" ", kinds.Select(k => TextureClassifier.Name(k)[..4].PadLeft(5)));
        GD.Print($"ffx selftest: classifier confusion ({name}): {head}");
        foreach (var w in kinds)
        {
            if (pairs.All(p => p.Want != w))
                continue;
            GD.Print($"ffx selftest: classifier confusion ({name}): {TextureClassifier.Name(w),-13} "
                + string.Join(" ", kinds.Select(g => pairs.Count(p => p.Want == w && p.Got == g).ToString().PadLeft(5))));
        }
        return sb.ToString();
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }

    /// <summary><c>--selftest-wizard-classifier --no-game --classifier-features=FILE</c>: the labelled set classified from a dump, no game (tuning the rules).</summary>
    public static void RunAlone(Node parent)
    {
        var check = new ClassifierCheck(60);
        bool pass = GameSettings.Arg("classifier-features") is { Length: > 0 } file && check.FromFile(file) == true;
        GD.Print(pass ? $"ffx selftest: PASS ({check.Note})" : $"ffx selftest: FAIL ({check.Note})");
        parent.GetTree().Quit(pass ? 0 : 1);
    }
}
