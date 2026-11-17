// Ported from ffx-remix (MIT; the same author as gd-ffx): shared/FfxRemix.Core/Texturing/Generators/MapGenerator.cs, Generators/Albedo.cs, Generators/Planes.cs.
// The algorithms as they are there; namespaces and small adaptions for gd-ffx.
using System;
using System.Threading.Tasks;
using System.Threading;

namespace GdFfx.Remaster.Texturing;

/// <summary>The derivative kernel of <see cref="MapGenerator.Normal"/>.</summary>
public enum GradientFilter {
    /// <summary>3x3 Sobel (1 2 1).</summary>
    Sobel,
    /// <summary>3x3 Scharr (3 10 3): rotation-invariant, crisper on diagonals.</summary>
    Scharr,
}

/// <summary>Which way a normal map's green channel points (docs/pack-format.md, <c>normal.convention</c>).</summary>
public enum NormalConvention {
    /// <summary>Green down (Y-), the game's and the renderer's.</summary>
    DirectX,
    /// <summary>Green up (Y+): Blender, Substance's default.</summary>
    OpenGl,
}

/// <summary>What <see cref="MapGenerator.Normal"/> and <see cref="MapGenerator.AmbientOcclusion"/> take as height.</summary>
public enum HeightSource {
    /// <summary>The input is a colour texture: its perceptual luminance is the height.</summary>
    Luminance,
    /// <summary>The input is a height map (as <see cref="MapGenerator.Height"/> makes): its red channel is the height.</summary>
    HeightMap,
}

/// <summary>Height from luminance.</summary>
/// <param name="Contrast">Factor around mid grey (1 = as is).</param>
/// <param name="BlurRadius">Gaussian σ in pixels before the contrast (0 = none); smooths out paint detail.</param>
/// <param name="Invert">Dark is high (cracks in a bright wall are deep without it).</param>
/// <param name="Wrap">Tile the edges (the blur wraps).</param>
public sealed record HeightOptions(double Contrast = 1, double BlurRadius = 1, bool Invert = false, bool Wrap = false) {
    public static HeightOptions Default { get; } = new();
}

/// <summary>Tangent-space normals from the height's slope.</summary>
/// <param name="Strength">Height units per pixel of slope (higher = bumpier).</param>
/// <param name="BlurRadius">Gaussian σ in pixels on the height first.</param>
/// <param name="Invert">Swap up and down (dark is high).</param>
/// <param name="Wrap">Tile the edges (seamless textures); clamp otherwise.</param>
public sealed record NormalOptions(
    HeightSource Source = HeightSource.Luminance,
    GradientFilter Filter = GradientFilter.Sobel,
    double Strength = 4,
    double BlurRadius = 0,
    bool Invert = false,
    NormalConvention Convention = NormalConvention.DirectX,
    bool Wrap = false) {
    public static NormalOptions Default { get; } = new();
}

/// <summary>Roughness from luminance and local contrast (darker and busier = rougher).</summary>
/// <param name="Base">Roughness of a mid-grey, flat area, 0-1.</param>
/// <param name="Range">How far luminance and detail move it from <paramref name="Base"/>, 0-2.</param>
/// <param name="DetailWeight">Share of local contrast against luminance, 0-1.</param>
/// <param name="DetailRadius">Radius (Gaussian σ, pixels) of the local contrast.</param>
/// <param name="Invert">Brighter = rougher.</param>
/// <param name="Wrap">Tile the edges (the local contrast wraps).</param>
public sealed record RoughnessOptions(double Base = 0.7, double Range = 0.5, double DetailWeight = 0.5, double DetailRadius = 2, bool Invert = false, bool Wrap = false) {
    public static RoughnessOptions Default { get; } = new();
}

/// <summary>Ambient occlusion from height, horizon-based.</summary>
/// <param name="Radius">How far to look, in pixels.</param>
/// <param name="Strength">Factor on the darkening, 0-4.</param>
/// <param name="Samples">Directions per pixel (4-32).</param>
/// <param name="Depth">The full height range (0 to 1) in pixels: how deep the relief is.</param>
public sealed record AmbientOcclusionOptions(
    HeightSource Source = HeightSource.HeightMap,
    double Radius = 8,
    double Strength = 1,
    int Samples = 8,
    double Depth = 16,
    bool Wrap = false) {
    public static AmbientOcclusionOptions Default { get; } = new();
}

