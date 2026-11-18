using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GdFfx.Remaster.Texturing;

namespace GdFfx.Remaster.Materialize;

/// <summary>
/// An image of the pipeline: a pass's output or an input (the source, an imported map). Its
/// <see cref="Sig"/> names what made it (the kernel, its settings, its inputs' signatures), so
/// a pass whose signature is unchanged is not run again.
/// </summary>
public abstract class Tex
{
    public required string Slot { get; init; }
    public required string Sig { get; init; }
    public required int W { get; init; }
    public required int H { get; init; }
    /// <summary>Float (true) or 8-bit (false) storage: an 8-bit pass's values are quantised to 1/255 as the GPU stores them.</summary>
    public required bool Hdr { get; init; }
    /// <summary>The channels that matter (r, rg, rgb, rgba: 1 to 4); the others read as 0 (alpha 1).</summary>
    public required int Channels { get; init; }
}

/// <summary>A pass's settings, in a fixed order: floats, ints, bools, colours (RGBA) and float arrays.</summary>
public sealed class Uni : List<(string Name, object Value)>
{
    public Uni Add(string name, object value)
    {
        base.Add((name, value));
        return this;
    }

    public float F(string name) => Convert.ToSingle(Get(name), CultureInfo.InvariantCulture);
    public int I(string name) => Convert.ToInt32(Get(name), CultureInfo.InvariantCulture);
    public bool B(string name) => Get(name) is bool b ? b : Convert.ToSingle(Get(name), CultureInfo.InvariantCulture) != 0;
    public System.Numerics.Vector4 V(string name) => (System.Numerics.Vector4)Get(name);
    public float[] A(string name) => (float[])Get(name);

    public object Get(string name)
    {
        foreach (var (n, v) in this)
            if (n == name)
                return v;
        throw new KeyNotFoundException($"no setting {name}");
    }

