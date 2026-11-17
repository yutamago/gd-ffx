// Ported from ffx-remix (MIT; the same author as gd-ffx): shared/FfxRemix.Core/Texturing/Lanczos.cs.
// The algorithms as they are there; namespaces and small adaptions for gd-ffx.
using System;
using System.Threading;

namespace GdFfx.Remaster.Texturing;

/// <summary>How <see cref="Lanczos.Resize"/> treats the pixels.</summary>
/// <param name="Srgb">Colour is sRGB-encoded: filter RGB in linear light and encode back (alpha is always linear).</param>
/// <param name="PremultiplyAlpha">Filter colour premultiplied by alpha, so transparent pixels don't bleed their colour into the edge.</param>
public sealed record ResizeOptions(bool Srgb = true, bool PremultiplyAlpha = true) {
    /// <summary>Colour images: sRGB, premultiplied (the default).</summary>
    public static ResizeOptions Colour { get; } = new();

    /// <summary>Data (normal, roughness, height, masks): every channel as stored, independently.</summary>
    public static ResizeOptions Data { get; } = new(Srgb: false, PremultiplyAlpha: false);
}

/// <summary>
///     Lanczos-3 resampling of RGBA8 images (features 5.4's fallback without AI): separable, any target size, edges clamped. Down-
///     scaling widens the kernel by the scale factor, so it filters rather than skips. Deterministic; rows run in parallel.
/// </summary>
public static class Lanczos {
    /// <summary>Lobes of the kernel.</summary>
    public const int Lobes = 3;

    /// <summary>The largest side it makes (the game's limit).</summary>
    public const int MaxSide = 16384;

