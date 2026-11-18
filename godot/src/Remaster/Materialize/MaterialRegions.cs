using System;
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster.Texturing;

namespace GdFfx.Remaster.Materialize;

/// <summary>
/// A region of a texture as the graph takes it (docs/remaster-materials.md, "Phase 6: what there
/// is"): its settings (its profile with its exceptions; the Source step's and the material's own
/// settings are the texture's, <see cref="MaterialRegions.Shared"/>), its mask (8-bit, R the
/// coverage, at the texture's own size; <paramref name="MaskKey"/> names its content) and how far
/// its border is feathered (classic texels).
/// </summary>
public sealed record RegionInput(string Name, PbrRecipe Recipe, string MaskKey, RgbaImage Mask, float Feather);

/// <summary>
/// Regions within one texture (R2b phase 6): many FFX textures are atlases (rock, grass and wood
/// side by side), so a texture can be split into regions, each made with its own settings and
/// composited over the texture's own maps with a soft mask (in the regions' order: a later one
/// over an earlier). The helpers here are plain C# (any thread): the settings a region takes from
/// the texture, the neutral value of a map a region does not make, and the masks (UV islands
/// rasterised, a colour's mask, rectangles, polygons and brush strokes) as one byte per pixel.
/// </summary>
public static class MaterialRegions
{
    /// <summary>
    /// The settings a region takes from the texture, not from its own profile: the Source step's
    /// (the upscale, the sizes, the tiling, the seam tool: every region's maps must line up) and
    /// the material's own (one material: the parallax depth, the emission energy, the added maps'
    /// amounts).
    /// </summary>
    public static readonly string[] Shared = MaterialPipeline.Settings[MaterialStep.Source].Select(s => s.Property)
        .Concat([nameof(PbrRecipe.Upscaler), nameof(PbrRecipe.SourceMode), nameof(PbrRecipe.SourceFile)])
        .Concat(MaterialPipeline.MaterialOnly).Distinct().ToArray();

    /// <summary><paramref name="region"/>'s settings with the texture's shared ones (<see cref="Shared"/>) from <paramref name="texture"/>.</summary>
    public static PbrRecipe WithShared(PbrRecipe region, PbrRecipe texture)
    {
        var r = (PbrRecipe)region.Duplicate();
        r.Version = 3;
        foreach (var p in Shared)
            MaterialPipeline.Prop(p).SetValue(r, MaterialPipeline.Prop(p).GetValue(texture));
        return r;
    }

    /// <summary>
    /// The recipe the material is written with when regions make maps the texture's own settings do
    /// not: the texture's, with every map any of them makes on (an added map, the height, the
    /// emission), so the material has the map's slot and flag.
    /// </summary>
    public static PbrRecipe Union(PbrRecipe texture, IEnumerable<PbrRecipe> regions)
    {
        var u = (PbrRecipe)texture.Duplicate();
        u.Version = 3;
        foreach (var r in regions)
        {
            foreach (var s in MaterialPipeline.Extras)
                if (MaterialPipeline.On(r, s))
                    MaterialPipeline.Prop(MaterialPipeline.OnProp(s)).SetValue(u, true);
            if (MaterialPipeline.ModeOf(u, MaterialStep.Emission) == StepMode.Skip && MaterialPipeline.ModeOf(r, MaterialStep.Emission) != StepMode.Skip)
                u.EmissionMode = StepMode.Generate;
            if (MaterialPipeline.ModeOf(u, MaterialStep.Height) == StepMode.Skip && MaterialPipeline.ModeOf(r, MaterialStep.Height) != StepMode.Skip)
                u.HeightMode = StepMode.Generate;
        }
        return u;
    }

    /// <summary>The value of an added map where a region (or the texture) does not make it: nothing (no rim, no coat, flat, no strength), as RGBA.</summary>
    public static System.Numerics.Vector4 Neutral(MaterialStep s) => s switch
    {
        MaterialStep.BentNormal => new(0.5f, 0.5f, 1, 1),
        MaterialStep.Anisotropy => new(1, 0.5f, 0, 0),
        MaterialStep.Transmittance => new(1, 1, 1, 0),
        MaterialStep.Backlight => new(0, 0, 0, 1),
        _ => new(0, 1, 0, 1),
    };