    public void Text(StringBuilder sb)
    {
        foreach (var (n, v) in this)
        {
            sb.Append(n).Append('=');
            switch (v)
            {
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case float[] a: sb.Append(string.Join(",", a.Select(x => x.ToString("R", CultureInfo.InvariantCulture)))); break;
                case System.Numerics.Vector4 c: sb.Append(c.X.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(c.Y.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',').Append(c.Z.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(c.W.ToString("R", CultureInfo.InvariantCulture)); break;
                default: sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            }
            sb.Append(';');
        }
    }
}

/// <summary>
/// Where the pipeline's passes run (docs/remaster-materials.md, "Speed"): on the CPU
/// (<see cref="CpuBackend"/>: the headless runs and the reference) or as fragment-shader passes
/// on the GPU (<c>GpuBackend</c>). Both run the same kernels (each written twice, held together
/// by the parity check) over the same graph (<see cref="MaterializeGraph"/>).
/// </summary>
public interface IMaterializeBackend
{
    /// <summary>An input image (8-bit RGBA, sRGB colour or data as stored); <paramref name="key"/> names its content.</summary>
    Tex Input(string key, RgbaImage image);

    /// <summary>
    /// The pass <paramref name="slot"/> (a stable name): <paramref name="kernel"/> over
    /// <paramref name="inputs"/> (t0, t1, ...) with <paramref name="uni"/>, a
    /// <paramref name="w"/>x<paramref name="h"/> image with <paramref name="channels"/> channels
    /// that matter (the CPU stores only those). <paramref name="hold"/>: not run yet (a
    /// progressive pass whose turn has not come).
    /// </summary>
    Tex Pass(string slot, string kernel, int w, int h, bool hdr, int channels, bool wrap, Tex?[] inputs, Uni uni, bool hold = false);
}

/// <summary>The maps a build gives (each step's map, the ORM pack, internal ones) and the colour check.</summary>
public sealed class MaterializeOutputs
{
    public readonly Dictionary<MaterialStep, Tex> Maps = new();
    public Tex Orm = null!;
    /// <summary>The isolated colour mask (the 2D view), when one was asked for.</summary>
    public Tex? Mask;
    /// <summary>1x1: the mean OKLab lightness (r) and chroma (g) of the source and of the albedo (null: the albedo is not generated).</summary>
    public Tex? SourceStats, AlbedoStats;
    /// <summary>The occlusion's progressive parts: how many there are, how many are in.</summary>
    public int AoChunks, AoDone;
    /// <summary>Maps besides the steps' own (the detail's albedo and normal, <see cref="MaterialPipeline.DetailAlbedo"/>), each at its own size.</summary>
    public readonly Dictionary<string, Tex> Aux = new();
}

/// <summary>What a build is asked for.</summary>
/// <param name="AoDone">The preview's progressive occlusion: how many chunks of four directions are in (the rest held); -1: the bake, every direction in one pass.</param>
/// <param name="Isolate">The colour sample to show as a mask: (step, sample 1-3; the metal's and the mask steps' colour: 1).</param>
public sealed record BuildRequest(int W, int H, double Scale, int AoDone = -1, (MaterialStep Step, int Sample)? Isolate = null);

/// <summary>
/// The material workbench's graph (docs/remaster-materials.md, "The steps", "Speed"; phase 2):
/// every map as passes of small kernels over images, in one order for both backends. The
/// expensive passes (blurred bands, blurs of every radius) are kept apart from the cheap ones
/// (the combining passes, whose weights, contrasts, biases and gains are their settings), so a
/// cheap change runs only the combining pass and what follows it.
/// <para>Blurs are a Gaussian pyramid: the image reduced by halves (a 1 3 3 1 kernel) to the
/// level where the radius is 2 to 4 of its pixels, a separable Gaussian there (integer taps),
/// read back bilinearly by whoever uses it: large radii cost little at any size. Bands are
/// such blurs of one image at radii doubling from half a classic texel to 16 (and a wide one at
/// 32 to measure detail against); their pyramid is shared.</para>
/// </summary>
public sealed class MaterializeGraph
{
    /// <summary>The bands' radii (classic texels), fine to coarse; band 0 is the image itself.</summary>
    public static readonly float[] BandSigma = [0f, 0.5f, 1f, 2f, 4f, 8f, 16f];

    /// <summary>The wide average the height's bands are measured against (classic texels).</summary>
    public const float WideSigma = 32f;

    /// <summary>The height's bands' sum is scaled by this before the final contrast (so contrast 1 gives a usable range).</summary>
    public const float HeightBandGain = 2.5f;

    /// <summary>Directions per progressive chunk of the occlusion.</summary>
    public const int AoChunk = 4;

    /// <summary>Steps along each direction of the occlusion.</summary>
    public const int AoSteps = 12;

    private readonly IMaterializeBackend _b;
    private readonly PbrRecipe _r;
    private readonly BuildRequest _q;
    private readonly bool _wrap;
    private readonly float _s;
    private readonly int _w, _h;
    private readonly Dictionary<string, Tex> _levels = new();
    private readonly string _key;
    private readonly RgbaImage _original;
    private readonly IReadOnlyDictionary<string, RgbaImage> _imports;
    private Tex? _dummy, _flat;

    private MaterializeGraph(IMaterializeBackend b, string key, RgbaImage original, IReadOnlyDictionary<string, RgbaImage> imports, PbrRecipe r, BuildRequest q)
    {
        (_b, _key, _original, _imports, _r, _q) = (b, key, original, imports, r, q);
        (_w, _h, _s, _wrap) = (q.W, q.H, (float)q.Scale, r.Wrap);
    }

    /// <summary>Builds the graph over <paramref name="b"/> (the CPU runs passes when they are read; the GPU when it commits).</summary>
    public static MaterializeOutputs Build(IMaterializeBackend b, string key, RgbaImage original, IReadOnlyDictionary<string, RgbaImage> imports, PbrRecipe r, BuildRequest q) =>
        new MaterializeGraph(b, key, original, imports, r, q).Run();

    // ---- helpers ------------------------------------------------------------------------------------

    private Tex P(string slot, string kernel, int w, int h, bool hdr, int ch, Tex?[] ins, Uni? u = null, bool hold = false) =>
        _b.Pass(slot, kernel, w, h, hdr, ch, _wrap, ins, u ?? new Uni(), hold);

    private Tex F(string slot, string kernel, int ch, Tex?[] ins, Uni? u = null) => P(slot, kernel, _w, _h, true, ch, ins, u);

    /// <summary>An 8-bit map at the working size (what is saved).</summary>
    private Tex M(string slot, string kernel, Tex?[] ins, Uni? u = null) => P(slot, kernel, _w, _h, false, 4, ins, u);

    private Tex Dummy => _dummy ??= _b.Input("dummy", new RgbaImage(1, 1, [0, 0, 0, 255]));
    private Tex Flat => _flat ??= _b.Input("flat-normal", new RgbaImage(1, 1, [128, 128, 255, 255]));

    private static Uni U() => new();

    private static System.Numerics.Vector4 C(Godot.Color c) => new(c.R, c.G, c.B, c.A);

    /// <summary>The size of level <paramref name="j"/> of an image of <paramref name="w"/>x<paramref name="h"/> (halves, rounded up).</summary>
    public static (int W, int H) LevelSize(int w, int h, int j)
    {
        for (int i = 0; i < j; i++)
            (w, h) = (Math.Max(1, (w + 1) / 2), Math.Max(1, (h + 1) / 2));
        return (w, h);
    }

    /// <summary>Level <paramref name="j"/> of <paramref name="x"/>'s pyramid (shared by every blur of it).</summary>
    private Tex Level(Tex x, int j)
    {
        if (j == 0)
            return x;
        var key = $"{x.Slot}/r{j}";
        if (_levels.TryGetValue(key, out var t))
            return t;
        var below = Level(x, j - 1);
        var (w, h) = LevelSize(below.W, below.H, 1);
        t = P(key, "reduce", w, h, true, Channels(x), [below]);
        _levels[key] = t;
        return t;
    }

    private static int Channels(Tex x) => x.Channels;

    private static Tex Ch(Tex t, int ch) => t;

    /// <summary>
    /// <paramref name="x"/> blurred by a Gaussian of <paramref name="sigma"/> pixels of the
    /// working size (the pyramid's level where it is 2 to 4 of that level's pixels, a separable
    /// Gaussian there): an image at that level, read bilinearly. Under half a pixel: x itself.
    /// </summary>
    public Tex Blur(string slot, Tex x, float sigma)
    {
        // x's own pixels may be a level already (a blur of a blur)
        float sx = sigma * x.W / _w;
        if (sx < 0.5f)
            return x;
        int j = sx < 2 ? 0 : (int)MathF.Floor(MathF.Log2(sx / 2));
        // never below 2 pixels
        while (j > 0 && Math.Min(LevelSize(x.W, x.H, j).W, LevelSize(x.W, x.H, j).H) < 2)
            j--;
        var l = Level(x, j);
        float rel = sx / (1 << j);
        float sp = MathF.Sqrt(MathF.Max(0, rel * rel - (j > 0 ? 0.25f : 0)));
        if (sp < 0.3f)
            return l;
        int taps = Math.Clamp((int)MathF.Ceiling(2.5f * sp), 1, 8);
        int ch = Channels(x);
        var hp = P($"{slot}/h", "gauss", l.W, l.H, true, ch, [l], U().Add("dir", 0).Add("sigma", sp).Add("taps", taps));
        var vp = P($"{slot}/v", "gauss", l.W, l.H, true, ch, [hp], U().Add("dir", 1).Add("sigma", sp).Add("taps", taps));
        return Ch(vp, ch);
    }

    /// <summary>The weighted mean of <paramref name="x"/>'s RGB (weights in alpha), as a 1x1 image (rgb: the mean, a: the mean weight).</summary>
    private Tex WMean(string slot, Tex x)
    {
        var t = x;
        int i = 0;
        while (t.W > 1 || t.H > 1)
            t = P($"{slot}/m{i++}", "wmean8", (t.W + 7) / 8, (t.H + 7) / 8, true, 4, [t]);
        return t;
    }

    private RgbaImage? Import(MaterialStep s)
    {
        var file = MaterialPipeline.FileOf(_r, s);
        return file.Length > 0 && _imports.TryGetValue(file, out var img) ? img : null;
    }

    /// <summary>An imported map at the working size (Lanczos on the CPU, once per size).</summary>
    private Tex ImportTex(MaterialStep s, RgbaImage img)
    {
        var file = MaterialPipeline.FileOf(_r, s);
        var colour = s is MaterialStep.Source or MaterialStep.Albedo or MaterialStep.Emission;
        var key = $"import:{file}:{img.Width}x{img.Height}:{_w}x{_h}";
        var sized = img.Width == _w && img.Height == _h ? img : ImportCache.Resized(key, img, _w, _h, colour);
        var t = _b.Input(key, sized);
        // the seam tool moves every map the same way (an imported map too), so they stay aligned
        return _seamH != null && s != MaterialStep.Source ? Seam($"seam/{s}", t, s is MaterialStep.Normal or MaterialStep.BentNormal) : t;
    }

    // ---- the seam tool (docs/remaster-materials.md, "Source"; R2b phase 4) ----------------------------

    /// <summary>The seam tool's height (the source's brightness, smoothed), and after the horizontal overlap (null: the tool is off).</summary>
    private Tex? _seamH, _seamHH;

    /// <summary>The source through the seam tool (when on); its height kept for the imported maps.</summary>
    private Tex SeamSource(Tex src)
    {
        if (_r.SeamMode == SeamMode.Off)
            return src;
        var lum = Ch(F("seam/lum", "lum", 1, [src]), 1);
        _seamH = Blur("seam/hb", lum, _r.SeamHeightBlur * _s);
        if (_r.SeamMode == SeamMode.Overlap)
            _seamHH = P("seam/hh", "seam", _w, _h, true, 1, [_seamH, _seamH], SeamUni(0, false));
        return Seam("src", src, false);
    }

    private Uni SeamUni(int dir, bool normal)
    {
        float o = MathF.Max(2, MathF.Round(Math.Clamp(_r.SeamOverlap, 0.02f, 0.45f) * (dir == 0 ? _w : _h)));
        return U().Add("dir", dir).Add("o", o).Add("k", MathF.Max(0, _r.SeamHeight)).Add("s", MathF.Max(0.01f, _r.SeamSoftness)).Add("normal", normal);
    }

    /// <summary>
    /// <paramref name="x"/> through the seam tool: the overlap (horizontally, then vertically: each
    /// pass the tile shortened by the overlap, its two ends blended where the taller feature wins,
    /// stretched back to the size) or the scatter (stamps in a staggered grid, rotated, composited
    /// by height under a soft centre mask; normals turned with their stamp). The same height drives
    /// every map, so they stay aligned.
    /// </summary>
    private Tex Seam(string slot, Tex x, bool normal)
    {
        if (_r.SeamMode == SeamMode.Scatter)
        {
            int grid = Math.Clamp(_r.ScatterGrid / 2 * 2, 2, 16);
            var u = U().Add("grid", grid).Add("scale", Math.Clamp(_r.ScatterScale, 0.5f, 4f)).Add("rot", _r.ScatterRotation * MathF.PI / 180f)
                .Add("rrot", Math.Clamp(_r.ScatterRandomRotation, 0, 180) * MathF.PI / 180f).Add("jitter", Math.Clamp(_r.ScatterJitter, 0, 1))
                .Add("seed", Math.Max(0, _r.ScatterSeed)).Add("soft", Math.Clamp(_r.ScatterSoftness, 0.05f, 1f)).Add("k", MathF.Max(0, _r.ScatterHeight)).Add("normal", normal);
            return P($"{slot}/sc", "scatter", _w, _h, true, x.Channels, [x, _seamH], u);
        }
        var h = P($"{slot}/sh", "seam", _w, _h, true, x.Channels, [x, _seamH], SeamUni(0, normal));
        return P($"{slot}/sv", "seam", _w, _h, true, x.Channels, [h, _seamHH], SeamUni(1, normal));
    }

    private Tex Const(string slot, bool hdr, float r, float g, float b, float a = 1) =>
        P(slot, "const", _w, _h, hdr, 4, [], U().Add("value", new System.Numerics.Vector4(r, g, b, a)));

    private Uni Mask(Uni u, string prefix)
    {
        float F(string p) => (float)MaterialPipeline.Prop(prefix + p).GetValue(_r)!;
        return u.Add("mh", F("MaskHue")).Add("ms", F("MaskSat")).Add("ml", F("MaskLum")).Add("lo", F("MaskLow")).Add("hi", F("MaskHigh"));
    }

    // ---- the steps --------------------------------------------------------------------------------

    private MaterializeOutputs Run()
    {
        var o = new MaterializeOutputs();
        var src = Source();
        o.Maps[MaterialStep.Source] = src;
        var srcLum = Ch(F("lum/src", "lum", 1, [src]), 1);

        // albedo
        var albedo = Albedo(src, srcLum, o);
        o.Maps[MaterialStep.Albedo] = albedo;

        // height
        var height = Height(src, albedo);
        o.Maps[MaterialStep.Height] = height;

        // normal
        var normal = Normal(height, srcLum);
        o.Maps[MaterialStep.Normal] = normal;

        // the albedo's luminance and its fine high-pass, shared by metallic and roughness
        var lumA = Ch(F("lum/alb", "lum", 1, [albedo]), 1);
        var metal = Metallic(albedo, lumA);
        o.Maps[MaterialStep.Metallic] = metal;
        var rough = Roughness(albedo, lumA, metal, _normalF ?? normal);
        o.Maps[MaterialStep.Roughness] = rough;
        var ao = Occlusion(height, _normalF ?? normal, o);
        o.Maps[MaterialStep.Occlusion] = ao;
        o.Maps[MaterialStep.Emission] = Emission(albedo);
        o.Orm = M("orm", "orm", [ao, rough, metal]);

        // the maps added with "+ map" (R2b phase 4)
        foreach (var s in MaterialPipeline.MaskSteps)
            if (MaterialPipeline.On(_r, s))
                o.Maps[s] = MaskMap(s, albedo, height, _normalF ?? normal);
        if (_r.AnisotropyOn)
            o.Maps[MaterialStep.Anisotropy] = Anisotropy(lumA);
        if (_r.TransmittanceOn)
            o.Maps[MaterialStep.Transmittance] = Transmittance(albedo, height, lumA);
        if (_r.DetailOn)
        {
            var (ak, nk) = MaterialPipeline.DetailKeys(_r);
            o.Aux[MaterialPipeline.DetailAlbedo] = DetailAux("detail/albedo", ak, new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 0));
            o.Aux[MaterialPipeline.DetailNormal] = DetailAux("detail/normal", nk, new System.Numerics.Vector4(0.5f, 0.5f, 1, 1));
        }

        if (_q.Isolate is { } iso)
        {
            bool maskStep = MaterialPipeline.MaskSteps.Contains(iso.Step);
            var colour = iso.Step switch
            {
                MaterialStep.Height => _r.HeightFrom == HeightInput.Albedo ? albedo : src,
                MaterialStep.Metallic => Blur("m/cb", albedo, _r.MetallicBlur * _s),
                _ when maskStep => Blur($"x/{MaterialPipeline.Role(iso.Step)}/cb", albedo, (float)MaterialPipeline.Prop($"{iso.Step}Blur").GetValue(_r)! * _s),
                _ => albedo,
            };
            var (sample, prefix) = iso.Step switch
            {
                MaterialStep.Height => ((Godot.Color)MaterialPipeline.Prop($"HeightSample{iso.Sample}").GetValue(_r)!, "Height"),
                MaterialStep.Metallic => (_r.MetallicColour, "Metallic"),
                _ when maskStep => ((Godot.Color)MaterialPipeline.Prop($"{iso.Step}Colour").GetValue(_r)!, iso.Step.ToString()),
                _ => ((Godot.Color)MaterialPipeline.Prop($"RoughnessSample{Math.Clamp(iso.Sample, 1, 3)}").GetValue(_r)!, "Roughness"),
            };
            o.Mask = M("mask", "mask_view", [colour], Mask(U().Add("smp", C(sample)), prefix));
        }
        return o;
    }

    private Tex Source()
    {
        var mode = MaterialPipeline.ModeOf(_r, MaterialStep.Source);
        var img = mode == StepMode.Import && Import(MaterialStep.Source) is { } own ? own : _original;
        var inKey = mode == StepMode.Import ? $"source-import:{_r.SourceFile}:{img.Width}x{img.Height}" : $"orig:{_key}:{img.Width}x{img.Height}";
        // an upscale made outside the graph (ComfyUI's, R2b phase 5): the texture at the upscaled
        // size, named by the import "upscaled:<key>|x<factor>|<content>"; resized to the size asked
        if (mode != StepMode.Import && _r.Upscaler == "comfyui" && Math.Clamp(_r.Upscale, 1, 4) > 1)
        {
            var prefix = $"upscaled:{_key}|x{Math.Clamp(_r.Upscale, 1, 4)}|";
            foreach (var (k, up) in _imports)
                if (k.StartsWith(prefix, StringComparison.Ordinal))
                {
                    (img, inKey) = (up, k);
                    break;
                }
        }
        var orig = _b.Input(inKey, img);
        if (img.Width == _w && img.Height == _h)
            return SeamSource(F("src", "copy", 4, [orig]));
        var (lh, lv) = (LanczosUni(img.Width, _w, 0), LanczosUni(img.Height, _h, 1));
        var hpass = P("src/h", "lanczos", _w, img.Height, true, 4, [orig], lh.Add("mode", 0));
        return SeamSource(F("src", "lanczos", 4, [hpass], lv.Add("mode", 1)));
    }

    private static Uni LanczosUni(int from, int to, int dir)
    {
        double scale = (double)from / to;
        double width = Math.Max(1, scale);
        double support = Lanczos.Lobes * width;
        int taps = (int)Math.Ceiling(support * 2) + 1;
        return U().Add("dir", dir).Add("scale", (float)scale).Add("width", (float)width).Add("support", (float)support).Add("taps", taps);
    }

    private Tex Albedo(Tex src, Tex srcLum, MaterializeOutputs o)
    {
        var r = _r;
        o.SourceStats = WMean("stats/src", F("stats/src/lc", "oklab_lc", 4, [src]));
        switch (MaterialPipeline.ModeOf(r, MaterialStep.Albedo))
        {
            case StepMode.Import when Import(MaterialStep.Albedo) is { } img:
                return M("albedo", "copy", [ImportTex(MaterialStep.Albedo, img)]);
            case StepMode.Import or StepMode.Skip:
                return M("albedo", "copy", [src]);
        }
        float sigma = MathF.Max(0.5f, r.DelightRadius * _s);
        float shading = Math.Clamp(r.Delight, 0, 1), ek = Math.Clamp(r.AlbedoEdgeKeep, 0, 1);
        float occ = Math.Clamp(r.AlbedoOcclusion, 0, 1), cav = Math.Clamp(r.AlbedoCavity, 0, 1);
        float hi = Math.Clamp(r.AlbedoHighlights, 0, 1), dark = Math.Clamp(r.AlbedoDarkSpots, 0, 1);
        float flatten = Math.Clamp(r.AlbedoColourCast, 0, 1), neutral = Math.Clamp(r.AlbedoNeutralise, 0, 1);

        var ay = Ch(F("a/y", "alb_y", 1, [src]), 1);
        var achr = F("a/chr", "alb_chr", 4, [src]);
        var chrMean = WMean("a/chrmean", achr);
        Tex? m = null, mab = null, aoA = null, hA = null, aroundH = null;
        if (shading > 0)
        {
            m = Blur("a/m", ay, sigma);
            if (ek > 0)
            {
                var res = Ch(F("a/res", "resid2", 1, [ay, m]), 1);
                var variance = Blur("a/var", res, sigma);
                var ab = Ch(F("a/ab", "alb_ab", 2, [m, variance], U().Add("eps", MathF.Pow(10, -2 * ek))), 2);
                mab = Blur("a/mab", ab, sigma);
            }
        }
        if (occ > 0 || cav > 0)
        {
            hA = Blur("a/h", srcLum, 1f * _s);
            if (occ > 0)
                aoA = Ch(F("a/ao", "ao", 1, [hA, Flat], U().Add("radius", 6f * _s).Add("depth", 16f * _s).Add("dirs", 8).Add("first", 0).Add("count", 8)
                    .Add("nshare", 0f).Add("bent", false)), 1);
            if (cav > 0)
                aroundH = Blur("a/hc", hA, MathF.Max(0.5f, 2f * _s));
        }
        var o1 = Ch(F("a/o1", "alb_o1", 1, [ay, m ?? Dummy, mab ?? Dummy, aoA ?? Dummy, hA ?? Dummy, aroundH ?? Dummy],
            U().Add("shading", shading).Add("ek", mab != null ? ek : 0f).Add("occ", aoA != null ? occ : 0f).Add("cav", aroundH != null ? cav : 0f)), 1);
        Tex? aroundO = hi > 0 || dark > 0 ? Blur("a/oa", o1, MathF.Max(1, sigma / 4)) : null;
        var o2 = F("a/o2", "alb_o2", 4, [o1, aroundO ?? Dummy, ay, src],
            U().Add("hi", aroundO != null ? hi : 0f).Add("dark", aroundO != null ? dark : 0f).Add("knee", MathF.Log(1 + 0.25f)));
        var shift = WMean("a/shift", o2);
        Tex? chrBlur = flatten > 0 ? Blur("a/cb", achr, sigma) : null;
        var pre = F("a/pre", "albedo", 4, [src, o2, shift, chrMean, chrBlur ?? Dummy], U().Add("flatten", chrBlur != null ? flatten : 0f).Add("neutral", neutral));
        var preStats = WMean("stats/pre", F("stats/pre/lc", "oklab_lc", 4, [pre]));
        var albedo = M("albedo", "colour_match", [pre, o.SourceStats, preStats], U().Add("match", Math.Clamp(r.AlbedoMatchClassic, 0, 1)));
        o.AlbedoStats = WMean("stats/alb", F("stats/alb/lc", "oklab_lc", 4, [albedo]));
        return albedo;
    }

    private Tex Height(Tex src, Tex albedo)
    {
        var r = _r;
        switch (MaterialPipeline.ModeOf(r, MaterialStep.Height))
        {
            case StepMode.Import when Import(MaterialStep.Height) is { } img:
                return F("height", "copy", 4, [ImportTex(MaterialStep.Height, img)]);
            case StepMode.Import or StepMode.Skip:
                return F("height", "const", 4, [], U().Add("value", new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1)));
        }
        var input = r.HeightFrom == HeightInput.Albedo ? albedo : src;
        var u = U();
        for (int i = 1; i <= 3; i++)
            u.Add($"s{i}", C((Godot.Color)MaterialPipeline.Prop($"HeightSample{i}").GetValue(r)!)).Add($"v{i}", (float)MaterialPipeline.Prop($"HeightSampleValue{i}").GetValue(r)!);
        u.Add("blend", r.HeightSampleBlend);
        var g = Ch(F("h/g", "h_grey", 1, [input], Mask(u, "Height")), 1);
        var g0 = Blur("h/b0", g, r.HeightBlur * _s);
        var bands = new Tex?[8];
        bands[0] = g0;
        for (int k = 1; k < 7; k++)
            bands[k] = Blur($"h/b{k}", g0, BandSigma[k] * _s);
        bands[7] = Blur("h/wide", g0, WideSigma * _s);
        var w = new float[7];
        var c = new float[7];
        for (int k = 0; k < 7; k++)
        {
            w[k] = (float)MaterialPipeline.Prop($"HeightBand{k}").GetValue(r)!;
            c[k] = (float)MaterialPipeline.Prop($"HeightBandContrast{k}").GetValue(r)!;
        }
        return F("height", "h_combine", 4, bands, U().Add("bw", w).Add("bc", c).Add("gain_amp", HeightBandGain).Add("invert", r.HeightInvert)
            .Add("contrast", r.HeightContrast).Add("bias", r.HeightBias).Add("gain", r.HeightGain));
    }