/// <summary>Metallic: a constant, or a mask of the bright (or dark) parts.</summary>
/// <param name="Threshold">With a value: luminance where the mask turns on, 0-1; null = <paramref name="Value"/> everywhere.</param>
/// <param name="Value">The constant, or the mask's on value, 0-1.</param>
/// <param name="Softness">Width of the ramp around the threshold.</param>
/// <param name="Invert">Dark parts are metal.</param>
public sealed record MetallicOptions(double Value = 0, double? Threshold = null, double Softness = 0.05, bool Invert = false) {
    public static MetallicOptions Default { get; } = new();
}

/// <summary>Emission mask from brightness (the largest of R, G, B).</summary>
public sealed record EmissionMaskOptions(double Threshold = 0.85, double Softness = 0.05) {
    public static EmissionMaskOptions Default { get; } = new();
}

/// <summary>
///     Classic generators of PBR maps from a colour texture (features 5.5; no AI). Each takes RGBA8 (sRGB colour) and gives RGBA8: a
///     grey image for single-value maps (R = G = B, A = 255), XYZ in RGB for normals (128 = 0). Deterministic: the same input and
///     options give the same bytes. Rows run in parallel; progress runs 0 to 1; cancellation is checked between bands of rows.
/// </summary>
/// <remarks>
///     <paramref name="scale"/> on every generator is the image's size relative to the full-size texture (1 at full size): the live
///     preview runs on a copy made by <see cref="PreviewSource"/> and passes its scale, so radii shrink and slopes steepen as they
///     would at full size, and the preview looks like the result.
/// </remarks>
public static partial class MapGenerator {
    /// <summary>The side of the live preview's copy.</summary>
    public const int PreviewSide = 256;

    /// <summary>
    ///     A copy of <paramref name="image"/> no larger than <paramref name="maxSide"/> (Lanczos, sRGB), and its scale relative to the
    ///     image (1 when it already fits: the image itself).
    /// </summary>
    public static (RgbaImage Image, double Scale) PreviewSource(RgbaImage image, int maxSide = PreviewSide, CancellationToken cancel = default) {
        (int w, int h) = Lanczos.FitWithin(image.Width, image.Height, maxSide);
        if (w == image.Width && h == image.Height) return (image, 1);
        return (Lanczos.Resize(image, w, h, ResizeOptions.Colour, null, cancel), (double)w / image.Width);
    }

    /// <summary>Height from luminance: blurred, contrast around mid grey, optionally inverted.</summary>
    public static RgbaImage Height(RgbaImage image, HeightOptions? options = null, double scale = 1, IProgress<double>? progress = null, CancellationToken cancel = default) {
        options ??= HeightOptions.Default;
        float[] h = HeightPlane(image, options, scale, cancel);
        progress?.Report(1);
        return Planes.Grey(h, image.Width, image.Height);
    }

    private static float[] HeightPlane(RgbaImage image, HeightOptions o, double scale, CancellationToken cancel) {
        int w = image.Width;
        float[] l = Planes.Blur(Planes.Luminance(image, cancel), w, image.Height, o.BlurRadius * scale, o.Wrap, cancel);
        float c = (float)o.Contrast;
        RowBands.For(image.Height, (y0, y1) => {
            for (int i = y0 * w; i < y1 * w; i++) {
                float v = (l[i] - 0.5f) * c + 0.5f;
                l[i] = Math.Clamp(o.Invert ? 1 - v : v, 0, 1);
            }
        }, null, cancel);
        return l;
    }

