using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GdFfx.Remaster.Texturing;

namespace GdFfx.Remaster;

/// <summary>The material workbench's core steps, in the strip's order (docs/remaster-materials.md, "The steps").</summary>
public enum MaterialStep
{
    Source,
    Albedo,
    Height,
    Normal,
    Roughness,
    Metallic,
    Occlusion,
    Emission,
}

/// <summary>A setting a step shows (a <see cref="PbrRecipe"/> property): its label, range, and whether only the material reads it (no map made again).</summary>
public sealed record StepSetting(string Property, string Label, double Min, double Max, double Step, string Tip, bool MaterialOnly = false);

/// <summary>
/// The material workbench's pipeline (docs/remaster-materials.md, "The steps", "Speed"; phase 1):
/// R2's CPU generators (<see cref="MapGenerator"/>, <see cref="Lanczos"/>) as one step per map,
/// joined by a dependency graph (Source → Albedo → Height → Normal → Occlusion; Albedo →
/// Roughness, Metallic, Emission). Each map is kept with a signature of what made it (its
/// settings, its mode and file, and its inputs' signatures), so a change makes again exactly the
/// maps downstream of it. Every step can instead take a file (<see cref="StepMode.Import"/>) or a
/// constant (<see cref="StepMode.Skip"/>).
/// <para>Runs off the main thread: <see cref="Request"/> takes a snapshot (the source pixels,
/// a copy of the recipe, the imported files' pixels: no Godot object is touched on the worker),
/// cancels the run before it and queues a new one; runs execute one after another (the cache is
/// theirs). A run makes the maps at each size it is asked for, smallest first (the preview's
/// 256 px copy, then the working size), the focused step's chain before the others, and posts
/// each map as it is made (<see cref="Results"/>, read on the main thread).</para>
/// </summary>
public sealed class MaterialPipeline
{
    /// <summary>The preview's first, small size.</summary>
    public const int SmallSide = 256;

    /// <summary>The full size (the save): the recipe's <see cref="PbrRecipe.MaxSide"/> after the upscale.</summary>
    public const int FullSide = int.MaxValue;

    /// <summary>What a run works on (immutable; made on the main thread).</summary>
    /// <param name="Key">The texture's ID (part of every signature).</param>
    /// <param name="Original">The colour texture at its own size (the classic one or its replacement).</param>
    /// <param name="Recipe">A copy of the settings (read only: plain C# properties).</param>
    /// <param name="Imports">The imported files' pixels by path (as the recipe names them).</param>
    public sealed record Inputs(string Key, RgbaImage Original, PbrRecipe Recipe, IReadOnlyDictionary<string, RgbaImage> Imports);

    /// <summary>A made map: the run, its size (the side asked for), the step (null: the ORM pack), the pixels, ms since the request.</summary>
    public sealed record Result(int Run, int Side, MaterialStep? Step, RgbaImage Image, double Ms, bool Last);

    /// <summary>Each step's inputs.</summary>
    public static readonly IReadOnlyDictionary<MaterialStep, MaterialStep[]> Needs = new Dictionary<MaterialStep, MaterialStep[]>
    {
        [MaterialStep.Source] = [],
        [MaterialStep.Albedo] = [MaterialStep.Source],
        [MaterialStep.Height] = [MaterialStep.Albedo, MaterialStep.Source],
        [MaterialStep.Normal] = [MaterialStep.Height],
        [MaterialStep.Occlusion] = [MaterialStep.Height, MaterialStep.Normal],
        [MaterialStep.Roughness] = [MaterialStep.Albedo],
        [MaterialStep.Metallic] = [MaterialStep.Albedo],
        [MaterialStep.Emission] = [MaterialStep.Albedo],
    };

    /// <summary>Every step, in order.</summary>
    public static readonly MaterialStep[] Steps = Enum.GetValues<MaterialStep>();