    private Tex Normal(Tex height, Tex srcLum)
    {
        var r = _r;
        switch (MaterialPipeline.ModeOf(r, MaterialStep.Normal))
        {
            case StepMode.Import when Import(MaterialStep.Normal) is { } img:
                return M("normal", "copy", [ImportTex(MaterialStep.Normal, img)]);
            case StepMode.Import or StepMode.Skip:
                return M("normal", "const", [], U().Add("value", new System.Numerics.Vector4(0.5f, 0.5f, 1f, 1f)));
        }
        var hb = Blur("n/hb", height, r.NormalBlur * _s);
        float shape = Math.Clamp(r.NormalShape, 0, 1);
        Tex? lb = shape > 0 ? Blur("n/lb", srcLum, MathF.Max(0.5f, r.NormalShapeRadius * _s)) : null;
        bool scharr = r.NormalFilter == GradientFilter.Scharr;
        float k = r.NormalStrength * _s / (scharr ? 32f : 8f) * (r.NormalInvert ? -1 : 1);
        // the painted light's direction in the image (x right, y down), from an angle counted anticlockwise from the right
        float a = r.NormalLightAngle * MathF.PI / 180f;
        var s0 = Ch(F("n/s", "n_slope", 2, [hb, lb != null ? srcLum : Dummy, lb ?? Dummy],
            U().Add("k", k).Add("scharr", scharr).Add("shape", lb != null ? shape : 0f).Add("lx", MathF.Cos(a)).Add("ly", -MathF.Sin(a))
                .Add("shape_k", r.NormalStrength)), 2);
        var bands = new Tex?[7];
        bands[0] = s0;
        for (int i = 1; i < 7; i++)
            bands[i] = Blur($"n/b{i}", s0, BandSigma[i] * _s);
        var w = new float[7];
        for (int i = 0; i < 7; i++)
            w[i] = (float)MaterialPipeline.Prop($"NormalBand{i}").GetValue(r)!;
        // made in floats (the curvature and the occlusion read these: no 8-bit steps amplified), saved as 8 bits
        _normalF = F("n/f", "n_combine", 4, bands, U().Add("bw", w).Add("ang", Math.Clamp(r.NormalAngularity, 0, 1)).Add("contrast", r.NormalContrast));
        return M("normal", "copy", [_normalF]);
    }

