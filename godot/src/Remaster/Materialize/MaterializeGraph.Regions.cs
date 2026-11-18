using System;
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster.Texturing;

namespace GdFfx.Remaster.Materialize;

/// <summary>Regions (R2b phase 6) and AI estimates in the graph.</summary>
public sealed partial class MaterializeGraph
{
    /// <summary>The import name prefix of an AI estimate of <paramref name="s"/>'s map for texture <paramref name="key"/> (the content follows: <c>ai:normal:&lt;texture&gt;|&lt;cache key&gt;</c>).</summary>
    public static string AiPrefix(MaterialStep s, string key) => $"ai:{s.ToString().ToLowerInvariant()}:{key}|";

    /// <summary>The AI estimate of <paramref name="s"/>'s map at the working size (null: none cached yet: the step generates meanwhile).</summary>
    private Tex? AiTex(MaterialStep s)
    {
        var prefix = AiPrefix(s, _key);
        foreach (var (k, img) in _imports)
            if (k.StartsWith(prefix, StringComparison.Ordinal))
            {
                bool colour = s == MaterialStep.Albedo;
                var key = $"{k}:{_w}x{_h}";
                var sized = img.Width == _w && img.Height == _h ? img : ImportCache.Resized(key, img, _w, _h, colour);
                var t = _b.Input(key, sized);
                return _seamH != null ? Seam($"seam/ai-{s}", t, s == MaterialStep.Normal) : t;
            }
        return null;
    }

    /// <summary>
    /// The texture's maps by its own settings, each region's by its own (the same source, the
    /// shared settings the texture's: <see cref="MaterialRegions.WithShared"/>), composited in the
    /// regions' order, each over what is below it with its mask (bilinear at the working size, its
    /// border feathered by a Gaussian): colour and data blended; normals blended as slopes and
    /// made unit length again; the height levelled first, the region's mean under its border band
    /// moved to the mean of what lies below there (so parallax does not step at the border). A
    /// map one side does not make takes its neutral value there (<see cref="MaterialRegions.Neutral"/>).
    /// </summary>
    private static MaterializeOutputs BuildRegions(IMaterializeBackend b, string key, RgbaImage original, IReadOnlyDictionary<string, RgbaImage> imports, PbrRecipe r,
        BuildRequest q, IReadOnlyList<RegionInput> regions)
    {
        var baseGraph = new MaterializeGraph(b, key, original, imports, r, q with { Isolate = q.Focus < 0 ? q.Isolate : null });
        var o = baseGraph.Run();
        var src = o.Maps[MaterialStep.Source];
        var outs = new List<MaterializeOutputs>();
        var graphs = new List<MaterializeGraph>();
        for (int i = 0; i < regions.Count; i++)
        {
            var rr = MaterialRegions.WithShared(regions[i].Recipe, r);
            var g = new MaterializeGraph(b, key, original, imports, rr, q with { Isolate = q.Focus == i ? q.Isolate : null }, $"rg{i}/", src)
            {
                _seamH = baseGraph._seamH, _seamHH = baseGraph._seamHH,
            };
            graphs.Add(g);
            outs.Add(g.Run());
        }
        var steps = MaterialPipeline.Steps.Where(s => o.Maps.ContainsKey(s) || outs.Any(x => x.Maps.ContainsKey(s))).ToList();
        var result = new MaterializeOutputs { Base = new Dictionary<MaterialStep, Tex>(o.Maps) };
        foreach (var (name, t) in o.Aux)
            result.Aux[name] = t;
        var cur = new Dictionary<MaterialStep, Tex>(o.Maps);
        var curOrm = o.Orm;
        for (int i = 0; i < regions.Count; i++)
        {
            var g = graphs[i];
            var reg = regions[i];
            // the mask at the working size, its border feathered
            var mIn = b.Input($"region-mask:{reg.MaskKey}", reg.Mask);
            var m0 = g.F("mask/r", "rmask", 1, [mIn]);
            var blurred = g.Blur("mask/f", m0, MathF.Max(0, reg.Feather) * g._s);
            // a blur's result is a pyramid level: back at the working size (bilinear), so the composite reads it pixel for pixel
            var mask = ReferenceEquals(blurred, m0) ? m0 : g.F("mask/o", "rmask", 1, [blurred]);
            var mine = new Dictionary<MaterialStep, Tex>();
            foreach (var s in steps)
            {
                if (s == MaterialStep.Source)
                {
                    mine[s] = src;
                    continue;
                }
                var below = cur.TryGetValue(s, out var c) ? c : g.Neutral($"cmp/n-{s}", s);
                var over = outs[i].Maps.TryGetValue(s, out var ov) ? ov : g.Neutral($"cmp/m-{s}", s);
                mine[s] = over;
                int mode = MaterialRegions.CompositeMode(s);
                Tex stats = g.Dummy;
                if (mode == 2 && q.MatchHeights)
                    stats = g.WMean($"cmp/hs", g.F("cmp/hb", "rg_hstat", 4, [below, over, mask]));
                bool hdr = s == MaterialStep.Height;
                cur[s] = g.P($"cmp/{MaterialPipeline.Role(s)}", "rcomp", g._w, g._h, hdr, 4, [below, over, mask, stats],
                    U().Add("mode", mode).Add("match", mode == 2 && q.MatchHeights));
            }
            curOrm = g.P("cmp/orm", "rcomp", g._w, g._h, false, 4, [curOrm, outs[i].Orm, mask, g.Dummy], U().Add("mode", 0).Add("match", false));
            result.Regions.Add((mine, mask));
        }
        foreach (var (s, t) in cur)
            result.Maps[s] = t;
        result.Orm = curOrm;
        // the edited side's mask view and colour check
        var focus = q.Focus >= 0 && q.Focus < outs.Count ? outs[q.Focus] : o;
        result.Mask = focus.Mask;
        (result.SourceStats, result.AlbedoStats) = (focus.SourceStats, focus.AlbedoStats);
        // the occlusion's chunks: the next one to add is the least any graph still needs
        var all = outs.Append(o).ToList();
        result.AoChunks = all.Max(x => x.AoChunks);
        result.AoDone = all.All(x => x.AoDone >= x.AoChunks) ? result.AoChunks : all.Where(x => x.AoDone < x.AoChunks).Min(x => x.AoDone);
        return result;
    }

    /// <summary>A map's neutral value (a region or the texture that does not make it).</summary>
    private Tex Neutral(string slot, MaterialStep s) =>
        P(slot, "const", _w, _h, s == MaterialStep.Height, 4, [], U().Add("value", MaterialRegions.Neutral(s)));
}