    /// <summary>The steps <paramref name="step"/> depends on, all the way up (itself last).</summary>
    public static List<MaterialStep> Chain(MaterialStep step)
    {
        var r = new List<MaterialStep>();
        void Walk(MaterialStep s)
        {
            foreach (var i in Needs[s])
                Walk(i);
            if (!r.Contains(s))
                r.Add(s);
        }
        Walk(step);
        return r;
    }

    /// <summary>The steps that depend on <paramref name="step"/> (directly or not), in order.</summary>
    public static List<MaterialStep> Downstream(MaterialStep step) =>
        Steps.Where(s => s != step && Chain(s).Contains(step)).ToList();

    /// <summary>A step's mode and file properties (none for the source's skip).</summary>
    public static (string Mode, string File) ModeProps(MaterialStep s) => ($"{s}Mode", $"{s}File");

    /// <summary>Each step's settings, as the step panel shows them (besides mode and file).</summary>
    public static readonly IReadOnlyDictionary<MaterialStep, StepSetting[]> Settings = new Dictionary<MaterialStep, StepSetting[]>
    {
        [MaterialStep.Source] =
        [
            new("Upscale", "Upscale", 1, 4, 1, "Before anything else (Lanczos-3): 1, 2 or 4"),
            new("WorkingSide", "Working size", 256, 2048, 256, "The preview's size (the largest side); Save makes the maps at the full size"),
            new("MaxSide", "Full size", 64, 8192, 64, "The largest side of the saved maps (after the upscale)"),
            new("Wrap", "Tiling", 0, 1, 1, "The texture repeats: every filter wraps at the edges"),
        ],
        [MaterialStep.Albedo] =
        [
            new("Delight", "Shading out", 0, 1, 0.05, "The painted light and shadow divided out"),
            new("DelightRadius", "Light size", 1, 64, 1, "What counts as light rather than paint (pixels of the classic texture)"),
            new("AlbedoEdgeKeep", "Edge keeping", 0, 1, 0.05, "0: a blur estimates the light; 1: a guided filter that follows edges (no halos)"),
            new("AlbedoOcclusion", "Creases lifted", 0, 1, 0.05, "Dark creases and corners (occlusion from the height) brightened"),
            new("AlbedoCavity", "Cracks lifted", 0, 1, 0.05, "Small dark cavities brightened"),
            new("AlbedoHighlights", "Highlights down", 0, 1, 0.05, "Painted highlights pressed down"),
            new("AlbedoColourCast", "Colour cast flat", 0, 1, 0.05, "Slow colour casts (coloured light) flattened"),
            new("AlbedoNeutralise", "White balance", 0, 1, 0.05, "The average colour pulled to grey"),
        ],
        [MaterialStep.Height] =
        [
            new("HeightFrom", "From", 0, 1, 1, "The albedo (light out first) or the source as it is"),
            new("HeightContrast", "Contrast", 0, 4, 0.05, "Around mid grey"),
            new("HeightBlur", "Smoothing", 0, 8, 0.1, "Paint detail smoothed out first (pixels of the classic texture)"),
            new("HeightInvert", "Dark is high", 0, 1, 1, "Invert"),
            new("HeightScale", "Parallax depth", 0, 0.2, 0.005, "The material's parallax depth (0: no height map in the material)", MaterialOnly: true),
        ],
        [MaterialStep.Normal] =
        [
            new("NormalStrength", "Strength", 0, 16, 0.1, "Slope steepness"),
            new("NormalBlur", "Smoothing", 0, 8, 0.1, "The height smoothed first (pixels of the classic texture)"),
            new("NormalFilter", "Filter", 0, 1, 1, "Sobel or Scharr (crisper on diagonals)"),
            new("NormalInvert", "Invert", 0, 1, 1, "Swap up and down"),
        ],
        [MaterialStep.Roughness] =
        [
            new("RoughnessBase", "Base", 0, 1, 0.01, "A flat mid-grey area's roughness (Skip: everywhere)"),
            new("RoughnessRange", "Range", 0, 2, 0.01, "How far brightness and detail move it"),
            new("RoughnessDetail", "Detail share", 0, 1, 0.05, "Local contrast against brightness"),
            new("RoughnessDetailRadius", "Detail size", 0.5, 16, 0.5, "The local contrast's radius (pixels of the classic texture)"),
            new("RoughnessInvert", "Brighter is rougher", 0, 1, 1, "Invert"),
        ],
        [MaterialStep.Metallic] =
        [
            new("Metallic", "Value", 0, 1, 0.01, "The constant (Skip), or the mask's value where it is on"),
            new("MetallicThreshold", "Mask from brightness", 0, 1.01, 0.01, "Where the brightness passes it, metal (above 1: the value everywhere)"),
            new("MetallicSoftness", "Softness", 0, 0.5, 0.01, "The ramp around the threshold"),
            new("MetallicInvert", "Dark is metal", 0, 1, 1, "Invert the mask"),
        ],
        [MaterialStep.Occlusion] =
        [
            new("OcclusionRadius", "Radius", 0, 32, 0.5, "How far it looks (pixels of the classic texture)"),
            new("OcclusionStrength", "Strength", 0, 4, 0.05, "Factor on the darkening"),
            new("OcclusionDepth", "Depth", 1, 64, 0.5, "How deep the relief is"),
            new("OcclusionSamples", "Directions", 4, 32, 1, "Directions per pixel"),
        ],
        [MaterialStep.Emission] =
        [
            new("EmissionThreshold", "Threshold", 0, 1, 0.01, "The colour glows where its brightest channel passes this"),
            new("EmissionSoftness", "Softness", 0, 0.5, 0.01, "The ramp around the threshold"),
            new("EmissionEnergy", "Energy", 0, 16, 0.05, "The material's emission energy", MaterialOnly: true),
        ],
    };