    /// <summary>The normal in floats (when generated), else the map itself.</summary>
    private Tex? _normalF;

    private Tex Metallic(Tex albedo, Tex lumA)
    {
        var r = _r;
        switch (MaterialPipeline.ModeOf(r, MaterialStep.Metallic))
        {
            case StepMode.Import when Import(MaterialStep.Metallic) is { } img:
                return M("metallic", "copy", [ImportTex(MaterialStep.Metallic, img)]);
            case StepMode.Import or StepMode.Skip:
                float v = Math.Clamp(r.Metallic, 0, 1);
                return M("metallic", "const", [], U().Add("value", new System.Numerics.Vector4(v, v, v, 1)));
        }
        var cb = Blur("m/cb", albedo, r.MetallicBlur * _s);
        Tex? hp = r.MetallicOverlay != 0 ? Blur("hp", lumA, MathF.Max(0.5f, 1f * _s)) : null;
        var u = Mask(U().Add("value", Math.Clamp(r.Metallic, 0, 1)).Add("colour_mode", r.MetallicColour.A > 0).Add("smp", C(r.MetallicColour))
            .Add("threshold", r.MetallicThreshold).Add("softness", r.MetallicSoftness).Add("invert", r.MetallicInvert)
            .Add("contrast", r.MetallicContrast).Add("bias", r.MetallicBias).Add("overlay", hp != null ? r.MetallicOverlay : 0f), "Metallic");
        return M("metallic", "metal", [cb, lumA, hp ?? Dummy], u);
    }

