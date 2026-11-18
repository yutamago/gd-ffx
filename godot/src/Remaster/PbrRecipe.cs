using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.Remaster;

/// <summary>How a step of the material workbench makes its map (docs/remaster-materials.md, "The steps").</summary>
public enum StepMode
{
    /// <summary>Made by the step's generator from the maps before it.</summary>
    Generate,
    /// <summary>A file of one's own (a hand-made map), resized to the working size.</summary>
    Import,
    /// <summary>A constant (flat height and normal, white occlusion, the base roughness, the metallic value, no emission; the albedo: the source as it is).</summary>
    Skip,
}

/// <summary>What the height step reads.</summary>
public enum HeightInput
{
    /// <summary>The albedo (the painted light out first).</summary>
    Albedo,
    /// <summary>The source colour texture as it is.</summary>
    Source,
}

/// <summary>
/// How a texture's physically based maps are made from its colour texture (the classic one or
/// its replacement) by the generators ported from ffx-remix (<see cref="Texturing.MapGenerator"/>;
/// docs/remaster-editor.md, "Materials"): the upscale, the de-lighting, then height, normal,
/// roughness, occlusion, metallic and emission. Kept on the texture's entry so the maps can be
/// made again with other values.
/// <para>R2b's material workbench (docs/remaster-materials.md) adds a mode per step (generate,
/// import a file, skip with a constant), the generators' other options and the material's own
/// settings; <see cref="Version"/> 1 is R2's recipe (the dock and the batch, which read only the
/// first fields), 2 the workbench's. Every field has a default, so old entries load as they
/// were.</para>
/// </summary>
[GlobalClass]
[Tool]
public partial class PbrRecipe : Resource
{
    /// <summary>Upscale before anything else: 1 (none), 2 or 4.</summary>
    [Export(PropertyHint.Enum, "1,2,4")] public int Upscale { get; set; } = 1;

    /// <summary>Which upscaler: <c>lanczos</c> (built in) or <c>comfyui</c> (a ComfyUI server of one's own, when configured).</summary>
    [Export(PropertyHint.Enum, "lanczos,comfyui")] public string Upscaler { get; set; } = "lanczos";

    /// <summary>Take the painted light out of the albedo (de-lighting), 0 (keep) to 1.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float Delight { get; set; } = 0.6f;

    /// <summary>The de-lighting's light size, pixels of the full-size texture.</summary>
    [Export(PropertyHint.Range, "1,64,1")] public float DelightRadius { get; set; } = 16f;

    [Export(PropertyHint.Range, "0,4,0.05")] public float HeightContrast { get; set; } = 1f;

    /// <summary>The material's parallax depth (0: no height map).</summary>
    [Export(PropertyHint.Range, "0,0.2,0.005")] public float HeightScale { get; set; } = 0.02f;

    [Export(PropertyHint.Range, "0,16,0.1")] public float NormalStrength { get; set; } = 4f;

    [Export(PropertyHint.Range, "0,8,0.1")] public float NormalBlur { get; set; }

    [Export(PropertyHint.Range, "0,1,0.01")] public float RoughnessBase { get; set; } = 0.75f;

    [Export(PropertyHint.Range, "0,2,0.01")] public float RoughnessRange { get; set; } = 0.5f;

    [Export(PropertyHint.Range, "0,1,0.01")] public float Metallic { get; set; }

    [Export(PropertyHint.Range, "0,32,0.5")] public float OcclusionRadius { get; set; } = 8f;

    [Export(PropertyHint.Range, "0,4,0.05")] public float OcclusionStrength { get; set; } = 1f;

    /// <summary>Emission where the brightest channel passes this (above 1: no emission map).</summary>
    [Export(PropertyHint.Range, "0,1.01,0.01")] public float EmissionThreshold { get; set; } = 1.01f;

    /// <summary>Tile the edges (a repeating texture stays seamless); from the classic sampler's wrap.</summary>
    [Export] public bool Wrap { get; set; } = true;

    /// <summary>The largest side of the made maps (after the upscale).</summary>
    [Export(PropertyHint.Range, "64,8192,64")] public int MaxSide { get; set; } = 4096;

    // ---- R2b: the material workbench's steps (docs/remaster-materials.md) -----------------------

    /// <summary>1: R2's recipe (the dock's Generate, the batch); 2: made in the Materials tab (the steps' modes are meant); 3: phase 2's algorithms (bands, colour samples, shape from shading).</summary>
    [Export] public int Version { get; set; } = 1;

    /// <summary>The preview's working size (the largest side); the save runs at the full size (<see cref="MaxSide"/>).</summary>
    [Export(PropertyHint.Range, "256,2048,256")] public int WorkingSide { get; set; } = 1024;

    [ExportGroup("Source", "Source")]
    [Export] public StepMode SourceMode { get; set; } = StepMode.Generate;
    /// <summary>With <see cref="StepMode.Import"/>: an image used instead of the texture (store-relative once saved).</summary>
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string SourceFile { get; set; } = "";

