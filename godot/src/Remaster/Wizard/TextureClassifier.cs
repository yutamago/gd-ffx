using System;
using System.Collections.Generic;
using System.Linq;

namespace GdFfx.Remaster.Wizard;

/// <summary>What a texture is, for the wizard: one of the starter profiles' kinds, or <see cref="Keep"/> (keep it classic: sky, water, effects, the painted layers, UI-like art).</summary>
public enum TextureKind
{
    Keep,
    Rock,
    Stone,
    Sand,
    Grass,
    Wood,
    Metal,
    Fabric,
    Plaster,
}

/// <summary>One reason a kind scored: what was seen and how much it gave.</summary>
public sealed record ScoreReason(TextureKind Kind, string What, float Points, float Edge);

/// <summary>
/// A texture's classification: the kind chosen, every kind's score, and the reasons (the review
/// shows them). <see cref="Margin"/> is the chosen kind's lead over the runner-up (a small one: look
/// at it).
/// </summary>
public sealed record Classification(string Id, TextureKind Kind, IReadOnlyDictionary<TextureKind, float> Scores, IReadOnlyList<ScoreReason> Reasons)
{
    /// <summary>The starter profile of the kind (empty: keep classic).</summary>
    public string Profile => TextureClassifier.ProfileOf(Kind);

    public float Score => Scores.GetValueOrDefault(Kind);

    public TextureKind RunnerUp => Scores.Where(kv => kv.Key != Kind).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).FirstOrDefault();

    public float Margin => Score - Scores.GetValueOrDefault(RunnerUp);

    /// <summary>The chosen kind's reasons, strongest first.</summary>
    public IEnumerable<ScoreReason> Why => Reasons.Where(r => r.Kind == Kind && r.Edge > 0.02f).OrderByDescending(r => r.Edge);

    /// <summary>"rock 3.2 (wall 1.0, grey and coarse 0.9, ...); then stone 2.1".</summary>
    public string Explain(int reasons = 4) =>
        $"{TextureClassifier.Name(Kind)} {Score:0.0} ({string.Join(", ", Why.Take(reasons).Select(r => $"{r.What} {r.Points:+0.0;-0.0}"))}); "
        + $"then {TextureClassifier.Name(RunnerUp)} {Scores.GetValueOrDefault(RunnerUp):0.0}";
}

/// <summary>
/// The wizard's texture classifier (docs/remaster-plan.md, "R4 in detail"): a hand-made scorer, no
/// learned model. Each kind collects points from what the texture's name says (FFX's own names say
/// it only in the HD remaster's re-made maps: <c>lchb00_rockwall_06</c>), from how the map draws it
/// (<see cref="TextureUseInfo"/>: sky, water, additive, blended, which way its surfaces face), from
/// the map's area (Besaid's sand, Luca's stone) and from its colour and statistics
/// (<see cref="TextureFeatures"/>); the most points win. Every point is a named reason, so the
/// review can say why. Measured on a hand-labelled set (<c>remaster/classifier-labels.tsv</c>,
/// <c>--selftest-wizard-classifier</c>).
/// </summary>
public static class TextureClassifier
{
    public static readonly TextureKind[] Kinds = Enum.GetValues<TextureKind>();

    /// <summary>The starter profile of <paramref name="k"/> (empty: keep classic).</summary>
    public static string ProfileOf(TextureKind k) => k switch
    {
        TextureKind.Rock => "rock",
        TextureKind.Stone => "stone-and-concrete",
        TextureKind.Sand => "sand-and-dirt",
        TextureKind.Grass => "grass-and-foliage",
        TextureKind.Wood => "wood",
        TextureKind.Metal => "metal",
        TextureKind.Fabric => "fabric",
        TextureKind.Plaster => "plaster-and-painted-walls",
        _ => "",
    };