    /// <summary>Tangent-space normals (DirectX or OpenGL) from the slope of the luminance or of a height map.</summary>
    public static RgbaImage Normal(RgbaImage image, NormalOptions? options = null, double scale = 1, IProgress<double>? progress = null, CancellationToken cancel = default) {
        options ??= NormalOptions.Default;
        int w = image.Width, hgt = image.Height;
        float[] src = options.Source == HeightSource.HeightMap ? Planes.Channel(image, 0) : Planes.Luminance(image, cancel);
        float[] h = Planes.Blur(src, w, hgt, options.BlurRadius * scale, options.Wrap, cancel);
        progress?.Report(0.3);
        // Sobel 1 2 1 / 8, Scharr 3 10 3 / 32: the slope in height units per pixel.
        (float side, float mid, float norm) = options.Filter == GradientFilter.Scharr ? (3f, 10f, 32f) : (1f, 2f, 8f);
        float k = (float)(options.Strength * scale) / norm * (options.Invert ? -1 : 1);
        float gy = options.Convention == NormalConvention.DirectX ? 1 : -1;
        bool wrap = options.Wrap;
        byte[] o = new byte[w * hgt * 4];
        RowBands.For(hgt, (y0, y1) => {
            for (int y = y0; y < y1; y++) {
                int up = Planes.Index(y - 1, hgt, wrap) * w, row = y * w, down = Planes.Index(y + 1, hgt, wrap) * w;
                for (int x = 0; x < w; x++) {
                    int l = Planes.Index(x - 1, w, wrap), r = Planes.Index(x + 1, w, wrap);
                    float dx = side * (h[up + r] - h[up + l]) + mid * (h[row + r] - h[row + l]) + side * (h[down + r] - h[down + l]);
                    float dy = side * (h[down + l] - h[up + l]) + mid * (h[down + x] - h[up + x]) + side * (h[down + r] - h[up + r]);
                    // The surface's normal in image space (x right, y down, z out) is (-dh/dx, -dh/dy, 1); DirectX keeps y down.
                    float nx = -dx * k, ny = -dy * k * gy;
                    float len = MathF.Sqrt(nx * nx + ny * ny + 1);
                    int p = (row + x) * 4;
                    o[p] = Srgb.ToByte(0.5f + 0.5f * nx / len);
                    o[p + 1] = Srgb.ToByte(0.5f + 0.5f * ny / len);
                    o[p + 2] = Srgb.ToByte(0.5f + 0.5f / len);
                    o[p + 3] = 255;
                }
            }
        }, progress, cancel, 0.3, 0.7);
        return new RgbaImage(w, hgt, o);
    }

    /// <summary>Roughness from luminance (darker = rougher) and local contrast (busier = rougher).</summary>
    public static RgbaImage Roughness(RgbaImage image, RoughnessOptions? options = null, double scale = 1, IProgress<double>? progress = null, CancellationToken cancel = default) {
        options ??= RoughnessOptions.Default;
        int w = image.Width, h = image.Height;
        float[] l = Planes.Luminance(image, cancel);
        double sigma = Math.Max(0.5, options.DetailRadius * scale);
        float[] mean = Planes.Blur(l, w, h, sigma, options.Wrap, cancel);
        float[] sq = new float[l.Length];
        for (int i = 0; i < l.Length; i++) sq[i] = l[i] * l[i];
        float[] meanSq = Planes.Blur(sq, w, h, sigma, options.Wrap, cancel);
        progress?.Report(0.6);
        float dw = (float)Math.Clamp(options.DetailWeight, 0, 1), b = (float)options.Base, range = (float)options.Range;
        float[] o = new float[l.Length];
        RowBands.For(h, (y0, y1) => {
            for (int i = y0 * w; i < y1 * w; i++) {
                float var = meanSq[i] - mean[i] * mean[i];
                // Float noise of a flat area stays flat.
                float detail = var < 1e-6f ? 0 : Math.Min(1, MathF.Sqrt(var) * 4);
                float t = (1 - dw) * (1 - l[i]) + dw * detail;
                float v = Math.Clamp(b + range * (t - 0.5f), 0, 1);
                o[i] = options.Invert ? 1 - v : v;
            }
        }, progress, cancel, 0.6, 0.4);
        return Planes.Grey(o, w, h);
    }

