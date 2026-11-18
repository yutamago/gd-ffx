using System;
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster.Texturing;
using Godot;
using Vec3 = System.Numerics.Vector3;

namespace GdFfx.Remaster.Materialize;

/// <summary>
/// The starter profiles measured on the game's textures (docs/remaster-materials.md, "Profiles";
/// <c>--selftest-materialize --materialize-profiles[=DIR]</c>): each starter profile baked on the
/// CPU on five textures of its kind (Besaid, Luca, Macalania, Bevelle, Baaj, Mi'ihen, Zanarkand, found through the
/// where-used index), measured (the colour check: the albedo's OKLab lightness and chroma against
/// the classic texture's; the roughness's mean and spread, the metallic's and occlusion's means,
/// the height's spread, the normals' mean tilt), held to its kind's ranges
/// (<see cref="Kinds"/>), and with DIR a contact sheet per texture (classic, albedo, height,
/// normal, roughness, occlusion, metallic, and both lit by one light: the classic flat, the
/// material with its maps), never in git. <c>--materialize-atlas=DIR --materialize-ids=FILE</c>:
/// thumbnails of the textures listed in FILE (one ID a line), to choose textures by eye.
/// </summary>
public static class ProfileSheet
{
    /// <summary>
    /// A kind's ranges, over its textures' means: the roughness's mean and its least spread (the
    /// 90th less the 10th percentile), the metallic's mean, the normals' mean tilt (1 - z: 0.015 is
    /// about 10 degrees, 0.06 about 20).
    /// </summary>
    public sealed record Kind(string Profile, float RoughMin, float RoughMax, float SpreadMin, float MetalMin, float MetalMax, float TiltMin, float TiltMax, string[] Textures);

    private const string Pre = "x:/ffx_data/gamedata/ps3data/map/";

    private static string Id(string level, string name) => $"{Pre}{level[..4]}/{level}/tex/d3d11/{name}.dds.phyre";

    /// <summary>Each starter profile, its plausible ranges, the textures it was tuned on.</summary>
    public static readonly Kind[] Kinds =
    [
        new("rock", 0.70f, 0.95f, 0.08f, 0, 0.05f, 0.025f, 0.08f, [Id("lchb00", "lchb00_rockwall_06"), Id("lchb00", "lchb00_rockwall_01"), Id("bjyt04", "bjyt04_rockwall_01"), Id("znkd06", "znkd06_stone_01"), Id("mcfr00", "15872_19_000_000_8_256_256")]),
        new("stone-and-concrete", 0.75f, 0.97f, 0.06f, 0, 0.05f, 0.012f, 0.05f, [Id("lchb00", "lchb00_concretewall_01"), Id("znkd08", "znkd08_concreteground_01"), Id("lchb00", "lchb00_brickground_04"), Id("bjyt04", "bjyt04_brickwall_01"), Id("bvyt00", "12928_19_000_000_8_256_128")]),
        new("sand-and-dirt", 0.85f, 1.0f, 0.04f, 0, 0.05f, 0.008f, 0.035f, [Id("bsil00", "12800_20_128_000_4_128_128"), Id("bsvr00", "15360_19_000_000_8_128_64"), Id("znkd06", "znkd06_ground_01"), Id("znkd06", "znkd06_ground_03"), Id("lchb00", "lchb00_rockground_01")]),
        new("grass-and-foliage", 0.50f, 0.85f, 0.06f, 0, 0.05f, 0.006f, 0.035f, [Id("bsvr00", "15616_20_192_096_4_64_32"), Id("bsil00", "12288_20_128_128_4_128_128"), Id("lchb00", "lchb00_leaves_01"), Id("bjyt04", "bjyt04_leaves_01"), Id("mihn02", "mihn00_tree_01")]),
        new("wood", 0.55f, 0.85f, 0.08f, 0, 0.05f, 0.008f, 0.04f, [Id("bsil00", "12928_20_128_000_4_128_256"), Id("bsvr00", "plank01"), Id("mcfr00", "12288_19_000_000_8_256_256"), Id("lchb00", "lchb00_woodwall_01a"), Id("lchb00", "lchb00_woodboard_01")]),
        new("metal", 0.25f, 0.65f, 0.07f, 0.5f, 1.0f, 0.004f, 0.035f, [Id("znkd08", "znkd08_metal_01"), Id("znkd08", "znkd08_metal_02"), Id("znkd08", "znkd08_metal_03"), Id("znkd08", "znkd08_streetlamp_01"), Id("bvyt00", "12416_20_000_128_4_256_128")]),
        new("fabric", 0.80f, 1.0f, 0.03f, 0, 0.05f, 0.005f, 0.03f, [Id("bsil00", "bsil01_fabric_02"), Id("bsil00", "bsil01_tent_01"), Id("bvyt00", "15872_20_064_000_4_64_256"), Id("lchb00", "lchb00_fabric_01"), Id("bjyt04", "bjyt04_fabric_01")]),
        new("plaster-and-painted-walls", 0.75f, 0.95f, 0.04f, 0, 0.05f, 0.003f, 0.02f, [Id("bvyt00", "12416_20_000_064_4_64_64"), Id("lchb00", "lchb00_concreteceiling_01"), Id("znkd06", "znkd06_wall_01"), Id("znkd06", "znkd06_wall_02"), Id("bjyt04", "bjyt04_ceiling_01")]),
    ];

