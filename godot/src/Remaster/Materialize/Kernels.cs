using System;
using System.Numerics;

namespace GdFfx.Remaster.Materialize;

/// <summary>
/// The math the kernels share, in C# (the CPU's) with the same formulas as the GLSL in
/// <see cref="GpuKernels"/>: the sRGB transfer, perceptual luminance, HSL, the colour mask,
/// OKLab, the occlusion's hash, the threshold ramp, the S-curve gain.
/// </summary>
public static class Kernels
{
    public static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    public static float Enc(float l)
    {
        if (l <= 0) return 0;
        if (l >= 1) return 1;
        return l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1 / 2.4f) - 0.055f;
    }

    public static Vector3 Lin(Vector3 c) => new(Lin(c.X), Lin(c.Y), Lin(c.Z));

    /// <summary>Linear luminance (Rec. 709) of linear RGB.</summary>
    public static float LumLin(Vector3 l) => 0.2126f * l.X + 0.7152f * l.Y + 0.0722f * l.Z;

    /// <summary>Perceptual luminance of sRGB-encoded colour: the encoded linear luminance.</summary>
    public static float Lum(Vector3 srgb) => Enc(LumLin(Lin(srgb)));

    /// <summary>Hue (0-1), saturation, lightness of an sRGB-encoded colour.</summary>
    public static Vector3 Hsl(Vector3 c)
    {
        float mx = MathF.Max(c.X, MathF.Max(c.Y, c.Z)), mn = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float l = (mx + mn) * 0.5f, d = mx - mn;
        float den = 1 - MathF.Abs(2 * l - 1);
        float s = d <= 1e-6f || den <= 1e-6f ? 0 : MathF.Min(1, d / den);
        float h = 0;
        if (d > 1e-6f)
        {
            if (mx == c.X) h = (c.Y - c.Z) / d;
            else if (mx == c.Y) h = 2 + (c.Z - c.X) / d;
            else h = 4 + (c.X - c.Y) / d;
            h /= 6;
            if (h < 0) h += 1;
        }
        return new Vector3(h, s, l);
    }

    public static float SmoothStep(float lo, float hi, float x)
    {
        if (hi <= lo) return x >= lo ? 1 : 0;
        float t = Math.Clamp((x - lo) / (hi - lo), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// How much <paramref name="c"/> is the colour <paramref name="sample"/> (both sRGB), 0-1:
    /// the hue's, saturation's and lightness's closeness weighted (the hue counts only as far as
    /// both are saturated: grey has no hue), through a soft threshold.
    /// </summary>
    public static float ColourMask(Vector3 c, Vector3 sample, float wh, float ws, float wl, float lo, float hi)
    {
        var a = Hsl(c);
        var b = Hsl(sample);
        float dh = MathF.Abs(a.X - b.X);
        dh = MathF.Min(dh, 1 - dh);
        float hs = 1 - 2 * dh, ss = 1 - MathF.Abs(a.Y - b.Y), ls = 1 - MathF.Abs(a.Z - b.Z);
        float whe = wh * MathF.Min(1, 4 * MathF.Min(a.Y, b.Y));
        float sim = (whe * hs + ws * ss + wl * ls) / MathF.Max(1e-4f, whe + ws + wl);
        return SmoothStep(lo, hi, sim);
    }

    /// <summary>R2's threshold ramp: a smoothstep across the softness around the threshold.</summary>
    public static float Ramp(float v, float threshold, float softness)
    {
        float lo = threshold - softness / 2, hi = threshold + softness / 2;
        float t = hi <= lo ? (v >= threshold ? 1 : 0) : Math.Clamp((v - lo) / (hi - lo), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>An S-curve (Schlick's gain) around mid grey: <paramref name="gain"/> 0 none, 1 steep, -1 flat.</summary>
    public static float Gain(float x, float gain)
    {
        if (gain == 0) return x;
        float k = MathF.Pow(4, gain);
        return x < 0.5f ? 0.5f * MathF.Pow(2 * x, k) : 1 - 0.5f * MathF.Pow(2 - 2 * x, k);
    }

    private static float Cbrt(float x) => MathF.Sign(x) * MathF.Pow(MathF.Abs(x), 1f / 3);

    /// <summary>OKLab (Björn Ottosson's) of linear sRGB.</summary>
    public static Vector3 OkLab(Vector3 c)
    {
        float l = 0.4122214708f * c.X + 0.5363325363f * c.Y + 0.0514459929f * c.Z;
        float m = 0.2119034982f * c.X + 0.6806995451f * c.Y + 0.1073969566f * c.Z;
        float s = 0.0883024619f * c.X + 0.2817188376f * c.Y + 0.6299787005f * c.Z;
        l = Cbrt(l); m = Cbrt(m); s = Cbrt(s);
        return new Vector3(0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    public static Vector3 FromOkLab(Vector3 c)
    {
        float l = c.X + 0.3963377774f * c.Y + 0.2158037573f * c.Z;
        float m = c.X - 0.1055613458f * c.Y - 0.0638541728f * c.Z;
        float s = c.X - 0.0894841775f * c.Y - 1.2914855480f * c.Z;
        l = l * l * l; m = m * m * m; s = s * s * s;
        return new Vector3(4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
            -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
            -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    /// <summary>An integer hash (the same in GLSL): the occlusion's per-pixel jitter.</summary>
    public static uint Hash(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 2146121005u;
            x ^= x >> 15;
            x *= 2221713035u;
            x ^= x >> 16;
            return x;
        }
    }

    /// <summary>A number in [0, 1) per pixel and salt.</summary>
    public static float Rnd(int x, int y, uint salt) =>
        (Hash(unchecked((uint)x + Hash((uint)y + Hash(salt)))) >> 8) / 16777216f;

    public static double Lanczos(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-6) return 1;
        if (x >= 3) return 0;
        double px = Math.PI * x;
        return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
    }
}
