// Ported from ffx-remix (MIT; the same author as gd-ffx): shared/FfxRemix.Core/Texturing/RgbaImage.cs, Srgb.cs, RowBands.cs.
// The algorithms as they are there; namespaces and small adaptions for gd-ffx.
using System;
using System.Threading.Tasks;
using System.Threading;

namespace GdFfx.Remaster.Texturing;

/// <summary>An image in memory: RGBA8, rows top first.</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Rgba) {
    /// <summary>True if any pixel's alpha is below 255.</summary>
    public bool HasAlpha {
        get {
            for (int i = 3; i < Rgba.Length; i += 4) if (Rgba[i] != 255) return true;
            return false;
        }
    }

    /// <summary>True if every pixel's alpha is 0 or 255 (a cut-out, BC1's 1-bit alpha can hold it).</summary>
    public bool HasOnlyBinaryAlpha {
        get {
            for (int i = 3; i < Rgba.Length; i += 4) if (Rgba[i] is not (0 or 255)) return false;
            return true;
        }
    }
}

/// <summary>The sRGB transfer function (IEC 61966-2-1), for code that works in linear light.</summary>
internal static class Srgb {
    /// <summary>Byte (sRGB-encoded) to linear light, 0-1.</summary>
    public static readonly float[] ToLinear = BuildToLinear();

    /// <summary>Byte to 0-1, no transfer function.</summary>
    public static readonly float[] Unit = BuildUnit();

    /// <summary>Linear light, 0-1, to the sRGB-encoded value, 0-1.</summary>
    public static float Encode(float linear) {
        if (linear <= 0) return 0;
        if (linear >= 1) return 1;
        return linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
    }

    /// <summary>0-1 to a byte, rounded, clamped.</summary>
    public static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)(v * 255 + 0.5f);

    private static float[] BuildToLinear() {
        float[] t = new float[256];
        for (int i = 0; i < 256; i++) {
            double c = i / 255.0;
            t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return t;
    }

    private static float[] BuildUnit() {
        float[] t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = i / 255f;
        return t;
    }
}

/// <summary>
///     The parallel loop of the image code (<see cref="Lanczos"/>, the map generators): rows in bands of <see cref="Band"/>, each
///     band on one thread, cancellation checked before each band, progress reported after each.
/// </summary>
internal static class RowBands {
    /// <summary>Rows per band.</summary>
    public const int Band = 16;

    /// <summary>
    ///     Runs <paramref name="body"/>(first row, end row) over <paramref name="rows"/> rows. Progress goes from
    ///     <paramref name="start"/> to <paramref name="start"/> + <paramref name="span"/> (reports may arrive slightly out of order).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was cancelled.</exception>
    public static void For(int rows, Action<int, int> body, IProgress<double>? progress, CancellationToken cancel, double start = 0, double span = 1) {
        cancel.ThrowIfCancellationRequested();
        int bands = (rows + Band - 1) / Band;
        int done = 0;
        Parallel.For(0, bands, new ParallelOptions { CancellationToken = cancel }, b => {
            cancel.ThrowIfCancellationRequested();
            int y0 = b * Band;
            body(y0, Math.Min(rows, y0 + Band));
            int d = Interlocked.Increment(ref done);
            progress?.Report(start + span * d / bands);
        });
    }
}
