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

    // ---- R2b phase 4: the seam tool and the maps added with "+ map" (docs/remaster-materials.md) --
    // Every setting below is declared after EmissionEnergy on purpose: a profile's hash counts
    // them only when they differ from their defaults (MaterialProfiles.Hash), so the textures made
    // before phase 4 keep their profile's hash.

    [ExportGroup("Seam", "Seam")]
    /// <summary>The source's seam tool: off, a height-aware overlap across the wrap edges, or a scatter of rotated copies.</summary>
    [Export] public SeamMode SeamMode { get; set; } = SeamMode.Off;
    /// <summary>The overlap's width, a fraction of the side (the tile is that much shorter, stretched back to the size).</summary>
    [Export(PropertyHint.Range, "0.02,0.45,0.01")] public float SeamOverlap { get; set; } = 0.15f;
    /// <summary>How much the taller feature wins across the seam (0: a plain cross-fade).</summary>
    [Export(PropertyHint.Range, "0,10,0.1")] public float SeamHeight { get; set; } = 3f;
    /// <summary>The blend's width (smaller: a harder edge between the two sides).</summary>
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float SeamSoftness { get; set; } = 0.5f;
    /// <summary>The height the seam tool compares (the source's brightness) is smoothed first (classic texels).</summary>
    [Export(PropertyHint.Range, "0,8,0.1")] public float SeamHeightBlur { get; set; } = 1f;
    /// <summary>Scatter: stamps per side (even: the staggered rows wrap).</summary>
    [Export(PropertyHint.Range, "2,16,2")] public int ScatterGrid { get; set; } = 4;
    /// <summary>Scatter: a stamp's size against its cell.</summary>
    [Export(PropertyHint.Range, "0.8,3,0.05")] public float ScatterScale { get; set; } = 1.6f;
    /// <summary>Scatter: every stamp's rotation (degrees).</summary>
    [Export(PropertyHint.Range, "0,360,1")] public float ScatterRotation { get; set; }
    /// <summary>Scatter: each stamp's random rotation, up to this either way (degrees).</summary>
    [Export(PropertyHint.Range, "0,180,1")] public float ScatterRandomRotation { get; set; } = 180f;
    /// <summary>Scatter: each stamp's random offset from its place (a fraction of a cell).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float ScatterJitter { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0,9999,1")] public int ScatterSeed { get; set; } = 1;
    /// <summary>Scatter: the stamps' soft edge (0: hard discs, 1: all soft).</summary>
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float ScatterSoftness { get; set; } = 0.5f;
    /// <summary>Scatter: how much the taller stamp wins where stamps overlap (0: an even blend).</summary>
    [Export(PropertyHint.Range, "0,24,0.5")] public float ScatterHeight { get; set; } = 6f;

    [ExportGroup("Bent normal", "BentNormal")]
    /// <summary>The step is in the strip and the material ("+ map").</summary>
    [Export] public bool BentNormalOn { get; set; }
    [Export] public StepMode BentNormalMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string BentNormalFile { get; set; } = "";
    /// <summary>0: flat (the surface's normal), 1: the mean open direction.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float BentNormalStrength { get; set; } = 1f;
    /// <summary>How much the normal map's own tilt bends it too.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float BentNormalFromNormals { get; set; } = 1f;

    [ExportGroup("Rim", "Rim")]
    [Export] public bool RimOn { get; set; }
    [Export] public StepMode RimMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string RimFile { get; set; } = "";
    [Export] public MaskSource RimSource { get; set; } = MaskSource.Constant;
    [Export] public Color RimColour { get; set; } = new(0, 0, 0, 0);
    [Export(PropertyHint.Range, "0,1,0.05")] public float RimMaskHue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float RimMaskSat { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float RimMaskLum { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RimMaskLow { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RimMaskHigh { get; set; } = 0.97f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RimThreshold { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RimSoftness { get; set; } = 0.2f;
    [Export] public bool RimInvert { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float RimValue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,8,0.1")] public float RimBlur { get; set; } = 1f;
    /// <summary>The material's rim (Godot's <c>rim</c>).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float RimAmount { get; set; } = 0.5f;
    /// <summary>The material's rim tint (0: white light, 1: the albedo's colour).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float RimTint { get; set; } = 0.5f;

    [ExportGroup("Clearcoat", "Clearcoat")]
    [Export] public bool ClearcoatOn { get; set; }
    [Export] public StepMode ClearcoatMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string ClearcoatFile { get; set; } = "";
    [Export] public MaskSource ClearcoatSource { get; set; } = MaskSource.Constant;
    [Export] public Color ClearcoatColour { get; set; } = new(0, 0, 0, 0);
    [Export(PropertyHint.Range, "0,1,0.05")] public float ClearcoatMaskHue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float ClearcoatMaskSat { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float ClearcoatMaskLum { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClearcoatMaskLow { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClearcoatMaskHigh { get; set; } = 0.97f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClearcoatThreshold { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClearcoatSoftness { get; set; } = 0.2f;
    [Export] public bool ClearcoatInvert { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClearcoatValue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,8,0.1")] public float ClearcoatBlur { get; set; } = 1f;
    /// <summary>The material's clearcoat (Godot's <c>clearcoat</c>).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClearcoatAmount { get; set; } = 1f;
    /// <summary>The coat's roughness (Godot's <c>clearcoat_roughness</c>: 1 - gloss).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClearcoatRoughness { get; set; } = 0.3f;

    [ExportGroup("Anisotropy", "Anisotropy")]
    [Export] public bool AnisotropyOn { get; set; }
    [Export] public StepMode AnisotropyMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string AnisotropyFile { get; set; } = "";
    /// <summary>The direction from the texture's own grain, or a fixed angle.</summary>
    [Export] public FlowSource AnisotropySource { get; set; } = FlowSource.Grain;
    /// <summary>The fixed angle (degrees, 0: right, 90: up), and the grain's turn when it is read from the texture.</summary>
    [Export(PropertyHint.Range, "0,180,1")] public float AnisotropyAngle { get; set; }
    /// <summary>The grain's direction field smoothed (classic texels).</summary>
    [Export(PropertyHint.Range, "0.5,16,0.5")] public float AnisotropySmooth { get; set; } = 3f;
    /// <summary>The highlight along the grain instead of across it (a brushed surface's lies across its scratches).</summary>
    [Export] public bool AnisotropyAlongGrain { get; set; }
    /// <summary>How much the grain's clarity sets the strength (0: the same everywhere).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float AnisotropyCoherence { get; set; } = 1f;
    /// <summary>The material's anisotropy (Godot's <c>anisotropy</c>).</summary>
    [Export(PropertyHint.Range, "-1,1,0.01")] public float AnisotropyAmount { get; set; } = 0.5f;

    [ExportGroup("Subsurface", "Subsurface")]
    [Export] public bool SubsurfaceOn { get; set; }
    [Export] public StepMode SubsurfaceMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string SubsurfaceFile { get; set; } = "";
    [Export] public MaskSource SubsurfaceSource { get; set; } = MaskSource.Constant;
    [Export] public Color SubsurfaceColour { get; set; } = new(0, 0, 0, 0);
    [Export(PropertyHint.Range, "0,1,0.05")] public float SubsurfaceMaskHue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float SubsurfaceMaskSat { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float SubsurfaceMaskLum { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float SubsurfaceMaskLow { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float SubsurfaceMaskHigh { get; set; } = 0.97f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float SubsurfaceThreshold { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float SubsurfaceSoftness { get; set; } = 0.2f;
    [Export] public bool SubsurfaceInvert { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float SubsurfaceValue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,8,0.1")] public float SubsurfaceBlur { get; set; } = 1f;
    /// <summary>The material's subsurface scattering strength.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float SubsurfaceStrength { get; set; } = 0.5f;
    /// <summary>Godot's skin mode (a skin's scattering profile).</summary>
    [Export] public bool SubsurfaceSkin { get; set; }

    [ExportGroup("Transmittance", "Transmittance")]
    [Export] public bool TransmittanceOn { get; set; }
    [Export] public StepMode TransmittanceMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string TransmittanceFile { get; set; } = "";
    /// <summary>The light's colour through the surface: from the albedo, or a picked colour.</summary>
    [Export] public TintSource TransmittanceTint { get; set; } = TintSource.Albedo;
    [Export] public Color TransmittanceColour { get; set; } = new(1f, 0.55f, 0.35f, 1f);
    /// <summary>The albedo's tint's saturation (1: as it is).</summary>
    [Export(PropertyHint.Range, "0,2,0.05")] public float TransmittanceSaturation { get; set; } = 1f;
    /// <summary>The thickness from the height (high is thick), the brightness, or one thickness.</summary>
    [Export] public ThicknessSource TransmittanceThickness { get; set; } = ThicknessSource.Height;
    /// <summary>High (or bright) is thin.</summary>
    [Export] public bool TransmittanceInvert { get; set; }
    [Export(PropertyHint.Range, "0,4,0.05")] public float TransmittanceContrast { get; set; } = 1f;
    [Export(PropertyHint.Range, "-0.5,0.5,0.01")] public float TransmittanceBias { get; set; }
    /// <summary>The material's transmittance depth (how deep light goes through).</summary>
    [Export(PropertyHint.Range, "0.001,8,0.001")] public float TransmittanceDepth { get; set; } = 0.1f;
    /// <summary>The material's transmittance boost.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float TransmittanceBoost { get; set; }

    [ExportGroup("Backlight", "Backlight")]
    [Export] public bool BacklightOn { get; set; }
    [Export] public StepMode BacklightMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string BacklightFile { get; set; } = "";
    [Export] public MaskSource BacklightSource { get; set; } = MaskSource.Constant;
    [Export] public Color BacklightColour { get; set; } = new(0, 0, 0, 0);
    [Export(PropertyHint.Range, "0,1,0.05")] public float BacklightMaskHue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float BacklightMaskSat { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float BacklightMaskLum { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float BacklightMaskLow { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float BacklightMaskHigh { get; set; } = 0.97f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float BacklightThreshold { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float BacklightSoftness { get; set; } = 0.2f;
    [Export] public bool BacklightInvert { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float BacklightValue { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,8,0.1")] public float BacklightBlur { get; set; } = 1f;
    /// <summary>The light's tint through the surface (with <see cref="BacklightFromAlbedo"/> 0).</summary>
    [Export] public Color BacklightTint { get; set; } = new(1, 1, 1, 1);
    /// <summary>The tint taken from the albedo instead (1: the surface's own colour).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float BacklightFromAlbedo { get; set; } = 1f;

    [ExportGroup("Refraction", "Refraction")]
    [Export] public bool RefractionOn { get; set; }
    [Export] public StepMode RefractionMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string RefractionFile { get; set; } = "";
    [Export] public MaskSource RefractionSource { get; set; } = MaskSource.Constant;
    [Export] public Color RefractionColour { get; set; } = new(0, 0, 0, 0);
    [Export(PropertyHint.Range, "0,1,0.05")] public float RefractionMaskHue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float RefractionMaskSat { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float RefractionMaskLum { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RefractionMaskLow { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RefractionMaskHigh { get; set; } = 0.97f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RefractionThreshold { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RefractionSoftness { get; set; } = 0.2f;
    [Export] public bool RefractionInvert { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float RefractionValue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,8,0.1")] public float RefractionBlur { get; set; } = 1f;
    /// <summary>The material's refraction scale.</summary>
    [Export(PropertyHint.Range, "-1,1,0.005")] public float RefractionScale { get; set; } = 0.05f;

    [ExportGroup("Detail", "Detail")]
    [Export] public bool DetailOn { get; set; }
    /// <summary>The detail mask's mode (its file: an imported mask).</summary>
    [Export] public StepMode DetailMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string DetailFile { get; set; } = "";
    [Export] public MaskSource DetailSource { get; set; } = MaskSource.Constant;
    [Export] public Color DetailColour { get; set; } = new(0, 0, 0, 0);
    [Export(PropertyHint.Range, "0,1,0.05")] public float DetailMaskHue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float DetailMaskSat { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.05")] public float DetailMaskLum { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DetailMaskLow { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DetailMaskHigh { get; set; } = 0.97f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DetailThreshold { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DetailSoftness { get; set; } = 0.2f;
    [Export] public bool DetailInvert { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")] public float DetailValue { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,8,0.1")] public float DetailBlur { get; set; } = 1f;
    /// <summary>Where the detail albedo and normal come from: files, or another texture's made maps.</summary>
    [Export] public DetailFrom DetailFrom { get; set; } = DetailFrom.Files;
    /// <summary>The texture whose made albedo and normal are the detail (its ID).</summary>
    [Export] public string DetailTexture { get; set; } = "";
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string DetailAlbedoFile { get; set; } = "";
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string DetailNormalFile { get; set; } = "";
    /// <summary>The detail's repeats over the texture (its own UV scale).</summary>
    [Export(PropertyHint.Range, "1,32,0.5")] public float DetailScale { get; set; } = 4f;
    [Export] public DetailBlend DetailBlend { get; set; } = DetailBlend.Mix;
}

/// <summary>The source's seam tool (docs/remaster-materials.md, "Source").</summary>
public enum SeamMode
{
    Off,
    /// <summary>Height-aware overlap across the wrap edges: the taller feature wins (horizontally, then vertically).</summary>
    Overlap,
    /// <summary>Stamps of the texture in a staggered grid, rotated, composited by height.</summary>
    Scatter,
}

/// <summary>What a mask step (rim, clearcoat, subsurface, backlight, refraction, detail) reads.</summary>
public enum MaskSource
{
    /// <summary>The value everywhere.</summary>
    Constant,
    /// <summary>A ramp over the albedo's brightness.</summary>
    Brightness,
    /// <summary>A colour mask (a picked colour; hue, saturation and lightness weights).</summary>
    Colour,
    /// <summary>A ramp over the height.</summary>
    Height,
    /// <summary>A ramp over the normals' tilt.</summary>
    Tilt,
}

/// <summary>The anisotropy's direction.</summary>
public enum FlowSource
{
    /// <summary>From the texture's own grain (the luminance gradient's structure tensor, smoothed).</summary>
    Grain,
    /// <summary>A fixed angle.</summary>
    Angle,
}

/// <summary>The transmittance's colour.</summary>
public enum TintSource
{
    Albedo,
    Colour,
}

/// <summary>The transmittance's thickness.</summary>
public enum ThicknessSource
{
    Height,
    Brightness,
    Constant,
}

/// <summary>Where the detail's albedo and normal come from.</summary>
public enum DetailFrom
{
    /// <summary>Two files of one's own.</summary>
    Files,
    /// <summary>Another texture's made maps (a texture of the project with a material).</summary>
    Texture,
}

/// <summary>How the detail albedo is laid over (Godot's detail blend modes).</summary>
public enum DetailBlend
{
    Mix,
    Add,
    Sub,
    Mul,
}