    /// <summary>The colour check's limits: the mean lightness within 0.03 of the classic's, the chroma within 20 %.</summary>
    public const float LightnessLimit = 0.03f, ChromaLimit = 0.2f;

    /// <summary>One texture's numbers.</summary>
    public sealed record Measure(string Texture, int W, int H, float L0, float L1, float C0, float C1, float Rough, float RoughP10, float RoughP90,
        float Metal, float Ao, float HeightSd, float Tilt, double Ms, string Added = "");

    /// <summary>Bakes every starter profile on its textures (main thread: the classic pixels); the failures (empty: all plausible), and the numbers.</summary>
    public static (List<string> Fails, List<string> Lines) Run(string? dir, int maxSide)
    {
        var fails = new List<string>();
        var lines = new List<string>();
        if (dir != null)
            DirAccess.MakeDirRecursiveAbsolute(dir);
        foreach (var k in Kinds)
        {
            if (MaterialProfiles.Load(k.Profile) is not { } p)
            {
                fails.Add($"no starter profile {k.Profile}");
                continue;
            }
            var ms = new List<Measure>();
            foreach (var id in k.Textures)
            {
                if (RemasterTextures.ClassicImage(id) is not { } img)
                {
                    fails.Add($"{k.Profile}: no pixels for {RemasterTextures.ShortName(id)}");
                    continue;
                }
                var src = PbrBuilder.From(img);
                var r = MaterialProfiles.Effective(p, null);
                r.MaxSide = Math.Min(r.MaxSide, maxSide);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var baked = MaterialBaker.Cpu(id, src, r);
                var m = Measured(RemasterTextures.ShortName(id), src, baked, clock.Elapsed.TotalMilliseconds);
                ms.Add(m);
                var line = $"{k.Profile} {m.Texture} {m.W}x{m.H}: L {m.L0:0.000}->{m.L1:0.000} C {m.C0:0.000}->{m.C1:0.000} rough {m.Rough:0.00} ({m.RoughP10:0.00}..{m.RoughP90:0.00}) "
                    + $"metal {m.Metal:0.00} ao {m.Ao:0.00} height sd {m.HeightSd:0.000} tilt {m.Tilt:0.000}{(m.Added.Length > 0 ? $" added: {m.Added}" : "")} ({m.Ms:0} ms)";
                // the added maps (R2b phase 4): each made, its mean in its range
                foreach (var x in MaterialPipeline.Extras)
                    if (MaterialPipeline.On(r, x) && !baked.Maps.ContainsKey(x))
                        fails.Add($"{k.Profile} on {m.Texture}: no {x} map");
                if (AddedFails(baked) is { } af)
                    fails.Add($"{k.Profile} on {m.Texture}: {af}");
                lines.Add(line);
                GD.Print($"ffx materialize: profile {line}");
                if (MathF.Abs(m.L1 - m.L0) > LightnessLimit)
                    fails.Add($"{k.Profile} on {m.Texture}: lightness {m.L0:0.000} -> {m.L1:0.000}");
                if (m.C0 > 0.01f && MathF.Abs(m.C1 / m.C0 - 1) > ChromaLimit)
                    fails.Add($"{k.Profile} on {m.Texture}: chroma {m.C0:0.000} -> {m.C1:0.000}");
                if (dir != null)
                    Sheet(src, baked, r).SavePng($"{dir}/{k.Profile}--{m.Texture}.png");
            }
            if (ms.Count == 0)
                continue;
            float rough = ms.Average(x => x.Rough), metal = ms.Average(x => x.Metal), spread = ms.Average(x => x.RoughP90 - x.RoughP10), tilt = ms.Average(x => x.Tilt);
            if (rough < k.RoughMin || rough > k.RoughMax)
                fails.Add($"{k.Profile}: mean roughness {rough:0.00}, not in {k.RoughMin:0.00}..{k.RoughMax:0.00}");
            if (spread < k.SpreadMin)
                fails.Add($"{k.Profile}: the roughness's spread {spread:0.000}, under {k.SpreadMin:0.00} (flat)");
            if (metal < k.MetalMin || metal > k.MetalMax)
                fails.Add($"{k.Profile}: mean metallic {metal:0.00}, not in {k.MetalMin:0.00}..{k.MetalMax:0.00}");
            if (tilt < k.TiltMin || tilt > k.TiltMax)
                fails.Add($"{k.Profile}: the normals' mean tilt {tilt:0.000}, not in {k.TiltMin:0.000}..{k.TiltMax:0.000}");
            lines.Add($"{k.Profile}: roughness {rough:0.00} (in {k.RoughMin:0.00}..{k.RoughMax:0.00}), spread {spread:0.00}, metallic {metal:0.00}, tilt {tilt:0.000} "
                + $"(in {k.TiltMin:0.000}..{k.TiltMax:0.000}) on {ms.Count} textures");
        }
        return (fails, lines);
    }

