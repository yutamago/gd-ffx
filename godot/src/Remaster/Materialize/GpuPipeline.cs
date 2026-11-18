using System.Collections.Generic;
using System.Diagnostics;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.Remaster.Materialize;

/// <summary>
/// The workbench's preview on the GPU (docs/remaster-materials.md, "Speed"): the graph at the
/// working size over a <see cref="GpuBackend"/>; a change builds it again (only the passes whose
/// signature changed draw, in the next frame); the occlusion settles over frames, four
/// directions a frame (<see cref="Tick"/>). The maps are the passes' targets themselves
/// (<see cref="Texture"/>): the preview's material samples them, nothing is copied.
/// </summary>
public sealed class GpuPipeline
{
    private MaterialPipeline.Inputs? _inputs;
    private (int W, int H, double S) _size;
    private int _aoDone = 1;
    private readonly Stopwatch _clock = new();

    public GpuBackend Backend { get; }

    /// <summary>The backend's commits before the last <see cref="Update"/> (with <see cref="GpuBackend.DrawnSince"/>: what it drew).</summary>
    public int UpdateMark { get; private set; }

    public MaterializeOutputs? Outputs { get; private set; }

    /// <summary>The colour sample shown as a mask, or null.</summary>
    public (MaterialStep Step, int Sample)? Isolate { get; set; }

    /// <summary>Milliseconds from the last change to its passes drawn (seen at the frame after), and to the occlusion settled.</summary>
    public double DrawnMs { get; private set; }

    public double SettledMs { get; private set; }

    public GpuPipeline(Node parent)
    {
        Backend = new GpuBackend();
        parent.AddChild(Backend);
    }

    /// <summary>The maps of <paramref name="inputs"/> at <paramref name="side"/>: built now, drawn this frame; the occlusion from its first directions again.</summary>
    public void Update(MaterialPipeline.Inputs inputs, int side)
    {
        _inputs = inputs;
        UpdateMark = Backend.Commits;
        _size = MaterialPipeline.Size(inputs, side);
        _aoDone = 1;
        _clock.Restart();
        (DrawnMs, SettledMs) = (-1, -1);
        Build();
    }

    private void Build()
    {
        var i = _inputs!;
        Backend.Begin();
        Outputs = MaterializeGraph.Build(Backend, i.Key, i.Original, i.Imports, i.Recipe, new BuildRequest(_size.W, _size.H, _size.S, _aoDone, Isolate));
        Backend.Commit();
    }

    /// <summary>Once a frame: the times, and the occlusion's next directions once the last are drawn. True when something new was drawn.</summary>
    public bool Tick()
    {
        if (Outputs == null || !Backend.Drawn)
            return false;
        if (DrawnMs < 0)
        {
            // the change is drawn: a tick of its own (the occlusion's next directions on the next)
            DrawnMs = _clock.Elapsed.TotalMilliseconds;
            return true;
        }
        if (Outputs.AoDone < Outputs.AoChunks)
        {
            _aoDone = Outputs.AoDone + 1;
            Build();
            return true;
        }
        if (SettledMs < 0)
        {
            SettledMs = _clock.Elapsed.TotalMilliseconds;
            return true;
        }
        return false;
    }

    /// <summary>Whether every pass is drawn and the occlusion has all its directions.</summary>
    public bool Settled => Outputs != null && Backend.Drawn && Outputs.AoDone >= Outputs.AoChunks;

    public Texture2D? Texture(MaterialStep s) => Outputs?.Maps.TryGetValue(s, out var t) == true ? ((GpuTex)t).Texture : null;

    public Texture2D? Orm => (Outputs?.Orm as GpuTex)?.Texture;

    public Texture2D? Mask => (Outputs?.Mask as GpuTex)?.Texture;

    /// <summary>An aux map's target (the detail's albedo or normal), null: none.</summary>
    public Texture2D? Aux(string name) => Outputs?.Aux.TryGetValue(name, out var t) == true ? ((GpuTex)t).Texture : null;

    /// <summary>A map's pixels (a read back: waits for the GPU).</summary>
    public RgbaImage? Pixels(MaterialStep s) => Outputs?.Maps.TryGetValue(s, out var t) == true && Backend.Drawn ? Backend.Read(t) : null;

    public RgbaImage? OrmPixels => Outputs != null && Backend.Drawn ? Backend.Read(Outputs.Orm) : null;

    /// <summary>The colour check (four 1x1 read backs).</summary>
    public ColourCheck? Check()
    {
        if (Outputs?.SourceStats is not { } s || Outputs.AlbedoStats is not { } a || !Backend.Drawn)
            return null;
        var cs = Backend.Pixel(s);
        var ca = Backend.Pixel(a);
        return new ColourCheck(cs.R, cs.G, ca.R, ca.G);
    }
}

/// <summary>
/// The bakes' passes kept a while after a bake (R2b phase 5): a save, a tweak and a save again, or
/// Apply to queue's textures one after another, draw into the targets the last bake allocated
/// (at the same size nothing is allocated again: a 4096 px bake about 1 s on the Arc 140T against
/// 2.5 s with new targets). Freed when idle for <see cref="KeepSeconds"/>. Main thread.
/// </summary>
public static class BakeBackends
{
    /// <summary>How long a bake's passes are kept after it (their targets: about 5 GB at 4096 px).</summary>
    public const double KeepSeconds = 15;

