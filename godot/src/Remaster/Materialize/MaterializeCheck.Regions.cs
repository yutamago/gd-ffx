using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.Remaster.Materialize;

/// <summary>R2b phase 6's part of the pipeline's check: regions (parity, the composite's validity, the cost on the GPU).</summary>
public sealed partial class MaterializeCheck
{
    /// <summary>
    /// Up to four regions over a <paramref name="w"/>x<paramref name="h"/> texture (no game data):
    /// a hard rectangle on the left (a rock-like setting set), a soft painted disc (metal), a polygon
    /// at the top right (grass-like, with backlight: a map the texture does not make), a stripe at
    /// the bottom (smooth plaster).
    /// </summary>
    public static List<RegionInput> CheckRegions(int n, int w, int h, int upscale)
    {
        var r = new List<RegionInput>();
        byte[] M(Action<byte[]> draw)
        {
            var m = new byte[w * h];
            draw(m);
            return m;
        }
        void Add(string name, byte[] m, PbrRecipe rec, float feather) =>
            r.Add(new RegionInput(name, rec, MaterialRegions.Key(m, w, h), MaterialRegions.ToRgba(m, w, h), feather));
        if (n >= 1)
            Add("rock", M(m => MaterialRegions.FillRect(m, w, h, 0, 0, 0.42f, 1)),
                new PbrRecipe { Version = 3, Upscale = upscale, NormalStrength = 10, HeightContrast = 1.6f, HeightBias = 0.15f, RoughnessBase = 0.9f, OcclusionStrength = 1.5f, Delight = 0.8f }, 1.5f);
        if (n >= 2)
            Add("metal", M(m =>
            {
                for (float a = 0; a < MathF.Tau; a += 0.2f)
                    MaterialRegions.Stroke(m, w, h, (0.68f, 0.6f), (0.68f + 0.1f * MathF.Cos(a), 0.6f + 0.1f * MathF.Sin(a)), w * 0.09f, 0.6f, false, false);
            }), new PbrRecipe { Version = 3, Upscale = upscale, MetallicMode = StepMode.Generate, Metallic = 1, RoughnessBase = 0.3f, RoughnessRange = 0.2f, HeightBias = -0.2f, HeightContrast = 0.6f }, 2f);
        if (n >= 3)
            Add("grass", M(m => MaterialRegions.FillPolygon(m, w, h, [(0.55f, 0.02f), (0.98f, 0.05f), (0.95f, 0.3f), (0.6f, 0.36f)])),
                new PbrRecipe { Version = 3, Upscale = upscale, BacklightOn = true, BacklightValue = 0.4f, RoughnessBase = 0.7f, NormalStrength = 3, HeightBias = 0.25f }, 1f);
        if (n >= 4)
            Add("plaster", M(m => MaterialRegions.FillRect(m, w, h, 0.45f, 0.85f, 1, 1)),
                new PbrRecipe { Version = 3, Upscale = upscale, NormalStrength = 2, RoughnessBase = 0.85f, HeightContrast = 0.4f, OcclusionMode = StepMode.Skip }, 3f);
        return r;
    }

    /// <summary>
    /// The composite on the CPU (<paramref name="cpu"/>'s build of <paramref name="o"/>): its normals
    /// unit length, each region's border in the height and the normal (the step across it against
    /// the steps inside, <see cref="MaterialRegions.BorderStep"/>) and the height's level across it,
    /// with the levels matched and without. Returns a failure or null, and the line.
    /// </summary>
    public static (string? Fail, string Line) CompositeCheck(CpuBackend cpu, MaterializeOutputs o, Func<bool, MaterializeOutputs> unmatched)
    {
        var normal = cpu.Get(o.Maps[MaterialStep.Normal]).ToRgba();
        var (maxErr, p999, bad) = MaterialRegions.NormalLength(normal);
        var height = cpu.Get(o.Maps[MaterialStep.Height]).Red();
        var u = unmatched(false);
        var heightRaw = cpu.Get(u.Maps[MaterialStep.Height]).Red();
        var parts = new List<string>();
        string? fail = null;
        int w = normal.Width, h = normal.Height;
        var nx = new float[w * h];
        for (int i = 0; i < nx.Length; i++)
            nx[i] = normal.Rgba[i * 4] / 255f + normal.Rgba[i * 4 + 1] / 255f;
        for (int i = 0; i < o.Regions.Count; i++)
        {
            var mask = cpu.Get(o.Regions[i].Mask).Red();
            var hb = MaterialRegions.BorderStep(height, mask, w, h);
            var nb = MaterialRegions.BorderStep(nx, mask, w, h);
            double lv = MaterialRegions.LevelStep(height, mask), lvRaw = MaterialRegions.LevelStep(heightRaw, mask);
            parts.Add($"region {i}: height border {hb.Ratio:0.00} (step {hb.Border:0.0000} against {hb.Inside:0.0000} inside), normal border {nb.Ratio:0.00}, "
                + $"height level across it {lv:0.000} of the spread (unmatched {lvRaw:0.000})");
            if (hb.Ratio > SeamLimit || nb.Ratio > SeamLimit)
                fail ??= $"region {i}'s border shows (height {hb.Ratio:0.00}, normal {nb.Ratio:0.00}; limit {SeamLimit})";
            if (lv > LevelLimit)
                fail ??= $"region {i}'s height steps at its border ({lv:0.000} of the spread)";
        }
        if (p999 > 0.02 || bad > 0.0005)
            fail ??= $"the composited normals are not unit length (99.9th percentile {p999:0.0000}, {bad * 100:0.000} % above 0.03)";
        return (fail, $"composite at {w} px: normals unit length (|n| - 1: max {maxErr:0.0000}, 99.9th percentile {p999:0.0000}, {bad * 100:0.000} % above 0.03); {string.Join("; ", parts)}");
    }