    /// <summary>The settings of the material only (no map made again when they change).</summary>
    public static readonly HashSet<string> MaterialOnly = Settings.Values.SelectMany(s => s).Where(s => s.MaterialOnly).Select(s => s.Property).ToHashSet();

    /// <summary>A recipe property by name (plain C# reflection: safe on any thread).</summary>
    public static PropertyInfo Prop(string name) =>
        typeof(PbrRecipe).GetProperty(name) ?? throw new ArgumentException($"PbrRecipe has no {name}");

    /// <summary>A recipe property's value as invariant text.</summary>
    public static string Text(PbrRecipe r, string name) =>
        Convert.ToString(Prop(name).GetValue(r), CultureInfo.InvariantCulture) ?? "";

    public static StepMode ModeOf(PbrRecipe r, MaterialStep s) =>
        s == MaterialStep.Source && r.SourceMode == StepMode.Skip ? StepMode.Generate : (StepMode)Prop(ModeProps(s).Mode).GetValue(r)!;

    public static string FileOf(PbrRecipe r, MaterialStep s) => (string)Prop(ModeProps(s).File).GetValue(r)!;

    /// <summary>
    /// Makes an R2 recipe (<see cref="PbrRecipe.Version"/> 1) a workbench one: emission on where
    /// R2's threshold made one, metallic as its constant. The fields both read keep their meaning.
    /// </summary>
    public static void Migrate(PbrRecipe r)
    {
        if (r.Version >= 2)
            return;
        r.Version = 2;
        r.EmissionMode = r.EmissionThreshold <= 1f ? StepMode.Generate : StepMode.Skip;
        r.MetallicMode = StepMode.Skip;
    }

    // ---- running ---------------------------------------------------------------------------------

    private readonly Dictionary<(int Side, MaterialStep? Step), (string Sig, RgbaImage Image)> _cache = new();
    private Task _chain = Task.CompletedTask;
    private CancellationTokenSource? _cts;
    private int _run;