    private Tex Roughness(Tex albedo, Tex lumA, Tex metal, Tex normal)
    {
        var r = _r;
        switch (MaterialPipeline.ModeOf(r, MaterialStep.Roughness))
        {
            case StepMode.Import when Import(MaterialStep.Roughness) is { } img:
                return M("roughness", "copy", [ImportTex(MaterialStep.Roughness, img)]);
            case StepMode.Import or StepMode.Skip:
                float v = Math.Clamp(r.RoughnessBase, 0, 1);
                return M("roughness", "const", [], U().Add("value", new System.Numerics.Vector4(v, v, v, 1)));
        }
        float dr = MathF.Max(0.5f, r.RoughnessDetailRadius * _s);
        var mean = Blur("r/m", lumA, dr);
        var res = Ch(F("r/res", "resid2", 1, [lumA, mean]), 1);
        var variance = Blur("r/v", res, dr);
        Tex? hp = r.RoughnessOverlay != 0 ? Blur("hp", lumA, MathF.Max(0.5f, 1f * _s)) : null;
        Tex? curv = r.RoughnessEdgeWear > 0 ? Ch(F("curv", "curv", 1, [normal], U().Add("s", _s)), 1) : null;
        var u = U().Add("base", r.RoughnessBase).Add("range", r.RoughnessRange).Add("dw", Math.Clamp(r.RoughnessDetail, 0, 1)).Add("invert", r.RoughnessInvert);
        for (int i = 1; i <= 3; i++)
            u.Add($"s{i}", C((Godot.Color)MaterialPipeline.Prop($"RoughnessSample{i}").GetValue(r)!)).Add($"v{i}", (float)MaterialPipeline.Prop($"RoughnessSampleValue{i}").GetValue(r)!);
        u.Add("metal_r", r.RoughnessMetal).Add("contrast", r.RoughnessContrast).Add("bias", r.RoughnessBias)
            .Add("overlay", hp != null ? r.RoughnessOverlay : 0f).Add("wear", curv != null ? r.RoughnessEdgeWear : 0f);
        return M("roughness", "rough", [albedo, lumA, variance, hp ?? Dummy, metal, curv ?? Dummy], Mask(u, "Roughness"));
    }