    /// <summary>The kind of the starter profile <paramref name="profile"/> (Keep: none of them).</summary>
    public static TextureKind KindOf(string profile) =>
        Kinds.FirstOrDefault(k => k != TextureKind.Keep && ProfileOf(k) == profile);

    /// <summary>A kind's short name, as the labels file and the review write it.</summary>
    public static string Name(TextureKind k) => k.ToString().ToLowerInvariant();

    public static TextureKind? Parse(string s) =>
        Kinds.Cast<TextureKind?>().FirstOrDefault(k => Name(k!.Value) == s.Trim().ToLowerInvariant()
            || ProfileOf(k!.Value) == s.Trim().ToLowerInvariant() || s.Trim().ToLowerInvariant() is "none" or "classic" && k == TextureKind.Keep);

    /// <summary>Words of texture names and what they say (whole tokens of the short name, split at _ - digits).</summary>
    private static readonly (string[] Words, TextureKind Kind)[] Words =
    [
        (["sky", "cloud", "clouds", "water", "sea", "wave", "waves", "fire", "flame", "light", "glow", "effect", "sun", "moon", "star", "shadow", "reflection", "refl", "spark", "fog", "mist", "background", "bg", "sceen", "screen", "hologram", "lens", "halo", "beam", "hill"], TextureKind.Keep),
        (["rock", "rockwall", "rockground", "cliff", "boulder", "mountain", "cave", "crag"], TextureKind.Rock),
        (["stone", "brick", "brickwall", "brickground", "concrete", "concretewall", "concreteground", "tile", "tiles", "pavement", "paving", "cobble", "stair", "stairs", "step", "steps", "pillar", "column", "statue", "marble", "ruin", "ruins", "road", "decoration"], TextureKind.Stone),
        (["sand", "dirt", "soil", "ground", "mud", "earth", "beach", "path", "gravel"], TextureKind.Sand),
        (["grass", "grassground", "leaf", "leaves", "tree", "plant", "plants", "bush", "foliage", "fern", "palm", "flower", "flowers", "vine", "vines", "moss", "weed", "weeds", "reed", "reeds"], TextureKind.Grass),
        (["wood", "wooden", "plank", "planks", "board", "woodboard", "woodwall", "log", "logs", "trunk", "bark", "fence", "crate", "barrel", "bridge", "deck", "hut"], TextureKind.Wood),
        (["metal", "iron", "steel", "pipe", "pipes", "streetlamp", "lamp", "rail", "rails", "machina", "machine", "panel", "plate", "grate", "chain", "bolt", "gear"], TextureKind.Metal),
        (["fabric", "cloth", "tent", "flag", "curtain", "sail", "banner", "carpet", "rug", "rope", "net", "canvas", "awning"], TextureKind.Fabric),
        // not "wall": walls are stone, rock, wood or plaster alike (Zanarkand's "wall" textures are ruined facades)
        (["plaster", "ceiling", "concreteceiling", "paint", "painted", "stucco", "ornament", "mural"], TextureKind.Plaster),
    ];