    private static Vec3 Lin(byte[] px, int i) =>
        new(Kernels.Lin(px[i] / 255f), Kernels.Lin(px[i + 1] / 255f), Kernels.Lin(px[i + 2] / 255f));

    private static (float L, float C) MeanLab(RgbaImage img)
    {
        double l = 0, c = 0;
        int n = 0;
        for (int i = 0; i < img.Rgba.Length; i += 4)
        {
            if (img.Rgba[i + 3] < 128)
                continue;
            var lab = Kernels.OkLab(Lin(img.Rgba, i));
            l += lab.X;
            c += MathF.Sqrt(lab.Y * lab.Y + lab.Z * lab.Z);
            n++;
        }
        return n == 0 ? (0, 0) : ((float)(l / n), (float)(c / n));
    }

    private static Measure Measured(string name, RgbaImage src, BakedMaps b, double ms)
    {
        var (l0, c0) = MeanLab(src);
        var (l1, c1) = MeanLab(b.Maps[MaterialStep.Albedo]);
        var orm = b.Orm.Rgba;
        var rough = new List<float>();
        double metal = 0, ao = 0;
        for (int i = 0; i < orm.Length; i += 4)
        {
            ao += orm[i] / 255.0;
            rough.Add(orm[i + 1] / 255f);
            metal += orm[i + 2] / 255.0;
        }
        rough.Sort();
        int n = rough.Count;
        var hd = b.Height.GetData();
        var hf = new float[hd.Length / 4];
        Buffer.BlockCopy(hd, 0, hf, 0, hd.Length);
        double hm = hf.Average(), hsd = Math.Sqrt(hf.Average(x => (x - hm) * (x - hm)));
        var nm = b.Maps[MaterialStep.Normal].Rgba;
        double tilt = 0;
        for (int i = 0; i < nm.Length; i += 4)
            tilt += 1 - (nm[i + 2] / 255.0 * 2 - 1);
        return new Measure(name, b.Maps[MaterialStep.Albedo].Width, b.Maps[MaterialStep.Albedo].Height, l0, l1, c0, c1,
            (float)rough.Average(), rough[n / 10], rough[n * 9 / 10], (float)(metal / n), (float)(ao / n), (float)hsd, (float)(tilt / (nm.Length / 4)), ms, Added(b));
    }