    private Tex Occlusion(Tex height, Tex normal, MaterializeOutputs o)
    {
        var r = _r;
        bool aoGen = MaterialPipeline.ModeOf(r, MaterialStep.Occlusion) == StepMode.Generate;
        bool bentOn = r.BentNormalOn;
        bool bentGen = bentOn && MaterialPipeline.ModeOf(r, MaterialStep.BentNormal) == StepMode.Generate;
        // one sampling of the directions for both (the bent normal: the mean open direction)
        Tex? sum = aoGen || bentGen ? AoSampling(height, normal, bentGen, o) : null;
        if (bentOn)
            o.Maps[MaterialStep.BentNormal] = MaterialPipeline.ModeOf(r, MaterialStep.BentNormal) switch
            {
                StepMode.Generate => M("bent_normal", "bent_out", [sum!, normal],
                    U().Add("strength", Math.Clamp(r.BentNormalStrength, 0, 1)).Add("from_normals", Math.Clamp(r.BentNormalFromNormals, 0, 1))),
                StepMode.Import when Import(MaterialStep.BentNormal) is { } bimg => M("bent_normal", "copy", [ImportTex(MaterialStep.BentNormal, bimg)]),
                _ => M("bent_normal", "const", [], U().Add("value", new System.Numerics.Vector4(0.5f, 0.5f, 1f, 1f))),
            };
        switch (MaterialPipeline.ModeOf(r, MaterialStep.Occlusion))
        {
            case StepMode.Import when Import(MaterialStep.Occlusion) is { } img:
                return M("occlusion", "copy", [ImportTex(MaterialStep.Occlusion, img)]);
            case StepMode.Import or StepMode.Skip:
                return M("occlusion", "const", [], U().Add("value", System.Numerics.Vector4.One));
        }
        return M("occlusion", "ao_out", [sum!], U().Add("strength", r.OcclusionStrength).Add("power", MathF.Max(0.01f, r.OcclusionPower)).Add("bias", r.OcclusionBias));
    }

