using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GdFfx.Api;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.Remaster.Materialize;

/// <summary>
/// R2b phase 2's check of the pipeline (<c>--selftest-materialize</c>; docs/remaster-materials.md,
/// "Checks"): on a texture (a Besaid one with the game, else a synthetic one), the CPU's maps and,
/// under a renderer, the GPU's: their parity per map (mean and 99th percentile of the absolute
/// difference, of 255; the height in floats), the GPU's time per cheap change, per band rebuild
/// and per bake at 1024, 2048 and 4096 px, the occlusion settling, and the CPU's bake times.
/// Runs over frames (the GPU draws between them); <see cref="Passed"/> is set at the end.
/// </summary>
public sealed partial class MaterializeCheck : Node
{
    /// <summary>The parity's limits: an 8-bit map's mean and 99th percentile difference (of 255), the height's (0-1).</summary>
    public const double MeanLimit = 2.0, P99Limit = 12, HeightMeanLimit = 0.004, HeightP99Limit = 0.03;

    private readonly RgbaImage _texture;
    private readonly string _name;
    private IEnumerator<string?>? _flow;
    private readonly List<string> _notes = new();

    public bool? Passed { get; private set; }

    public string Note => string.Join("; ", _notes);

    public MaterializeCheck(RgbaImage texture, string name)
    {
        (_texture, _name) = (texture, name);
        Name = "MaterializeCheck";
    }