    /// <summary>Areas (the level key's first four letters) and the kinds they are full of: a small nudge each.</summary>
    private static readonly Dictionary<string, TextureKind[]> AreaKinds = new()
    {
        ["bsil"] = [TextureKind.Sand, TextureKind.Rock, TextureKind.Grass, TextureKind.Wood],
        ["bsvr"] = [TextureKind.Sand, TextureKind.Rock, TextureKind.Grass, TextureKind.Wood, TextureKind.Fabric],
        ["bsyt"] = [TextureKind.Stone, TextureKind.Plaster],
        ["lchb"] = [TextureKind.Stone, TextureKind.Rock, TextureKind.Metal, TextureKind.Wood],
        ["luca"] = [TextureKind.Stone, TextureKind.Metal, TextureKind.Plaster],
        ["mcfr"] = [TextureKind.Grass, TextureKind.Rock],
        ["maca"] = [TextureKind.Rock, TextureKind.Stone],
        ["mcyt"] = [TextureKind.Stone, TextureKind.Plaster, TextureKind.Metal],
        ["bvyt"] = [TextureKind.Stone, TextureKind.Metal, TextureKind.Plaster],
        ["stbv"] = [TextureKind.Stone, TextureKind.Metal],
        ["znkd"] = [TextureKind.Stone, TextureKind.Rock, TextureKind.Metal],
        ["dome"] = [TextureKind.Stone, TextureKind.Metal, TextureKind.Plaster],
        ["zkrn"] = [TextureKind.Rock, TextureKind.Stone],
        ["mihn"] = [TextureKind.Stone, TextureKind.Grass, TextureKind.Sand, TextureKind.Rock],
        ["kino"] = [TextureKind.Rock, TextureKind.Metal],
        ["djyt"] = [TextureKind.Rock, TextureKind.Stone, TextureKind.Sand],
        ["klyt"] = [TextureKind.Wood, TextureKind.Stone],
        ["kami"] = [TextureKind.Wood, TextureKind.Grass, TextureKind.Fabric],
        ["guad"] = [TextureKind.Stone, TextureKind.Wood, TextureKind.Plaster],
        ["slik"] = [TextureKind.Wood, TextureKind.Grass, TextureKind.Metal],
        ["bika"] = [TextureKind.Sand, TextureKind.Rock],
        ["hiku"] = [TextureKind.Metal, TextureKind.Wood, TextureKind.Fabric],
        ["nagi"] = [TextureKind.Rock, TextureKind.Sand],
        ["mtgz"] = [TextureKind.Rock, TextureKind.Stone],
        ["sins"] = [TextureKind.Stone, TextureKind.Rock],
        ["ptkl"] = [TextureKind.Grass, TextureKind.Sand],
        ["azit"] = [TextureKind.Metal, TextureKind.Stone],
        ["cdsp"] = [TextureKind.Metal, TextureKind.Stone],
        ["genk"] = [TextureKind.Wood, TextureKind.Metal, TextureKind.Fabric],
        ["omeg"] = [TextureKind.Rock, TextureKind.Stone],
        ["lmyt"] = [TextureKind.Rock, TextureKind.Stone],
    };

    /// <summary>The name's tokens: the short name split at _ - . and digits, lower case.</summary>
    public static string[] Tokens(string id)
    {
        var name = RemasterTextures.ShortName(id).ToLowerInvariant();
        return name.Split(['_', '-', '.', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(ch => !char.IsAsciiDigit(ch)).ToArray()))
            .Where(t => t.Length > 1).Distinct().ToArray();
    }

    /// <summary>
    /// A kind's prototype: the medians of its labelled textures' features
    /// (<c>remaster/classifier-labels.tsv</c>), rounded. The
    /// features a prototype compares, and how far apart counts as "unlike" (<see cref="Spread"/>:
    /// the features' spread over the labelled set).
    /// </summary>
    private static readonly string[] Stats = ["Grey", "Warm", "Green", "Blue", "C", "L", "LSd", "Aniso", "Seam", "Edges", "Detail", "AlphaCut", "Bright"];

    private static readonly string[] StatNames = ["grey share", "warm share", "green share", "blue share", "chroma", "lightness", "lightness spread", "direction", "seams", "edges", "detail", "cut out", "bright share"];

    private static readonly float[] Spread = [0.332f, 0.374f, 0.343f, 0.224f, 0.0295f, 0.107f, 0.0437f, 0.219f, 1.66f, 0.223f, 0.0136f, 0.215f, 0.0162f];