    /// <summary>
    /// The occlusion's directions over the height: per pixel the horizon's mean (r) and, with
    /// <paramref name="bent"/>, the mean open direction (gba: the cosine-weighted mean of each
    /// direction's open arc). In the preview in chunks of four directions (the rest held), in the
    /// bake all at once.
    /// </summary>
    private Tex AoSampling(Tex height, Tex normal, bool bent, MaterializeOutputs o)
    {
        var r = _r;
        int ch = bent ? 4 : 1;
        int dirs = Math.Clamp(r.OcclusionSamples, 1, 64);
        Uni Ao(int first, int count) => U().Add("radius", MathF.Max(1, r.OcclusionRadius * _s)).Add("depth", r.OcclusionDepth * _s).Add("dirs", dirs)
            .Add("first", first).Add("count", count).Add("nshare", Math.Clamp(r.OcclusionNormalShare, 0, 1)).Add("bent", bent);
        if (_q.AoDone < 0)
        {
            (o.AoChunks, o.AoDone) = (1, 1);
            return Ch(F("ao", "ao", ch, [height, normal], Ao(0, dirs)), ch);
        }
        int chunks = (dirs + AoChunk - 1) / AoChunk;
        int done = Math.Clamp(_q.AoDone, 1, chunks);
        var parts = new Tex?[8];
        var counts = new float[8];
        // at most eight chunks: more directions go into the chunks evenly
        int n = Math.Min(chunks, 8);
        int per = (dirs + n - 1) / n;
        done = Math.Min(done, n);
        for (int c = 0; c < n; c++)
        {
            int first = c * per, count = Math.Max(0, Math.Min(per, dirs - first));
            counts[c] = count;
            parts[c] = Ch(F($"ao/c{c}", "ao", ch, [height, normal], Ao(first, count), hold: c >= done), ch);
        }
        for (int c = n; c < 8; c++)
            parts[c] = Dummy;
        (o.AoChunks, o.AoDone) = (n, done);
        return Ch(F("ao/sum", "ao_sum", ch, parts, U().Add("counts", counts).Add("done", done)), ch);
    }

    // ---- the maps added with "+ map" (R2b phase 4) ----------------------------------------------------

    /// <summary>
    /// A mask step's map (rim, clearcoat, subsurface, backlight, refraction, the detail's mask):
    /// the value everywhere, or a ramp over the (pre-blurred) brightness, a colour mask, a ramp
    /// over the height or the normals' tilt; inverted, times the value. R the mask (G 1: the rim's
    /// tint and the coat's roughness at the material's values); the backlight: the mask times its
    /// tint (linear RGB: Godot adds it to the material's backlight colour).
    /// </summary>
    private Tex MaskMap(MaterialStep s, Tex albedo, Tex height, Tex normalF)
    {
        var slot = MaterialPipeline.Role(s);
        string p = s.ToString();
        object V(string n) => MaterialPipeline.Prop(p + n).GetValue(_r)!;
        var mode = MaterialPipeline.ModeOf(_r, s);
        if (mode == StepMode.Import)
            return Import(s) is { } img ? M(slot, "copy", [ImportTex(s, img)]) : M(slot, "const", [], U().Add("value", System.Numerics.Vector4.One));
        var src = mode == StepMode.Skip ? MaskSource.Constant : (MaskSource)V("Source");
        var colour = (Godot.Color)V("Colour");
        if (src == MaskSource.Colour && colour.A <= 0)
            src = MaskSource.Constant;
        bool rgb = s == MaterialStep.Backlight;
        Tex? cb = src is MaskSource.Colour or MaskSource.Brightness ? Blur($"x/{slot}/cb", albedo, (float)V("Blur") * _s) : null;
        var tint = rgb ? _r.BacklightTint : Godot.Colors.White;
        var u = Mask(U().Add("src", (int)src).Add("smp", C(colour)).Add("threshold", (float)V("Threshold")).Add("softness", (float)V("Softness"))
            .Add("invert", (bool)V("Invert")).Add("value", Math.Clamp((float)V("Value"), 0, 1)).Add("rgb", rgb).Add("tint", C(tint))
            .Add("from_albedo", rgb ? Math.Clamp(_r.BacklightFromAlbedo, 0, 1) : 0f), p);
        return M(slot, "xmask", [cb ?? Dummy, src == MaskSource.Height ? height : Dummy, src == MaskSource.Tilt ? normalF : Dummy, rgb ? albedo : Dummy], u);
    }

