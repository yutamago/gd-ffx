using Godot;

namespace GdFfx.Characters;

/// <summary>
/// The material presets for remade characters (docs/remake.md, "Character materials"), each a
/// copy to change: skin (<c>res://remake/materials/skin.tres</c>: Godot's subsurface scattering in
/// skin mode with transmittance), hair (<c>hair.tres</c>: anisotropic highlights along the strands,
/// a flow map, hashed alpha; <c>hair_a2c.tres</c>: alpha to coverage), eyes (<c>eye.tres</c>: a
/// refracted parallax iris under a wet clear coat), cloth (<c>cloth.tres</c>). Original work.
/// </summary>
public static class CharacterMaterials
{
    public const string Dir = "res://remake/materials";

    private static Material Load(string name, System.Func<Material> fallback) =>
        ResourceLoader.Exists($"{Dir}/{name}.tres") && ResourceLoader.Load<Material>($"{Dir}/{name}.tres") is { } m
            ? (Material)m.Duplicate() : fallback();

    public static Material Skin() => Load("skin", () => new StandardMaterial3D
    {
        ResourceName = "Skin", AlbedoColor = new Color(0.82f, 0.6f, 0.48f), Roughness = 0.55f, MetallicSpecular = 0.4f,
        SubsurfScatterEnabled = true, SubsurfScatterStrength = 0.45f, SubsurfScatterSkinMode = true,
        SubsurfScatterTransmittanceEnabled = true, SubsurfScatterTransmittanceColor = new Color(0.95f, 0.4f, 0.3f),
        SubsurfScatterTransmittanceDepth = 0.12f, SubsurfScatterTransmittanceBoost = 0.25f,
    });

    public static Material Hair(bool alphaToCoverage = false) => Load(alphaToCoverage ? "hair_a2c" : "hair",
        () => new StandardMaterial3D { ResourceName = "Hair", AlbedoColor = new Color(0.2f, 0.13f, 0.07f), AnisotropyEnabled = true, Anisotropy = 0.75f });

    public static Material Eye() => Load("eye", () => new StandardMaterial3D { ResourceName = "Eye", AlbedoColor = new Color(0.9f, 0.88f, 0.85f), ClearcoatEnabled = true });

    public static Material Cloth() => Load("cloth", () => new StandardMaterial3D { ResourceName = "Cloth", AlbedoColor = new Color(0.2f, 0.32f, 0.55f), Roughness = 0.88f });

    /// <summary>The presets' paths (the editor's material menu; the checks compile them).</summary>
    public static readonly string[] Presets = ["skin", "hair", "hair_a2c", "eye", "cloth"];
}
