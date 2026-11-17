using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// How a texture's physically based maps are made from its colour texture (the classic one or
/// its replacement) by the generators ported from ffx-remix (<see cref="Texturing.MapGenerator"/>;
/// docs/remaster-editor.md, "Materials"): the upscale, the de-lighting, then height, normal,
/// roughness, occlusion, metallic and emission. Kept on the texture's entry so the maps can be
/// made again with other values.
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
}