    /// <summary>
    ///     Ambient occlusion: per pixel, the highest horizon in <see cref="AmbientOcclusionOptions.Samples"/> directions within the
    ///     radius; the mean sine of those horizon angles darkens it. Flat height gives white.
    /// </summary>
    public static RgbaImage AmbientOcclusion(RgbaImage image, AmbientOcclusionOptions? options = null, double scale = 1, IProgress<double>? progress = null, CancellationToken cancel = default) {
        options ??= AmbientOcclusionOptions.Default;
        int w = image.Width, hgt = image.Height;
        float[] h = options.Source == HeightSource.HeightMap ? Planes.Channel(image, 0) : Planes.Luminance(image, cancel);
        int samples = Math.Clamp(options.Samples, 1, 64);
        float radius = (float)Math.Max(1, options.Radius * scale);
        int steps = Math.Clamp((int)MathF.Ceiling(radius), 1, 8);
        float depth = (float)(options.Depth * scale), strength = (float)options.Strength;
        bool wrap = options.Wrap;
        // Fixed offsets per direction and step, so every pixel does the same work.
        int taps = samples * steps;
        int[] ox = new int[taps], oy = new int[taps];
        float[] dist = new float[taps];
        for (int d = 0; d < samples; d++) {
            double a = 2 * Math.PI * (d + 0.5) / samples;
            for (int s = 0; s < steps; s++) {
                float r = radius * (s + 1) / steps;
                ox[d * steps + s] = (int)Math.Round(Math.Cos(a) * r);
                oy[d * steps + s] = (int)Math.Round(Math.Sin(a) * r);
                dist[d * steps + s] = Math.Max(1, MathF.Sqrt(ox[d * steps + s] * ox[d * steps + s] + oy[d * steps + s] * oy[d * steps + s]));
            }
        }
        float[] o = new float[w * hgt];
        RowBands.For(hgt, (y0, y1) => {
            for (int y = y0; y < y1; y++) {
                for (int x = 0; x < w; x++) {
                    float h0 = h[y * w + x], occlusion = 0;
                    for (int d = 0; d < samples; d++) {
                        float best = 0;
                        for (int s = 0; s < steps; s++) {
                            int t = d * steps + s;
                            float q = h[Planes.Index(y + oy[t], hgt, wrap) * w + Planes.Index(x + ox[t], w, wrap)];
                            float slope = (q - h0) * depth / dist[t];
                            if (slope > best) best = slope;
                        }
                        occlusion += best / MathF.Sqrt(1 + best * best);
                    }
                    o[y * w + x] = Math.Clamp(1 - strength * occlusion / samples, 0, 1);
                }
            }
        }, progress, cancel);
        return Planes.Grey(o, w, hgt);
    }

    /// <summary>Metallic: <see cref="MetallicOptions.Value"/> everywhere, or where the luminance passes the threshold.</summary>
    public static RgbaImage Metallic(RgbaImage image, MetallicOptions? options = null, IProgress<double>? progress = null, CancellationToken cancel = default) {
        options ??= MetallicOptions.Default;
        int w = image.Width, h = image.Height;
        float value = (float)Math.Clamp(options.Value, 0, 1);
        float[] o = new float[w * h];
        if (options.Threshold is not { } threshold) {
            Array.Fill(o, value);
        } else {
            float[] l = Planes.Luminance(image, cancel);
            Mask(l, o, w, h, (float)threshold, (float)options.Softness, options.Invert, value, cancel);
        }
        progress?.Report(1);
        return Planes.Grey(o, w, h);
    }

    /// <summary>Emission mask: white where the brightest channel passes the threshold.</summary>
    public static RgbaImage EmissionMask(RgbaImage image, EmissionMaskOptions? options = null, IProgress<double>? progress = null, CancellationToken cancel = default) {
        options ??= EmissionMaskOptions.Default;
        int w = image.Width, h = image.Height;
        float[] o = new float[w * h];
        Mask(Planes.Value(image), o, w, h, (float)options.Threshold, (float)options.Softness, false, 1, cancel);
        progress?.Report(1);
        return Planes.Grey(o, w, h);
    }

    private static void Mask(float[] v, float[] o, int w, int h, float threshold, float softness, bool invert, float on, CancellationToken cancel) {
        float lo = threshold - softness / 2, hi = threshold + softness / 2;
        RowBands.For(h, (y0, y1) => {
            for (int i = y0 * w; i < y1 * w; i++) {
                float t = hi <= lo ? (v[i] >= threshold ? 1 : 0) : Math.Clamp((v[i] - lo) / (hi - lo), 0, 1);
                t = t * t * (3 - 2 * t);
                o[i] = (invert ? 1 - t : t) * on;
            }
        }, null, cancel);
    }
}

