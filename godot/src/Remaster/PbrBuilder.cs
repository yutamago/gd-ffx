using System;
using System.Collections.Generic;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.Remaster;

/// <summary>A texture's physically based maps (<see cref="Materialize.MaterialBaker.Images"/>): albedo (sRGB), normal (OpenGL, green up), ORM (occlusion, roughness, metallic), height, emission (null: none).</summary>
public sealed record PbrImages(Image Albedo, Image Normal, Image Orm, Image? Height, Image? Emission);

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
            m.EmissionEnergyMultiplier = r.EmissionEnergy;
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