    /// <summary>A map's channel's mean (0-1).</summary>
    private static float Mean(RgbaImage img, int c)
    {
        double s = 0;
        for (int i = c; i < img.Rgba.Length; i += 4)
            s += img.Rgba[i] / 255.0;
        return (float)(s / (img.Rgba.Length / 4));
    }

    /// <summary>The added maps' numbers: the bent normal's tilt (1 - z), the masks' means, the backlight's and transmittance's.</summary>
    private static string Added(BakedMaps b)
    {
        var parts = new List<string>();
        foreach (var (s, img) in b.Maps)
        {
            if (MaterialPipeline.IsCore(s))
                continue;
            parts.Add(s switch
            {
                MaterialStep.BentNormal => $"bent tilt {1 - (Mean(img, 2) * 2 - 1):0.000}",
                MaterialStep.Anisotropy => $"flow strength {Mean(img, 3):0.00}",
                MaterialStep.Transmittance => $"transmittance {Mean(img, 3):0.00}",
                MaterialStep.Backlight => $"backlight {(Mean(img, 0) + Mean(img, 1) + Mean(img, 2)) / 3:0.00}",
                _ => $"{s.ToString().ToLowerInvariant()} {Mean(img, 0):0.00}",
            });
        }
        return string.Join(", ", parts);
    }

    /// <summary>An added map out of its range (null: all plausible): the bent normal tilted less than 60 degrees on average, a mask's mean not all off, the backlight lit.</summary>
    private static string? AddedFails(BakedMaps b)
    {
        foreach (var (s, img) in b.Maps)
        {
            if (MaterialPipeline.IsCore(s))
                continue;
            float v = s switch
            {
                MaterialStep.BentNormal => 1 - (Mean(img, 2) * 2 - 1),
                MaterialStep.Anisotropy or MaterialStep.Transmittance => Mean(img, 3),
                MaterialStep.Backlight => (Mean(img, 0) + Mean(img, 1) + Mean(img, 2)) / 3,
                _ => Mean(img, 0),
            };
            if (s == MaterialStep.BentNormal ? v > 0.5f : v < 0.01f)
                return $"the {s} map's mean {v:0.000} is implausible";
        }
        return null;
    }

    private const int Cell = 192;

    /// <summary>One texture's contact sheet: classic, albedo, height, normal, roughness, occlusion, metallic, classic lit, material lit.</summary>
    private static Image Sheet(RgbaImage src, BakedMaps b, PbrRecipe r)
    {
        var cells = new List<RgbaImage>
        {
            src, b.Maps[MaterialStep.Albedo], b.Maps[MaterialStep.Height], b.Maps[MaterialStep.Normal],
            Channel(b.Orm, 1), Channel(b.Orm, 0), Channel(b.Orm, 2), Lit(src, null), Lit(b.Maps[MaterialStep.Albedo], b),
        };
        var sheet = Image.CreateEmpty(Cell * cells.Count + 4 * (cells.Count - 1), Cell, false, Image.Format.Rgba8);
        sheet.Fill(new Color(0.1f, 0.1f, 0.1f));
        for (int i = 0; i < cells.Count; i++)
        {
            var img = PbrBuilder.ToImage(cells[i]);
            img.Resize(Cell, Cell, Image.Interpolation.Bilinear);
            sheet.BlitRect(img, new Rect2I(0, 0, Cell, Cell), new Vector2I(i * (Cell + 4), 0));
        }
        return sheet;
    }

    private static RgbaImage Channel(RgbaImage img, int c)
    {
        var px = new byte[img.Rgba.Length];
        for (int i = 0; i < px.Length; i += 4)
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (img.Rgba[i + c], img.Rgba[i + c], img.Rgba[i + c], 255);
        return new RgbaImage(img.Width, img.Height, px);
    }

