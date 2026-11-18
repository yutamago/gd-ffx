using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using GdFfx.Remaster.Texturing;
using static GdFfx.Remaster.Materialize.Kernels;

namespace GdFfx.Remaster.Materialize;

/// <summary>A CPU image: <see cref="Tex.Channels"/> floats per pixel, made when first read.</summary>
public sealed class CpuTex : Tex
{
    public float[]? Data;
    internal string Kernel = "";
    public Tex?[] Inputs { get; internal set; } = [];
    public Uni Uni { get; internal set; } = new();
    public bool Wrap { get; internal set; }
    /// <summary>Not to be made yet (a progressive part whose turn has not come).</summary>
    internal bool Hold;

    public Vector4 Fet(int x, int y)
    {
        x = Idx(x, W, Wrap);
        y = Idx(y, H, Wrap);
        return At((y * W + x) * Channels);
    }

    public Vector4 At(int i)
    {
        var d = Data!;
        return Channels switch
        {
            1 => new Vector4(d[i], 0, 0, 1),
            2 => new Vector4(d[i], d[i + 1], 0, 1),
            3 => new Vector4(d[i], d[i + 1], d[i + 2], 1),
            _ => new Vector4(d[i], d[i + 1], d[i + 2], d[i + 3]),
        };
    }

    /// <summary>Bilinear at <paramref name="u"/>, <paramref name="v"/> (0-1 over the image; texel centres at (i + 0.5) / size), wrapped or clamped as the GPU's sampler.</summary>
    public Vector4 Bil(float u, float v)
    {
        if (!Wrap)
        {
            float hx = 0.5f / W, hy = 0.5f / H;
            u = Math.Clamp(u, hx, 1 - hx);
            v = Math.Clamp(v, hy, 1 - hy);
        }
        float px = u * W - 0.5f, py = v * H - 0.5f;
        float fx0 = MathF.Floor(px), fy0 = MathF.Floor(py);
        int x0 = (int)fx0, y0 = (int)fy0;
        float fx = px - fx0, fy = py - fy0;
        var a = Fet(x0, y0);
        var b = Fet(x0 + 1, y0);
        var c = Fet(x0, y0 + 1);
        var d = Fet(x0 + 1, y0 + 1);
        return Vector4.Lerp(Vector4.Lerp(a, b, fx), Vector4.Lerp(c, d, fx), fy);
    }

    /// <summary>Channel 0 at a texel (wrapped or clamped).</summary>
    public float FetR(int x, int y) => Data![(Idx(y, H, Wrap) * W + Idx(x, W, Wrap)) * Channels];

    /// <summary>Channel 0 bilinearly (as <see cref="Bil"/>).</summary>
    public float BilR(float u, float v)
    {
        if (!Wrap)
        {
            float hx = 0.5f / W, hy = 0.5f / H;
            u = Math.Clamp(u, hx, 1 - hx);
            v = Math.Clamp(v, hy, 1 - hy);
        }
        float px = u * W - 0.5f, py = v * H - 0.5f;
        float fx0 = MathF.Floor(px), fy0 = MathF.Floor(py);
        int x0 = (int)fx0, y0 = (int)fy0;
        float fx = px - fx0, fy = py - fy0;
        int xa = Idx(x0, W, Wrap), xb = Idx(x0 + 1, W, Wrap), ya = Idx(y0, H, Wrap) * W, yb = Idx(y0 + 1, H, Wrap) * W;
        var d = Data!;
        int c = Channels;
        float a = d[(ya + xa) * c], b = d[(ya + xb) * c], e = d[(yb + xa) * c], f = d[(yb + xb) * c];
        float top = a + (b - a) * fx, bottom = e + (f - e) * fx;
        return top + (bottom - top) * fy;
    }

    public static int Idx(int i, int n, bool wrap)
    {
        if ((uint)i < (uint)n) return i;
        if (!wrap) return i < 0 ? 0 : n - 1;
        i %= n;
        return i < 0 ? i + n : i;
    }

    /// <summary>As 8-bit RGBA (what the GPU's 8-bit target holds; float channels clamped and rounded).</summary>
    public RgbaImage ToRgba()
    {
        var o = new byte[W * H * 4];
        for (int p = 0; p < W * H; p++)
        {
            var v = At(p * Channels);
            if (Channels == 1)
                v = new Vector4(v.X, v.X, v.X, 1);
            o[p * 4] = B(v.X);
            o[p * 4 + 1] = B(v.Y);
            o[p * 4 + 2] = B(v.Z);
            o[p * 4 + 3] = B(v.W);
        }
        return new RgbaImage(W, H, o);
    }

    public static byte B(float v) => v <= 0 ? (byte)0 : v >= 1 ? (byte)255 : (byte)(v * 255 + 0.5f);

    /// <summary>Channel 0 as floats.</summary>
    public float[] Red()
    {
        var o = new float[W * H];
        for (int p = 0; p < o.Length; p++)
            o[p] = Data![p * Channels];
        return o;
    }
}

/// <summary>
/// The pipeline on the CPU (docs/remaster-materials.md, "Speed": the headless runs and the
/// reference): each pass a C# kernel, the same math as the GPU's (<see cref="GpuKernels"/>), run
/// when its output is first read; a pass is kept per slot with its signature, so a build with
/// only cheap settings changed runs only the passes whose signature changed (and what reads
/// them). One build at a time (not thread-safe); the kernels run rows in parallel.
/// </summary>
public sealed class CpuBackend : IMaterializeBackend
{
    private readonly Dictionary<string, CpuTex> _slots = new();
    private readonly Dictionary<string, CpuTex> _inputs = new();