    public override void _Process(double delta)
    {
        if (Passed != null)
            return;
        _flow ??= Flow();
        // the check's numbers in the invariant culture (a Windows locale would write decimal commas)
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            if (!_flow.MoveNext())
                Finish(true, null);
            else if (_flow.Current is { } fail)
                Finish(false, fail);
        }
        catch (Exception e)
        {
            Finish(false, e.ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    private void Finish(bool pass, string? fail)
    {
        if (fail != null)
            _notes.Add("FAIL: " + fail);
        Passed = pass;
    }

    private void Say(string s)
    {
        GD.Print($"ffx materialize: {s}");
        _notes.Add(s);
    }

    /// <summary>A synthetic texture (painted bricks: mortar, a colour per brick, light painted from the top left, grain, blue-grey bolts, hot spots): no game data.</summary>
    public static RgbaImage Synthetic(int side = 256, int height = 0)
    {
        if (height <= 0)
            height = side;
        var px = new byte[side * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < side; x++)
            {
                int row = y / 32, bx = (x + (row % 2) * 24) / 48;
                float fx = ((x + (row % 2) * 24) % 48) / 48f, fy = (y % 32) / 32f;
                float hue = Kernels.Rnd(bx, row, 7);
                bool mortar = fx < 0.06f || fy < 0.09f;
                float light = 1.15f - 0.35f * fx - 0.4f * fy;
                float grain = 0.85f + 0.3f * Kernels.Rnd(x, y, 9);
                Vector3 c = mortar ? new Vector3(0.32f, 0.3f, 0.27f) * grain
                    : new Vector3(0.62f + 0.15f * hue, 0.38f + 0.1f * hue, 0.26f) * light * grain;
                float bdx = fx - 0.5f, bdy = fy - 0.5f;
                if (!mortar && bdx * bdx * 2.3f + bdy * bdy < 0.012f)
                    c = new Vector3(0.45f, 0.5f, 0.62f) * (1.2f - 2 * bdy);
                if (!mortar && (x * 7 + y * 3) % 211 == 0)
                    c = new Vector3(1, 0.97f, 0.9f);
                int i = (y * side + x) * 4;
                px[i] = (byte)Math.Clamp(c.X * 255, 0, 255);
                px[i + 1] = (byte)Math.Clamp(c.Y * 255, 0, 255);
                px[i + 2] = (byte)Math.Clamp(c.Z * 255, 0, 255);
                px[i + 3] = 255;
            }
        return new RgbaImage(side, height, px);
    }

    /// <summary>Every feature on (so the parity covers every kernel).</summary>
    public static PbrRecipe Everything(int upscale)
    {
        var r = new PbrRecipe
        {
            Version = 3, Upscale = upscale, AlbedoDarkSpots = 0.5f, AlbedoNeutralise = 0.2f, AlbedoEdgeKeep = 0.6f,
            HeightSample1 = new Color(0.32f, 0.3f, 0.27f), HeightSampleValue1 = 0.1f, HeightGain = 0.3f, HeightBandContrast6 = -0.5f,
            NormalShape = 0.5f, NormalAngularity = 0.3f, NormalContrast = 1.3f, NormalFilter = GradientFilter.Scharr,
            RoughnessSample1 = new Color(0.45f, 0.5f, 0.62f), RoughnessOverlay = 1f, RoughnessEdgeWear = 0.5f, RoughnessContrast = 1.2f,
            MetallicMode = StepMode.Generate, Metallic = 1f, MetallicColour = new Color(0.45f, 0.5f, 0.62f), MetallicOverlay = 0.5f,
            EmissionMode = StepMode.Generate, EmissionThreshold = 0.9f, OcclusionNormalShare = 0.5f, OcclusionPower = 1.3f,
        };
        return r;
    }

    private static (double Mean, double P99, int Max) Diff(RgbaImage a, RgbaImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return (999, 999, 999);
        var d = new int[256];
        long sum = 0;
        int n = 0, max = 0;
        for (int i = 0; i < a.Rgba.Length; i++)
        {
            if (i % 4 == 3)
                continue;
            int x = Math.Abs(a.Rgba[i] - b.Rgba[i]);
            d[x]++;
            sum += x;
            n++;
            max = Math.Max(max, x);
        }
        int acc = 0, p99 = 0;
        for (int k = 0; k < 256; k++)
        {
            acc += d[k];
            if (acc >= n * 0.99) { p99 = k; break; }
        }
        return ((double)sum / n, p99, max);
    }

    private static (double Mean, double P99) DiffF(float[] a, float[] b)
    {
        var d = new float[a.Length];
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            d[i] = MathF.Abs(a[i] - b[i]);
            sum += d[i];
        }
        Array.Sort(d);
        return (sum / a.Length, d[(int)(d.Length * 0.99)]);
    }

    private static float[] Red(Image img)
    {
        var o = new float[img.GetWidth() * img.GetHeight()];
        int w = img.GetWidth();
        for (int y = 0; y < img.GetHeight(); y++)
            for (int x = 0; x < w; x++)
                o[y * w + x] = img.GetPixel(x, y).R;
        return o;
    }

    private static double Median(IEnumerable<double> v)
    {
        var l = v.OrderBy(x => x).ToList();
        return l.Count == 0 ? -1 : l[l.Count / 2];
    }

    private IEnumerator<string?> Flow()
    {
        int side = Math.Max(_texture.Width, _texture.Height);
        int up = Math.Clamp(1024 / side, 1, 4);
        var recipe = Everything(up);
        recipe.WorkingSide = 1024;
        var inputs = new MaterialPipeline.Inputs("check:" + _name, _texture, recipe, new Dictionary<string, RgbaImage>());
        var (w, h, scale) = MaterialPipeline.Size(inputs, 1024);
        Say($"{_name} {_texture.Width}x{_texture.Height}, x{up} to {w}x{h}");

        // 1. the CPU, every feature on, at the working size
        var cpu = new CpuBackend();
        var clock = Stopwatch.StartNew();
        var co = MaterializeGraph.Build(cpu, inputs.Key, _texture, inputs.Imports, recipe, new BuildRequest(w, h, scale));
        var cpuMaps = co.Maps.ToDictionary(kv => kv.Key, kv => cpu.Get(kv.Value).ToRgba());
        var cpuOrm = cpu.Get(co.Orm).ToRgba();
        var cpuHeight = cpu.Get(co.Maps[MaterialStep.Height]).Red();
        double cpuMs = clock.Elapsed.TotalMilliseconds;
        Say($"CPU: every map at {w} px in {cpuMs:0} ms ({cpu.Runs} passes; slowest: {string.Join(", ", cpu.KernelMs.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {k.Value:0}"))})");
        if (!GpuBackend.Available)
        {
            Say("no renderer (headless): the GPU parts skipped");
            yield break;
        }
        Say($"renderer: {RenderingServer.GetCurrentRenderingMethod()} on {RenderingServer.GetVideoAdapterName()}");

        // 2. the GPU's bake of the same, its parity with the CPU's
        var bake = new GpuBake(this, inputs, w, h, scale);
        int bakePasses = bake.Passes;
        while (!bake.Done) yield return null;
        var passDiffs = new List<(string Slot, double Mean, double Max)>();
        foreach (var (slot, ct) in cpu.Slots)
        {
            if (ct.Data == null || bake.Backend.ReadSlot(slot) is not { } gi || gi.GetWidth() != ct.W || gi.GetHeight() != ct.H)
                continue;
            double sum = 0, max = 0;
            for (int y = 0; y < ct.H; y++)
                for (int x = 0; x < ct.W; x++)
                {
                    var gc = gi.GetPixel(x, y);
                    var cc = ct.Fet(x, y);
                    float[] g = [gc.R, gc.G, gc.B, gc.A];
                    float[] c = [cc.X, cc.Y, cc.Z, cc.W];
                    for (int k = 0; k < ct.Channels; k++)
                    {
                        double d = Math.Abs(g[k] - c[k]);
                        sum += d;
                        max = Math.Max(max, d);
                    }
                }
            passDiffs.Add((slot, sum / (ct.W * ct.H * ct.Channels), max));
        }
        if (GameSettings.Arg("materialize-probe") is { } probe)
        {
            // --materialize-probe=X,Y[,slot...]: one pixel of passes from both backends (development)
            var parts = probe.Split(',');
            int px = int.Parse(parts[0]), py = int.Parse(parts[1]);
            foreach (var slot in parts.Length > 2 ? parts[2..] : ["albedo", "height", "normal", "roughness", "metallic", "occlusion"])
                if (cpu.Slots.TryGetValue(slot, out var ct) && ct.Data != null && bake.Backend.ReadSlot(slot) is { } gi)
                {
                    int sx = px * ct.W / w, sy = py * ct.H / h;
                    Say($"probe {slot} ({ct.W}x{ct.H}) at {sx},{sy}: CPU {ct.Fet(sx, sy)}, GPU {gi.GetPixel(sx, sy)}");
                }
        }
        Say("passes furthest apart (mean/max): " + string.Join(", ", passDiffs.OrderByDescending(d => d.Mean).Take(12).Select(d => $"{d.Slot} {d.Mean:0.#####}/{d.Max:0.####}")));
        if (bake.Backend.OrderErrors > 0)
            yield return $"{bake.Backend.OrderErrors} pass(es) read a pass drawn after them";
        var (gm, gorm, gheight, gms) = bake.Finish();
        if (GameSettings.Arg("shot") is { Length: > 0 } shot)
        {
            // the GPU's maps side by side (a picture outside git: made from the game's texture)
            var sheet = Image.CreateEmpty(256 * 4, 256 * 2, false, Image.Format.Rgba8);
            var order = new[] { MaterialStep.Source, MaterialStep.Albedo, MaterialStep.Height, MaterialStep.Normal, MaterialStep.Roughness, MaterialStep.Metallic, MaterialStep.Occlusion, MaterialStep.Emission };
            for (int i = 0; i < order.Length; i++)
            {
                var img = PbrBuilder.ToImage(gm[order[i]]);
                img.Resize(256, 256, Image.Interpolation.Bilinear);
                sheet.BlitRect(img, new Rect2I(0, 0, 256, 256), new Vector2I(i % 4 * 256, i / 4 * 256));
            }
            sheet.SavePng(shot);
            Say($"picture {shot}");
        }
        var parity = new List<string>();
        bool ok = true;
        foreach (var s in MaterialPipeline.Steps)
        {
            if (s == MaterialStep.Height)
                continue;
            var (mean, p99, max) = Diff(cpuMaps[s], gm[s]);
            parity.Add($"{s} {mean:0.00}/{p99:0}/{max}");
            bool within = mean <= MeanLimit && p99 <= P99Limit;
            ok &= within;
            if (!within && GameSettings.Arg("shot") is { Length: > 0 } dump)
            {
                // the two maps beside the picture (outside git)
                PbrBuilder.ToImage(cpuMaps[s]).SavePng($"{dump[..^4]}-cpu-{s}.png");
                PbrBuilder.ToImage(gm[s]).SavePng($"{dump[..^4]}-gpu-{s}.png");
            }
        }
        var (om, op, ox) = Diff(cpuOrm, gorm);
        parity.Add($"ORM {om:0.00}/{op:0}/{ox}");
        ok &= om <= MeanLimit && op <= P99Limit;
        var (hm, hp) = DiffF(cpuHeight, Red(gheight));
        parity.Add($"height {hm:0.0000}/{hp:0.0000} (float, {gheight.GetFormat()})");
        ok &= hm <= HeightMeanLimit && hp <= HeightP99Limit;
        Say($"parity GPU vs CPU at {w} px (mean/p99/max of 255): {string.Join(", ", parity)}; GPU bake {gms:0} ms, {bakePasses} passes");

        if (GameSettings.Flag("materialize-parity-only"))
        {
            if (!ok)
                yield return "the GPU's maps are not within the parity's limits of the CPU's";
            yield break;
        }

        // 3. the preview: the first build, cheap changes, band rebuilds, the occlusion settling
        var pipe = new GpuPipeline(this);
        var r = (PbrRecipe)recipe.Duplicate();
        r.Version = 3;
        var builds = new List<double>();
        IEnumerator<double> Change(Action<PbrRecipe> edit)
        {
            edit(r);
            var t0 = Stopwatch.StartNew();
            pipe.Update(inputs with { Recipe = (PbrRecipe)r.Duplicate() }, 1024);
            builds.Add(t0.Elapsed.TotalMilliseconds);
            int draws = pipe.Backend.LastDraws;
            while (!pipe.Backend.Drawn) { yield return -1; }
            // the read back of one pixel waits for the GPU to finish
            _ = pipe.Backend.Pixel(pipe.Outputs!.AlbedoStats ?? pipe.Outputs.Orm);
            yield return t0.Elapsed.TotalMilliseconds;
            yield return draws;
        }
        double first = 0;
        {
            var e = Change(_ => { });
            while (e.MoveNext() && e.Current < 0) yield return null;
            first = e.Current;
        }
        while (!pipe.Settled) { pipe.Tick(); yield return null; }
        int settleFrames = 0;
        var cheap = new List<double>();
        var cheapDraws = new List<double>();
        var bands = new List<double>();
        var bandDraws = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            var e = Change(x => x.HeightBand3 = i % 2 == 0 ? 0.8f : 0.5f);
            while (e.MoveNext() && e.Current < 0) yield return null;
            cheap.Add(e.Current);
            e.MoveNext();
            cheapDraws.Add(e.Current);
            // the occlusion settles over frames (counted once)
            int f = 0;
            while (!pipe.Settled) { pipe.Tick(); f++; yield return null; }
            settleFrames = Math.Max(settleFrames, f);
        }
        var albedoCheap = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            var e = Change(x => x.RoughnessContrast = i % 2 == 0 ? 1.4f : 1.1f);
            while (e.MoveNext() && e.Current < 0) yield return null;
            albedoCheap.Add(e.Current);
            while (!pipe.Settled) { pipe.Tick(); yield return null; }
        }
        for (int i = 0; i < 6; i++)
        {
            var e = Change(x => x.HeightBlur = i % 2 == 0 ? 1.5f : 1f);
            while (e.MoveNext() && e.Current < 0) yield return null;
            bands.Add(e.Current);
            e.MoveNext();
            bandDraws.Add(e.Current);
            while (!pipe.Settled) { pipe.Tick(); yield return null; }
        }
        Say($"preview at {w} px: first build {first:0} ms ({pipe.Backend.Count} passes); a cheap change (a height band's weight) {Median(cheap):0} ms "
            + $"(median of {cheap.Count}, {Median(cheapDraws):0} passes drawn), roughness contrast {Median(albedoCheap):0} ms; "
            + $"a band rebuild (height smoothing) {Median(bands):0} ms ({Median(bandDraws):0} passes); occlusion settled in {settleFrames} frames; "
            + $"building the graph (CPU, in those) {Median(builds):0.0} ms; a frame here {1000.0 / Math.Max(1, Engine.GetFramesPerSecond()):0.0} ms");
        pipe.Backend.Clear();
        pipe.Backend.QueueFree();
        yield return null;

        // 4. bakes at 1024, 2048, 4096 (the GPU), 1024 and 2048 (the CPU)
        var bakes = new List<string>();
        foreach (int target in new[] { 1024, 2048, 4096 })
        {
            int bw = target, bh = (int)Math.Round((double)target * _texture.Height / _texture.Width);
            var times = new List<double>();
            for (int k = 0; k < 2; k++)
            {
                var b = new GpuBake(this, inputs, bw, bh, (double)bw / _texture.Width);
                while (!b.Done) yield return null;
                times.Add(b.Finish().Ms);
                yield return null;
            }
            bakes.Add($"{target} px {times.Min():0} ms (first {times[0]:0})");
        }
        var cpuBakes = new List<string>();
        foreach (int target in GameSettings.Flag("materialize-cpu-2048") ? new[] { 1024, 2048 } : new[] { 1024 })
        {
            int bw = target, bh = (int)Math.Round((double)target * _texture.Height / _texture.Width);
            var t0 = Stopwatch.StartNew();
            var b = new CpuBackend();
            var o = MaterializeGraph.Build(b, inputs.Key, _texture, inputs.Imports, recipe, new BuildRequest(bw, bh, (double)bw / _texture.Width));
            foreach (var t in o.Maps.Values)
                b.Get(t);
            b.Get(o.Orm);
            cpuBakes.Add($"{target} px {t0.Elapsed.TotalMilliseconds:0} ms");
            yield return null;
        }
        Say($"bakes (every map, read back): GPU {string.Join(", ", bakes)}; CPU {string.Join(", ", cpuBakes)}");
        Say($"targets: a slider {Median(cheap):0} ms (33), bake 2048 and 4096 above (1000, 4000)");
        if (!ok)
            yield return "the GPU's maps are not within the parity's limits of the CPU's";
    }
}