    [ExportGroup("Albedo", "Albedo")]
    [Export] public StepMode AlbedoMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string AlbedoFile { get; set; } = "";
    /// <summary>The de-lighting's edge keeping (0: a blur, 1: a guided filter).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float AlbedoEdgeKeep { get; set; } = 0.2f;
    /// <summary>Creases and corners lifted (occlusion found from the height).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float AlbedoOcclusion { get; set; } = 0.25f;
    /// <summary>Small cracks and pores lifted.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float AlbedoCavity { get; set; } = 0.2f;
    /// <summary>Painted highlights pressed down.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float AlbedoHighlights { get; set; } = 0.5f;
    /// <summary>Slow colour casts flattened.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float AlbedoColourCast { get; set; } = 0.5f;
    /// <summary>The average colour pulled to grey (white balance).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float AlbedoNeutralise { get; set; }
    /// <summary>Dark spots (well below their surroundings) lifted (R2b phase 2).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float AlbedoDarkSpots { get; set; }
    /// <summary>The albedo's mean lightness and chroma pulled to the classic texture's (OKLab), 0 (off) to 1.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float AlbedoMatchClassic { get; set; } = 1f;

    [ExportGroup("Height", "Height")]
    [Export] public StepMode HeightMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string HeightFile { get; set; } = "";
    [Export] public HeightInput HeightFrom { get; set; } = HeightInput.Albedo;
    /// <summary>Paint detail smoothed out first (Gaussian σ, pixels of the classic texture).</summary>
    [Export(PropertyHint.Range, "0,8,0.1")] public float HeightBlur { get; set; } = 1f;
    /// <summary>Dark is high.</summary>
    [Export] public bool HeightInvert { get; set; }
    // the band equaliser (R2b phase 2): seven bands, fine (0) to coarse (6), each a weight and a contrast
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightBand0 { get; set; } = 0.15f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightBand1 { get; set; } = 0.19f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightBand2 { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightBand3 { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightBand4 { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightBand5 { get; set; } = 0.9f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightBand6 { get; set; } = 1f;
    [Export(PropertyHint.Range, "-2,2,0.05")] public float HeightBandContrast0 { get; set; } = 1f;
    [Export(PropertyHint.Range, "-2,2,0.05")] public float HeightBandContrast1 { get; set; } = 1f;
    [Export(PropertyHint.Range, "-2,2,0.05")] public float HeightBandContrast2 { get; set; } = 1f;
    [Export(PropertyHint.Range, "-2,2,0.05")] public float HeightBandContrast3 { get; set; } = 1f;
    [Export(PropertyHint.Range, "-2,2,0.05")] public float HeightBandContrast4 { get; set; } = 1f;
    [Export(PropertyHint.Range, "-2,2,0.05")] public float HeightBandContrast5 { get; set; } = 1f;
    [Export(PropertyHint.Range, "-2,2,0.05")] public float HeightBandContrast6 { get; set; } = 1f;
    /// <summary>Added after the contrast.</summary>
    [Export(PropertyHint.Range, "-0.5,0.5,0.01")] public float HeightBias { get; set; }
    /// <summary>An S-curve around mid grey (0: none; above: steeper; below: flatter).</summary>
    [Export(PropertyHint.Range, "-1,1,0.05")] public float HeightGain { get; set; }
    /// <summary>Colour samples (alpha 0: off): where the colour matches, the height is the sample's value.</summary>
    [Export] public Color HeightSample1 { get; set; } = new(0, 0, 0, 0);
    [Export] public Color HeightSample2 { get; set; } = new(0, 0, 0, 0);
    [Export] public Color HeightSample3 { get; set; } = new(0, 0, 0, 0);
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightSampleValue1 { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightSampleValue2 { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightSampleValue3 { get; set; } = 0.5f;
    /// <summary>How far the samples pull the height to their values.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightSampleBlend { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float HeightMaskHue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float HeightMaskSat { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float HeightMaskLum { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightMaskLow { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float HeightMaskHigh { get; set; } = 0.97f;

    [ExportGroup("Normal", "Normal")]
    [Export] public StepMode NormalMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string NormalFile { get; set; } = "";
    [Export] public GradientFilter NormalFilter { get; set; } = GradientFilter.Sobel;
    [Export] public bool NormalInvert { get; set; }
    // the band equaliser over the slopes (R2b phase 2), fine (0) to coarse (6)
    [Export(PropertyHint.Range, "0,1,0.01")] public float NormalBand0 { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float NormalBand1 { get; set; } = 0.7f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float NormalBand2 { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float NormalBand3 { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float NormalBand4 { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float NormalBand5 { get; set; } = 0.15f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float NormalBand6 { get; set; } = 0.1f;
    /// <summary>Shape from shading: the painted light's direction in the texture (degrees, 0: from the right, 90: from the top).</summary>
    [Export(PropertyHint.Range, "0,360,1")] public float NormalLightAngle { get; set; } = 135f;
    /// <summary>How much of the slope along the painted light comes from the shading (0: none).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float NormalShape { get; set; }
    /// <summary>The shading's size: what is wider counts as paint (classic texels).</summary>
    [Export(PropertyHint.Range, "0.5,32,0.5")] public float NormalShapeRadius { get; set; } = 4f;
    /// <summary>A bevelled, faceted look: every slope pulled to the same tilt.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float NormalAngularity { get; set; }
    /// <summary>The final contrast (the tilt scaled, the normal renormalised).</summary>
    [Export(PropertyHint.Range, "0,4,0.05")] public float NormalContrast { get; set; } = 1f;

    [ExportGroup("Roughness", "Roughness")]
    [Export] public StepMode RoughnessMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string RoughnessFile { get; set; } = "";
    /// <summary>The share of local contrast against brightness.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float RoughnessDetail { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0.5,16,0.5")] public float RoughnessDetailRadius { get; set; } = 2f;
    /// <summary>Brighter is rougher.</summary>
    [Export] public bool RoughnessInvert { get; set; }
    /// <summary>Colour samples (alpha 0: off): where the colour matches, the roughness is the sample's value.</summary>
    [Export] public Color RoughnessSample1 { get; set; } = new(0, 0, 0, 0);
    [Export] public Color RoughnessSample2 { get; set; } = new(0, 0, 0, 0);
    [Export] public Color RoughnessSample3 { get; set; } = new(0, 0, 0, 0);
    [Export(PropertyHint.Range, "0,1,0.01")] public float RoughnessSampleValue1 { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RoughnessSampleValue2 { get; set; } = 0.9f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RoughnessSampleValue3 { get; set; } = 0.6f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float RoughnessMaskHue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float RoughnessMaskSat { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float RoughnessMaskLum { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RoughnessMaskLow { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RoughnessMaskHigh { get; set; } = 0.97f;
    /// <summary>The metallic parts' roughness.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float RoughnessMetal { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float RoughnessContrast { get; set; } = 1f;
    [Export(PropertyHint.Range, "-0.5,0.5,0.01")] public float RoughnessBias { get; set; }
    /// <summary>Fine detail laid over (x (1 + high-pass x amount)).</summary>
    [Export(PropertyHint.Range, "0,4,0.05")] public float RoughnessOverlay { get; set; }
    /// <summary>Convex edges (the normals' curvature) made smoother: worn edges.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float RoughnessEdgeWear { get; set; }

    [ExportGroup("Metallic", "Metallic")]
    [Export] public StepMode MetallicMode { get; set; } = StepMode.Skip;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string MetallicFile { get; set; } = "";
    /// <summary>Brightness where the metal mask turns on (above 1: <see cref="Metallic"/> everywhere).</summary>
    [Export(PropertyHint.Range, "0,1.01,0.01")] public float MetallicThreshold { get; set; } = 1.01f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float MetallicSoftness { get; set; } = 0.05f;
    [Export] public bool MetallicInvert { get; set; }
    /// <summary>The metal's colour (alpha 0: the brightness mask instead): a colour mask on a pre-blurred copy.</summary>
    [Export] public Color MetallicColour { get; set; } = new(0, 0, 0, 0);
    [Export(PropertyHint.Range, "0,1,0.05")] public float MetallicMaskHue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float MetallicMaskSat { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float MetallicMaskLum { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MetallicMaskLow { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MetallicMaskHigh { get; set; } = 0.97f;
    /// <summary>The copy the mask is read from is blurred first (classic texels).</summary>
    [Export(PropertyHint.Range, "0,8,0.1")] public float MetallicBlur { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float MetallicContrast { get; set; } = 1f;
    [Export(PropertyHint.Range, "-0.5,0.5,0.01")] public float MetallicBias { get; set; }
    [Export(PropertyHint.Range, "0,4,0.05")] public float MetallicOverlay { get; set; }

    [ExportGroup("Occlusion", "Occlusion")]
    [Export] public StepMode OcclusionMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string OcclusionFile { get; set; } = "";
    /// <summary>Directions per pixel (the preview adds four a frame; the bake takes all at once).</summary>
    [Export(PropertyHint.Range, "4,64,4")] public int OcclusionSamples { get; set; } = 16;
    /// <summary>How deep the relief is (the full height range, pixels of the classic texture).</summary>
    [Export(PropertyHint.Range, "1,64,0.5")] public float OcclusionDepth { get; set; } = 16f;
    /// <summary>The normals' share of the occlusion (the surface's own tilt), against the height's horizon.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float OcclusionNormalShare { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "-0.5,0.5,0.01")] public float OcclusionBias { get; set; }
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float OcclusionPower { get; set; } = 1f;

    [ExportGroup("Emission", "Emission")]
    [Export] public StepMode EmissionMode { get; set; } = StepMode.Skip;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string EmissionFile { get; set; } = "";
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float EmissionSoftness { get; set; } = 0.05f;
    /// <summary>The material's emission energy.</summary>
    [Export(PropertyHint.Range, "0,16,0.05")] public float EmissionEnergy { get; set; } = 1f;
}