    /// <summary>A border's step against the steps inside, at most (1: no stronger than the texture's own detail).</summary>
    public const double SeamLimit = 2.5;

    /// <summary>The height's level step across a border, at most (a fraction of the height's 5-95 % spread).</summary>
    public const double LevelLimit = 0.08;

    /// <summary>The regions' cost on the GPU: bakes (as a save reads them) with 0, 2 and 4 regions at 1024 and 2048 px; a cheap change in a region in the preview.</summary>
    private IEnumerator<string?> RegionCost(MaterialPipeline.Inputs inputs, int up)
    {
        var lines = new List<string>();
        foreach (int target in new[] { 1024, 2048 })
        {
            int bw = target, bh = (int)Math.Round((double)target * _texture.Height / _texture.Width);
            var row = new List<string>();
            foreach (int n in new[] { 0, 2, 4 })
            {
                var ins = inputs with { Regions = n == 0 ? null : CheckRegions(n, _texture.Width, _texture.Height, up) };
                var times = new List<double>();
                int passes = 0;
                for (int k = 0; k < 3; k++)
                {
                    var b = new GpuBake(this, ins, bw, bh, (double)bw / _texture.Width);
                    passes = b.Passes;
                    while (!b.Done) yield return null;
                    times.Add(b.Finish(recipe: inputs.Recipe).Ms);
                    yield return null;
                }
                row.Add($"{n} regions {times.Skip(1).Min():0} ms ({passes} passes)");
            }
            lines.Add($"{target} px: {string.Join(", ", row)}");
        }
        // the preview with two regions: a cheap change in the first (its height band) draws its passes and the composite after it
        var pipe = new GpuPipeline(this);
        var regs = CheckRegions(2, _texture.Width, _texture.Height, up);
        var cheap = new List<double>();
        var draws = new List<int>();
        for (int i = 0; i < 7; i++)
        {
            var rec = (PbrRecipe)regs[0].Recipe.Duplicate();
            rec.HeightBand3 = i % 2 == 0 ? 0.8f : 0.5f;
            var r2 = new List<RegionInput> { regs[0] with { Recipe = rec }, regs[1] };
            var t0 = Stopwatch.StartNew();
            pipe.Update(inputs with { Regions = r2 }, 1024);
            int d = pipe.Backend.LastDraws;
            while (!pipe.Backend.Drawn) yield return null;
            _ = pipe.Backend.Pixel(pipe.Outputs!.AlbedoStats ?? pipe.Outputs.Orm);
            if (i > 0)
            {
                cheap.Add(t0.Elapsed.TotalMilliseconds);
                draws.Add(d);
            }
            while (!pipe.Settled) { pipe.Tick(); yield return null; }
        }
        lines.Add($"preview with 2 regions at 1024 px: a cheap change in a region (its height band) {Median(cheap):0} ms ({Median(draws.Select(x => (double)x)):0} of {pipe.Backend.Count} passes drawn)");
        pipe.Backend.Clear();
        pipe.Backend.QueueFree();
        Say($"regions on the GPU: bakes as a save reads them, {string.Join("; ", lines)}");
    }
}
