using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GdFfx.Remaster.Materialize;
using GdFfx.Remaster.Texturing;

namespace GdFfx.Remaster;

/// <summary>
/// The material workbench's steps, in the strip's order (docs/remaster-materials.md, "The
/// steps"): the core ones (Source to Emission, always in the strip), then the maps added per
/// profile with "+ map" (R2b phase 4: <see cref="PbrRecipe"/>'s <c>&lt;Step&gt;On</c>).
/// </summary>
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
    BentNormal,
    Rim,
    Clearcoat,
    Anisotropy,
    Subsurface,
    Transmittance,
    Backlight,
    Refraction,
    Detail,
}

/// <summary>What a pipeline result is: a step's map, the ORM pack, or the isolated colour mask.</summary>
public enum ResultKind
{
    Map,
    Orm,
    Mask,
    /// <summary>A map besides the steps' own: the detail's albedo or normal (<see cref="MaterialPipeline.Result.Aux"/> names it).</summary>
    Aux,
}

/// <summary>The colour check (OKLab means): the source's lightness and chroma, the albedo's.</summary>
public sealed record ColourCheck(float SourceL, float SourceC, float AlbedoL, float AlbedoC)
{
    public override string ToString() =>
        $"lightness {SourceL:0.000} → {AlbedoL:0.000} ({(AlbedoL - SourceL) * 100:+0.0;-0.0} %), chroma {SourceC:0.000} → {AlbedoC:0.000} ({(SourceC > 1e-4f ? (AlbedoC / SourceC - 1) * 100 : 0):+0;-0} %)";
}

