using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// The colour check of a remastered view against the classic one of the same view
/// (docs/remaster-editor.md, "The colour check"): mean OKLab lightness, chroma and hue over the
/// picture and over a 4 x 3 grid, within tolerances, so a remastered map keeps the classic map's
/// hue and saturation balance (plan: "Art direction"). <c>tools/compare/colour.py</c> computes
/// the same numbers for the GPU comparison.
/// </summary>
public static class ColourBalance
{
    public const float Lightness = 0.06f, ChromaMin = 0.75f, ChromaMax = 1.35f, Hue = 12f, CellLightness = 0.15f, CellHue = 25f;
    private const int Cols = 4, Rows = 3;

    public sealed record Stats(float L, float C, float HueDeg, float HueChroma);

    public sealed record Result(Stats Classic, Stats Remastered, float DL, float ChromaRatio, float DHue, float CellsDL, float CellsDHue, List<string> Fails)
    {
        public bool Ok => Fails.Count == 0;

        public override string ToString() =>
            FormattableString.Invariant($"classic L {Classic.L:0.000} C {Classic.C:0.000} hue {Classic.HueDeg:0}, remastered L {Remastered.L:0.000} C {Remastered.C:0.000} hue {Remastered.HueDeg:0}: ")
            + FormattableString.Invariant($"dL {DL:+0.000;-0.000;0.000}, chroma x{ChromaRatio:0.00}, hue {DHue:0.0} deg, cells |dL| {CellsDL:0.000}, hue {CellsDHue:0.0} deg")
            + (Ok ? "" : " (OFF: " + string.Join("; ", Fails) + ")");
    }

    private static readonly float[] Lut = Enumerable.Range(0, 256).Select(i =>
    {
        float c = i / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }).ToArray();

    private sealed class Acc
    {
        public double N, L, C, A, B;

        public void Add(float l, float a, float b)
        {
            N++;
            L += l;
            C += MathF.Sqrt(a * a + b * b);
            A += a;
            B += b;
        }

        public Stats Stats()
        {
            double n = Math.Max(N, 1);
            return new((float)(L / n), (float)(C / n), (float)((Math.Atan2(B, A) * 180 / Math.PI + 360) % 360), (float)(Math.Sqrt(A * A + B * B) / n));
        }
    }

    /// <summary>The picture's stats and its cells' (every second pixel each way, as colour.py).</summary>
    public static (Stats Total, Stats[] Cells) Measure(Image image)
    {
        var img = (Image)image.Duplicate();
        if (img.GetFormat() != Image.Format.Rgb8)
            img.Convert(Image.Format.Rgb8);
        int w = img.GetWidth(), h = img.GetHeight();
        var data = img.GetData();
        var total = new Acc();
        var cells = Enumerable.Range(0, Cols * Rows).Select(_ => new Acc()).ToArray();
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
            {
                int i = (y * w + x) * 3;
                float r = Lut[data[i]], g = Lut[data[i + 1]], b = Lut[data[i + 2]];
                float l = MathF.Cbrt(0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b);
                float m = MathF.Cbrt(0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b);
                float s = MathF.Cbrt(0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b);
                float L = 0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s;
                float A = 1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s;
                float B = 0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s;
                total.Add(L, A, B);
                cells[Math.Min(Rows - 1, y * Rows / h) * Cols + Math.Min(Cols - 1, x * Cols / w)].Add(L, A, B);
            }
        return (total.Stats(), cells.Select(c => c.Stats()).ToArray());
    }

    private static float HueDiff(float a, float b)
    {
        float d = MathF.Abs(a - b) % 360;
        return MathF.Min(d, 360 - d);
    }

    private static float Median(List<float> v)
    {
        if (v.Count == 0)
            return 0;
        v.Sort();
        return v[v.Count / 2];
    }

    /// <summary>The remastered picture against the classic one (same size).</summary>
    public static Result Compare(Image classic, Image remastered)
    {
        var (c, cc) = Measure(classic);
        var (r, rc) = Measure(remastered);
        float dl = r.L - c.L, ratio = r.C / MathF.Max(c.C, 1e-6f), dh = HueDiff(r.HueDeg, c.HueDeg);
        float cellDl = Median(cc.Zip(rc, (a, b) => MathF.Abs(b.L - a.L)).ToList());
        float cellDh = Median(cc.Zip(rc, (a, b) => (a, b)).Where(p => p.a.HueChroma > 0.01f && p.b.HueChroma > 0.01f)
            .Select(p => HueDiff(p.a.HueDeg, p.b.HueDeg)).ToList());
        var fails = new List<string>();
        string I(FormattableString f) => FormattableString.Invariant(f);
        // a remastered picture that is the classic one measures nothing (a frame not drawn: a lost GPU device)
        if (classic.GetData().AsSpan().SequenceEqual(remastered.GetData()))
            fails.Add("the remastered picture is the classic one");
        if (MathF.Abs(dl) > Lightness)
            fails.Add(I($"lightness {dl:+0.000;-0.000;0.000} (limit {Lightness})"));
        if (ratio < ChromaMin || ratio > ChromaMax)
            fails.Add(I($"chroma x{ratio:0.00} (limit {ChromaMin}..{ChromaMax})"));
        if (dh > Hue)
            fails.Add(I($"hue {dh:0.0} deg (limit {Hue})"));
        if (cellDl > CellLightness)
            fails.Add(I($"cells' lightness {cellDl:0.000} (limit {CellLightness})"));
        if (cellDh > CellHue)
            fails.Add(I($"cells' hue {cellDh:0.0} deg (limit {CellHue})"));
        return new Result(c, r, dl, ratio, dh, cellDl, cellDh, fails);
    }
}