    /// <summary>The passes by slot (the checks compare them with the GPU's).</summary>
    public IReadOnlyDictionary<string, CpuTex> Slots => _slots;

    /// <summary>Checked between passes and bands of rows.</summary>
    public CancellationToken Cancel { get; set; }

    /// <summary>Milliseconds per kernel since made (the checks).</summary>
    public Dictionary<string, double> KernelMs { get; } = new();

    /// <summary>Passes run since made (the checks: a cheap change runs few).</summary>
    public int Runs { get; private set; }

    public Tex Input(string key, RgbaImage image)
    {
        if (_inputs.TryGetValue(key, out var t))
            return t;
        var d = new float[image.Width * image.Height * 4];
        for (int i = 0; i < d.Length; i++)
            d[i] = image.Rgba[i] / 255f;
        t = new CpuTex { Slot = "in:" + key, Sig = SigHash.Of("in:" + key), W = image.Width, H = image.Height, Hdr = false, Channels = 4, Data = d };
        if (_inputs.Count > 8)
            _inputs.Clear();
        return _inputs[key] = t;
    }

    public Tex Pass(string slot, string kernel, int w, int h, bool hdr, int channels, bool wrap, Tex?[] inputs, Uni uni, bool hold = false)
    {
        var sig = SigHash.Pass(kernel, w, h, hdr, wrap, inputs, uni);
        if (_slots.TryGetValue(slot, out var t) && t.Sig == sig)
        {
            t.Hold = hold;
            return t;
        }
        t = new CpuTex { Slot = slot, Sig = sig, W = w, H = h, Hdr = hdr, Channels = channels, Kernel = kernel, Inputs = inputs, Uni = uni, Wrap = wrap, Hold = hold };
        _slots[slot] = t;
        return t;
    }

    /// <summary>Makes <paramref name="t"/> (and what it reads) if not made yet.</summary>
    public CpuTex Get(Tex t)
    {
        var c = (CpuTex)t;
        if (c.Data != null)
            return c;
        foreach (var i in c.Inputs)
            if (i is CpuTex { Hold: false } ic)
                Get(ic);
        Cancel.ThrowIfCancellationRequested();
        c.Data = new float[c.W * c.H * c.Channels];
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Run(c);
        Runs++;
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        KernelMs[c.Kernel] = KernelMs.GetValueOrDefault(c.Kernel) + ms;
        if (!c.Hdr)
            for (int i = 0; i < c.Data.Length; i++)
                c.Data[i] = CpuTex.B(c.Data[i]) / 255f;
        return c;
    }

    // ---- the kernels ----------------------------------------------------------------------------