/// <summary>
///     De-lighting (<see cref="MapGenerator.Albedo"/>): how much of each kind of baked light to take out of a colour texture. Every
///     strength runs 0 (keep) to 1 (take it all out); radii are in pixels of the full-size texture.
/// </summary>
/// <param name="Shading">Divides out the low-frequency luminance (painted light and shadow), 0-1.</param>
/// <param name="Radius">Size (Gaussian σ, pixels) of what counts as light rather than paint. Larger keeps more of the painted pattern.</param>
/// <param name="EdgeKeep">
///     0: the light estimate is a plain blur (soft painted shadows go, sharp paint edges may halo); 1: a guided filter that follows
///     strong edges (no halos, but a hard-edged shadow stays). In between mixes the two.
/// </param>
/// <param name="Occlusion">Lifts ambient occlusion found from the height (dark creases and corners), 0-1.</param>
/// <param name="OcclusionRadius">How far the occlusion looks, in pixels.</param>
/// <param name="Cavity">Lifts small dark cavities (fine cracks, pores) found from the height, 0-1.</param>
/// <param name="CavityRadius">Size of a cavity, in pixels.</param>
/// <param name="Highlights">Compresses painted highlights (spots well above their surroundings), 0-1.</param>
/// <param name="HighlightThreshold">How much brighter than its surroundings a spot must be to count as a highlight (0.25 = 25 %).</param>
/// <param name="ColourCast">Flattens tints that change slowly over the texture (coloured light), 0-1.</param>
/// <param name="Neutralise">Pulls the texture's average colour towards grey (white balance), 0-1.</param>
/// <param name="Wrap">Tile the edges: every filter wraps, so a tiling texture stays seamless.</param>
public sealed record AlbedoOptions(
    double Shading = 0.85,
    double Radius = 16,
    double EdgeKeep = 0.2,
    double Occlusion = 0.25,
    double OcclusionRadius = 6,
    double Cavity = 0.2,
    double CavityRadius = 2,
    double Highlights = 0.5,
    double HighlightThreshold = 0.25,
    double ColourCast = 0.5,
    double Neutralise = 0,
    bool Wrap = false) {
    public static AlbedoOptions Default { get; } = new();

    /// <summary>Everything off: the output is the input (rounding aside).</summary>
    public static AlbedoOptions None { get; } = Default with { Shading = 0, Occlusion = 0, Cavity = 0, Highlights = 0, ColourCast = 0, Neutralise = 0 };
}

public static partial class MapGenerator {
    /// <summary>The darkest linear luminance the de-lighting divides by (black stays black, no blow-up).</summary>
    private const float MinLuminance = 1e-3f;

    /// <summary>
    ///     Albedo from a colour texture with light painted in: classic de-lighting, no AI. In linear light: the low-frequency luminance
    ///     (a Gaussian blur and a self-guided filter, mixed by <see cref="AlbedoOptions.EdgeKeep"/>) is divided out, occlusion and
    ///     cavities found from <paramref name="height"/> are lifted, highlights compressed, slow colour casts flattened and the average
    ///     colour optionally neutralised. The luminance changes scale each pixel's RGB, so hue and saturation stay; the average
    ///     luminance stays the input's. Alpha is kept. With <see cref="AlbedoOptions.Wrap"/>, a tiling texture stays tiling.
    /// </summary>
    /// <param name="height">The height to find occlusion and cavities in (as <see cref="Height"/> makes, red channel); null: made from the image with the default height options.</param>
    public static RgbaImage Albedo(RgbaImage image, AlbedoOptions? options = null, RgbaImage? height = null, double scale = 1, IProgress<double>? progress = null, CancellationToken cancel = default) {
        ArgumentNullException.ThrowIfNull(image);
        options ??= AlbedoOptions.Default;
        int w = image.Width, h = image.Height, n = w * h;
        bool wrap = options.Wrap;
        byte[] p = image.Rgba;
        float[] lin = Srgb.ToLinear;

        // Linear RGB, luminance, its log, and a weight per pixel (alpha: transparent texels are often junk).
        float[] r = new float[n], g = new float[n], b = new float[n], y = new float[n], wt = new float[n];
        RowBands.For(h, (y0, y1) => {
            for (int i = y0 * w; i < y1 * w; i++) {
                int s = i * 4;
                r[i] = lin[p[s]];
                g[i] = lin[p[s + 1]];
                b[i] = lin[p[s + 2]];
                y[i] = MathF.Log(MathF.Max(MinLuminance, 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i]));
                wt[i] = 0.02f + p[s + 3] / 255f;
            }
        }, null, cancel);
        float meanIn = WeightedMean(y, wt);
        float[] o = (float[])y.Clone(); // the output's log luminance
        progress?.Report(0.1);

