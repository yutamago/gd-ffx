using System;
using GdFfx.Remaster.Materialize;
using GdFfx.Remaster.Texturing;
using Vec3 = System.Numerics.Vector3;

namespace GdFfx.Remaster.Wizard;

/// <summary>
/// A texture's colour and statistics (docs/remaster-plan.md, "R4 in detail"): what the wizard's
/// classifier reads from its pixels, measured on a copy at most <see cref="Side"/> pixels on its
/// longer side. Colours are OKLab (lightness 0..1, chroma, hue in degrees: red about 30, yellow
/// 100, green 140, cyan 195, blue 265); the statistics are over the opaque pixels (alpha at least
/// 128) unless said otherwise.
/// </summary>
public sealed record TextureFeatures
{
    /// <summary>The size the statistics are measured at (the longer side).</summary>
    public const int Side = 128;

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Pixels with alpha under 128 (cut out), and with alpha between 16 and 240 (soft: glows, decals, painted layers).</summary>
    public float AlphaCut { get; init; }
    public float AlphaSoft { get; init; }

    /// <summary>Mean OKLab lightness and its spread (standard deviation).</summary>
    public float L { get; init; }
    public float LSd { get; init; }

    /// <summary>Mean OKLab chroma; the chroma-weighted mean hue (degrees).</summary>
    public float C { get; init; }
    public float Hue { get; init; }

    /// <summary>Shares of the opaque pixels: grey (chroma under 0.03), warm (hue 25..105: browns, tans, oranges, yellows), green (105..175), blue (195..300: blues, cyans, violets).</summary>
    public float Grey { get; init; }
    public float Warm { get; init; }
    public float Green { get; init; }
    public float Blue { get; init; }

    /// <summary>Very bright (L above 0.92) and very dark (under 0.12) shares.</summary>
    public float Bright { get; init; }
    public float Dark { get; init; }

    /// <summary>Local contrast: the mean |L - its 3x3 mean| (fine detail).</summary>
    public float Detail { get; init; }

    /// <summary>Coarse contrast: the spread of L blurred by 8 px (large shapes: rock faces, planks, bricks).</summary>
    public float Coarse { get; init; }

    /// <summary>Edge density: the share of pixels whose L gradient (Sobel) passes 0.25.</summary>
    public float Edges { get; init; }

    /// <summary>Flat share: pixels whose L gradient is under 0.02 (painted, smooth, UI-like).</summary>
    public float Flat { get; init; }

    /// <summary>Directionality: |Σgx² - Σgy²| / Σ(gx² + gy²) (0 isotropic; wood grain and planks, stripes near 1).</summary>
    public float Aniso { get; init; }

    /// <summary>How well the texture tiles: the mean L difference across its wrap seams over the mean between neighbours (about 1 tiles; much more: an image, not a tile).</summary>
    public float Seam { get; init; }

    /// <summary>Distinct colours at 5 bits a channel over the pixels (UI art and flat paint have few).</summary>
    public int Colours { get; init; }