    /// <summary>The prototypes in use: the medians over every labelled texture (<see cref="Fit"/> on the labels file), rounded.</summary>
    public static readonly IReadOnlyDictionary<TextureKind, float[]> Prototypes = new Dictionary<TextureKind, float[]>
    {
        //                       grey    warm    green   blue  C       L       LSd     aniso   seam    edges   detail  cut     bright
        [TextureKind.Rock] =    [0.415f, 0.272f, 0f,     0f,   0.033f, 0.536f, 0.128f, 0.159f, 2.088f, 0.521f, 0.028f, 0f,     0f],
        [TextureKind.Stone] =   [0.588f, 0.196f, 0f,     0f,   0.030f, 0.531f, 0.105f, 0.339f, 2.239f, 0.526f, 0.028f, 0f,     0f],
        [TextureKind.Sand] =    [0.020f, 0.980f, 0f,     0f,   0.040f, 0.627f, 0.068f, 0.167f, 1.364f, 0.419f, 0.026f, 0f,     0f],
        [TextureKind.Grass] =   [0.026f, 0.004f, 0.922f, 0f,   0.080f, 0.475f, 0.102f, 0.115f, 0.154f, 0.699f, 0.041f, 0.404f, 0f],
        [TextureKind.Wood] =    [0.178f, 0.681f, 0.030f, 0f,   0.046f, 0.417f, 0.118f, 0.170f, 1.546f, 0.545f, 0.029f, 0f,     0f],
        [TextureKind.Metal] =   [0.155f, 0.832f, 0.003f, 0f,   0.062f, 0.481f, 0.154f, 0.301f, 3.637f, 0.464f, 0.025f, 0f,     0f],
        [TextureKind.Fabric] =  [0f,     0.443f, 0f,     0f,   0.102f, 0.519f, 0.068f, 0.396f, 2.204f, 0.192f, 0.021f, 0f,     0f],
        [TextureKind.Plaster] = [0.243f, 0.676f, 0f,     0.003f, 0.055f, 0.580f, 0.120f, 0.458f, 1.855f, 0.468f, 0.034f, 0f,   0f],
    };

    /// <summary>Prototypes fitted to <paramref name="labelled"/>: each kind's medians (the check's leave-one-area-out refits them without the area it scores).</summary>
    public static Dictionary<TextureKind, float[]> Fit(IEnumerable<(TextureKind Kind, TextureFeatures F)> labelled)
    {
        var by = labelled.Where(x => x.Kind != TextureKind.Keep).GroupBy(x => x.Kind);
        var r = new Dictionary<TextureKind, float[]>();
        foreach (var g in by)
        {
            var rows = g.Select(x => Values(x.F)).ToList();
            var p = new float[Stats.Length];
            for (int i = 0; i < p.Length; i++)
            {
                var col = rows.Select(v => v[i]).OrderBy(v => v).ToList();
                p[i] = col.Count % 2 == 1 ? col[col.Count / 2] : (col[col.Count / 2 - 1] + col[col.Count / 2]) / 2;
            }
            r[g.Key] = p;
        }
        return r;
    }

    private static float[] Values(TextureFeatures f) =>
        [f.Grey, f.Warm, f.Green, f.Blue, f.C, f.L, f.LSd, f.Aniso, Math.Min(f.Seam, 6f), f.Edges, f.Detail, f.AlphaCut, f.Bright];

    private static float Ramp(float x, float lo, float hi) => Math.Clamp((x - lo) / (hi - lo), 0, 1);