    /// <summary>How a map is composited: 0 a blend (colour, data), 1 a normal map (blended as slopes, renormalised), 2 the height (levels matched across the border).</summary>
    public static int CompositeMode(MaterialStep s) => s switch
    {
        MaterialStep.Normal or MaterialStep.BentNormal => 1,
        MaterialStep.Height => 2,
        _ => 0,
    };

    // ---- masks (one byte per pixel) -----------------------------------------------------------------

    /// <summary>A mask as an 8-bit RGBA image (R the coverage; the graph's input).</summary>
    public static RgbaImage ToRgba(byte[] mask, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < mask.Length; i++)
        {
            px[i * 4] = mask[i];
            px[i * 4 + 1] = mask[i];
            px[i * 4 + 2] = mask[i];
            px[i * 4 + 3] = 255;
        }
        return new RgbaImage(w, h, px);
    }

    /// <summary>The R channel of an RGBA image (a mask read back).</summary>
    public static byte[] FromRgba(RgbaImage img)
    {
        var m = new byte[img.Width * img.Height];
        for (int i = 0; i < m.Length; i++)
            m[i] = img.Rgba[i * 4];
        return m;
    }

    /// <summary>A mask's content key (its pixels' hash).</summary>
    public static string Key(byte[] mask, int w, int h) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(mask))[..16].ToLowerInvariant() + $"-{w}x{h}";

    /// <summary>The share of the mask that is on (its mean coverage, 0 to 1).</summary>
    public static double Coverage(byte[] mask) => mask.Length == 0 ? 0 : mask.Sum(b => (double)b) / (255.0 * mask.Length);

    /// <summary>Fills the triangle <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/> (pixel coordinates) with <paramref name="value"/> (pixel centres inside; conservative by half a pixel so thin islands are not lost).</summary>
    public static void FillTriangle(byte[] mask, int w, int h, (float X, float Y) a, (float X, float Y) b, (float X, float Y) c, byte value = 255, bool wrap = false)
    {
        float minx = MathF.Min(a.X, MathF.Min(b.X, c.X)), maxx = MathF.Max(a.X, MathF.Max(b.X, c.X));
        float miny = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), maxy = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-9f)
            return;
        // the edges pushed out by half a pixel (a pixel touched by the triangle counts)
        float E(float px, float py, (float X, float Y) p, (float X, float Y) q)
        {
            float ex = q.X - p.X, ey = q.Y - p.Y;
            float len = MathF.Sqrt(ex * ex + ey * ey);
            float e = (ex * (py - p.Y) - ey * (px - p.X)) * MathF.Sign(area);
            return e + 0.5f * len * (MathF.Abs(ex) + MathF.Abs(ey)) / MathF.Max(1e-6f, len);
        }
        int x0 = (int)MathF.Floor(minx - 1), x1 = (int)MathF.Ceiling(maxx + 1), y0 = (int)MathF.Floor(miny - 1), y1 = (int)MathF.Ceiling(maxy + 1);
        if (!wrap)
            (x0, x1, y0, y1) = (Math.Max(0, x0), Math.Min(w - 1, x1), Math.Max(0, y0), Math.Min(h - 1, y1));
        if (x1 - x0 > 4 * w || y1 - y0 > 4 * h)
            return;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                if (E(px, py, a, b) < 0 || E(px, py, b, c) < 0 || E(px, py, c, a) < 0)
                    continue;
                int ix = ((x % w) + w) % w, iy = ((y % h) + h) % h;
                if (!wrap && (x < 0 || y < 0 || x >= w || y >= h))
                    continue;
                mask[iy * w + ix] = Math.Max(mask[iy * w + ix], value);
            }
    }

    /// <summary>A rectangle (UV, 0-1) set to <paramref name="value"/> (or cleared, <paramref name="erase"/>).</summary>
    public static void FillRect(byte[] mask, int w, int h, float u0, float v0, float u1, float v1, bool erase = false)
    {
        int x0 = (int)MathF.Floor(MathF.Min(u0, u1) * w), x1 = (int)MathF.Ceiling(MathF.Max(u0, u1) * w);
        int y0 = (int)MathF.Floor(MathF.Min(v0, v1) * h), y1 = (int)MathF.Ceiling(MathF.Max(v0, v1) * h);
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
                mask[y * w + x] = erase ? (byte)0 : (byte)255;
    }

    /// <summary>A polygon (UV points, 0-1) filled (even-odd), or cleared.</summary>
    public static void FillPolygon(byte[] mask, int w, int h, IReadOnlyList<(float U, float V)> poly, bool erase = false)
    {
        if (poly.Count < 3)
            return;
        for (int y = 0; y < h; y++)
        {
            float py = (y + 0.5f) / h;
            var xs = new List<float>();
            for (int i = 0; i < poly.Count; i++)
            {
                var (a, b) = (poly[i], poly[(i + 1) % poly.Count]);
                if ((a.V <= py) == (b.V <= py))
                    continue;
                xs.Add(a.U + (py - a.V) / (b.V - a.V) * (b.U - a.U));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int x0 = Math.Max(0, (int)MathF.Ceiling(xs[k] * w - 0.5f)), x1 = Math.Min(w - 1, (int)MathF.Floor(xs[k + 1] * w - 0.5f));
                for (int x = x0; x <= x1; x++)
                    mask[y * w + x] = erase ? (byte)0 : (byte)255;
            }
        }
    }

    /// <summary>
    /// A brush dab at <paramref name="u"/>, <paramref name="v"/> (UV), <paramref name="radius"/>
    /// pixels, soft at its edge by <paramref name="hardness"/> (1: hard): painted up (max) or erased
    /// (down), as a painting program does.
    /// </summary>
    public static void Dab(byte[] mask, int w, int h, float u, float v, float radius, float hardness, bool erase, bool wrap)
    {
        float cx = u * w, cy = v * h;
        int r = (int)MathF.Ceiling(radius + 1);
        float inner = radius * Math.Clamp(hardness, 0, 1);
        for (int y = (int)cy - r; y <= (int)cy + r; y++)
            for (int x = (int)cx - r; x <= (int)cx + r; x++)
            {
                if (!wrap && (x < 0 || y < 0 || x >= w || y >= h))
                    continue;
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy, d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > radius)
                    continue;
                float a = d <= inner ? 1 : 1 - Kernels.SmoothStep(inner, radius, d);
                byte b = (byte)Math.Clamp(a * 255 + 0.5f, 0, 255);
                int i = (((y % h) + h) % h) * w + ((x % w) + w) % w;
                mask[i] = erase ? (byte)Math.Max(0, mask[i] - b) : Math.Max(mask[i], b);
            }
    }

    /// <summary>A brush stroke from one point to the next (dabs a quarter radius apart).</summary>
    public static void Stroke(byte[] mask, int w, int h, (float U, float V) from, (float U, float V) to, float radius, float hardness, bool erase, bool wrap)
    {
        float len = MathF.Sqrt(MathF.Pow((to.U - from.U) * w, 2) + MathF.Pow((to.V - from.V) * h, 2));
        int n = Math.Max(1, (int)MathF.Ceiling(len / MathF.Max(0.5f, radius * 0.25f)));
        for (int i = 1; i <= n; i++)
        {
            float t = (float)i / n;
            Dab(mask, w, h, from.U + (to.U - from.U) * t, from.V + (to.V - from.V) * t, radius, hardness, erase, wrap);
        }
    }

    /// <summary>
    /// The pixels of <paramref name="px"/> (sRGB) that are colour <paramref name="sample"/>: the
    /// colour mask the height's and roughness's samples use (<see cref="Kernels.ColourMask"/>, hue,
    /// saturation and lightness weighted, through a soft threshold), over a copy blurred by
    /// <paramref name="blur"/> pixels (a box, twice), then opened and closed by a pixel so specks
    /// and pinholes go.
    /// </summary>
    public static byte[] ByColour(RgbaImage px, System.Numerics.Vector3 sample, float lo = 0.86f, float hi = 0.95f, int blur = 1,
        float wh = 1, float ws = 0.5f, float wl = 0.5f)
    {
        int w = px.Width, h = px.Height;
        var rgb = new float[w * h * 3];
        for (int i = 0; i < w * h; i++)
            for (int k = 0; k < 3; k++)
                rgb[i * 3 + k] = px.Rgba[i * 4 + k] / 255f;
        for (int pass = 0; pass < 2 && blur > 0; pass++)
            rgb = BoxBlur(rgb, w, h, 3, blur);
        var m = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            var c = new System.Numerics.Vector3(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
            m[i] = (byte)Math.Clamp(Kernels.ColourMask(c, sample, wh, ws, wl, lo, hi) * 255 + 0.5f, 0, 255);
        }
        // opened (specks out), then closed (pinholes filled)
        m = Morph(Morph(m, w, h, false), w, h, true);
        return Morph(Morph(m, w, h, true), w, h, false);
    }

    private static float[] BoxBlur(float[] d, int w, int h, int ch, int r)
    {
        var t = new float[d.Length];
        var o = new float[d.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < ch; k++)
                {
                    float s = 0;
                    int n = 0;
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1);
                        s += d[(y * w + xx) * ch + k];
                        n++;
                    }
                    t[(y * w + x) * ch + k] = s / n;
                }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < ch; k++)
                {
                    float s = 0;
                    int n = 0;
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int yy = Math.Clamp(y + dy, 0, h - 1);
                        s += t[(yy * w + x) * ch + k];
                        n++;
                    }
                    o[(y * w + x) * ch + k] = s / n;
                }
        return o;
    }

    /// <summary>A 3x3 dilation (<paramref name="grow"/>) or erosion of a mask.</summary>
    private static byte[] Morph(byte[] m, int w, int h, bool grow)
    {
        var o = new byte[m.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = grow ? (byte)0 : (byte)255;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        byte q = m[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)];
                        v = grow ? Math.Max(v, q) : Math.Min(v, q);
                    }
                o[y * w + x] = v;
            }
        return o;
    }

    // ---- UV islands -----------------------------------------------------------------------------------

    /// <summary>A surface's UV triangles (0-1 per unit of UV; any range: the texture repeats) and who draws them (the part, for grouping).</summary>
    public sealed record UvSurface(string Part, float[] Uv, int[] Indices);

    /// <summary>One island: connected triangles (sharing a UV point) of one part's surfaces, rasterised.</summary>
    public sealed record Island(string Part, int Triangles, byte[] Mask, double Coverage);

    /// <summary>A proposed region: the parts whose islands make it, its mask, its share of the texture.</summary>
    public sealed record Proposal(string Name, IReadOnlyList<string> Parts, int Islands, byte[] Mask, double Coverage);

    /// <summary>
    /// The UV islands of <paramref name="surfaces"/> at <paramref name="w"/>x<paramref name="h"/>:
    /// each surface's triangles grouped into islands (triangles sharing a UV point, by a union-find
    /// over the index buffer), each island moved into the texture by its own whole UV offset and
    /// rasterised (wrapping when <paramref name="wrap"/>). An island larger than the texture (a
    /// repeating surface) covers all of it.
    /// </summary>
    public static List<Island> Islands(IEnumerable<UvSurface> surfaces, int w, int h, bool wrap)
    {
        var r = new List<Island>();
        foreach (var s in surfaces)
        {
            int nv = s.Uv.Length / 2;
            var parent = Enumerable.Range(0, nv).ToArray();
            int Find(int a)
            {
                while (parent[a] != a)
                    a = parent[a] = parent[parent[a]];
                return a;
            }
            // vertices at the same UV are one point (a mesh splits vertices at normal seams)
            var byUv = new Dictionary<(int, int), int>();
            for (int v = 0; v < nv; v++)
            {
                var k = ((int)MathF.Round(s.Uv[v * 2] * 4096), (int)MathF.Round(s.Uv[v * 2 + 1] * 4096));
                if (byUv.TryGetValue(k, out var o))
                    parent[Find(v)] = Find(o);
                else
                    byUv[k] = v;
            }
            for (int t = 0; t + 2 < s.Indices.Length; t += 3)
            {
                parent[Find(s.Indices[t + 1])] = Find(s.Indices[t]);
                parent[Find(s.Indices[t + 2])] = Find(s.Indices[t]);
            }
            var groups = new Dictionary<int, List<int>>();
            for (int t = 0; t + 2 < s.Indices.Length; t += 3)
            {
                int g = Find(s.Indices[t]);
                if (!groups.TryGetValue(g, out var l))
                    groups[g] = l = new List<int>();
                l.Add(t);
            }
            foreach (var tris in groups.Values)
            {
                float minu = float.MaxValue, minv = float.MaxValue, maxu = float.MinValue, maxv = float.MinValue;
                foreach (var t in tris)
                    for (int k = 0; k < 3; k++)
                    {
                        int v = s.Indices[t + k];
                        (minu, maxu) = (MathF.Min(minu, s.Uv[v * 2]), MathF.Max(maxu, s.Uv[v * 2]));
                        (minv, maxv) = (MathF.Min(minv, s.Uv[v * 2 + 1]), MathF.Max(maxv, s.Uv[v * 2 + 1]));
                    }
                var mask = new byte[w * h];
                if (maxu - minu >= 0.999f && maxv - minv >= 0.999f)
                    Array.Fill(mask, (byte)255);
                else
                {
                    // the island's own whole offset (a part drawn at u 3.2 uses u 0.2 of the texture)
                    float ou = MathF.Floor(minu), ov = MathF.Floor(minv);
                    foreach (var t in tris)
                    {
                        (float, float) P(int k) => ((s.Uv[s.Indices[t + k] * 2] - ou) * w, (s.Uv[s.Indices[t + k] * 2 + 1] - ov) * h);
                        FillTriangle(mask, w, h, P(0), P(1), P(2), 255, wrap || maxu - ou > 1 || maxv - ov > 1);
                    }
                }
                r.Add(new Island(s.Part, tris.Count, mask, Coverage(mask)));
            }
        }
        return r;
    }

    /// <summary>
    /// Regions proposed from the islands (docs/remaster-materials.md, phase 6): the islands grouped
    /// by the part that draws them; groups that cover the same pixels (their overlap over the
    /// smaller's area above <paramref name="merge"/>) joined; groups covering nearly the whole
    /// texture (a repeating use: no region) or almost nothing dropped. Largest first, at most
    /// <paramref name="max"/>; each region's mask without the pixels of the ones before it.
    /// </summary>
    public static List<Proposal> Propose(IReadOnlyList<Island> islands, int w, int h, double merge = 0.5, int max = 6)
    {
        var groups = islands.Where(i => i.Coverage < 0.97).GroupBy(i => i.Part)
            .Select(g => (Parts: new List<string> { g.Key }, Islands: g.Count(), Mask: Union(g.Select(i => i.Mask), w * h))).ToList();
        // join groups over the same pixels (two parts drawing the same patch)
        for (bool joined = true; joined;)
        {
            joined = false;
            for (int a = 0; a < groups.Count && !joined; a++)
                for (int b = a + 1; b < groups.Count && !joined; b++)
                {
                    double ov = Overlap(groups[a].Mask, groups[b].Mask);
                    double small = Math.Min(Coverage(groups[a].Mask), Coverage(groups[b].Mask));
                    if (small > 0 && ov / small > merge)
                    {
                        groups[a] = (groups[a].Parts.Concat(groups[b].Parts).ToList(), groups[a].Islands + groups[b].Islands, Union([groups[a].Mask, groups[b].Mask], w * h));
                        groups.RemoveAt(b);
                        joined = true;
                    }
                }
        }
        var ranked = groups.Where(g => Coverage(g.Mask) is > 0.01 and < 0.97).OrderByDescending(g => Coverage(g.Mask)).Take(max).ToList();
        var taken = new byte[w * h];
        var r = new List<Proposal>();
        foreach (var g in ranked)
        {
            var m = (byte[])g.Mask.Clone();
            for (int i = 0; i < m.Length; i++)
                if (taken[i] > 0)
                    m[i] = 0;
            if (Coverage(m) < 0.005)
                continue;
            for (int i = 0; i < m.Length; i++)
                taken[i] = Math.Max(taken[i], m[i]);
            r.Add(new Proposal($"Region {r.Count + 1}", g.Parts, g.Islands, m, Coverage(m)));
        }
        return r;
    }

    private static byte[] Union(IEnumerable<byte[]> masks, int n)
    {
        var u = new byte[n];
        foreach (var m in masks)
            for (int i = 0; i < n; i++)
                u[i] = Math.Max(u[i], m[i]);
        return u;
    }

    /// <summary>The share of all pixels on in both masks.</summary>
    public static double Overlap(byte[] a, byte[] b)
    {
        long n = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] > 127 && b[i] > 127)
                n++;
        return (double)n / Math.Max(1, a.Length);
    }

    /// <summary>The islands whose pixels a stroke or shape touched (<paramref name="drawn"/>): every pixel of each (snapping to the islands).</summary>
    public static byte[] Snap(byte[] drawn, IReadOnlyList<byte[]> islands)
    {
        var o = (byte[])drawn.Clone();
        foreach (var isl in islands)
        {
            bool touched = false;
            for (int i = 0; i < drawn.Length && !touched; i++)
                touched = drawn[i] > 127 && isl[i] > 127;
            if (touched)
                for (int i = 0; i < o.Length; i++)
                    o[i] = Math.Max(o[i], isl[i]);
        }
        return o;
    }

    // ---- the composite's measures (the checks) -------------------------------------------------------

    /// <summary>The worst unit-length error of an 8-bit normal map's vectors (|n| - 1, after decoding), its 99.9th percentile, and the share above 0.03.</summary>
    public static (double Max, double P999, double Bad) NormalLength(RgbaImage n)
    {
        var e = new float[n.Width * n.Height];
        int bad = 0;
        for (int i = 0; i < e.Length; i++)
        {
            float x = n.Rgba[i * 4] / 255f * 2 - 1, y = n.Rgba[i * 4 + 1] / 255f * 2 - 1, z = n.Rgba[i * 4 + 2] / 255f * 2 - 1;
            e[i] = MathF.Abs(MathF.Sqrt(x * x + y * y + z * z) - 1);
            if (e[i] > 0.03f)
                bad++;
        }
        var s = (float[])e.Clone();
        Array.Sort(s);
        return (s[^1], s[(int)(0.999 * (s.Length - 1))], (double)bad / e.Length);
    }

    /// <summary>
    /// How visible a region's border is in a map (<paramref name="values"/>, one float per pixel,
    /// e.g. the height): the mean step between neighbouring pixels where the region's mask
    /// (<paramref name="mask"/>, 0-1 at the map's size) crosses one half, against the mean step
    /// inside either side (away from the border). 1: the border is no stronger than the texture's
    /// own detail.
    /// </summary>
    public static (double Border, double Inside, double Ratio) BorderStep(float[] values, float[] mask, int w, int h)
    {
        double border = 0, inside = 0;
        int nb = 0, ni = 0;
        for (int y = 0; y < h - 1; y++)
            for (int x = 0; x < w - 1; x++)
            {
                int i = y * w + x;
                foreach (int j in new[] { i + 1, i + w })
                {
                    double d = Math.Abs(values[i] - values[j]);
                    bool crosses = (mask[i] - 0.5f) * (mask[j] - 0.5f) <= 0 && mask[i] != mask[j];
                    if (crosses)
                    {
                        border += d;
                        nb++;
                    }
                    else if (mask[i] is < 0.02f or > 0.98f && mask[j] is < 0.02f or > 0.98f)
                    {
                        inside += d;
                        ni++;
                    }
                }
            }
        border /= Math.Max(1, nb);
        inside /= Math.Max(1, ni);
        return (border, inside, border / Math.Max(1e-9, inside));
    }

    /// <summary>
    /// The level step across a region's border: the mean of <paramref name="values"/> just inside
    /// the region (its mask 0.55 to 0.8) against just outside (0.2 to 0.45), as a fraction of the
    /// map's own spread (its 5th to 95th percentile). Height levels matched: near 0.
    /// </summary>
    public static double LevelStep(float[] values, float[] mask)
    {
        double a = 0, b = 0;
        int na = 0, nb = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (mask[i] is >= 0.55f and <= 0.8f) { a += values[i]; na++; }
            else if (mask[i] is >= 0.2f and <= 0.45f) { b += values[i]; nb++; }
        }
        var s = (float[])values.Clone();
        Array.Sort(s);
        double spread = Math.Max(1e-6, s[(int)(0.95 * (s.Length - 1))] - s[(int)(0.05 * (s.Length - 1))]);
        return na == 0 || nb == 0 ? 0 : Math.Abs(a / na - b / nb) / spread;
    }
}