    /// <summary>The features of <paramref name="src"/>.</summary>
    public static TextureFeatures Of(RgbaImage src)
    {
        var img = src.Width > Side || src.Height > Side ? Shrink(src, Side) : src;
        int w = img.Width, h = img.Height, n = w * h;
        var lum = new float[n];
        var opaque = new bool[n];
        int cut = 0, soft = 0, op = 0, grey = 0, warm = 0, green = 0, blue = 0, bright = 0, dark = 0;
        double sl = 0, sl2 = 0, sc = 0, ha = 0, hb = 0;
        var colours = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            int k = i * 4;
            byte a = img.Rgba[k + 3];
            if (a < 128) cut++;
            if (a > 16 && a < 240) soft++;
            var lab = Kernels.OkLab(new Vec3(Kernels.Lin(img.Rgba[k] / 255f), Kernels.Lin(img.Rgba[k + 1] / 255f), Kernels.Lin(img.Rgba[k + 2] / 255f)));
            lum[i] = lab.X;
            colours.Add((img.Rgba[k] >> 3) << 10 | (img.Rgba[k + 1] >> 3) << 5 | img.Rgba[k + 2] >> 3);
            if (a < 128)
                continue;
            opaque[i] = true;
            op++;
            float c = MathF.Sqrt(lab.Y * lab.Y + lab.Z * lab.Z);
            sl += lab.X;
            sl2 += lab.X * lab.X;
            sc += c;
            ha += lab.Y;
            hb += lab.Z;
            if (lab.X > 0.92f) bright++;
            if (lab.X < 0.12f) dark++;
            if (c < 0.03f)
            {
                grey++;
                continue;
            }
            float hue = Deg(lab.Y, lab.Z);
            if (hue >= 25 && hue < 105) warm++;
            else if (hue >= 105 && hue < 175) green++;
            else if (hue >= 195 && hue < 300) blue++;
        }
        float on = Math.Max(op, 1);
        float mean = (float)(sl / on);
        // the local statistics over every pixel (cut-out ones count as they are drawn: by their colour)
        var blur3 = Box(lum, w, h, 1);
        var blur8 = Box(Box(lum, w, h, 4), w, h, 4);
        double detail = 0, gx2 = 0, gy2 = 0, cm = 0, cm2 = 0;
        int edges = 0, flat = 0, m = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!opaque[i] && op > 0)
                    continue;
                m++;
                detail += MathF.Abs(lum[i] - blur3[i]);
                float L(int dx, int dy) => lum[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)];
                float gx = (L(1, -1) + 2 * L(1, 0) + L(1, 1) - L(-1, -1) - 2 * L(-1, 0) - L(-1, 1)) * 0.25f;
                float gy = (L(-1, 1) + 2 * L(0, 1) + L(1, 1) - L(-1, -1) - 2 * L(0, -1) - L(1, -1)) * 0.25f;
                gx2 += gx * gx;
                gy2 += gy * gy;
                float g = MathF.Sqrt(gx * gx + gy * gy);
                if (g > 0.0625f) edges++;
                if (g < 0.005f) flat++;
                cm += blur8[i];
                cm2 += blur8[i] * blur8[i];
            }
        float mm = Math.Max(m, 1);
        double cmean = cm / mm;
        // the seams: across the wrap against between neighbours
        double seam = 0, near = 0;
        for (int y = 0; y < h; y++)
        {
            seam += MathF.Abs(lum[y * w] - lum[y * w + w - 1]);
            near += MathF.Abs(lum[y * w + w / 2] - lum[y * w + w / 2 - 1]);
        }
        for (int x = 0; x < w; x++)
        {
            seam += MathF.Abs(lum[x] - lum[(h - 1) * w + x]);
            near += MathF.Abs(lum[h / 2 * w + x] - lum[(h / 2 - 1) * w + x]);
        }
        return new TextureFeatures
        {
            Width = src.Width,
            Height = src.Height,
            AlphaCut = cut / (float)n,
            AlphaSoft = soft / (float)n,
            L = mean,
            LSd = MathF.Sqrt(MathF.Max(0, (float)(sl2 / on) - mean * mean)),
            C = (float)(sc / on),
            Hue = Deg((float)ha, (float)hb),
            Grey = grey / on,
            Warm = warm / on,
            Green = green / on,
            Blue = blue / on,
            Bright = bright / on,
            Dark = dark / on,
            Detail = (float)(detail / mm),
            Coarse = MathF.Sqrt(MathF.Max(0, (float)(cm2 / mm - cmean * cmean))),
            Edges = edges / mm,
            Flat = flat / mm,
            Aniso = gx2 + gy2 > 1e-9 ? (float)(Math.Abs(gx2 - gy2) / (gx2 + gy2)) : 0,
            Seam = near > 1e-6 ? (float)(seam / near) : 1,
            Colours = colours.Count,
        };
    }

    private static float Deg(float a, float b)
    {
        float d = MathF.Atan2(b, a) * 180f / MathF.PI;
        return d < 0 ? d + 360 : d;
    }

    /// <summary>A box blur of radius <paramref name="r"/> (separable, clamped at the edges).</summary>
    private static float[] Box(float[] v, int w, int h, int r)
    {
        var t = new float[v.Length];
        var o = new float[v.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int d = -r; d <= r; d++)
                    s += v[y * w + Math.Clamp(x + d, 0, w - 1)];
                t[y * w + x] = s / (2 * r + 1);
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int d = -r; d <= r; d++)
                    s += t[Math.Clamp(y + d, 0, h - 1) * w + x];
                o[y * w + x] = s / (2 * r + 1);
            }
        return o;
    }

    /// <summary><paramref name="src"/> averaged down so its longer side is <paramref name="side"/> (box filter).</summary>
    private static RgbaImage Shrink(RgbaImage src, int side)
    {
        float s = (float)side / Math.Max(src.Width, src.Height);
        int w = Math.Max(1, (int)MathF.Round(src.Width * s)), h = Math.Max(1, (int)MathF.Round(src.Height * s));
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int x0 = x * src.Width / w, x1 = Math.Max(x0 + 1, (x + 1) * src.Width / w);
                int y0 = y * src.Height / h, y1 = Math.Max(y0 + 1, (y + 1) * src.Height / h);
                int r = 0, g = 0, b = 0, a = 0, k = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int i = (yy * src.Width + xx) * 4;
                        r += src.Rgba[i];
                        g += src.Rgba[i + 1];
                        b += src.Rgba[i + 2];
                        a += src.Rgba[i + 3];
                        k++;
                    }
                int o = (y * w + x) * 4;
                (px[o], px[o + 1], px[o + 2], px[o + 3]) = ((byte)(r / k), (byte)(g / k), (byte)(b / k), (byte)(a / k));
            }
        return new RgbaImage(w, h, px);
    }

    public override string ToString() =>
        $"{Width}x{Height} L {L:0.00}±{LSd:0.00} C {C:0.000} hue {Hue:0} grey {Grey:0.00} warm {Warm:0.00} green {Green:0.00} blue {Blue:0.00} "
        + $"detail {Detail:0.000} coarse {Coarse:0.000} edges {Edges:0.00} flat {Flat:0.00} aniso {Aniso:0.00} seam {Seam:0.0} "
        + $"cut {AlphaCut:0.00} soft {AlphaSoft:0.00} bright {Bright:0.00} dark {Dark:0.00} colours {Colours}";
}
