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

    /// <summary>1: R2's recipe (the dock's Generate, the batch); 2: made in the Materials tab (the steps' modes are meant).</summary>
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

    [ExportGroup("Height", "Height")]
    [Export] public StepMode HeightMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string HeightFile { get; set; } = "";
    [Export] public HeightInput HeightFrom { get; set; } = HeightInput.Albedo;
    /// <summary>Paint detail smoothed out first (Gaussian σ, pixels of the classic texture).</summary>
    [Export(PropertyHint.Range, "0,8,0.1")] public float HeightBlur { get; set; } = 1f;
    /// <summary>Dark is high.</summary>
    [Export] public bool HeightInvert { get; set; }

    [ExportGroup("Normal", "Normal")]
    [Export] public StepMode NormalMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string NormalFile { get; set; } = "";
    [Export] public GradientFilter NormalFilter { get; set; } = GradientFilter.Sobel;
    [Export] public bool NormalInvert { get; set; }

    [ExportGroup("Roughness", "Roughness")]
    [Export] public StepMode RoughnessMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string RoughnessFile { get; set; } = "";
    /// <summary>The share of local contrast against brightness.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float RoughnessDetail { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0.5,16,0.5")] public float RoughnessDetailRadius { get; set; } = 2f;
    /// <summary>Brighter is rougher.</summary>
    [Export] public bool RoughnessInvert { get; set; }

    [ExportGroup("Metallic", "Metallic")]
    [Export] public StepMode MetallicMode { get; set; } = StepMode.Skip;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string MetallicFile { get; set; } = "";
    /// <summary>Brightness where the metal mask turns on (above 1: <see cref="Metallic"/> everywhere).</summary>
    [Export(PropertyHint.Range, "0,1.01,0.01")] public float MetallicThreshold { get; set; } = 1.01f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float MetallicSoftness { get; set; } = 0.05f;
    [Export] public bool MetallicInvert { get; set; }

    [ExportGroup("Occlusion", "Occlusion")]
    [Export] public StepMode OcclusionMode { get; set; } = StepMode.Generate;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string OcclusionFile { get; set; } = "";
    [Export(PropertyHint.Range, "4,32,1")] public int OcclusionSamples { get; set; } = 8;
    /// <summary>How deep the relief is (the full height range, pixels of the classic texture).</summary>
    [Export(PropertyHint.Range, "1,64,0.5")] public float OcclusionDepth { get; set; } = 16f;

    [ExportGroup("Emission", "Emission")]
    [Export] public StepMode EmissionMode { get; set; } = StepMode.Skip;
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg")] public string EmissionFile { get; set; } = "";
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float EmissionSoftness { get; set; } = 0.05f;
    /// <summary>The material's emission energy.</summary>
    [Export(PropertyHint.Range, "0,16,0.05")] public float EmissionEnergy { get; set; } = 1f;
}