    private void Each(CpuTex o, Func<int, int, Vector4> f)
    {
        int w = o.W, ch = o.Channels;
        var d = o.Data!;
        RowBands.For(o.H, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < w; x++)
                {
                    var v = f(x, y);
                    int i = (y * w + x) * ch;
                    d[i] = v.X;
                    if (ch > 1) d[i + 1] = v.Y;
                    if (ch > 2) d[i + 2] = v.Z;
                    if (ch > 3) d[i + 3] = v.W;
                }
        }, null, Cancel);
    }

    private static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);

    private static Vector3 SafeNormal(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitZ;

    /// <summary>An encoded normal (0-1) made unit length again (after a blend).</summary>
    private static Vector4 Renormal(Vector4 c)
    {
        var n = SafeNormal(new Vector3(c.X * 2 - 1, c.Y * 2 - 1, c.Z * 2 - 1));
        return new Vector4(n.X * 0.5f + 0.5f, n.Y * 0.5f + 0.5f, n.Z * 0.5f + 0.5f, c.W);
    }

    /// <summary>The tilt of an encoded normal (0: facing out; 1: 30 degrees or more).</summary>
    private static float Tilt(Vector4 n)
    {
        float x = n.X * 2 - 1, y = n.Y * 2 - 1;
        return MathF.Min(1, 2 * MathF.Sqrt(x * x + y * y));
    }

    private void Run(CpuTex o)
    {
        var u = o.Uni;
        CpuTex In(int i) => (CpuTex)o.Inputs[i]!;
        int W = o.W, H = o.H;
        float Ux(int x) => (x + 0.5f) / W;
        float Uy(int y) => (y + 0.5f) / H;
        switch (o.Kernel)
        {
            case "const":
            {
                var v = u.V("value");
                Each(o, (_, _) => v);
                break;
            }
            case "copy":
            {
                var t = In(0);
                Each(o, (x, y) => t.Fet(x, y));
                break;
            }
            case "lanczos":
            {
                var t = In(0);
                int dir = u.I("dir"), taps = u.I("taps"), mode = u.I("mode");
                float scale = u.F("scale"), width = u.F("width"), support = u.F("support");
                int n = dir == 0 ? t.W : t.H;
                Each(o, (x, y) =>
                {
                    int oi = dir == 0 ? x : y;
                    float center = (oi + 0.5f) * scale - 0.5f;
                    int first = (int)MathF.Floor(center - support) + 1;
                    Vector4 sum = Vector4.Zero;
                    float ws = 0;
                    for (int k = 0; k < taps; k++)
                    {
                        float wk = (float)Kernels.Lanczos((first + k - center) / width);
                        int i = Math.Clamp(first + k, 0, n - 1);
                        var v = dir == 0 ? t.At((y * t.W + i) * t.Channels) : t.At((i * t.W + x) * t.Channels);
                        if (mode == 0)
                            v = new Vector4(Lin(v.X) * v.W, Lin(v.Y) * v.W, Lin(v.Z) * v.W, v.W);
                        sum += wk * v;
                        ws += wk;
                    }
                    var r = ws != 0 ? sum / ws : Vector4.Zero;
                    if (mode == 1)
                    {
                        float a = Math.Clamp(r.W, 0, 1), m = a > 1f / 1024 ? 1 / a : 0;
                        r = new Vector4(Enc(Math.Clamp(r.X * m, 0, 1)), Enc(Math.Clamp(r.Y * m, 0, 1)), Enc(Math.Clamp(r.Z * m, 0, 1)), a);
                    }
                    return r;
                });
                break;
            }
            case "lum":
            {
                var t = In(0);
                Each(o, (x, y) => new Vector4(Lum(Xyz(t.Fet(x, y))), 0, 0, 1));
                break;
            }
            case "reduce":
            {
                var t = In(0);
                Span<float> kw = [1, 3, 3, 1];
                float[] k = kw.ToArray();
                if (t.Channels == 1)
                {
                    Each(o, (x, y) =>
                    {
                        float s = 0;
                        for (int j = 0; j < 4; j++)
                            for (int i = 0; i < 4; i++)
                                s += k[i] * k[j] * t.FetR(2 * x + i - 1, 2 * y + j - 1);
                        return new Vector4(s / 64f, 0, 0, 1);
                    });
                    break;
                }
                Each(o, (x, y) =>
                {
                    Vector4 s = Vector4.Zero;
                    for (int j = 0; j < 4; j++)
                        for (int i = 0; i < 4; i++)
                            s += k[i] * k[j] * t.Fet(2 * x + i - 1, 2 * y + j - 1);
                    return s / 64f;
                });
                break;
            }
            case "gauss":
            {
                var t = In(0);
                int dir = u.I("dir"), n = u.I("taps");
                float sigma = u.F("sigma");
                var wt = new float[2 * n + 1];
                float tot = 0;
                for (int k = -n; k <= n; k++)
                    tot += wt[k + n] = MathF.Exp(-(k * k) / (2 * sigma * sigma));
                if (t.Channels == 1)
                {
                    Each(o, (x, y) =>
                    {
                        float s = 0;
                        for (int k = -n; k <= n; k++)
                            s += wt[k + n] * (dir == 0 ? t.FetR(x + k, y) : t.FetR(x, y + k));
                        return new Vector4(s / tot, 0, 0, 1);
                    });
                    break;
                }
                Each(o, (x, y) =>
                {
                    Vector4 s = Vector4.Zero;
                    for (int k = -n; k <= n; k++)
                        s += wt[k + n] * (dir == 0 ? t.Fet(x + k, y) : t.Fet(x, y + k));
                    return s / tot;
                });
                break;
            }
            case "wmean8":
            {
                var t = In(0);
                Each(o, (x, y) =>
                {
                    Vector3 acc = Vector3.Zero;
                    float wa = 0;
                    int cnt = 0;
                    for (int j = 0; j < 8; j++)
                        for (int i = 0; i < 8; i++)
                        {
                            int qx = x * 8 + i, qy = y * 8 + j;
                            if (qx >= t.W || qy >= t.H)
                                continue;
                            var v = t.At((qy * t.W + qx) * t.Channels);
                            acc += Xyz(v) * v.W;
                            wa += v.W;
                            cnt++;
                        }
                    var m = wa > 0 ? acc / wa : Vector3.Zero;
                    return new Vector4(m, cnt > 0 ? wa / cnt : 0);
                });
                break;
            }
            case "alb_y":
            {
                var t = In(0);
                Each(o, (x, y) => new Vector4(MathF.Log(MathF.Max(1e-3f, LumLin(Lin(Xyz(t.Fet(x, y)))))), 0, 0, 1));
                break;
            }
            case "alb_chr":
            {
                var t = In(0);
                Each(o, (x, y) =>
                {
                    var c = t.Fet(x, y);
                    var l = Lin(Xyz(c));
                    float lum = MathF.Max(1e-3f, LumLin(l));
                    return new Vector4(l / lum, 0.02f + c.W);
                });
                break;
            }
            case "resid2":
            {
                var (t, m) = (In(0), In(1));
                Each(o, (x, y) =>
                {
                    float d = t.Fet(x, y).X - m.Bil(Ux(x), Uy(y)).X;
                    return new Vector4(d * d, 0, 0, 1);
                });
                break;
            }
            case "alb_ab":
            {
                var (m, v) = (In(0), In(1));
                float eps = u.F("eps");
                Each(o, (x, y) =>
                {
                    float mm = m.Bil(Ux(x), Uy(y)).X, var = MathF.Max(0, v.Bil(Ux(x), Uy(y)).X);
                    float a = var / (var + eps);
                    return new Vector4(a, mm - a * mm, 0, 1);
                });
                break;
            }
            case "ao":
            {
                var (ht, nt) = (In(0), In(1));
                float radius = u.F("radius"), depth = u.F("depth"), nshare = u.F("nshare");
                int dirs = u.I("dirs"), first = u.I("first"), count = u.I("count");
                bool bent = u.B("bent");
                bool flatN = nt.W == 1 && nt.H == 1;
                // each direction's angle without the pixel's own turn (added by a rotation per pixel)
                var bc = new float[Math.Max(0, count)];
                var bs = new float[Math.Max(0, count)];
                for (int i = 0; i < count; i++)
                {
                    float a = 6.2831853f * (first + i) / dirs;
                    (bc[i], bs[i]) = (MathF.Cos(a), MathF.Sin(a));
                }
                Each(o, (x, y) =>
                {
                    float h0 = ht.BilR(Ux(x), Uy(y));
                    var n = flatN ? new Vector4(0.5f, 0.5f, 1, 1) : nt.Fet(x, y);
                    float nx = n.X * 2 - 1, ny = -(n.Y * 2 - 1), nz = n.Z * 2 - 1;
                    float r1 = Rnd(x, y, 1), r2 = Rnd(x, y, 2);
                    float turn = 6.2831853f * r1 / dirs, ct = MathF.Cos(turn), st = MathF.Sin(turn);
                    float acc = 0, bx = 0, by = 0, bz = 0;
                    for (int i = 0; i < count; i++)
                    {
                        float dx = bc[i] * ct - bs[i] * st, dy = bs[i] * ct + bc[i] * st;
                        float best = 0;
                        for (int j = 0; j < MaterializeGraph.AoSteps; j++)
                        {
                            float dist = MathF.Max(1, radius * (j + r2) / MaterializeGraph.AoSteps);
                            float qx = x + 0.5f + dx * dist, qy = y + 0.5f + dy * dist;
                            float hq = ht.BilR(qx / W, qy / H);
                            best = MathF.Max(best, (hq - h0) * depth / dist);
                        }
                        float oh = best / MathF.Sqrt(1 + best * best);
                        float ns = MathF.Max(0, -(nx * dx + ny * dy) / MathF.Max(0.05f, nz));
                        float on = ns / MathF.Sqrt(1 + ns * ns);
                        acc += oh + (on - oh) * nshare;
                        if (bent)
                        {
                            // the open arc above the horizon (elevation a to the zenith), cosine-weighted: its mean direction
                            float a = MathF.Atan(best), hz = (1 - oh * oh) * 0.5f;
                            bx += hz * dx;
                            by += hz * dy;
                            bz += (MathF.PI / 2 - a) * 0.5f + MathF.Sin(2 * a) * 0.25f;
                        }
                    }
                    float inv = count > 0 ? 1f / count : 0;
                    return new Vector4(acc * inv, bx * inv, by * inv, bent ? bz * inv : 1);
                });
                break;
            }
            case "alb_o1":
            {
                var (ay, m, mab, ao, hA, ar) = (In(0), In(1), In(2), In(3), In(4), In(5));
                float shading = u.F("shading"), ek = u.F("ek"), occ = u.F("occ"), cav = u.F("cav");
                Each(o, (x, y) =>
                {
                    float uu = Ux(x), vv = Uy(y);
                    float yy = ay.Fet(x, y).X, oo = yy;
                    if (shading > 0)
                    {
                        float mm = m.Bil(uu, vv).X, light = mm;
                        if (ek > 0)
                        {
                            var ab = mab.Bil(uu, vv);
                            light = mm + ek * ((ab.X * yy + ab.Y) - mm);
                        }
                        oo -= shading * light;
                    }
                    if (occ > 0)
                        oo -= occ * MathF.Log(MathF.Max(0.2f, Math.Clamp(1 - ao.Fet(x, y).X, 0, 1)));
                    if (cav > 0)
                    {
                        float d = Math.Clamp((ar.Bil(uu, vv).X - hA.Bil(uu, vv).X) * 8, 0, 1);
                        oo -= cav * MathF.Log(1 - 0.5f * d);
                    }
                    return new Vector4(oo, 0, 0, 1);
                });
                break;
            }
            case "alb_o2":
            {
                var (o1, around, ay, src) = (In(0), In(1), In(2), In(3));
                float hi = u.F("hi"), dark = u.F("dark"), knee = u.F("knee");
                Each(o, (x, y) =>
                {
                    float oo = o1.Fet(x, y).X;
                    if (hi > 0 || dark > 0)
                    {
                        float ar = around.Bil(Ux(x), Uy(y)).X;
                        float over = oo - ar - knee;
                        if (hi > 0 && over > 0)
                            oo -= hi * (over - over / (1 + 4 * over));
                        float under = ar - oo - knee;
                        if (dark > 0 && under > 0)
                            oo += dark * (under - under / (1 + 4 * under));
                    }
                    return new Vector4(ay.Fet(x, y).X - oo, oo, 0, 0.02f + src.Fet(x, y).W);
                });
                break;
            }
            case "albedo":
            {
                var (src, o2, shiftT, chrT, cb) = (In(0), In(1), In(2), In(3), In(4));
                float flatten = u.F("flatten"), neutral = u.F("neutral");
                float shift = shiftT.Fet(0, 0).X;
                var gm = Xyz(chrT.Fet(0, 0));
                Each(o, (x, y) =>
                {
                    var c = src.Fet(x, y);
                    var l = Lin(Xyz(c));
                    var f = Vector3.One;
                    if (flatten > 0)
                    {
                        var b = Xyz(cb.Bil(Ux(x), Uy(y)));
                        f = new Vector3(MathF.Pow(gm.X / MathF.Max(1e-3f, b.X), flatten), MathF.Pow(gm.Y / MathF.Max(1e-3f, b.Y), flatten),
                            MathF.Pow(gm.Z / MathF.Max(1e-3f, b.Z), flatten));
                    }
                    if (neutral > 0)
                        f *= new Vector3(MathF.Pow(1 / MathF.Max(1e-3f, gm.X), neutral), MathF.Pow(1 / MathF.Max(1e-3f, gm.Y), neutral),
                            MathF.Pow(1 / MathF.Max(1e-3f, gm.Z), neutral));
                    var rr = l * f;
                    float ly = LumLin(rr);
                    float k = ly > 1e-6f ? MathF.Exp(o2.Fet(x, y).Y + shift) / ly : 0;
                    float mx = MathF.Max(rr.X, MathF.Max(rr.Y, rr.Z)) * k;
                    if (mx > 1) k /= mx;
                    return new Vector4(Enc(rr.X * k), Enc(rr.Y * k), Enc(rr.Z * k), c.W);
                });
                break;
            }
            case "oklab_lc":
            {
                var t = In(0);
                Each(o, (x, y) =>
                {
                    var c = t.Fet(x, y);
                    var lab = OkLab(Lin(Xyz(c)));
                    return new Vector4(lab.X, MathF.Sqrt(lab.Y * lab.Y + lab.Z * lab.Z), 0, 0.02f + c.W);
                });
                break;
            }
            case "colour_match":
            {
                var (pre, ss, ps) = (In(0), In(1), In(2));
                float match = u.F("match");
                var s = ss.Fet(0, 0);
                var p = ps.Fet(0, 0);
                float dl = match * (s.X - p.X);
                float fc = 1 + match * (Math.Clamp(s.Y / MathF.Max(1e-4f, p.Y), 0, 4) - 1);
                Each(o, (x, y) =>
                {
                    var c = pre.Fet(x, y);
                    if (match <= 0)
                        return c;
                    var lab = OkLab(Lin(Xyz(c)));
                    lab = new Vector3(lab.X + dl, lab.Y * fc, lab.Z * fc);
                    var l = FromOkLab(lab);
                    return new Vector4(Enc(Math.Clamp(l.X, 0, 1)), Enc(Math.Clamp(l.Y, 0, 1)), Enc(Math.Clamp(l.Z, 0, 1)), c.W);
                });
                break;
            }
            case "h_grey":
            {
                var t = In(0);
                var smp = new[] { u.V("s1"), u.V("s2"), u.V("s3") };
                var val = new[] { u.F("v1"), u.F("v2"), u.F("v3") };
                float blend = u.F("blend"), mh = u.F("mh"), ms = u.F("ms"), ml = u.F("ml"), lo = u.F("lo"), hi = u.F("hi");
                Each(o, (x, y) =>
                {
                    var c = Xyz(t.Fet(x, y));
                    float g = Lum(c);
                    for (int i = 0; i < 3; i++)
                        if (smp[i].W > 0)
                            g += (val[i] - g) * ColourMask(c, Xyz(smp[i]), mh, ms, ml, lo, hi) * blend;
                    return new Vector4(g, 0, 0, 1);
                });
                break;
            }
            case "h_combine":
            {
                var b = new CpuTex[8];
                for (int i = 0; i < 8; i++)
                    b[i] = In(i);
                var bw = u.A("bw");
                var bc = u.A("bc");
                float amp = u.F("gain_amp"), contrast = u.F("contrast"), bias = u.F("bias"), gain = u.F("gain");
                bool invert = u.B("invert");
                Each(o, (x, y) =>
                {
                    float uu = Ux(x), vv = Uy(y);
                    float a = b[7].BilR(uu, vv), sum = 0, ws = 0;
                    for (int k = 0; k < 7; k++)
                    {
                        sum += bw[k] * bc[k] * (b[k].BilR(uu, vv) - a);
                        ws += bw[k];
                    }
                    float h = 0.5f + amp * sum / MathF.Max(1e-4f, ws);
                    if (invert) h = 1 - h;
                    h = Math.Clamp((h - 0.5f) * contrast + 0.5f + bias, 0, 1);
                    h = Gain(h, gain);
                    return new Vector4(h, h, h, 1);
                });
                break;
            }
            case "n_slope":
            {
                var (hb, ls, lb) = (In(0), In(1), In(2));
                float k = u.F("k"), shape = u.F("shape"), lx = u.F("lx"), ly = u.F("ly"), sk = u.F("shape_k");
                bool scharr = u.B("scharr");
                float side = scharr ? 3 : 1, mid = scharr ? 10 : 2;
                Each(o, (x, y) =>
                {
                    float Hh(int dx, int dy) => hb.BilR((x + 0.5f + dx) / W, (y + 0.5f + dy) / H);
                    float gx = side * (Hh(1, -1) - Hh(-1, -1)) + mid * (Hh(1, 0) - Hh(-1, 0)) + side * (Hh(1, 1) - Hh(-1, 1));
                    float gy = side * (Hh(-1, 1) - Hh(-1, -1)) + mid * (Hh(0, 1) - Hh(0, -1)) + side * (Hh(1, 1) - Hh(1, -1));
                    var s = new Vector2(gx * k, gy * k);
                    if (shape > 0)
                    {
                        float hp = ls.Fet(x, y).X - lb.Bil(Ux(x), Uy(y)).X;
                        var d = new Vector2(lx, ly);
                        float along = -hp * sk, cur = Vector2.Dot(s, d);
                        s += shape * (along - cur) * d;
                    }
                    return new Vector4(s.X, s.Y, 0, 1);
                });
                break;
            }
            case "n_combine":
            {
                var b = new CpuTex[7];
                for (int i = 0; i < 7; i++)
                    b[i] = In(i);
                var bw = u.A("bw");
                float ang = u.F("ang"), contrast = u.F("contrast");
                float wsum = 0;
                foreach (var x in bw) wsum += x;
                Each(o, (x, y) =>
                {
                    float uu = Ux(x), vv = Uy(y);
                    Vector2 s = Vector2.Zero;
                    if (wsum < 1e-4f)
                    {
                        var v0 = b[0].Bil(uu, vv);
                        s = new Vector2(v0.X, v0.Y);
                    }
                    else
                    {
                        for (int k = 0; k < 7; k++)
                        {
                            var v = b[k].Bil(uu, vv);
                            s += bw[k] * new Vector2(v.X, v.Y);
                        }
                        s /= wsum;
                    }
                    float len = s.Length();
                    if (ang > 0 && len > 1e-6f)
                        s = Vector2.Lerp(s, s / len, ang * SmoothStep(0, 0.1f, len));
                    var n = Vector3.Normalize(new Vector3(-s.X, -s.Y, 1));
                    if (contrast != 1)
                    {
                        n = new Vector3(n.X * contrast, n.Y * contrast, MathF.Pow(MathF.Max(n.Z, 0), contrast));
                        n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
                    }
                    return new Vector4(n.X * 0.5f + 0.5f, -n.Y * 0.5f + 0.5f, n.Z * 0.5f + 0.5f, 1);
                });
                break;
            }
            case "curv":
            {
                var t = In(0);
                float s = u.F("s");
                Each(o, (x, y) =>
                {
                    float div = (t.Fet(x + 1, y).X - t.Fet(x - 1, y).X) * 0.5f * 2
                        - (t.Fet(x, y + 1).Y - t.Fet(x, y - 1).Y) * 0.5f * 2;
                    return new Vector4(Math.Clamp(0.5f + div * s * 0.5f, 0, 1), 0, 0, 1);
                });
                break;
            }
            case "ao_sum":
            {
                var counts = u.A("counts");
                int done = u.I("done");
                var p = new CpuTex[8];
                for (int i = 0; i < 8; i++)
                    p[i] = In(i);
                Each(o, (x, y) =>
                {
                    Vector4 s = Vector4.Zero;
                    float n = 0;
                    for (int c = 0; c < done; c++)
                    {
                        s += p[c].Fet(x, y) * counts[c];
                        n += counts[c];
                    }
                    return n > 0 ? s / n : Vector4.Zero;
                });
                break;
            }
            case "ao_out":
            {
                var t = In(0);
                float strength = u.F("strength"), power = u.F("power"), bias = u.F("bias");
                Each(o, (x, y) =>
                {
                    float a = Math.Clamp(1 - strength * t.Fet(x, y).X, 0, 1);
                    a = Math.Clamp(MathF.Pow(a, power) + bias, 0, 1);
                    return new Vector4(a, a, a, 1);
                });
                break;
            }
            case "rough":
            {
                var (alb, lumA, variance, hp, metal, curv) = (In(0), In(1), In(2), In(3), In(4), In(5));
                float bse = u.F("base"), range = u.F("range"), dw = u.F("dw"), metalR = u.F("metal_r"), contrast = u.F("contrast"),
                    bias = u.F("bias"), overlay = u.F("overlay"), wear = u.F("wear");
                bool invert = u.B("invert");
                var smp = new[] { u.V("s1"), u.V("s2"), u.V("s3") };
                var val = new[] { u.F("v1"), u.F("v2"), u.F("v3") };
                float mh = u.F("mh"), ms = u.F("ms"), ml = u.F("ml"), lo = u.F("lo"), hi = u.F("hi");
                Each(o, (x, y) =>
                {
                    float uu = Ux(x), vv = Uy(y);
                    float l = lumA.Fet(x, y).X, var = MathF.Max(0, variance.Bil(uu, vv).X);
                    float detail = var < 1e-6f ? 0 : MathF.Min(1, MathF.Sqrt(var) * 4);
                    float t = (1 - dw) * (1 - l) + dw * detail;
                    float r = Math.Clamp(bse + range * (t - 0.5f), 0, 1);
                    if (invert) r = 1 - r;
                    var c = Xyz(alb.Fet(x, y));
                    for (int i = 0; i < 3; i++)
                        if (smp[i].W > 0)
                            r += (val[i] - r) * ColourMask(c, Xyz(smp[i]), mh, ms, ml, lo, hi);
                    r += (metalR - r) * metal.Fet(x, y).X;
                    r = (r - 0.5f) * contrast + 0.5f + bias;
                    if (overlay != 0)
                        r *= 1 + (l - hp.Bil(uu, vv).X) * overlay;
                    if (wear > 0)
                        r -= wear * MathF.Max(0, (curv.Fet(x, y).X - 0.5f) * 2);
                    r = Math.Clamp(r, 0, 1);
                    return new Vector4(r, r, r, 1);
                });
                break;
            }
            case "metal":
            {
                var (cb, lumA, hp) = (In(0), In(1), In(2));
                float value = u.F("value"), threshold = u.F("threshold"), softness = u.F("softness"), contrast = u.F("contrast"),
                    bias = u.F("bias"), overlay = u.F("overlay");
                bool colourMode = u.B("colour_mode"), invert = u.B("invert");
                var sample = Xyz(u.V("smp"));
                float mh = u.F("mh"), ms = u.F("ms"), ml = u.F("ml"), lo = u.F("lo"), hi = u.F("hi");
                Each(o, (x, y) =>
                {
                    float uu = Ux(x), vv = Uy(y);
                    var c = Xyz(cb.Bil(uu, vv));
                    float m;
                    if (colourMode) m = ColourMask(c, sample, mh, ms, ml, lo, hi);
                    else if (threshold > 1) m = 1;
                    else
                    {
                        m = Ramp(Lum(c), threshold, softness);
                        if (invert) m = 1 - m;
                    }
                    float v = m * value;
                    v = (v - 0.5f) * contrast + 0.5f + bias;
                    if (overlay != 0)
                        v *= 1 + (lumA.Fet(x, y).X - hp.Bil(uu, vv).X) * overlay;
                    v = Math.Clamp(v, 0, 1);
                    return new Vector4(v, v, v, 1);
                });
                break;
            }
            case "orm":
            {
                var (a, r, m) = (In(0), In(1), In(2));
                Each(o, (x, y) => new Vector4(a.Fet(x, y).X, r.Fet(x, y).X, m.Fet(x, y).X, 1));
                break;
            }
            case "emis":
            {
                var t = In(0);
                float threshold = u.F("threshold"), softness = u.F("softness");
                Each(o, (x, y) =>
                {
                    var c = t.Fet(x, y);
                    float m = Ramp(MathF.Max(c.X, MathF.Max(c.Y, c.Z)), threshold, softness);
                    return new Vector4(c.X * m, c.Y * m, c.Z * m, 1);
                });
                break;
            }
            case "mask_view":
            {
                var t = In(0);
                var sample = u.V("smp");
                float mh = u.F("mh"), ms = u.F("ms"), ml = u.F("ml"), lo = u.F("lo"), hi = u.F("hi");
                Each(o, (x, y) =>
                {
                    float m = sample.W > 0 ? ColourMask(Xyz(t.Bil(Ux(x), Uy(y))), Xyz(sample), mh, ms, ml, lo, hi) : 0;
                    return new Vector4(m, m, m, 1);
                });
                break;
            }
            case "bent_out":
            {
                var (sum, nt) = (In(0), In(1));
                float strength = u.F("strength"), fromN = u.F("from_normals");
                bool flatN = nt.W == 1 && nt.H == 1;
                Each(o, (x, y) =>
                {
                    var v = sum.Fet(x, y);
                    var b = new Vector3(v.Y, v.Z, v.W);
                    float l = b.Length();
                    b = l > 1e-6f ? b / l : Vector3.UnitZ;
                    var n = flatN ? new Vector4(0.5f, 0.5f, 1, 1) : nt.Fet(x, y);
                    var nn = new Vector3(n.X * 2 - 1, -(n.Y * 2 - 1), n.Z * 2 - 1);
                    b = SafeNormal(b + fromN * (nn - Vector3.UnitZ));
                    b = SafeNormal(Vector3.Lerp(Vector3.UnitZ, b, strength));
                    return new Vector4(b.X * 0.5f + 0.5f, -b.Y * 0.5f + 0.5f, b.Z * 0.5f + 0.5f, 1);
                });
                break;
            }
            case "seam":
            {
                var (t, hgt) = (In(0), In(1));
                int dir = u.I("dir");
                float ov = u.F("o"), k = u.F("k"), sm = u.F("s");
                bool normal = u.B("normal");
                Each(o, (x, y) =>
                {
                    // near the edges the copy shifted by half the size (its own seam in the middle, under
                    // the original) takes over; across the band the taller of the two wins
                    int n = dir == 0 ? W : H, i = dir == 0 ? x : y, j = (i + n / 2) % n;
                    float dist = MathF.Min(i + 0.5f, n - i - 0.5f);
                    float ramp = Math.Clamp(2 * (dist - 1) / MathF.Max(1, ov - 1) - 1, -1, 1);
                    if (ramp >= 1)
                        return t.Fet(x, y);
                    var a = t.Fet(x, y);
                    var b = dir == 0 ? t.Fet(j, y) : t.Fet(x, j);
                    float ha = hgt.Fet(x, y).X, hb = (dir == 0 ? hgt.Fet(j, y) : hgt.Fet(x, j)).X;
                    float wa = SmoothStep(-sm, sm, (ha - hb) * k + ramp * (sm + k));
                    var c = Vector4.Lerp(b, a, wa);
                    return normal ? Renormal(c) : c;
                });
                break;
            }
            case "scatter":
            {
                var (t, hgt) = (In(0), In(1));
                int n = u.I("grid");
                float scale = u.F("scale"), rot = u.F("rot"), rrot = u.F("rrot"), jitter = u.F("jitter"), soft = u.F("soft"), k = u.F("k");
                uint salt = (uint)u.I("seed") * 8u;
                bool normal = u.B("normal");
                float cell = 1f / n, size = scale * cell;
                // the stamps' sources stay inside the image (no wrap inside a stamp)
                float lo = size * 0.75f < 0.5f ? size * 0.75f : 0.5f, span = 1 - 2 * lo;
                Each(o, (x, y) =>
                {
                    float qx = Ux(x), qy = Uy(y);
                    int jq = (int)MathF.Floor(qy * n);
                    Vector4 acc = Vector4.Zero;
                    float wsum = 0;
                    for (int dj = -2; dj <= 2; dj++)
                    {
                        int j = jq + dj;
                        float off = ((j + 4 * n) & 1) == 1 ? 0.5f * cell : 0;
                        int iq = (int)MathF.Floor((qx - off) * n);
                        for (int di = -2; di <= 2; di++)
                        {
                            int i = iq + di;
                            int wi = (i + 4 * n) % n, wj = (j + 4 * n) % n;
                            float cx = (i + 0.5f) * cell + off + (Rnd(wi, wj, salt + 1) - 0.5f) * jitter * cell;
                            float cy = (j + 0.5f) * cell + (Rnd(wi, wj, salt + 2) - 0.5f) * jitter * cell;
                            float th = rot + rrot * (2 * Rnd(wi, wj, salt + 3) - 1);
                            float cs = MathF.Cos(th), sn = MathF.Sin(th);
                            float dx = qx - cx, dy = qy - cy;
                            float lx = (cs * dx + sn * dy) / size, ly = (-sn * dx + cs * dy) / size;
                            float rr = 2 * MathF.Sqrt(lx * lx + ly * ly);
                            if (rr >= 1.5f)
                                continue;
                            float su = lo + span * Rnd(wi, wj, salt + 4) + lx * size, sv = lo + span * Rnd(wi, wj, salt + 5) + ly * size;
                            float m = 1 - SmoothStep(1 - soft, 1, rr);
                            float w = (m + 1e-4f * (1.5f - rr)) * MathF.Exp(k * (hgt.BilR(su, sv) - 1));
                            var c = t.Bil(su, sv);
                            if (normal)
                            {
                                // the normal turned with its stamp (x right, y up in the map; the image's y down)
                                float nx = c.X * 2 - 1, ny = -(c.Y * 2 - 1);
                                c = new Vector4((cs * nx - sn * ny) * 0.5f + 0.5f, -(sn * nx + cs * ny) * 0.5f + 0.5f, c.Z, c.W);
                            }
                            acc += w * c;
                            wsum += w;
                        }
                    }
                    if (wsum < 1e-20f)
                        return t.Fet(x, y);
                    var r = acc / wsum;
                    return normal ? Renormal(r) : r;
                });
                break;
            }
            case "xmask":
            {
                var (cb, ht, nt, alb) = (In(0), In(1), In(2), In(3));
                int src = u.I("src");
                var smp = Xyz(u.V("smp"));
                float thr = u.F("threshold"), soft = u.F("softness"), value = u.F("value"), from = u.F("from_albedo");
                bool invert = u.B("invert"), rgb = u.B("rgb");
                var tint = Lin(Xyz(u.V("tint")));
                float mh = u.F("mh"), ms = u.F("ms"), ml = u.F("ml"), lo = u.F("lo"), hi = u.F("hi");
                Each(o, (x, y) =>
                {
                    float uu = Ux(x), vv = Uy(y);
                    float m = src switch
                    {
                        1 => Ramp(Lum(Xyz(cb.Bil(uu, vv))), thr, soft),
                        2 => ColourMask(Xyz(cb.Bil(uu, vv)), smp, mh, ms, ml, lo, hi),
                        3 => Ramp(ht.BilR(uu, vv), thr, soft),
                        4 => Ramp(Tilt(nt.Fet(x, y)), thr, soft),
                        _ => 1,
                    };
                    if (src != 0 && invert)
                        m = 1 - m;
                    m *= value;
                    if (!rgb)
                        return new Vector4(m, 1, 0, 1);
                    // the albedo's hue at full brightness (dark leaves still let their green through)
                    var al = Lin(Xyz(alb.Fet(x, y)));
                    float mx = MathF.Max(al.X, MathF.Max(al.Y, al.Z));
                    var c = Vector3.Lerp(tint, mx > 1e-4f ? al / mx : Vector3.One, from) * m;
                    return new Vector4(c, 1);
                });
                break;
            }
            case "st":
            {
                var t = In(0);
                Each(o, (x, y) =>
                {
                    float L(int dx, int dy) => t.FetR(x + dx, y + dy);
                    float gx = 2 * (L(1, -1) + 2 * L(1, 0) + L(1, 1) - L(-1, -1) - 2 * L(-1, 0) - L(-1, 1));
                    float gy = 2 * (L(-1, 1) + 2 * L(0, 1) + L(1, 1) - L(-1, -1) - 2 * L(0, -1) - L(1, -1));
                    return new Vector4(gx * gx, gy * gy, gx * gy, 1);
                });
                break;
            }
            case "aniso":
            {
                var t = In(0);
                int mode = u.I("mode");
                float angle = u.F("angle"), coh = u.F("coh");
                bool along = u.B("along");
                Each(o, (x, y) =>
                {
                    float th = angle, str = 1;
                    if (mode == 0)
                    {
                        var j = t.Bil(Ux(x), Uy(y));
                        float tr = j.X + j.Y;
                        if (tr > 1e-6f)
                        {
                            th += 0.5f * MathF.Atan2(2 * j.Z, j.X - j.Y);
                            float d = MathF.Sqrt((j.X - j.Y) * (j.X - j.Y) + 4 * j.Z * j.Z);
                            str = 1 + (d / tr - 1) * coh;
                        }
                        else
                            str = 1 - coh;
                    }
                    if (along)
                        th += MathF.PI / 2;
                    // the image's (cos, sin) as the flowmap's x right, y up; one of the two signs (the highlight is the same)
                    float fx = MathF.Cos(th), fy = -MathF.Sin(th);
                    if (fx < 0 || (fx == 0 && fy < 0))
                        (fx, fy) = (-fx, -fy);
                    return new Vector4(fx * 0.5f + 0.5f, fy * 0.5f + 0.5f, 0, Math.Clamp(str, 0, 1));
                });
                break;
            }
            case "transmit":
            {
                var (alb, ht, la) = (In(0), In(1), In(2));
                int tintMode = u.I("tint"), thick = u.I("thick");
                var colour = Lin(Xyz(u.V("colour")));
                float sat = u.F("sat"), contrast = u.F("contrast"), bias = u.F("bias");
                bool invert = u.B("invert");
                Each(o, (x, y) =>
                {
                    Vector3 tint = colour;
                    if (tintMode == 0)
                    {
                        var l = Lin(Xyz(alb.Fet(x, y)));
                        float ly = LumLin(l);
                        l = Vector3.Max(Vector3.Zero, Vector3.Lerp(new Vector3(ly), l, sat));
                        float mx = MathF.Max(l.X, MathF.Max(l.Y, l.Z));
                        tint = mx > 1e-4f ? l / mx : Vector3.One;
                    }
                    float th = thick == 0 ? ht.Fet(x, y).X : thick == 1 ? la.Fet(x, y).X : 0.5f;
                    float thin = invert ? th : 1 - th;
                    return new Vector4(tint, Math.Clamp((thin - 0.5f) * contrast + 0.5f + bias, 0, 1));
                });
                break;
            }
            default:
                throw new ArgumentException($"no kernel {o.Kernel}");
        }
    }
}