    /// <summary>
    ///     <paramref name="image"/> resized to <paramref name="width"/>x<paramref name="height"/>. The same size gives an exact copy.
    ///     Progress runs 0 to 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A side under 1 or over <see cref="MaxSide"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was cancelled.</exception>
    public static RgbaImage Resize(RgbaImage image, int width, int height, ResizeOptions? options = null, IProgress<double>? progress = null, CancellationToken cancel = default) {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxSide);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxSide);
        options ??= ResizeOptions.Colour;
        cancel.ThrowIfCancellationRequested();
        if (width == image.Width && height == image.Height) {
            progress?.Report(1);
            return image with { Rgba = (byte[])image.Rgba.Clone() };
        }
        int sw = image.Width, sh = image.Height;
        byte[] src = image.Rgba;
        float[] rgbLut = options.Srgb ? Srgb.ToLinear : Srgb.Unit;
        bool pre = options.PremultiplyAlpha;
        float[] planes = new float[sw * sh * 4];
        for (int i = 0; i < sw * sh; i++) {
            int o = i * 4;
            float a = src[o + 3] / 255f, m = pre ? a : 1;
            planes[o] = rgbLut[src[o]] * m;
            planes[o + 1] = rgbLut[src[o + 1]] * m;
            planes[o + 2] = rgbLut[src[o + 2]] * m;
            planes[o + 3] = a;
        }
        float[] r = ResizePlanes(planes, sw, sh, 4, width, height, progress, cancel);
        byte[] dst = new byte[width * height * 4];
        RowBands.For(height, (y0, y1) => {
            for (int i = y0 * width; i < y1 * width; i++) {
                int o = i * 4;
                float a = Math.Clamp(r[o + 3], 0, 1);
                float m = pre ? (a > 1f / 1024 ? 1 / a : 0) : 1;
                for (int c = 0; c < 3; c++) {
                    float v = Math.Clamp(r[o + c] * m, 0, 1);
                    dst[o + c] = Srgb.ToByte(options.Srgb ? Srgb.Encode(v) : v);
                }
                dst[o + 3] = Srgb.ToByte(a);
            }
        }, null, cancel);
        return new RgbaImage(width, height, dst);
    }

    /// <summary>Twice the size on both sides.</summary>
    public static RgbaImage Upscale2x(RgbaImage image, ResizeOptions? options = null, IProgress<double>? progress = null, CancellationToken cancel = default) =>
        Resize(image, image.Width * 2, image.Height * 2, options, progress, cancel);

    /// <summary>Four times the size on both sides.</summary>
    public static RgbaImage Upscale4x(RgbaImage image, ResizeOptions? options = null, IProgress<double>? progress = null, CancellationToken cancel = default) =>
        Resize(image, image.Width * 4, image.Height * 4, options, progress, cancel);

    /// <summary>
    ///     The size of <paramref name="width"/>x<paramref name="height"/> scaled down (aspect kept, sides at least 1) so that no side is
    ///     over <paramref name="maxSide"/>; the size itself when it fits.
    /// </summary>
    public static (int Width, int Height) FitWithin(int width, int height, int maxSide) {
        int side = Math.Max(width, height);
        if (side <= maxSide) return (width, height);
        double s = (double)maxSide / side;
        return (Math.Clamp((int)Math.Round(width * s), 1, maxSide), Math.Clamp((int)Math.Round(height * s), 1, maxSide));
    }

    /// <summary>
    ///     Resizes <paramref name="channels"/> interleaved float planes (no clamping, no transfer function). Progress 0 to 1 over the
    ///     two passes.
    /// </summary>
    internal static float[] ResizePlanes(float[] src, int sw, int sh, int channels, int dw, int dh, IProgress<double>? progress, CancellationToken cancel) {
        (int[] xi, float[] xw, int xt) = Weights(sw, dw);
        (int[] yi, float[] yw, int yt) = Weights(sh, dh);
        // Horizontal: sh rows of dw.
        float[] tmp = new float[dw * sh * channels];
        RowBands.For(sh, (y0, y1) => {
            for (int y = y0; y < y1; y++) {
                int srow = y * sw * channels, trow = y * dw * channels;
                for (int x = 0; x < dw; x++) {
                    int t0 = x * xt;
                    for (int c = 0; c < channels; c++) {
                        float sum = 0;
                        for (int k = 0; k < xt; k++) sum += xw[t0 + k] * src[srow + xi[t0 + k] * channels + c];
                        tmp[trow + x * channels + c] = sum;
                    }
                }
            }
        }, progress, cancel, 0, 0.5);
        // Vertical: each output row a weighted sum of whole rows.
        float[] dst = new float[dw * dh * channels];
        int rowLen = dw * channels;
        RowBands.For(dh, (y0, y1) => {
            for (int y = y0; y < y1; y++) {
                Span<float> row = dst.AsSpan(y * rowLen, rowLen);
                int t0 = y * yt;
                for (int k = 0; k < yt; k++) {
                    float w = yw[t0 + k];
                    if (w == 0) continue;
                    ReadOnlySpan<float> s = tmp.AsSpan(yi[t0 + k] * rowLen, rowLen);
                    for (int i = 0; i < rowLen; i++) row[i] += w * s[i];
                }
            }
        }, progress, cancel, 0.5, 0.5);
        return dst;
    }

    /// <summary>For each output sample: <c>taps</c> source indices (edges clamped) and normalised weights.</summary>
    private static (int[] Index, float[] Weight, int Taps) Weights(int sourceSize, int destSize) {
        double scale = (double)sourceSize / destSize;
        double width = Math.Max(1, scale); // the kernel's stretch: wider when shrinking
        double support = Lobes * width;
        int taps = (int)Math.Ceiling(support * 2) + 1;
        int[] index = new int[destSize * taps];
        float[] weight = new float[destSize * taps];
        double[] w = new double[taps];
        for (int o = 0; o < destSize; o++) {
            double center = (o + 0.5) * scale - 0.5;
            int first = (int)Math.Floor(center - support) + 1;
            double sum = 0;
            for (int k = 0; k < taps; k++) {
                w[k] = Kernel((first + k - center) / width);
                sum += w[k];
            }
            for (int k = 0; k < taps; k++) {
                index[o * taps + k] = Math.Clamp(first + k, 0, sourceSize - 1);
                weight[o * taps + k] = (float)(sum != 0 ? w[k] / sum : 0);
            }
        }
        return (index, weight, taps);
    }

    private static double Kernel(double x) {
        x = Math.Abs(x);
        if (x < 1e-9) return 1;
        if (x >= Lobes) return 0;
        double px = Math.PI * x;
        return Lobes * Math.Sin(px) * Math.Sin(px / Lobes) / (px * px);
    }
}