/// <summary>A setting a step shows (a <see cref="PbrRecipe"/> property): its label, range, and whether only the material reads it (no map made again).</summary>
/// <param name="Group">Settings shown together: a band equaliser ("bands:..."), or a colour sample with its value ("sample").</param>
public sealed record StepSetting(string Property, string Label, double Min, double Max, double Step, string Tip, bool MaterialOnly = false, string? Group = null);

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

    /// <summary>A made map: the run, its size (the side asked for), the step (null: the ORM pack or the mask), the pixels, ms since the request; the height also as floats (the 16-bit save); the albedo's colour check.</summary>
    public sealed record Result(int Run, int Side, MaterialStep? Step, RgbaImage Image, double Ms, bool Last, ResultKind Kind = ResultKind.Map,
        float[]? Float = null, ColourCheck? Check = null, string? Aux = null);

    /// <summary>Each step's inputs.</summary>
    public static readonly IReadOnlyDictionary<MaterialStep, MaterialStep[]> Needs = new Dictionary<MaterialStep, MaterialStep[]>
    {
        [MaterialStep.Source] = [],
        [MaterialStep.Albedo] = [MaterialStep.Source],
        [MaterialStep.Height] = [MaterialStep.Albedo, MaterialStep.Source],
        [MaterialStep.Normal] = [MaterialStep.Height],
        [MaterialStep.Occlusion] = [MaterialStep.Height, MaterialStep.Normal],
        [MaterialStep.Roughness] = [MaterialStep.Albedo, MaterialStep.Metallic, MaterialStep.Normal],
        [MaterialStep.Metallic] = [MaterialStep.Albedo],
        [MaterialStep.Emission] = [MaterialStep.Albedo],
        [MaterialStep.BentNormal] = [MaterialStep.Height, MaterialStep.Normal],
        [MaterialStep.Rim] = [MaterialStep.Albedo, MaterialStep.Height, MaterialStep.Normal],
        [MaterialStep.Clearcoat] = [MaterialStep.Albedo, MaterialStep.Height, MaterialStep.Normal],
        [MaterialStep.Anisotropy] = [MaterialStep.Albedo],
        [MaterialStep.Subsurface] = [MaterialStep.Albedo, MaterialStep.Height, MaterialStep.Normal],
        [MaterialStep.Transmittance] = [MaterialStep.Albedo, MaterialStep.Height],
        [MaterialStep.Backlight] = [MaterialStep.Albedo, MaterialStep.Height, MaterialStep.Normal],
        [MaterialStep.Refraction] = [MaterialStep.Albedo, MaterialStep.Height, MaterialStep.Normal],
        [MaterialStep.Detail] = [MaterialStep.Albedo, MaterialStep.Height, MaterialStep.Normal],
    };

    /// <summary>Every step, in order.</summary>
    public static readonly MaterialStep[] Steps = Enum.GetValues<MaterialStep>();

    /// <summary>The core steps (always in the strip and the material).</summary>
    public static readonly MaterialStep[] Core = Steps.Where(s => s <= MaterialStep.Emission).ToArray();

    /// <summary>The maps added per profile with "+ map" (R2b phase 4).</summary>
    public static readonly MaterialStep[] Extras = Steps.Where(s => s > MaterialStep.Emission).ToArray();

    /// <summary>The mask steps (one kernel: a constant, the brightness, a colour mask, the height or the normals' tilt).</summary>
    public static readonly MaterialStep[] MaskSteps =
        [MaterialStep.Rim, MaterialStep.Clearcoat, MaterialStep.Subsurface, MaterialStep.Backlight, MaterialStep.Refraction, MaterialStep.Detail];

    /// <summary>Whether <paramref name="s"/> is a core step.</summary>
    public static bool IsCore(MaterialStep s) => s <= MaterialStep.Emission;

    /// <summary>A "+ map" step's switch (<c>RimOn</c>...).</summary>
    public static string OnProp(MaterialStep s) => $"{s}On";

    /// <summary>Whether <paramref name="s"/> is made (a core step, or an added map that is on).</summary>
    public static bool On(PbrRecipe r, MaterialStep s) => IsCore(s) || (bool)Prop(OnProp(s)).GetValue(r)!;

    /// <summary>The steps <paramref name="r"/> makes, in order.</summary>
    public static List<MaterialStep> Active(PbrRecipe r) => Steps.Where(s => On(r, s)).ToList();

    /// <summary>Godot's name of an added map's texture file (the store's role).</summary>
    public static string Role(MaterialStep s) => s switch
    {
        MaterialStep.BentNormal => "bent_normal",
        MaterialStep.Anisotropy => "flowmap",
        MaterialStep.Detail => "detail_mask",
        _ => s.ToString().ToLowerInvariant(),
    };

    /// <summary>The detail's albedo and normal (aux maps of the Detail step).</summary>
    public const string DetailAlbedo = "detail_albedo", DetailNormal = "detail_normal";

    /// <summary>
    /// The keys the detail's albedo and normal are read under (<see cref="Inputs.Imports"/>): the
    /// files, or <c>texture:&lt;id&gt;:albedo</c> and <c>:normal</c> (another texture's made maps,
    /// read by the editor on the main thread). Empty: none.
    /// </summary>
    public static (string Albedo, string Normal) DetailKeys(PbrRecipe r) =>
        !r.DetailOn ? ("", "")
        : r.DetailFrom == DetailFrom.Texture ? (r.DetailTexture.Length > 0 ? ($"texture:{r.DetailTexture}:albedo", $"texture:{r.DetailTexture}:normal") : ("", ""))
        : (r.DetailAlbedoFile, r.DetailNormalFile);

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
            new("SeamMode", "Seam tool", 0, 2, 1, "Make a texture that does not tile tile: a height-aware overlap across the edges (the taller feature wins), or a scatter of rotated copies; every map follows"),
            new("SeamOverlap", "Overlap", 0.02, 0.45, 0.01, "The overlap's width, a fraction of the side"),
            new("SeamHeight", "Taller wins", 0, 10, 0.1, "How much the taller feature wins across the seam (0: a plain cross-fade)"),
            new("SeamSoftness", "Seam softness", 0.05, 2, 0.05, "The blend's width (smaller: a harder edge)"),
            new("SeamHeightBlur", "Height smoothing", 0, 8, 0.1, "The brightness the seam tool reads as height, smoothed first (classic texels)"),
            new("ScatterGrid", "Stamps", 2, 16, 2, "Scatter: stamps per side (even)"),
            new("ScatterScale", "Stamp size", 0.8, 3, 0.05, "Scatter: a stamp's size against its cell"),
            new("ScatterRotation", "Rotation", 0, 360, 1, "Scatter: every stamp's rotation (degrees)"),
            new("ScatterRandomRotation", "Random rotation", 0, 180, 1, "Scatter: each stamp's random turn, up to this either way (degrees)"),
            new("ScatterJitter", "Jitter", 0, 1, 0.01, "Scatter: each stamp's random offset (a fraction of a cell)"),
            new("ScatterSeed", "Seed", 0, 9999, 1, "Scatter: another random layout"),
            new("ScatterSoftness", "Stamp softness", 0.05, 1, 0.01, "Scatter: the stamps' soft edge"),
            new("ScatterHeight", "Taller stamp wins", 0, 24, 0.5, "Scatter: how much the taller stamp wins where they overlap (0: an even blend)"),
        ],
        [MaterialStep.Albedo] =
        [
            new("Delight", "Shading out", 0, 1, 0.05, "The painted light and shadow divided out"),
            new("DelightRadius", "Light size", 1, 64, 1, "What counts as light rather than paint (pixels of the classic texture)"),
            new("AlbedoEdgeKeep", "Edge keeping", 0, 1, 0.05, "0: a blur estimates the light; 1: a guided filter that follows edges (no halos)"),
            new("AlbedoOcclusion", "Creases lifted", 0, 1, 0.05, "Dark creases and corners (occlusion from the height) brightened"),
            new("AlbedoCavity", "Cracks lifted", 0, 1, 0.05, "Small dark cavities brightened"),
            new("AlbedoHighlights", "Hot spots down", 0, 1, 0.05, "Painted highlights (spots well above their surroundings) pressed down"),
            new("AlbedoDarkSpots", "Dark spots up", 0, 1, 0.05, "Spots well below their surroundings lifted"),
            new("AlbedoColourCast", "Colour cast flat", 0, 1, 0.05, "Slow colour casts (coloured light) flattened"),
            new("AlbedoNeutralise", "White balance", 0, 1, 0.05, "The average colour pulled to grey"),
            new("AlbedoMatchClassic", "Match classic", 0, 1, 0.05, "The albedo's mean lightness and chroma pulled to the classic texture's (the colour check)"),
        ],
        [MaterialStep.Height] =
        [
            new("HeightFrom", "From", 0, 1, 1, "The albedo (light out first) or the source as it is"),
            new("HeightBlur", "Smoothing", 0, 8, 0.1, "Paint detail smoothed out before the bands (pixels of the classic texture)"),
            .. Band("HeightBand", "Band weights", 0, 1, 0.01, "bands:weights", "How much each band (fine to coarse) adds to the height"),
            .. Band("HeightBandContrast", "Band contrast", -2, 2, 0.05, "bands:contrast", "Each band's contrast (negative: inverted, e.g. cracks)"),
            new("HeightContrast", "Contrast", 0, 4, 0.05, "The final contrast around mid grey"),
            new("HeightBias", "Bias", -0.5, 0.5, 0.01, "Added after the contrast"),
            new("HeightGain", "S-curve", -1, 1, 0.05, "Steeper (above 0) or flatter (below) around mid grey"),
            new("HeightInvert", "Dark is high", 0, 1, 1, "Invert"),
            .. Samples("Height", "Height", "This colour's height"),
            new("HeightSampleBlend", "Samples' pull", 0, 1, 0.01, "How far the colour samples pull the height to their values"),
            .. MaskSettings("Height"),
            new("HeightScale", "Parallax depth", 0, 0.2, 0.005, "The material's parallax depth (0: no height map in the material)", MaterialOnly: true),
        ],
        [MaterialStep.Normal] =
        [
            new("NormalStrength", "Strength", 0, 16, 0.1, "Slope steepness"),
            new("NormalBlur", "Smoothing", 0, 8, 0.1, "The height smoothed first (pixels of the classic texture)"),
            new("NormalFilter", "Filter", 0, 1, 1, "Sobel or Scharr (crisper on diagonals)"),
            new("NormalInvert", "Invert", 0, 1, 1, "Swap up and down"),
            .. Band("NormalBand", "Band weights", 0, 1, 0.01, "bands:normal", "Each band of the slopes (fine to coarse): crisp, mids or smooth"),
            new("NormalLightAngle", "Painted light", 0, 360, 1, "Where the texture's painted light comes from (degrees; drag the arrow on the map)"),
            new("NormalShape", "Shape from shading", 0, 1, 0.05, "How much of the slope along the painted light comes from the shading"),
            new("NormalShapeRadius", "Shading size", 0.5, 32, 0.5, "What is wider counts as paint, not shape (pixels of the classic texture)"),
            new("NormalAngularity", "Angularity", 0, 1, 0.05, "A bevelled, faceted look: every slope pulled to the same tilt"),
            new("NormalContrast", "Contrast", 0, 4, 0.05, "The final tilt scaled"),
        ],
        [MaterialStep.Roughness] =
        [
            new("RoughnessBase", "Base", 0, 1, 0.01, "A flat mid-grey area's roughness (Skip: everywhere)"),
            new("RoughnessRange", "Range", 0, 2, 0.01, "How far brightness and detail move it"),
            new("RoughnessDetail", "Detail share", 0, 1, 0.05, "Local contrast against brightness"),
            new("RoughnessDetailRadius", "Detail size", 0.5, 16, 0.5, "The local contrast's radius (pixels of the classic texture)"),
            new("RoughnessInvert", "Brighter is rougher", 0, 1, 1, "Invert"),
            .. Samples("Roughness", "Roughness", "This colour's roughness"),
            .. MaskSettings("Roughness"),
            new("RoughnessMetal", "Metal's roughness", 0, 1, 0.01, "Where the metallic map is on"),
            new("RoughnessContrast", "Contrast", 0, 4, 0.05, "Around mid grey"),
            new("RoughnessBias", "Bias", -0.5, 0.5, 0.01, "Added after the contrast"),
            new("RoughnessOverlay", "Fine detail", 0, 4, 0.05, "The fine detail laid over (x (1 + high-pass x amount))"),
            new("RoughnessEdgeWear", "Edge wear", 0, 1, 0.05, "Convex edges (the normals' curvature) smoother"),
        ],
        [MaterialStep.Metallic] =
        [
            new("Metallic", "Value", 0, 1, 0.01, "The constant (Skip), or the mask's value where it is on"),
            new("MetallicColour", "Metal's colour", 0, 1, 1, "Pick the metal's colour (a colour mask); none: the brightness mask below", Group: "sample"),
            .. MaskSettings("Metallic"),
            new("MetallicBlur", "Pre-blur", 0, 8, 0.1, "The copy the mask reads is blurred first (pixels of the classic texture)"),
            new("MetallicThreshold", "Mask from brightness", 0, 1.01, 0.01, "Without a colour: where the brightness passes it, metal (above 1: the value everywhere)"),
            new("MetallicSoftness", "Softness", 0, 0.5, 0.01, "The ramp around the threshold"),
            new("MetallicInvert", "Dark is metal", 0, 1, 1, "Invert the brightness mask"),
            new("MetallicContrast", "Contrast", 0, 4, 0.05, "Around mid grey"),
            new("MetallicBias", "Bias", -0.5, 0.5, 0.01, "Added after the contrast"),
            new("MetallicOverlay", "Fine detail", 0, 4, 0.05, "The fine detail laid over"),
        ],
        [MaterialStep.Occlusion] =
        [
            new("OcclusionRadius", "Radius", 0, 32, 0.5, "How far it looks (pixels of the classic texture)"),
            new("OcclusionStrength", "Strength", 0, 4, 0.05, "Factor on the darkening"),
            new("OcclusionDepth", "Depth", 1, 64, 0.5, "How deep the relief is"),
            new("OcclusionSamples", "Directions", 4, 64, 4, "Directions per pixel (the preview adds four a frame)"),
            new("OcclusionNormalShare", "From the normals", 0, 1, 0.05, "The surface's own tilt against the height's horizon"),
            new("OcclusionBias", "Bias", -0.5, 0.5, 0.01, "Added at the end"),
            new("OcclusionPower", "Power", 0.1, 4, 0.05, "The occlusion raised to it (above 1: darker)"),
        ],
        [MaterialStep.Emission] =
        [
            new("EmissionThreshold", "Threshold", 0, 1, 0.01, "The colour glows where its brightest channel passes this"),
            new("EmissionSoftness", "Softness", 0, 0.5, 0.01, "The ramp around the threshold"),
            new("EmissionEnergy", "Energy", 0, 16, 0.05, "The material's emission energy", MaterialOnly: true),
        ],
        [MaterialStep.BentNormal] =
        [
            new("BentNormalStrength", "Strength", 0, 1, 0.05, "0: the surface's own normal; 1: the mean open direction (from the occlusion's directions: its radius and depth)"),
            new("BentNormalFromNormals", "From the normals", 0, 1, 0.05, "How much the normal map's own tilt bends it too"),
        ],
        [MaterialStep.Rim] =
        [
            .. MaskStep("Rim", "rim"),
            new("RimAmount", "Rim", 0, 1, 0.01, "The material's rim light (soft light at grazing angles: cloth, velvet, moss)", MaterialOnly: true),
            new("RimTint", "Rim tint", 0, 1, 0.01, "0: the light's colour; 1: the albedo's", MaterialOnly: true),
        ],
        [MaterialStep.Clearcoat] =
        [
            .. MaskStep("Clearcoat", "lacquer"),
            new("ClearcoatAmount", "Coat", 0, 1, 0.01, "The material's clearcoat (a lacquer layer: polished wood, wet stone, paint)", MaterialOnly: true),
            new("ClearcoatRoughness", "Coat roughness", 0, 1, 0.01, "The coat's roughness (1 - gloss)", MaterialOnly: true),
        ],
        [MaterialStep.Anisotropy] =
        [
            new("AnisotropySource", "Direction", 0, 1, 1, "From the texture's own grain (the brightness gradient's orientation, smoothed), or a fixed angle"),
            new("AnisotropyAngle", "Angle", 0, 180, 1, "The fixed direction (degrees: 0 right, 90 up); with the grain: its turn"),
            new("AnisotropySmooth", "Grain smoothing", 0.5, 16, 0.5, "The grain's direction field smoothed (classic texels)"),
            new("AnisotropyAlongGrain", "Along the grain", 0, 1, 1, "The highlight along the grain instead of across it (brushed metal and hair: across)"),
            new("AnisotropyCoherence", "From clarity", 0, 1, 0.05, "How much a clear grain sets the strength (0: the same everywhere)"),
            new("AnisotropyAmount", "Anisotropy", -1, 1, 0.01, "The material's anisotropy: highlights stretched along the flow", MaterialOnly: true),
        ],
        [MaterialStep.Subsurface] =
        [
            .. MaskStep("Subsurface", "scattering"),
            new("SubsurfaceStrength", "Strength", 0, 1, 0.01, "The material's subsurface scattering (skin, wax, leaves; Forward+ only)", MaterialOnly: true),
            new("SubsurfaceSkin", "Skin", 0, 1, 1, "Godot's skin profile", MaterialOnly: true),
        ],
        [MaterialStep.Transmittance] =
        [
            new("TransmittanceTint", "Colour from", 0, 1, 1, "The light's colour through the surface: the albedo's, or a colour"),
            new("TransmittanceColour", "Colour", 0, 1, 1, "The colour (Colour from: Colour)"),
            new("TransmittanceSaturation", "Saturation", 0, 2, 0.05, "The albedo's tint's saturation"),
            new("TransmittanceThickness", "Thickness from", 0, 2, 1, "The height (high is thick), the brightness, or one thickness"),
            new("TransmittanceInvert", "High is thin", 0, 1, 1, "Invert the thickness"),
            new("TransmittanceContrast", "Contrast", 0, 4, 0.05, "The thinness's contrast around mid grey"),
            new("TransmittanceBias", "Bias", -0.5, 0.5, 0.01, "Added after the contrast"),
            new("TransmittanceDepth", "Depth", 0.001, 8, 0.001, "The material's transmittance depth", MaterialOnly: true),
            new("TransmittanceBoost", "Boost", 0, 1, 0.01, "The material's transmittance boost", MaterialOnly: true),
        ],
        [MaterialStep.Backlight] =
        [
            .. MaskStep("Backlight", "light through"),
            new("BacklightTint", "Tint", 0, 1, 1, "The light's colour through the surface"),
            new("BacklightFromAlbedo", "Tint from albedo", 0, 1, 0.05, "The tint taken from the albedo instead (leaves: their own green)"),
        ],
        [MaterialStep.Refraction] =
        [
            .. MaskStep("Refraction", "refraction"),
            new("RefractionScale", "Scale", -1, 1, 0.005, "The material's refraction (the normal map bends what is behind: glass, water; makes it transparent)", MaterialOnly: true),
        ],
        [MaterialStep.Detail] =
        [
            .. MaskStep("Detail", "detail"),
            new("DetailFrom", "Detail from", 0, 1, 1, "The detail albedo and normal: two files, or another texture's made maps"),
            new("DetailScale", "Detail repeats", 1, 32, 0.5, "How often the detail repeats over the texture (its own UV scale)", MaterialOnly: true),
            new("DetailBlend", "Blend", 0, 3, 1, "How the detail albedo is laid over", MaterialOnly: true),
        ],
    };

    /// <summary>A mask step's settings: where the mask comes from, the colour mask, the ramp, the value.</summary>
    private static IEnumerable<StepSetting> MaskStep(string step, string what) =>
    [
        new($"{step}Source", "Mask from", 0, 4, 1, $"Where the {what} is: everywhere (Constant), by brightness, by a colour (pick it), by height, by the normals' tilt"),
        new($"{step}Colour", "Colour", 0, 1, 1, "Mask from Colour: pick the colour (none: off)", Group: "sample"),
        .. MaskSettings(step),
        new($"{step}Threshold", "Ramp at", 0, 1, 0.01, "Brightness, height or tilt: where the mask turns on"),
        new($"{step}Softness", "Ramp softness", 0, 1, 0.01, "The ramp around it"),
        new($"{step}Invert", "Invert", 0, 1, 1, "Invert the mask (not the constant)"),
        new($"{step}Value", "Value", 0, 1, 0.01, "The mask's value where it is on"),
        new($"{step}Blur", "Pre-blur", 0, 8, 0.1, "The colour and brightness read from a blurred copy (classic texels)"),
    ];

    private static IEnumerable<StepSetting> Band(string prefix, string label, double min, double max, double step, string group, string tip) =>
        Enumerable.Range(0, 7).Select(k => new StepSetting($"{prefix}{k}", $"{label} {k}", min, max, step, tip, Group: group));

    private static IEnumerable<StepSetting> Samples(string step, string what, string tip) =>
        Enumerable.Range(1, 3).SelectMany(i => new StepSetting[]
        {
            new($"{step}Sample{i}", $"Colour {i}", 0, 1, 1, "Pick a colour in the 2D map (none: off)", Group: "sample"),
            new($"{step}SampleValue{i}", $"{what} {i}", 0, 1, 0.01, tip, Group: "sample"),
        });

    private static IEnumerable<StepSetting> MaskSettings(string step) =>
    [
        new($"{step}MaskHue", "Mask: hue", 0, 1, 0.05, "The colour masks' weight on hue"),
        new($"{step}MaskSat", "Mask: saturation", 0, 1, 0.05, "Their weight on saturation"),
        new($"{step}MaskLum", "Mask: lightness", 0, 1, 0.05, "Their weight on lightness"),
        new($"{step}MaskLow", "Mask: from", 0, 1, 0.01, "The similarity where a mask starts"),
        new($"{step}MaskHigh", "Mask: full", 0, 1, 0.01, "The similarity where it is full"),
    ];

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

    /// <summary>Every file a recipe reads (the steps' imports, the detail's albedo and normal), by key.</summary>
    public static IEnumerable<string> Files(PbrRecipe r)
    {
        foreach (var s in Steps)
            if (On(r, s) && ModeOf(r, s) == StepMode.Import && FileOf(r, s) is { Length: > 0 } f)
                yield return f;
        var (a, n) = DetailKeys(r);
        if (a.Length > 0)
            yield return a;
        if (n.Length > 0)
            yield return n;
    }

    public static string FileOf(PbrRecipe r, MaterialStep s) => (string)Prop(ModeProps(s).File).GetValue(r)!;

    /// <summary>
    /// Makes an R2 recipe (<see cref="PbrRecipe.Version"/> 1) a workbench one: emission on where
    /// R2's threshold made one, metallic as its constant. The fields both read keep their meaning.
    /// </summary>
    public static void Migrate(PbrRecipe r)
    {
        if (r.Version >= 3)
            return;
        if (r.Version == 2)
        {
            // phase 1's: the same settings, phase 2's algorithms
            r.Version = 3;
            return;
        }
        r.Version = 3;
        r.EmissionMode = r.EmissionThreshold <= 1f ? StepMode.Generate : StepMode.Skip;
        r.MetallicMode = StepMode.Skip;
    }

    // ---- running ---------------------------------------------------------------------------------

    private readonly Dictionary<int, CpuBackend> _backends = new();
    private readonly ConditionalWeakTable<Tex, RgbaImage> _converted = new();
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

    /// <summary>The colour sample shown as a mask (the 2D view's isolate), or null.</summary>
    public (MaterialStep Step, int Sample)? Isolate { get; set; }

    /// <summary>
    /// Asks for the maps of <paramref name="inputs"/> at each of <paramref name="sides"/>
    /// (smallest first), <paramref name="focus"/>'s chain first; the run before is cancelled
    /// (unless it was a kept one: the save's). <paramref name="keep"/>: this run is not cancelled
    /// by later ones (they wait for it). Returns the run's number (results carry it; older ones
    /// are stale). At the working size the occlusion comes in two parts (its first directions,
    /// then all), as the GPU's settles over frames; the save's takes every direction at once.
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
        var isolate = Isolate;
        _chain = _chain.ContinueWith(_ =>
        {
            try
            {
                if (cts.IsCancellationRequested)
                    return;
                Error = null;
                var posts = new List<Result>();
                void Post(Result r)
                {
                    // held back one, so the last can say it is
                    if (posts.Count > 0)
                        Results.Enqueue(posts[^1]);
                    posts.Clear();
                    posts.Add(r);
                }
                for (int i = 0; i < sides.Length; i++)
                {
                    bool bake = keep || sides[i] == FullSide;
                    bool small = !bake && i < sides.Length - 1;
                    var b = Backend(bake ? -1 : sides[i]);
                    b.Cancel = cts.Token;
                    var (w, h, scale) = Size(inputs, sides[i]);
                    var outs = MaterializeGraph.Build(b, inputs.Key, inputs.Original, inputs.Imports, inputs.Recipe,
                        new BuildRequest(w, h, scale, bake || small ? -1 : 1, isolate));
                    foreach (var s in order)
                        if (outs.Maps.ContainsKey(s))
                            Post(MakeResult(b, outs, run, sides[i], s, clock));
                    foreach (var (name, t) in outs.Aux)
                        Post(new Result(run, sides[i], null, ToImage8(b, t), clock.Elapsed.TotalMilliseconds, false, ResultKind.Aux, Aux: name));
                    Post(new Result(run, sides[i], null, ToImage8(b, outs.Orm), clock.Elapsed.TotalMilliseconds, false, ResultKind.Orm));
                    if (outs.Mask != null)
                        Post(new Result(run, sides[i], null, ToImage8(b, outs.Mask), clock.Elapsed.TotalMilliseconds, false, ResultKind.Mask));
                    if (!bake && !small && outs.AoDone < outs.AoChunks)
                    {
                        // the occlusion's other directions
                        outs = MaterializeGraph.Build(b, inputs.Key, inputs.Original, inputs.Imports, inputs.Recipe, new BuildRequest(w, h, scale, 64, isolate));
                        Post(MakeResult(b, outs, run, sides[i], MaterialStep.Occlusion, clock));
                        if (outs.Maps.ContainsKey(MaterialStep.BentNormal))
                            Post(MakeResult(b, outs, run, sides[i], MaterialStep.BentNormal, clock));
                        Post(new Result(run, sides[i], null, ToImage8(b, outs.Orm), clock.Elapsed.TotalMilliseconds, false, ResultKind.Orm));
                    }
                    if (bake)
                        _backends.Remove(-1);
                }
                if (posts.Count > 0)
                    Results.Enqueue(posts[0] with { Last = true });
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

    private CpuBackend Backend(int side)
    {
        if (!_backends.TryGetValue(side, out var b))
            _backends[side] = b = new CpuBackend();
        return b;
    }

    /// <summary>A pass's image as 8-bit RGBA, once per image (the same map gives the same object).</summary>
    private RgbaImage ToImage8(CpuBackend b, Tex t) => _converted.GetValue(t, _ => b.Get(t).ToRgba());

    private Result MakeResult(CpuBackend b, MaterializeOutputs outs, int run, int side, MaterialStep s, Stopwatch clock)
    {
        var tex = outs.Maps[s];
        var img = ToImage8(b, tex);
        float[]? f = s == MaterialStep.Height ? b.Get(tex).Red() : null;
        ColourCheck? check = null;
        if (s == MaterialStep.Albedo && outs.AlbedoStats != null && outs.SourceStats != null)
        {
            var src = b.Get(outs.SourceStats).Fet(0, 0);
            var alb = b.Get(outs.AlbedoStats).Fet(0, 0);
            check = new ColourCheck(src.X, src.Y, alb.X, alb.Y);
        }
        return new Result(run, side, s, img, clock.Elapsed.TotalMilliseconds, false, ResultKind.Map, f, check);
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

    public static RgbaImage Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (r, g, b, 255);
        return new RgbaImage(w, h, px);
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