        // 1. Shading: the light estimate, a blur and a self-guided filter mixed.
        float shading = Unit(options.Shading);
        double sigma = Math.Max(0.5, options.Radius * scale);
        if (shading > 0) {
            float[] light = LightEstimate(y, w, h, sigma, Unit(options.EdgeKeep), wrap, cancel);
            float meanLight = WeightedMean(light, wt);
            RowBands.For(h, (y0, y1) => {
                for (int i = y0 * w; i < y1 * w; i++) o[i] -= shading * (light[i] - meanLight);
            }, null, cancel);
        }
        progress?.Report(0.4);

        // 2. Occlusion and cavities, from the height.
        float occlusion = Unit(options.Occlusion), cavity = Unit(options.Cavity);
        if (occlusion > 0 || cavity > 0) {
            height ??= Height(image, new HeightOptions(Wrap: wrap), scale, null, cancel);
            float[] hp = Planes.Channel(height, 0);
            if (occlusion > 0) {
                AmbientOcclusionOptions ao = new(HeightSource.HeightMap, options.OcclusionRadius, 1, 8, 16, wrap);
                float[] a = Planes.Channel(AmbientOcclusion(height, ao, scale, null, cancel), 0);
                RowBands.For(h, (y0, y1) => {
                    for (int i = y0 * w; i < y1 * w; i++) o[i] -= occlusion * MathF.Log(MathF.Max(0.2f, a[i]));
                }, null, cancel);
            }
            if (cavity > 0) {
                float[] around = Planes.Blur(hp, w, h, Math.Max(0.5, options.CavityRadius * scale), wrap, cancel);
                RowBands.For(h, (y0, y1) => {
                    for (int i = y0 * w; i < y1 * w; i++) {
                        // How far below its surroundings, 0-1; a full cavity is half as bright as it should be.
                        float depth = Math.Clamp((around[i] - hp[i]) * 8, 0, 1);
                        o[i] -= cavity * MathF.Log(1 - 0.5f * depth);
                    }
                }, null, cancel);
            }
        }
        progress?.Report(0.6);

        // 3. Highlights: soft knee on how far a pixel stands above its surroundings.
        float highlights = Unit(options.Highlights);
        if (highlights > 0) {
            float[] around = Planes.Blur(o, w, h, Math.Max(1, sigma / 4), wrap, cancel);
            float knee = MathF.Log(1 + (float)Math.Max(0, options.HighlightThreshold));
            RowBands.For(h, (y0, y1) => {
                for (int i = y0 * w; i < y1 * w; i++) {
                    float over = o[i] - around[i] - knee;
                    if (over <= 0) continue;
                    // A soft knee: the excess (natural log) never ends above a quarter.
                    float squeezed = over / (1 + 4 * over);
                    o[i] -= highlights * (over - squeezed);
                }
            }, null, cancel);
        }

        // The average luminance stays the input's.
        float shift = meanIn - WeightedMean(o, wt);
        progress?.Report(0.7);

        // 4. Colour: chromaticity (RGB / luminance) flattened towards its average and the average towards grey.
        float flatten = Unit(options.ColourCast), neutral = Unit(options.Neutralise);
        float[]? cr = null, cg = null, cb = null;
        float gr = 1, gg = 1, gb = 1;
        if (flatten > 0 || neutral > 0) {
            float[] chr = new float[n], chg = new float[n], chb = new float[n];
            RowBands.For(h, (y0, y1) => {
                for (int i = y0 * w; i < y1 * w; i++) {
                    float l = MathF.Exp(y[i]);
                    chr[i] = r[i] / l;
                    chg[i] = g[i] / l;
                    chb[i] = b[i] / l;
                }
            }, null, cancel);
            gr = WeightedMean(chr, wt);
            gg = WeightedMean(chg, wt);
            gb = WeightedMean(chb, wt);
            if (flatten > 0) {
                cr = Planes.Blur(chr, w, h, sigma, wrap, cancel);
                cg = Planes.Blur(chg, w, h, sigma, wrap, cancel);
                cb = Planes.Blur(chb, w, h, sigma, wrap, cancel);
            }
        }
        progress?.Report(0.85);