    /// <summary>
    /// Classifies <paramref name="id"/> from its <paramref name="f"/>eatures, how the map draws it
    /// (<paramref name="use"/>; null: not known) and the map's <paramref name="level"/> key.
    /// </summary>
    public static Classification Classify(string id, TextureFeatures f, TextureUseInfo? use, string level,
        IReadOnlyDictionary<TextureKind, float[]>? prototypes = null)
    {
        var reasons = new List<ScoreReason>();
        void Add(TextureKind k, string what, float points, float? edge = null)
        {
            if (Math.Abs(points) >= 0.01f)
                reasons.Add(new ScoreReason(k, what, points, edge ?? points));
        }

        // ---- the name (FFX's HD maps name their textures: lchb00_rockwall_06) ----
        var tokens = Tokens(id);
        foreach (var (words, kind) in Words)
            if (tokens.FirstOrDefault(t => words.Contains(t)) is { } hit)
                Add(kind, $"named \"{hit}\"", kind == TextureKind.Keep ? 2.5f : 2.2f);

        // ---- how the map draws it ----
        if (use != null)
        {
            // the sky rule (MapLook.FindSky) takes some big ground textures too (Luca's): trusted with scrolling or facing no way
            if (use.Sky && use.Scroll > 0.4f)
                Add(TextureKind.Keep, "a scrolling sky part", 4f);
            else if (use.Sky && use.Up < 0.1f)
                Add(TextureKind.Keep, "a sky part facing no ground", 1.5f);
            if (use.Special)
                Add(TextureKind.Keep, "drawn by the water's shader", 4f);
            if (use.Additive > 0.4f)
                Add(TextureKind.Keep, "additive (a glow or an effect)", 3f * use.Additive);
            if (use.Blend > 0.9f && f.AlphaSoft > 0.06f)
                Add(TextureKind.Keep, "blended with soft alpha (a painted layer)", 1.5f);
            if (use.Area > 0)
            {
                Add(TextureKind.Sand, "on the ground", 1.0f * use.Up);
                Add(TextureKind.Stone, "on the ground", 0.3f * use.Up);
                Add(TextureKind.Rock, "on walls", 0.25f * use.Wall);
                Add(TextureKind.Plaster, "on walls", 0.22f * use.Wall);
                Add(TextureKind.Plaster, "facing down (a ceiling)", 0.8f * use.Down);
                Add(TextureKind.Wood, "facing down (beams, roofs)", 0.3f * use.Down);
                Add(TextureKind.Sand, "not on the ground", -0.8f * (1 - use.Up));
            }
            if (use.AlphaTest > 0.4f && f.AlphaCut > 0.05f)
                Add(TextureKind.Grass, "alpha tested and cut out (leaves)", 0.8f);
        }

        // ---- the map's area ----
        var area = PartIds.LevelKey(level) is { Length: >= 4 } key ? key[..4] : "";
        if (AreaKinds.TryGetValue(area, out var kinds))
            for (int i = 0; i < kinds.Length; i++)
                Add(kinds[i], $"common in {area}", 0.375f - 0.05f * i);

        // ---- colour and statistics: how near each kind's prototype, feature by feature ----
        var v = Values(f);
        var protos = prototypes ?? Prototypes;
        for (int i = 0; i < Stats.Length; i++)
        {
            // each feature's fit to each kind; the review sorts by how much it sets the kind apart (its fit less the mean fit)
            var fits = protos.ToDictionary(kv => kv.Key, kv => Math.Clamp(1 - Math.Abs(v[i] - kv.Value[i]) / Spread[i], -1, 1) * 3f / Stats.Length);
            float mean = fits.Values.Average();
            foreach (var (k, pts) in fits)
                Add(k, $"{StatNames[i]} {v[i]:0.00#} (its {protos[k][i]:0.00#})", pts, pts - mean);
        }
        Add(TextureKind.Grass, "mostly green", 1.5f * Ramp(f.Green, 0.3f, 0.7f));
        Add(TextureKind.Keep, "soft alpha", 1.5f * Ramp(f.AlphaSoft, 0.1f, 0.4f));
        Add(TextureKind.Keep, "mostly cut out and not green", 1.0f * Ramp(f.AlphaCut, 0.5f, 0.85f) * (1 - Ramp(f.Green, 0.15f, 0.4f)));

        var scores = Kinds.ToDictionary(k => k, _ => 0f);
        foreach (var r in reasons)
            scores[r.Kind] += r.Points;
        var best = scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        return new Classification(id, best, scores, reasons);
    }
}
