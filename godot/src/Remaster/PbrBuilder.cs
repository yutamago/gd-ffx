using System;
using System.Collections.Generic;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// A texture's physically based maps (<see cref="Materialize.MaterialBaker.Images"/>): albedo
/// (sRGB), normal (OpenGL, green up), ORM (occlusion, roughness, metallic), height, emission (null:
/// none), and the maps added with "+ map" (R2b phase 4) by their file role
/// (<see cref="PbrBuilder.ExtraRoles"/>).
/// </summary>
public sealed record PbrImages(Image Albedo, Image Normal, Image Orm, Image? Height, Image? Emission, IReadOnlyDictionary<string, Image>? Extra = null);

/// <summary>
/// A texture's physically based material from its maps (docs/remaster-editor.md, "Materials"):
/// pixels to and from Godot images, the Lanczos upscale, the <see cref="OrmMaterial3D"/> over the
/// maps, the content hash of the files' names. The maps themselves are made by the workbench's
/// graph (<see cref="Materialize.MaterialBaker"/>; R2's own generator chain, <c>Make</c>, went
/// with the batch in R2b phase 3). Pure CPU work, any thread (the writing and importing: the
/// caller's).
/// </summary>
public static class PbrBuilder
{
    /// <summary><paramref name="img"/> as RGBA8 rows (decompressed and converted when needed; the top mip only).</summary>
    public static RgbaImage From(Image img)
    {
        var copy = (Image)img.Duplicate();
        if (copy.IsCompressed())
            copy.Decompress();
        if (copy.HasMipmaps())
            copy.ClearMipmaps();
        if (copy.GetFormat() != Image.Format.Rgba8)
            copy.Convert(Image.Format.Rgba8);
        return new RgbaImage(copy.GetWidth(), copy.GetHeight(), copy.GetData());
    }

    /// <summary>RGBA8 rows as a Godot image (no mipmaps).</summary>
    public static Image ToImage(RgbaImage img) => Image.CreateFromData(img.Width, img.Height, false, Image.Format.Rgba8, img.Rgba);

    /// <summary><paramref name="img"/> upscaled <paramref name="factor"/> times (Lanczos-3, colour: linear light, premultiplied), shrunk to fit <paramref name="maxSide"/>.</summary>
    public static RgbaImage Upscale(RgbaImage img, int factor, int maxSide = Lanczos.MaxSide)
    {
        var (w, h) = Lanczos.FitWithin(img.Width * Math.Max(1, factor), img.Height * Math.Max(1, factor), Math.Min(maxSide, Lanczos.MaxSide));
        return w == img.Width && h == img.Height ? img : Lanczos.Resize(img, w, h, ResizeOptions.Colour);
    }

    /// <summary>The added maps' file roles, in the order the files and the material list them.</summary>
    public static readonly string[] ExtraRoles =
        ["bent_normal", "rim", "clearcoat", "flowmap", "subsurface", "transmittance", "backlight", "refraction", "detail_mask", "detail_albedo", "detail_normal"];

    /// <summary>
    /// The added maps' features on <paramref name="m"/> (R2b phase 4): each map <paramref name="tex"/>
    /// gives (by role; null: none, the feature off) with the recipe's amounts: the bent normal; rim
    /// (the rim, its tint); clearcoat (the coat, its roughness); anisotropy (the amount, the
    /// flowmap); subsurface scattering (strength, skin) and its transmittance (white: the map holds
    /// the colour; depth, boost); backlight (black: the map holds the light, Godot adds the two);
    /// refraction (scale, the red channel); detail (mask, albedo, normal, blend, on UV2 scaled by the
    /// detail's repeats: the game's lit path reads UV1 at that scale, the preview's meshes carry
    /// UV1 as UV2).
    /// </summary>
    public static void ApplyExtras(BaseMaterial3D m, Func<string, Texture2D?> tex, PbrRecipe r)
    {
        var bent = tex("bent_normal");
        m.BentNormalEnabled = bent != null;
        m.BentNormalTexture = bent;
        var rim = tex("rim");
        m.RimEnabled = rim != null;
        m.RimTexture = rim;
        (m.Rim, m.RimTint) = (r.RimAmount, r.RimTint);
        var cc = tex("clearcoat");
        m.ClearcoatEnabled = cc != null;
        m.ClearcoatTexture = cc;
        (m.Clearcoat, m.ClearcoatRoughness) = (r.ClearcoatAmount, r.ClearcoatRoughness);
        var flow = tex("flowmap");
        m.AnisotropyEnabled = flow != null;
        m.AnisotropyFlowmap = flow;
        m.Anisotropy = r.AnisotropyAmount;
        var sss = tex("subsurface");
        m.SubsurfScatterEnabled = sss != null;
        m.SubsurfScatterTexture = sss;
        (m.SubsurfScatterStrength, m.SubsurfScatterSkinMode) = (r.SubsurfaceStrength, r.SubsurfaceSkin);
        var tr = tex("transmittance");
        m.SubsurfScatterTransmittanceEnabled = tr != null;
        m.SubsurfScatterTransmittanceTexture = tr;
        m.SubsurfScatterTransmittanceColor = Colors.White;
        (m.SubsurfScatterTransmittanceDepth, m.SubsurfScatterTransmittanceBoost) = (r.TransmittanceDepth, r.TransmittanceBoost);
        var back = tex("backlight");
        m.BacklightEnabled = back != null;
        m.BacklightTexture = back;
        m.Backlight = Colors.Black;
        var refr = tex("refraction");
        m.RefractionEnabled = refr != null;
        m.RefractionTexture = refr;
        m.RefractionScale = r.RefractionScale;
        m.RefractionTextureChannel = BaseMaterial3D.TextureChannel.Red;
        var mask = tex("detail_mask");
        m.DetailEnabled = mask != null;
        m.DetailMask = mask;
        m.DetailAlbedo = mask != null ? tex("detail_albedo") : null;
        m.DetailNormal = mask != null ? tex("detail_normal") : null;
        m.DetailBlendMode = (BaseMaterial3D.BlendModeEnum)(int)r.DetailBlend;
        m.DetailUVLayer = BaseMaterial3D.DetailUV.UV2;
        m.UV2Scale = new Vector3(r.DetailScale, r.DetailScale, 1);
    }