    private static GpuBackend? _kept;
    private static ulong _since;

    /// <summary>The kept passes (under <paramref name="parent"/>), or a new backend.</summary>
    public static GpuBackend Take(Node parent)
    {
        var b = _kept is { } k && GodotObject.IsInstanceValid(k) ? k : null;
        _kept = null;
        if (b == null)
        {
            b = new GpuBackend();
            parent.AddChild(b);
        }
        else if (b.GetParent() != parent)
            b.Reparent(parent);
        return b;
    }

    /// <summary>A bake's passes kept for the next bake.</summary>
    public static void Return(GpuBackend b)
    {
        if (_kept != null && _kept != b && GodotObject.IsInstanceValid(_kept))
            Free(_kept);
        (_kept, _since) = (b, Time.GetTicksMsec());
    }

    /// <summary>Frees the kept passes when idle long enough (<paramref name="now"/>: at once).</summary>
    public static void Trim(bool now = false)
    {
        if (_kept == null || !now && Time.GetTicksMsec() - _since < KeepSeconds * 1000)
            return;
        if (GodotObject.IsInstanceValid(_kept))
            Free(_kept);
        _kept = null;
    }

    private static void Free(GpuBackend b)
    {
        b.Clear();
        b.QueueFree();
    }
}

/// <summary>Every map made at full size on the GPU in one frame (no progressive part), read back.</summary>
public sealed class GpuBake
{
    private readonly GpuBackend _b;
    private readonly MaterializeOutputs _outs;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly bool _keep;

    /// <summary>The bake's graph build (ms: the passes made, their targets allocated) and the commit.</summary>
    public double BuildMs { get; }

    /// <summary>
    /// A bake of <paramref name="inputs"/> at <paramref name="w"/>x<paramref name="h"/>, drawn in the
    /// next frame. <paramref name="reuse"/>: a backend of an earlier bake (its passes, and their
    /// targets, kept: nothing allocated again at the same size; every pass drawn again), not freed
    /// by <see cref="Finish"/>.
    /// </summary>
    public GpuBake(Node parent, MaterialPipeline.Inputs inputs, int w, int h, double scale, GpuBackend? reuse = null)
    {
        _keep = reuse != null;
        _b = reuse ?? new GpuBackend();
        if (reuse == null)
            parent.AddChild(_b);
        else
            _b.Invalidate();
        _b.Begin();
        _outs = MaterializeGraph.Build(_b, inputs.Key, inputs.Original, inputs.Imports, inputs.Recipe, new BuildRequest(w, h, scale));
        _b.Commit();
        BuildMs = _clock.Elapsed.TotalMilliseconds;
    }

    public bool Done => _b.Drawn;

    public GpuBackend Backend => _b;

    /// <summary>The passes the bake draws.</summary>
    public int Passes => _b.Count;

    /// <summary>Waits for the GPU to finish the bake (a 1x1 read back).</summary>
    public void Sync()
    {
        if ((_outs.AlbedoStats ?? _outs.SourceStats) is { } t)
            _ = _b.Pixel(t);
    }

    /// <summary>Each read back of the last <see cref="Finish"/> (what, ms): the bake profile.</summary>
    public List<(string What, double Ms)> Reads { get; } = new();

    /// <summary>
    /// The maps a save writes (8-bit: the albedo, the normal, the emission unless skipped, the added
    /// maps; <paramref name="all"/>: every map, the checks' parity), the ORM pack, the height as
    /// stored (float), the aux maps; the time from the start to the last read back. Frees the passes.
    /// The occlusion, roughness and metallic are in the ORM; the source and the 8-bit height are not
    /// saved (each a float target: its read back converts 16 million pixels).
    /// </summary>
    public (BakedMaps Baked, double Ms) Finish(bool all = false, PbrRecipe? recipe = null)
    {
        var maps = new Dictionary<MaterialStep, RgbaImage>();
        Reads.Clear();
        var clock = Stopwatch.StartNew();
        double last = 0;
        void Mark(string what)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            Reads.Add((what, now - last));
            last = now;
        }
        foreach (var (s, t) in _outs.Maps)
        {
            if (!all && !Saved(s, recipe))
                continue;
            maps[s] = _b.Read(t);
            Mark(s.ToString());
        }
        var orm = _b.Read(_outs.Orm);
        Mark("orm");
        var height = _b.ReadImage(_outs.Maps[MaterialStep.Height]);
        Mark("height (float)");
        var aux = new Dictionary<string, RgbaImage>();
        foreach (var (name, t) in _outs.Aux)
        {
            aux[name] = _b.Read(t);
            Mark(name);
        }
        double ms = _clock.Elapsed.TotalMilliseconds;
        if (!_keep)
        {
            _b.Clear();
            _b.QueueFree();
        }
        return (new BakedMaps(maps, orm, height, aux), ms);
    }

    /// <summary>Whether a save writes step <paramref name="s"/>'s own map (the ORM's three and the source never; the emission unless skipped).</summary>
    private static bool Saved(MaterialStep s, PbrRecipe? r) => s switch
    {
        MaterialStep.Source or MaterialStep.Height or MaterialStep.Roughness or MaterialStep.Metallic or MaterialStep.Occlusion => false,
        MaterialStep.Emission => r == null || MaterialPipeline.ModeOf(r, MaterialStep.Emission) != StepMode.Skip,
        _ => true,
    };
}