        byte[] dst = new byte[n * 4];
        RowBands.For(h, (y0, y1) => {
            for (int i = y0 * w; i < y1 * w; i++) {
                float fr = 1, fg = 1, fb = 1;
                if (cr is not null) {
                    fr = MathF.Pow(gr / MathF.Max(1e-3f, cr[i]), flatten);
                    fg = MathF.Pow(gg / MathF.Max(1e-3f, cg![i]), flatten);
                    fb = MathF.Pow(gb / MathF.Max(1e-3f, cb![i]), flatten);
                }
                if (neutral > 0) {
                    fr *= MathF.Pow(1 / MathF.Max(1e-3f, gr), neutral);
                    fg *= MathF.Pow(1 / MathF.Max(1e-3f, gg), neutral);
                    fb *= MathF.Pow(1 / MathF.Max(1e-3f, gb), neutral);
                }
                float rr = r[i] * fr, rg = g[i] * fg, rb = b[i] * fb;
                float ly = 0.2126f * rr + 0.7152f * rg + 0.0722f * rb;
                // Scale RGB to the target luminance: hue and saturation stay.
                float k = ly > 1e-6f ? MathF.Exp(o[i] + shift) / ly : 0;
                float max = MathF.Max(rr, MathF.Max(rg, rb)) * k;
                if (max > 1) k /= max;
                int d = i * 4;
                dst[d] = Srgb.ToByte(Srgb.Encode(rr * k));
                dst[d + 1] = Srgb.ToByte(Srgb.Encode(rg * k));
                dst[d + 2] = Srgb.ToByte(Srgb.Encode(rb * k));
                dst[d + 3] = p[d + 3];
            }
        }, progress, cancel, 0.85, 0.15);
        return new RgbaImage(w, h, dst);
    }

    /// <summary>
    ///     The light in log luminance <paramref name="y"/>: a Gaussian blur, mixed by <paramref name="edgeKeep"/> with a self-guided
    ///     filter (He et al., Gaussian windows) whose ε is small enough to follow edges of about one stop.
    /// </summary>
    private static float[] LightEstimate(float[] y, int w, int h, double sigma, float edgeKeep, bool wrap, CancellationToken cancel) {
        float[] mean = Planes.Blur(y, w, h, sigma, wrap, cancel);
        if (edgeKeep <= 0) return mean;
        float[] sq = new float[y.Length];
        for (int i = 0; i < y.Length; i++) sq[i] = y[i] * y[i];
        float[] meanSq = Planes.Blur(sq, w, h, sigma, wrap, cancel);
        // ε from 1 (a blur, edges of a few stops) to 0.01 (edges of a tenth of a stop).
        float eps = MathF.Pow(10, -2 * edgeKeep);
        float[] a = new float[y.Length], bb = new float[y.Length];
        for (int i = 0; i < y.Length; i++) {
            float var = MathF.Max(0, meanSq[i] - mean[i] * mean[i]);
            a[i] = var / (var + eps);
            bb[i] = mean[i] - a[i] * mean[i];
        }
        float[] ma = Planes.Blur(a, w, h, sigma, wrap, cancel), mb = Planes.Blur(bb, w, h, sigma, wrap, cancel);
        float[] o = new float[y.Length];
        for (int i = 0; i < y.Length; i++) {
            float guided = ma[i] * y[i] + mb[i];
            o[i] = mean[i] + edgeKeep * (guided - mean[i]);
        }
        return o;
    }

    private static float WeightedMean(float[] v, float[] weight) {
        double sum = 0, total = 0;
        for (int i = 0; i < v.Length; i++) {
            sum += (double)v[i] * weight[i];
            total += weight[i];
        }
        return total > 0 ? (float)(sum / total) : 0;
    }

    private static float Unit(double v) => (float)Math.Clamp(v, 0, 1);
}

/// <summary>Single-channel float images (row-major, 0-1) and the operations the generators share.</summary>
internal static class Planes {
    /// <summary>
    ///     Perceptual luminance, 0-1: the sRGB-encoded value of the linear luminance (Rec. 709 weights on linear RGB). Even steps for
    ///     the eye, as painters see the texture.
    /// </summary>
    public static float[] Luminance(RgbaImage image, CancellationToken cancel) {
        float[] lin = Srgb.ToLinear;
        int w = image.Width;
        byte[] p = image.Rgba;
        float[] o = new float[w * image.Height];
        RowBands.For(image.Height, (y0, y1) => {
            for (int i = y0 * w; i < y1 * w; i++) {
                int s = i * 4;
                o[i] = Srgb.Encode(0.2126f * lin[p[s]] + 0.7152f * lin[p[s + 1]] + 0.0722f * lin[p[s + 2]]);
            }
        }, null, cancel);
        return o;
    }