    /// <summary>
    /// The material over the maps (textures already made from <see cref="PbrImages"/>): albedo,
    /// normal, ORM, height (parallax by the recipe's scale), emission, and the added maps (by role).
    /// </summary>
    public static OrmMaterial3D Material(Texture2D albedo, Texture2D normal, Texture2D orm, Texture2D? height, Texture2D? emission, PbrRecipe r,
        IReadOnlyDictionary<string, Texture2D>? extra = null)
    {
        var m = new OrmMaterial3D
        {
            AlbedoTexture = albedo,
            NormalEnabled = true,
            NormalTexture = normal,
            NormalScale = 1f,
            OrmTexture = orm,
            Roughness = 1f,
            Metallic = 1f,
            MetallicSpecular = 0.5f,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            TextureRepeat = r.Wrap,
        };
        if (height != null)
        {
            m.HeightmapEnabled = true;
            m.HeightmapTexture = height;
            // Godot's scale is in hundredths of the UV space
            m.HeightmapScale = r.HeightScale * 100f;
        }
        if (emission != null)
        {
            m.EmissionEnabled = true;
            m.EmissionTexture = emission;
            m.Emission = Colors.White;
            m.EmissionEnergyMultiplier = r.EmissionEnergy;
        }
        if (extra is { Count: > 0 })
            ApplyExtras(m, role => extra.GetValueOrDefault(role), r);
        return m;
    }

    /// <summary>The maps as textures in memory (mipmapped), for a material saved with them inside it (outside the project, the checks).</summary>
    public static (Texture2D Albedo, Texture2D Normal, Texture2D Orm, Texture2D? Height, Texture2D? Emission) InMemory(PbrImages p)
    {
        return (T(p.Albedo), T(p.Normal), T(p.Orm), p.Height is { } h ? T(h) : null, p.Emission is { } e ? T(e) : null);
    }

    /// <summary>The added maps as textures in memory (mipmapped), by role.</summary>
    public static Dictionary<string, Texture2D> ExtrasInMemory(PbrImages p)
    {
        var d = new Dictionary<string, Texture2D>();
        foreach (var (role, img) in p.Extra ?? new Dictionary<string, Image>())
            d[role] = T(img);
        return d;
    }

    private static Texture2D T(Image i)
    {
        var c = (Image)i.Duplicate();
        c.GenerateMipmaps();
        return ImageTexture.CreateFromImage(c);
    }

    /// <summary>A short content hash of an image's pixels (file names: written once, undo touches no file).</summary>
    public static string Hash(Image img)
    {
        var data = img.GetData();
        var h = System.Security.Cryptography.SHA256.HashData(data);
        return Convert.ToHexString(h, 0, 4).ToLowerInvariant() + $"{img.GetWidth()}x{img.GetHeight()}";
    }

    /// <summary>The maps' file roles, in the order <see cref="Files"/> lists them.</summary>
    public static IEnumerable<(string Role, Image Image)> Files(PbrImages p)
    {
        yield return ("albedo", p.Albedo);
        yield return ("normal", p.Normal);
        yield return ("orm", p.Orm);
        if (p.Height is { } h)
            yield return ("height", h);
        if (p.Emission is { } e)
            yield return ("emission", e);
        foreach (var role in ExtraRoles)
            if (p.Extra?.GetValueOrDefault(role) is { } x)
                yield return (role, x);
    }
}
