using System;
using System.Collections.Generic;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.Remaster;

/// <summary>A texture's physically based maps, made by <see cref="PbrBuilder.Make"/>: albedo (sRGB), normal (OpenGL, green up), ORM (occlusion, roughness, metallic), height, emission (null: none).</summary>
public sealed record PbrImages(Image Albedo, Image Normal, Image Orm, Image? Height, Image? Emission);

/// <summary>
/// Makes a texture's physically based material from its colour texture (docs/remaster-editor.md,
/// "Materials"): the generators ported from ffx-remix (<see cref="MapGenerator"/>: de-lighting,
/// height, normal, roughness, occlusion, metallic, emission; <see cref="Lanczos"/> for the
/// upscale) run as a <see cref="PbrRecipe"/> says, the maps written as images and an
/// <see cref="OrmMaterial3D"/> over them. Used by the editor (single textures, the batch) and the
/// game's check. Pure CPU work, any thread (the writing and importing: the caller's).
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

    /// <summary>
    /// The maps of <paramref name="colour"/> (the classic texture or its replacement) as
    /// <paramref name="r"/> says. <paramref name="upscaled"/>: the colour texture is upscaled
    /// already (ComfyUI's, by the editor); else the recipe's Lanczos upscale runs first.
    /// </summary>
    public static PbrImages Make(RgbaImage colour, PbrRecipe r, bool upscaled = false, IProgress<double>? progress = null)
    {
        var src = upscaled ? colour : Upscale(colour, r.Upscale, r.MaxSide);
        if (Math.Max(src.Width, src.Height) > r.MaxSide)
        {
            var (w, h) = Lanczos.FitWithin(src.Width, src.Height, r.MaxSide);
            src = Lanczos.Resize(src, w, h, ResizeOptions.Colour);
        }
        // the generators' radii are in pixels of the classic texture: scaled with the upscale
        double scale = (double)src.Width / Math.Max(1, colour.Width);
        if (upscaled)
            scale = 1;
        bool wrap = r.Wrap;
        progress?.Report(0.1);
        var height = MapGenerator.Height(src, new HeightOptions(r.HeightContrast, 1, false, wrap), scale);
        var albedo = r.Delight > 0
            ? MapGenerator.Albedo(src, AlbedoOptions.Default with { Shading = r.Delight, Radius = r.DelightRadius, Wrap = wrap }, height, scale)
            : src;
        progress?.Report(0.4);
        var normal = MapGenerator.Normal(height, new NormalOptions(HeightSource.HeightMap, GradientFilter.Sobel, r.NormalStrength, r.NormalBlur,
            false, NormalConvention.OpenGl, wrap), scale);
        var rough = MapGenerator.Roughness(src, new RoughnessOptions(r.RoughnessBase, r.RoughnessRange, 0.5, 2, false, wrap), scale);
        var ao = MapGenerator.AmbientOcclusion(height, new AmbientOcclusionOptions(HeightSource.HeightMap, r.OcclusionRadius,
            r.OcclusionStrength, 8, 16, wrap), scale);
        var metal = MapGenerator.Metallic(src, new MetallicOptions(r.Metallic));
        progress?.Report(0.8);
        // ORM: R occlusion, G roughness, B metallic (Godot's ORMMaterial3D)
        var orm = new byte[src.Width * src.Height * 4];
        for (int i = 0; i < orm.Length; i += 4)
        {
            orm[i] = ao.Rgba[i];
            orm[i + 1] = rough.Rgba[i];
            orm[i + 2] = metal.Rgba[i];
            orm[i + 3] = 255;
        }
        Image? emission = null;
        if (r.EmissionThreshold <= 1f)
        {
            var mask = MapGenerator.EmissionMask(src, new EmissionMaskOptions(r.EmissionThreshold));
            var e = new byte[src.Rgba.Length];
            bool any = false;
            for (int i = 0; i < e.Length; i += 4)
            {
                int m = mask.Rgba[i];
                any |= m > 0;
                e[i] = (byte)(src.Rgba[i] * m / 255);
                e[i + 1] = (byte)(src.Rgba[i + 1] * m / 255);
                e[i + 2] = (byte)(src.Rgba[i + 2] * m / 255);
                e[i + 3] = 255;
            }
            if (any)
                emission = ToImage(new RgbaImage(src.Width, src.Height, e));
        }
        progress?.Report(1);
        return new PbrImages(ToImage(albedo), ToImage(normal), ToImage(new RgbaImage(src.Width, src.Height, orm)),
            r.HeightScale > 0 ? ToImage(height) : null, emission);
    }

    /// <summary>
    /// The material over the maps (textures already made from <see cref="PbrImages"/>): albedo,
    /// normal, ORM, height (parallax by the recipe's scale), emission.
    /// </summary>
    public static OrmMaterial3D Material(Texture2D albedo, Texture2D normal, Texture2D orm, Texture2D? height, Texture2D? emission, PbrRecipe r)
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
            m.EmissionEnergyMultiplier = 1f;
        }
        return m;
    }

    /// <summary>The maps as textures in memory (mipmapped), for a material saved with them inside it (outside the project, the checks).</summary>
    public static (Texture2D Albedo, Texture2D Normal, Texture2D Orm, Texture2D? Height, Texture2D? Emission) InMemory(PbrImages p)
    {
        static Texture2D T(Image i)
        {
            var c = (Image)i.Duplicate();
            c.GenerateMipmaps();
            return ImageTexture.CreateFromImage(c);
        }
        return (T(p.Albedo), T(p.Normal), T(p.Orm), p.Height is { } h ? T(h) : null, p.Emission is { } e ? T(e) : null);
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
    }
}