    /// <summary>Channel 0-3 of the image, 0-1, as stored (data maps: height, masks).</summary>
    public static float[] Channel(RgbaImage image, int channel) {
        float[] o = new float[image.Width * image.Height];
        for (int i = 0; i < o.Length; i++) o[i] = image.Rgba[i * 4 + channel] / 255f;
        return o;
    }

    /// <summary>The largest of R, G and B (sRGB-encoded value), 0-1.</summary>
    public static float[] Value(RgbaImage image) {
        float[] o = new float[image.Width * image.Height];
        byte[] p = image.Rgba;
        for (int i = 0; i < o.Length; i++) o[i] = Math.Max(p[i * 4], Math.Max(p[i * 4 + 1], p[i * 4 + 2])) / 255f;
        return o;
    }

    /// <summary>A grey RGBA8 image (R = G = B = the value, A = 255).</summary>
    public static RgbaImage Grey(float[] v, int width, int height) {
        byte[] o = new byte[width * height * 4];
        for (int i = 0; i < v.Length; i++) {
            byte b = Srgb.ToByte(v[i]);
            o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = b;
            o[i * 4 + 3] = 255;
        }
        return new RgbaImage(width, height, o);
    }

    /// <summary>An index inside 0..n-1: wrapped (tiling) or clamped.</summary>
    public static int Index(int i, int n, bool wrap) {
        if ((uint)i < (uint)n) return i;
        if (!wrap) return i < 0 ? 0 : n - 1;
        i %= n;
        return i < 0 ? i + n : i;
    }

    /// <summary>
    ///     A Gaussian blur of standard deviation <paramref name="sigma"/> pixels (three box passes per axis); a copy when
    ///     <paramref name="sigma"/> is under 0.5.
    /// </summary>
    public static float[] Blur(float[] p, int w, int h, double sigma, bool wrap, CancellationToken cancel) {
        float[] a = (float[])p.Clone();
        if (sigma < 0.5) return a;
        float[] b = new float[a.Length];
        foreach (int r in BoxRadii(sigma)) {
            BoxRows(a, b, w, h, r, wrap, cancel);
            BoxColumns(b, a, w, h, r, wrap, cancel);
        }
        return a;
    }

    /// <summary>Box radii of three passes approximating a Gaussian (Kovesi's method).</summary>
    private static int[] BoxRadii(double sigma) {
        const int n = 3;
        double ideal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(ideal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        int m = (int)Math.Round((12 * sigma * sigma - n * wl * wl - 4.0 * n * wl - 3 * n) / (-4.0 * wl - 4));
        int[] radii = new int[n];
        for (int i = 0; i < n; i++) radii[i] = ((i < m ? wl : wu) - 1) / 2;
        return radii;
    }

    private static void BoxRows(float[] src, float[] dst, int w, int h, int r, bool wrap, CancellationToken cancel) {
        float inv = 1f / (2 * r + 1);
        RowBands.For(h, (y0, y1) => {
            for (int y = y0; y < y1; y++) {
                int row = y * w;
                double sum = 0;
                for (int k = -r; k <= r; k++) sum += src[row + Index(k, w, wrap)];
                for (int x = 0; x < w; x++) {
                    dst[row + x] = (float)(sum * inv);
                    sum += src[row + Index(x + r + 1, w, wrap)] - src[row + Index(x - r, w, wrap)];
                }
            }
        }, null, cancel);
    }

    private static void BoxColumns(float[] src, float[] dst, int w, int h, int r, bool wrap, CancellationToken cancel) {
        float inv = 1f / (2 * r + 1);
        // Bands of columns; each walks down its columns with one running sum per column.
        const int chunk = 32;
        int chunks = (w + chunk - 1) / chunk;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = cancel }, c => {
            int x0 = c * chunk, x1 = Math.Min(w, x0 + chunk);
            double[] sum = new double[x1 - x0];
            for (int k = -r; k <= r; k++) {
                int row = Index(k, h, wrap) * w;
                for (int x = x0; x < x1; x++) sum[x - x0] += src[row + x];
            }
            for (int y = 0; y < h; y++) {
                int add = Index(y + r + 1, h, wrap) * w, sub = Index(y - r, h, wrap) * w, row = y * w;
                for (int x = x0; x < x1; x++) {
                    dst[row + x] = (float)(sum[x - x0] * inv);
                    sum[x - x0] += src[add + x] - src[sub + x];
                }
            }
        });
    }
}