    /// <summary>
    /// <paramref name="albedo"/> lit by one light from the upper left (and a sky's ambient), seen
    /// from straight above: without maps a flat matte surface (the classic look's stand-in), with
    /// them the normal, occlusion, roughness and metallic of the bake (a GGX highlight).
    /// </summary>
    private static RgbaImage Lit(RgbaImage albedo, BakedMaps? b)
    {
        // a low light, so the relief shows
        var light = Vec3.Normalize(new Vec3(-0.6f, 0.55f, 0.45f));
        var view = new Vec3(0, 0, 1);
        var half = Vec3.Normalize(light + view);
        int w = albedo.Width, h = albedo.Height;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                var a = Lin(albedo.Rgba, i);
                Vec3 n = view;
                float rough = 0.9f, metal = 0, ao = 1;
                if (b != null)
                {
                    var nm = b.Maps[MaterialStep.Normal];
                    int j = ((y * nm.Height / h) * nm.Width + x * nm.Width / w) * 4;
                    // OpenGL normal map: green up, the image's rows go down
                    n = Vec3.Normalize(new Vec3(nm.Rgba[j] / 127.5f - 1, nm.Rgba[j + 1] / 127.5f - 1, nm.Rgba[j + 2] / 127.5f - 1));
                    var orm = b.Orm;
                    int k = ((y * orm.Height / h) * orm.Width + x * orm.Width / w) * 4;
                    (ao, rough, metal) = (orm.Rgba[k] / 255f, orm.Rgba[k + 1] / 255f, orm.Rgba[k + 2] / 255f);
                }
                float ndl = MathF.Max(0, Vec3.Dot(n, light)), ndh = MathF.Max(0, Vec3.Dot(n, half));
                float alpha = MathF.Max(0.02f, rough * rough), a2 = alpha * alpha;
                float d = a2 / (MathF.PI * MathF.Pow(ndh * ndh * (a2 - 1) + 1, 2));
                var f0 = Vec3.Lerp(new Vec3(0.04f), a, metal);
                var spec = f0 * d * 0.25f * ndl;
                var diffuse = a * (1 - metal) * (ndl * 1.1f + 0.25f * ao);
                var c = diffuse + spec + a * metal * 0.15f * ao;
                px[i] = (byte)Math.Clamp(Kernels.Enc(c.X) * 255, 0, 255);
                px[i + 1] = (byte)Math.Clamp(Kernels.Enc(c.Y) * 255, 0, 255);
                px[i + 2] = (byte)Math.Clamp(Kernels.Enc(c.Z) * 255, 0, 255);
                px[i + 3] = 255;
            }
        return new RgbaImage(w, h, px);
    }

    /// <summary>Thumbnails (128 px, 8 a row) of the textures listed in <paramref name="idsFile"/>; prints each cell's texture.</summary>
    public static string Atlas(string dir, string idsFile)
    {
        DirAccess.MakeDirRecursiveAbsolute(dir);
        var ids = System.IO.File.ReadAllLines(idsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        const int t = 128, cols = 8;
        int pages = 0;
        for (int start = 0; start < ids.Count; start += 48, pages++)
        {
            var part = ids.Skip(start).Take(48).ToList();
            int rows = (part.Count + cols - 1) / cols;
            var sheet = Image.CreateEmpty(cols * (t + 4), rows * (t + 4), false, Image.Format.Rgba8);
            sheet.Fill(new Color(0.2f, 0.0f, 0.2f));
            for (int i = 0; i < part.Count; i++)
            {
                if (RemasterTextures.ClassicImage(part[i]) is not { } img)
                    continue;
                var c = PbrBuilder.ToImage(PbrBuilder.From(img));
                c.Resize(t, t, Image.Interpolation.Bilinear);
                sheet.BlitRect(c, new Rect2I(0, 0, t, t), new Vector2I(i % cols * (t + 4), i / cols * (t + 4)));
                GD.Print($"ffx materialize: atlas {pages} r{i / cols} c{i % cols}: {part[i]} ({img.GetWidth()}x{img.GetHeight()})");
            }
            sheet.SavePng($"{dir}/atlas-{pages}.png");
        }
        return $"{ids.Count} textures in {pages} atlas page(s) in {dir}";
    }
}