    /// <summary>
    /// The anisotropy's flowmap (Godot's: RG the direction, x right and y up, A the strength): the
    /// grain from the albedo's brightness (its gradient's structure tensor, smoothed: the dominant
    /// orientation, the highlight across the grain unless along it; the strength from how clear the
    /// grain is), or a fixed angle.
    /// </summary>
    private Tex Anisotropy(Tex lumA)
    {
        var r = _r;
        var mode = MaterialPipeline.ModeOf(r, MaterialStep.Anisotropy);
        if (mode == StepMode.Import)
            return Import(MaterialStep.Anisotropy) is { } img ? M("flowmap", "copy", [ImportTex(MaterialStep.Anisotropy, img)])
                : M("flowmap", "const", [], U().Add("value", new System.Numerics.Vector4(1, 0.5f, 0, 1)));
        bool grain = mode == StepMode.Generate && r.AnisotropySource == FlowSource.Grain;
        var u = U().Add("mode", grain ? 0 : 1).Add("angle", -r.AnisotropyAngle * MathF.PI / 180f).Add("along", r.AnisotropyAlongGrain)
            .Add("coh", grain ? Math.Clamp(r.AnisotropyCoherence, 0, 1) : 0f);
        if (!grain)
            return M("flowmap", "aniso", [Dummy], u);
        var st = F("an/st", "st", 3, [lumA]);
        var sb = Blur("an/sb", st, MathF.Max(0.5f, r.AnisotropySmooth * _s));
        return M("flowmap", "aniso", [sb], u);
    }

    /// <summary>
    /// The transmittance (Godot multiplies its colour by it; alpha the amount): RGB the light's
    /// colour through the surface (the albedo's hue at full brightness, its saturation scaled, or a
    /// colour; linear), A the thinness (1 - the thickness from the height or brightness, or
    /// inverted), its contrast and bias.
    /// </summary>
    private Tex Transmittance(Tex albedo, Tex height, Tex lumA)
    {
        var r = _r;
        var mode = MaterialPipeline.ModeOf(r, MaterialStep.Transmittance);
        if (mode == StepMode.Import)
            return Import(MaterialStep.Transmittance) is { } img ? M("transmittance", "copy", [ImportTex(MaterialStep.Transmittance, img)])
                : M("transmittance", "const", [], U().Add("value", System.Numerics.Vector4.One));
        var thick = mode == StepMode.Skip ? ThicknessSource.Constant : r.TransmittanceThickness;
        var u = U().Add("tint", (int)r.TransmittanceTint).Add("colour", C(r.TransmittanceColour)).Add("sat", MathF.Max(0, r.TransmittanceSaturation))
            .Add("thick", (int)thick).Add("invert", r.TransmittanceInvert).Add("contrast", r.TransmittanceContrast).Add("bias", r.TransmittanceBias);
        return M("transmittance", "transmit", [albedo, thick == ThicknessSource.Height ? height : Dummy, thick == ThicknessSource.Brightness ? lumA : Dummy], u);
    }

    /// <summary>The detail's albedo or normal (a file, or another texture's made map) at its own size (at most 2048 px); none: <paramref name="none"/>, which changes nothing.</summary>
    private Tex DetailAux(string slot, string key, System.Numerics.Vector4 none)
    {
        if (key.Length == 0 || !_imports.TryGetValue(key, out var img))
            return P(slot, "const", 4, 4, false, 4, [], U().Add("value", none));
        var (w, h) = Lanczos.FitWithin(img.Width, img.Height, 2048);
        var sized = w == img.Width && h == img.Height ? img : ImportCache.Resized($"detail:{key}:{w}x{h}", img, w, h, slot.EndsWith("albedo", StringComparison.Ordinal));
        return P(slot, "copy", w, h, false, 4, [_b.Input($"detail:{key}:{w}x{h}", sized)]);
    }

    private Tex F(string slot, string kernel, int ch, Tex?[] ins, Uni u, bool hold) => P(slot, kernel, _w, _h, true, ch, ins, u, hold);

    private Tex Emission(Tex albedo)
    {
        var r = _r;
        switch (MaterialPipeline.ModeOf(r, MaterialStep.Emission))
        {
            case StepMode.Import when Import(MaterialStep.Emission) is { } img:
                return M("emission", "copy", [ImportTex(MaterialStep.Emission, img)]);
            case StepMode.Import or StepMode.Skip:
                return M("emission", "const", [], U().Add("value", new System.Numerics.Vector4(0, 0, 0, 1)));
        }
        return M("emission", "emis", [albedo], U().Add("threshold", Math.Min(r.EmissionThreshold, 1f)).Add("softness", r.EmissionSoftness));
    }
}

/// <summary>Imported maps resized to a working size (Lanczos, CPU), kept by file and size.</summary>
public static class ImportCache
{
    private static readonly Dictionary<string, RgbaImage> Cache = new();

    public static RgbaImage Resized(string key, RgbaImage img, int w, int h, bool colour)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit))
                return hit;
            if (Cache.Count > 16)
                Cache.Clear();
            return Cache[key] = Lanczos.Resize(img, w, h, colour ? ResizeOptions.Colour : ResizeOptions.Data);
        }
    }
}

/// <summary>A 64-bit FNV-1a hash as text: the passes' signatures stay short.</summary>
public static class SigHash
{
    public static string Of(string text)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in text)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>A pass's signature: its kernel, size, storage, wrap, settings and inputs' signatures.</summary>
    public static string Pass(string kernel, int w, int h, bool hdr, bool wrap, Tex?[] inputs, Uni uni)
    {
        var sb = new StringBuilder(kernel).Append('|').Append(w).Append('x').Append(h).Append(hdr ? "f" : "b").Append(wrap ? "w" : "c").Append('|');
        uni.Text(sb);
        foreach (var t in inputs)
            sb.Append('<').Append(t?.Sig ?? "-").Append('>');
        return Of(sb.ToString());
    }
}