    /// <summary>The maps made, oldest first (main thread: drain once a frame).</summary>
    public ConcurrentQueue<Result> Results { get; } = new();

    /// <summary>The run last asked for.</summary>
    public int Run => _run;

    /// <summary>Whether a run is going.</summary>
    public bool Busy => !_chain.IsCompleted;

    /// <summary>The last run's error (null: none).</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Asks for the maps of <paramref name="inputs"/> at each of <paramref name="sides"/>
    /// (smallest first), <paramref name="focus"/>'s chain first; the run before is cancelled
    /// (unless it was a kept one: the save's). <paramref name="keep"/>: this run is not cancelled
    /// by later ones (they wait for it). Returns the run's number (results carry it; older ones
    /// are stale).
    /// </summary>
    public int Request(Inputs inputs, MaterialStep focus, int[] sides, bool keep = false)
    {
        // the runs before (not a kept one: its token is never held here) give way
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        if (!keep)
            _cts = cts;
        int run = ++_run;
        var clock = Stopwatch.StartNew();
        var order = Chain(focus).Concat(Steps).Distinct().ToList();
        _chain = _chain.ContinueWith(_ =>
        {
            try
            {
                if (cts.IsCancellationRequested)
                    return;
                Error = null;
                for (int i = 0; i < sides.Length; i++)
                {
                    foreach (var s in order)
                        Results.Enqueue(new Result(run, sides[i], s, Map(inputs, s, sides[i], cts.Token), clock.Elapsed.TotalMilliseconds, false));
                    var orm = Orm(inputs, sides[i], cts.Token);
                    Results.Enqueue(new Result(run, sides[i], null, orm, clock.Elapsed.TotalMilliseconds, i == sides.Length - 1));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Error = e.Message;
                Godot.GD.PrintErr($"ffx remaster: the material pipeline failed: {e}");
                Results.Enqueue(new Result(run, 0, null, Solid(1, 1, 0, 0, 0), clock.Elapsed.TotalMilliseconds, true));
            }
        }, TaskScheduler.Default);
        return run;
    }

    /// <summary>Cancels the run going (if any; not a kept one).</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>Waits for the runs queued so far (the checks).</summary>
    public bool Wait(int ms) => _chain.Wait(ms);

    // ---- the maps ----------------------------------------------------------------------------------

    /// <summary>The size of the maps at <paramref name="side"/> and the scale of the generators' radii (the classic texture's pixels).</summary>
    public static (int W, int H, double Scale) Size(Inputs inputs, int side)
    {
        var r = inputs.Recipe;
        int up = Math.Clamp(r.Upscale, 1, 4);
        int limit = Math.Min(Math.Max(64, r.MaxSide), side == FullSide ? Lanczos.MaxSide : Math.Max(16, side));
        var (w, h) = Lanczos.FitWithin(inputs.Original.Width * up, inputs.Original.Height * up, Math.Min(limit, Lanczos.MaxSide));
        return (w, h, (double)w / Math.Max(1, inputs.Original.Width));
    }

    /// <summary>The signature of <paramref name="s"/>'s map: what made it (its settings, mode, file, and its inputs').</summary>
    public static string Signature(Inputs inputs, MaterialStep s)
    {
        var r = inputs.Recipe;
        var sb = new System.Text.StringBuilder();
        sb.Append(s).Append('|');
        if (s == MaterialStep.Source)
            sb.Append(inputs.Key).Append('|').Append(inputs.Original.Width).Append('x').Append(inputs.Original.Height)
              .Append('|').Append(Text(r, "Upscale")).Append('|').Append(Text(r, "MaxSide")).Append('|').Append(Text(r, "Wrap"));
        var mode = ModeOf(r, s);
        sb.Append('|').Append(mode);
        if (mode == StepMode.Import)
            sb.Append('|').Append(FileOf(r, s));
        if (mode == StepMode.Generate || s is MaterialStep.Roughness or MaterialStep.Metallic)
            foreach (var st in Settings[s])
                if (!st.MaterialOnly && st.Property is not ("Upscale" or "MaxSide" or "Wrap" or "WorkingSide"))
                    sb.Append('|').Append(Text(r, st.Property));
        if (mode == StepMode.Generate)
            foreach (var i in Needs[s])
                sb.Append("|<").Append(Signature(inputs, i)).Append('>');
        else if (s != MaterialStep.Source)
            // an imported map or a constant still takes the source's size
            sb.Append("|<").Append(Signature(inputs, MaterialStep.Source)).Append('>');
        return sb.ToString();
    }

    private RgbaImage Map(Inputs inputs, MaterialStep s, int side, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var sig = Signature(inputs, s);
        if (_cache.TryGetValue((side, s), out var hit) && hit.Sig == sig)
            return hit.Image;
        var img = Make(inputs, s, side, cancel);
        _cache[(side, s)] = (sig, img);
        return img;
    }

    private RgbaImage Orm(Inputs inputs, int side, CancellationToken cancel)
    {
        var sig = Signature(inputs, MaterialStep.Occlusion) + Signature(inputs, MaterialStep.Roughness) + Signature(inputs, MaterialStep.Metallic);
        if (_cache.TryGetValue((side, null), out var hit) && hit.Sig == sig)
            return hit.Image;
        var ao = Map(inputs, MaterialStep.Occlusion, side, cancel);
        var rough = Map(inputs, MaterialStep.Roughness, side, cancel);
        var metal = Map(inputs, MaterialStep.Metallic, side, cancel);
        var orm = PackOrm(ao, rough, metal);
        _cache[(side, null)] = (sig, orm);
        return orm;
    }

    /// <summary>ORM: R occlusion, G roughness, B metallic (Godot's ORMMaterial3D).</summary>
    public static RgbaImage PackOrm(RgbaImage ao, RgbaImage rough, RgbaImage metal)
    {
        var orm = new byte[ao.Width * ao.Height * 4];
        for (int i = 0; i < orm.Length; i += 4)
        {
            orm[i] = ao.Rgba[i];
            orm[i + 1] = rough.Rgba[i];
            orm[i + 2] = metal.Rgba[i];
            orm[i + 3] = 255;
        }
        return new RgbaImage(ao.Width, ao.Height, orm);
    }

    private RgbaImage Make(Inputs inputs, MaterialStep s, int side, CancellationToken cancel)
    {
        var r = inputs.Recipe;
        var (w, h, scale) = Size(inputs, side);
        bool wrap = r.Wrap;
        var mode = ModeOf(r, s);
        if (s == MaterialStep.Source)
        {
            var orig = mode == StepMode.Import && inputs.Imports.TryGetValue(r.SourceFile, out var own) ? own : inputs.Original;
            return orig.Width == w && orig.Height == h ? orig : Lanczos.Resize(orig, w, h, ResizeOptions.Colour, null, cancel);
        }
        if (mode == StepMode.Import && inputs.Imports.TryGetValue(FileOf(r, s), out var file))
            return file.Width == w && file.Height == h ? file
                : Lanczos.Resize(file, w, h, s is MaterialStep.Albedo or MaterialStep.Emission ? ResizeOptions.Colour : ResizeOptions.Data, null, cancel);
        RgbaImage In(MaterialStep i) => Map(inputs, i, side, cancel);
        if (mode != StepMode.Generate)
            return s switch
            {
                // an import whose file is missing falls back to the constant
                MaterialStep.Albedo => In(MaterialStep.Source),
                MaterialStep.Height => Solid(w, h, 128, 128, 128),
                MaterialStep.Normal => Solid(w, h, 128, 128, 255),
                MaterialStep.Roughness => Grey(w, h, r.RoughnessBase),
                MaterialStep.Metallic => Grey(w, h, r.Metallic),
                MaterialStep.Occlusion => Solid(w, h, 255, 255, 255),
                _ => Solid(w, h, 0, 0, 0),
            };
        switch (s)
        {
            case MaterialStep.Albedo:
            {
                var opt = new AlbedoOptions(r.Delight, r.DelightRadius, r.AlbedoEdgeKeep, r.AlbedoOcclusion, 6, r.AlbedoCavity, 2,
                    r.AlbedoHighlights, 0.25, r.AlbedoColourCast, r.AlbedoNeutralise, wrap);
                return MapGenerator.Albedo(In(MaterialStep.Source), opt, null, scale, null, cancel);
            }
            case MaterialStep.Height:
                return MapGenerator.Height(In(r.HeightFrom == HeightInput.Albedo ? MaterialStep.Albedo : MaterialStep.Source),
                    new HeightOptions(r.HeightContrast, r.HeightBlur, r.HeightInvert, wrap), scale, null, cancel);
            case MaterialStep.Normal:
                return MapGenerator.Normal(In(MaterialStep.Height), new NormalOptions(HeightSource.HeightMap, r.NormalFilter, r.NormalStrength,
                    r.NormalBlur, r.NormalInvert, NormalConvention.OpenGl, wrap), scale, null, cancel);
            case MaterialStep.Roughness:
                return MapGenerator.Roughness(In(MaterialStep.Albedo), new RoughnessOptions(r.RoughnessBase, r.RoughnessRange, r.RoughnessDetail,
                    r.RoughnessDetailRadius, r.RoughnessInvert, wrap), scale, null, cancel);
            case MaterialStep.Metallic:
                return MapGenerator.Metallic(In(MaterialStep.Albedo), new MetallicOptions(r.Metallic,
                    r.MetallicThreshold <= 1f ? r.MetallicThreshold : null, r.MetallicSoftness, r.MetallicInvert), null, cancel);
            case MaterialStep.Occlusion:
                _ = In(MaterialStep.Normal);
                return MapGenerator.AmbientOcclusion(In(MaterialStep.Height), new AmbientOcclusionOptions(HeightSource.HeightMap, r.OcclusionRadius,
                    r.OcclusionStrength, r.OcclusionSamples, r.OcclusionDepth, wrap), scale, null, cancel);
            case MaterialStep.Emission:
            {
                var albedo = In(MaterialStep.Albedo);
                var mask = MapGenerator.EmissionMask(albedo, new EmissionMaskOptions(Math.Min(r.EmissionThreshold, 1f), r.EmissionSoftness), null, cancel);
                var e = new byte[albedo.Rgba.Length];
                for (int i = 0; i < e.Length; i += 4)
                {
                    int m = mask.Rgba[i];
                    e[i] = (byte)(albedo.Rgba[i] * m / 255);
                    e[i + 1] = (byte)(albedo.Rgba[i + 1] * m / 255);
                    e[i + 2] = (byte)(albedo.Rgba[i + 2] * m / 255);
                    e[i + 3] = 255;
                }
                return new RgbaImage(albedo.Width, albedo.Height, e);
            }
        }
        throw new ArgumentOutOfRangeException(nameof(s));
    }

    public static RgbaImage Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (r, g, b, 255);
        return new RgbaImage(w, h, px);
    }

    private static RgbaImage Grey(int w, int h, float v)
    {
        byte b = (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
        return Solid(w, h, b, b, b);
    }

    /// <summary>Whether <paramref name="img"/> holds anything but black (an emission map worth saving).</summary>
    public static bool AnyLight(RgbaImage img)
    {
        for (int i = 0; i < img.Rgba.Length; i += 4)
            if (img.Rgba[i] > 0 || img.Rgba[i + 1] > 0 || img.Rgba[i + 2] > 0)
                return true;
        return false;
    }
}