/// <summary>
/// <c>--selftest-materialize</c> with the game (<c>--devsave=7</c>): the pipeline's check
/// (<see cref="MaterializeCheck"/>) on Besaid beach's most used colour texture, read from the
/// player's archive (nothing written). With <c>--no-game</c>: on a synthetic texture
/// (<see cref="RunAlone"/>).
/// </summary>
public sealed class MaterializeTest(double timeout) : ITest
{
    private double _t;
    private MaterializeCheck? _check;

    public string Note { get; private set; } = "";

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _t += delta;
        if (_t > timeout)
            return Done(false, $"timed out ({_check?.Note})");
        if (_check == null)
        {
            if (GameHost.Instance?.Classic is not { Open: true })
                return null;
            // --materialize-atlas=DIR --materialize-ids=FILE: thumbnails to choose textures by eye
            if (GameSettings.Arg("materialize-atlas") is { Length: > 0 } atlas)
                return Done(true, ProfileSheet.Atlas(atlas, GameSettings.Arg("materialize-ids") ?? ""));
            // --materialize-profiles[=DIR]: the starter profiles on the textures of their kinds (DIR: contact sheets)
            if (GameSettings.Flag("materialize-profiles") || GameSettings.Arg("materialize-profiles") is { Length: > 0 })
            {
                var dir = GameSettings.Arg("materialize-profiles") is { Length: > 0 } d && d != "true" ? d : null;
                var (fails, lines) = ProfileSheet.Run(dir, (int)GameSettings.Number("materialize-profile-size", 1024));
                return Done(fails.Count == 0, fails.Count == 0 ? $"the starter profiles plausible: {string.Join("; ", lines.Where(l => !l.Contains(" ms)")))}"
                    : $"{fails.Count} implausible: {string.Join("; ", fails)}");
            }
            var json = game.ClassicTextureUses(MaterialsTest.Level);
            if (json.Length == 0)
                return null;
            // the most used opaque colour texture of at least 256 px (so the working size is 1024 px: x2 or x4)
            var ranked = new List<(string Id, int Parts)>();
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
                foreach (var t in doc.RootElement.GetProperty("textures").EnumerateArray())
                    if (t.GetProperty("samplers").EnumerateArray().Any(x => x.GetString() == "TextureSampler"))
                        ranked.Add((t.GetProperty("id").GetString()!, t.GetProperty("parts").GetArrayLength()));
            ranked.Sort((a, b) => b.Parts != a.Parts ? b.Parts.CompareTo(a.Parts) : string.CompareOrdinal(a.Id, b.Id));
            RgbaImage? pick = null;
            string name = "";
            // --materialize-texture=NAME: that texture instead (a part of its short name), whatever its alpha and size
            var want = GameSettings.Arg("materialize-texture");
            foreach (var (id, _) in ranked)
            {
                if (want != null && !RemasterTextures.ShortName(id).Contains(want, StringComparison.Ordinal))
                    continue;
                if (RemasterTextures.ClassicImage(id) is not { } img)
                    continue;
                var px = PbrBuilder.From(img);
                if (want == null && (px.HasAlpha || Math.Max(px.Width, px.Height) < 256))
                    continue;
                (pick, name) = (px, RemasterTextures.ShortName(id));
                break;
            }
            if (pick == null)
                return Done(false, "no opaque colour texture of 256 px or more on Besaid beach");
            _check = new MaterializeCheck(pick, name);
            (Engine.GetMainLoop() as SceneTree)!.Root.AddChild(_check);
            return null;
        }
        return _check.Passed is { } p ? Done(p, _check.Note) : null;
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }

    /// <summary>Without the game: the check on <see cref="MaterializeCheck.Synthetic"/>, then quits (0: passed).</summary>
    public static void RunAlone(Node parent)
    {
        // --materialize-size=WxH: the synthetic texture's size (256x256)
        var size = (GameSettings.Arg("materialize-size") ?? "256x256").Split('x');
        var check = new MaterializeCheck(MaterializeCheck.Synthetic(int.Parse(size[0]), int.Parse(size[1])), "synthetic bricks");
        parent.AddChild(check);
        var timer = new Timer { WaitTime = 0.1, Autostart = true };
        parent.AddChild(timer);
        double t = 0;
        double limit = GameSettings.Number("selftest-timeout", 300);
        timer.Timeout += () =>
        {
            t += 0.1;
            if (check.Passed is not { } p && t < limit)
                return;
            bool pass = check.Passed == true;
            GD.Print(pass ? $"ffx selftest: PASS ({check.Note})" : $"ffx selftest: FAIL ({(t >= limit ? "timed out; " : "")}{check.Note})");
            timer.Stop();
            parent.GetTree().Quit(pass ? 0 : 1);
        };
    }
}
