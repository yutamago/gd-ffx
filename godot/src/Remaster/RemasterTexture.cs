using Godot;

namespace GdFfx.Remaster;

/// <summary>How a replacement image is imported and sampled (docs/remaster-editor.md, "Textures").</summary>
public enum TexturePreset
{
    /// <summary>A colour texture: mipmaps, lossless, alpha kept.</summary>
    Colour,
    /// <summary>A colour texture with a cut-out alpha (alpha tested): its alpha border fixed, so the mipmaps do not bleed.</summary>
    CutOut,
    /// <summary>A tangent-space normal map (OpenGL convention, green up, as Godot's materials read it).</summary>
    Normal,
    /// <summary>A single-channel mask or data map (roughness, metallic, occlusion, height): no colour handling.</summary>
    Mask,
}

/// <summary>What a layer's entry says about a texture: its own edits, or back to the classic texture (undoes an earlier layer's).</summary>
public enum TextureMode
{
    Edit,
    Classic,
}

/// <summary>
/// One texture's remaster (docs/remaster-editor.md, "Textures"), keyed by its stable texture ID
/// (<c>x:/.../&lt;name&gt;.dds.phyre</c>; docs/importer.md, "Stable part IDs"): a replacement image
/// drawn wherever the game uses the texture (classic maps too, through the classic shaders), and
/// a physically based material drawn on maps whose render path is remastered. Paths are
/// <c>res://</c>, <c>user://</c> or absolute, or relative to the index file's folder. An entry
/// of the project's index (<see cref="RemasterTextureSet"/>), a pack's, or one made in code.
/// </summary>
[GlobalClass]
[Tool] // the remaster editor reads and writes it: a real instance there, not a placeholder
public partial class RemasterTexture : Resource
{
    /// <summary>The texture's ID (<c>x:/ffx_data/gamedata/ps3data/.../tex/d3d11/&lt;name&gt;.dds.phyre</c>).</summary>
    [Export] public string Id { get; set; } = "";

    [Export] public TextureMode Mode { get; set; } = TextureMode.Edit;

    /// <summary>The replacement image (a PNG or any texture resource; empty: the classic texture).</summary>
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg,*.tres,*.res")] public string Replacement { get; set; } = "";

    [Export] public TexturePreset Preset { get; set; } = TexturePreset.Colour;

    /// <summary>The physically based material (an <c>ORMMaterial3D</c>, <c>StandardMaterial3D</c> or a <c>ShaderMaterial</c> of one's own; empty: none).</summary>
    [Export(PropertyHint.File, "*.tres,*.res,*.material")] public string Material { get; set; } = "";

    /// <summary>
    /// How the material's maps were made: every setting as baked (the profile's with the
    /// exceptions over them), to make them again and to load them into the Materials tab; null:
    /// by hand. R2's (<see cref="PbrRecipe.Version"/> 1) and phase 1's and 2's (2, 3) load as
    /// they are (<see cref="MaterialPipeline.Migrate"/>).
    /// </summary>
    [Export] public PbrRecipe? Recipe { get; set; }

    /// <summary>The material profile it was made with, by name (<see cref="MaterialProfiles"/>: the winning layer's; empty: its own settings only).</summary>
    [Export] public string Profile { get; set; } = "";

    /// <summary>The profile's <see cref="MaterialProfiles.Hash"/> when the maps were made: another hash now means the profile changed since.</summary>
    [Export] public string ProfileHash { get; set; } = "";

    /// <summary>The texture's own settings over the profile's (setting → value; only those pinned to it, its imported files among them).</summary>
    [Export] public Godot.Collections.Dictionary Exceptions { get; set; } = new();

    /// <summary>Where the replacement comes from: made by hand, upscaled (and how), the tool, model and licence of an AI step.</summary>
    [Export(PropertyHint.MultilineText)] public string Source { get; set; } = "";

    [Export(PropertyHint.MultilineText)] public string Note { get; set; } = "";
}
