using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.Remaster.Materialize;

/// <summary>A GPU image: a pass's offscreen target or an uploaded input.</summary>
public sealed class GpuTex : Tex
{
    public Texture2D Texture { get; internal set; } = null!;
}

/// <summary>
/// The pipeline on the GPU (docs/remaster-materials.md, "Speed"): every pass a
/// <see cref="SubViewport"/> drawing one rectangle with its kernel's canvas shader
/// (<see cref="GpuKernels"/>) into its own offscreen target: float (<c>use_hdr_2d</c>: half floats
/// under Forward+, 32-bit floats under Compatibility) or 8-bit (the maps as saved). Works in both
/// renderers. A pass draws once when its signature changed (<see cref="Commit"/>); the passes
/// are children of one container viewport, in the graph's order, so Godot draws them inputs
/// first, all in the same frame, before the preview's own viewports (children are drawn before
/// their parents' siblings). Main thread only.
/// </summary>
public sealed partial class GpuBackend : Node, IMaterializeBackend
{
    private sealed class PassNode
    {
        public required SubViewport Vp;
        public required ColorRect Rect;
        public required ShaderMaterial Mat;
        public required GpuTex Tex;
        public string Kernel = "";
        public string Drawn = "";
        public bool Hold;
        public Texture2D?[] Bound = new Texture2D?[9];
        public int DrawnAt = -1;
        public string[] InputSlots = [];
    }

    private readonly SubViewport _container = new() { Name = "Passes", Size = new Vector2I(2, 2), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
    private readonly Dictionary<string, PassNode> _passes = new();
    private readonly Dictionary<string, (ImageTexture Image, GpuTex Tex)> _inputs = new();
    private readonly List<string> _order = new();
    private readonly HashSet<string> _inOrder = new();
    private List<string> _lastOrder = new();
    private long _pendingFrame;
    private bool _pending;

    public GpuBackend()
    {
        Name = "GpuMaterialize";
        AddChild(_container);
    }

    /// <summary>
    /// Passes of at least this many pixels made from now on measure their GPU time (the check's
    /// bake profile: <see cref="GpuTimes"/>; 0: none). Godot keeps 256 timestamps a frame, two a
    /// viewport: only the large passes fit.
    /// </summary>
    public static long MeasureGpu { get; set; }

    /// <summary>
    /// Each pass's measured render time (ms; <see cref="MeasureGpu"/> on when it was made): GPU
    /// (timestamps, a few frames after the draw) and CPU, with its kernel and size.
    /// </summary>
    public List<(string Slot, string Kernel, int W, int H, bool Hdr, double Gpu, double Cpu)> GpuTimes() =>
        _passes.Select(kv => (kv.Key, kv.Value.Kernel, kv.Value.Vp.Size.X, kv.Value.Vp.Size.Y, kv.Value.Vp.UseHdr2D,
            RenderingServer.ViewportGetMeasuredRenderTimeGpu(kv.Value.Vp.GetViewportRid()), RenderingServer.ViewportGetMeasuredRenderTimeCpu(kv.Value.Vp.GetViewportRid()))).ToList();

    /// <summary>Passes found reading a pass that draws after them (a graph's bug; the checks fail on any).</summary>
    public int OrderErrors { get; private set; }

    /// <summary>Commits so far.</summary>
    public int Commits { get; private set; }

    /// <summary>Whether the pass <paramref name="slot"/> was drawn by a commit after the <paramref name="mark"/>-th (the checks: what a change made again).</summary>
    public bool DrawnSince(string slot, int mark) => _passes.TryGetValue(slot, out var p) && p.DrawnAt > mark;

    /// <summary>Passes drawn by the commits so far (the checks: a cheap change draws few).</summary>
    public int Draws { get; private set; }

    /// <summary>The passes drawn by the last commit.</summary>
    public int LastDraws { get; private set; }

    /// <summary>The passes there are.</summary>
    public int Count => _passes.Count;

    /// <summary>Whether the passes committed last are drawn (readable).</summary>
    public bool Drawn => !_pending || (long)Engine.GetFramesDrawn() > _pendingFrame;

    /// <summary>Whether a renderer is there to draw (not under <c>--headless</c>).</summary>
    public static bool Available => DisplayServer.GetName() != "headless";

    /// <summary>Starts a build: the passes it asks for, in order.</summary>
    public void Begin()
    {
        _order.Clear();
        _inOrder.Clear();
    }

    public Tex Input(string key, RgbaImage image)
    {
        if (_inputs.TryGetValue(key, out var hit))
            return hit.Tex;
        var tex = ImageTexture.CreateFromImage(Image.CreateFromData(image.Width, image.Height, false, Image.Format.Rgba8, image.Rgba));
        var t = new GpuTex { Slot = "in:" + key, Sig = SigHash.Of("in:" + key), W = image.Width, H = image.Height, Hdr = false, Channels = 4, Texture = tex };
        if (_inputs.Count > 32)
            _inputs.Clear();
        _inputs[key] = (tex, t);
        return t;
    }

    public Tex Pass(string slot, string kernel, int w, int h, bool hdr, int channels, bool wrap, Tex?[] inputs, Uni uni, bool hold = false)
    {
        var sig = SigHash.Pass(kernel, w, h, hdr, wrap, inputs, uni);
        // a pass asked for again in the same build (a shared blur) keeps its first place: it must draw before whatever read it first
        if (_inOrder.Add(slot))
            _order.Add(slot);
        if (!_passes.TryGetValue(slot, out var p))
        {
            // the size last: a viewport with a size allocates its target at once, and again for each
            // of these when it changes (3D buffers before Disable3D, an 8-bit target before UseHdr2D:
            // R2b phase 5 found them most of a 4096 px bake's time)
            var vp = new SubViewport
            {
                Name = slot.Replace('/', '_'), Disable3D = true, UseHdr2D = hdr, TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, RenderTargetClearMode = SubViewport.ClearMode.Always,
            };
            vp.Size = new Vector2I(w, h);
            if (MeasureGpu > 0 && (long)w * h >= MeasureGpu)
                RenderingServer.ViewportSetMeasureRenderTime(vp.GetViewportRid(), true);
            var mat = new ShaderMaterial();
            var rect = new ColorRect { Size = new Vector2(w, h), Material = mat, MouseFilter = Control.MouseFilterEnum.Ignore };
            vp.AddChild(rect);
            p = new PassNode { Vp = vp, Rect = rect, Mat = mat, Tex = null! };
            _passes[slot] = p;
        }
        if (p.Vp.Size != new Vector2I(w, h) || p.Vp.UseHdr2D != hdr)
        {
            p.Vp.UseHdr2D = hdr;
            p.Vp.Size = new Vector2I(w, h);
            p.Rect.Size = new Vector2(w, h);
            p.Drawn = "";
        }
        if (p.Kernel != kernel)
        {
            p.Mat.Shader = GpuKernels.Shader(kernel);
            p.Kernel = kernel;
            p.Drawn = "";
            p.Bound = new Texture2D?[9];
        }
        p.Tex = new GpuTex { Slot = slot, Sig = sig, W = w, H = h, Hdr = hdr, Channels = channels, Texture = p.Vp.GetTexture() };
        p.InputSlots = inputs.OfType<GpuTex>().Select(t => t.Slot).Where(_passes.ContainsKey).ToArray();
        p.Hold = hold;
        // the inputs (bound again when a slot's target is another object: a pass made anew)
        for (int i = 0; i < 9; i++)
        {
            var tex = i < inputs.Length ? (inputs[i] as GpuTex)?.Texture : null;
            if (!ReferenceEquals(tex, p.Bound[i]))
            {
                p.Mat.SetShaderParameter($"t{i}", tex!);
                p.Bound[i] = tex;
            }
        }
        if (p.Drawn != sig)
        {
            p.Mat.SetShaderParameter("size", new Vector2(w, h));
            p.Mat.SetShaderParameter("wrap", wrap);
            foreach (var (name, value) in uni)
                p.Mat.SetShaderParameter("u_" + name, value switch
                {
                    float f => f,
                    int n => n,
                    bool b => b,
                    System.Numerics.Vector4 v => new Vector4(v.X, v.Y, v.Z, v.W),
                    float[] a => a,
                    _ => Variant.From(value.ToString()),
                });
        }
        return p.Tex;
    }

    /// <summary>
    /// After a build: passes no longer asked for go; when the passes or their order changed, every
    /// pass is put back in the build's order (Godot draws sibling viewports in the order they
    /// entered the tree); each pass whose signature changed (and is not held) draws once, this
    /// frame. Returns how many draw.
    /// </summary>
    public int Commit()
    {
        Commits++;
        if (!_order.SequenceEqual(_lastOrder))
        {
            foreach (var gone in _passes.Keys.Except(_order).ToList())
            {
                _passes[gone].Vp.QueueFree();
                _passes.Remove(gone);
            }
            foreach (var slot in _order)
            {
                var vp = _passes[slot].Vp;
                if (vp.GetParent() == _container)
                    _container.RemoveChild(vp);
                _container.AddChild(vp);
            }
            _lastOrder = _order.ToList();
            // every pass after the passes it reads (else it would draw from their last frame)
            var at = new Dictionary<string, int>();
            for (int i = 0; i < _order.Count; i++)
                at[_order[i]] = i;
            foreach (var slot in _order)
                foreach (var input in _passes[slot].InputSlots)
                    if (at.TryGetValue(input, out var j) && j > at[slot])
                    {
                        OrderErrors++;
                        GD.PushError($"ffx materialize: pass {slot} reads {input}, which draws after it");
                    }
        }
        int n = 0;
        foreach (var slot in _order)
        {
            var p = _passes[slot];
            if (p.Hold || p.Drawn == p.Tex.Sig)
                continue;
            p.Vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            p.Drawn = p.Tex.Sig;
            p.DrawnAt = Commits;
            n++;
        }
        if (n > 0)
        {
            _pending = true;
            _pendingFrame = (long)Engine.GetFramesDrawn();
        }
        Draws += n;
        LastDraws = n;
        return n;
    }

    /// <summary>The image of <paramref name="t"/> as 8-bit RGBA (a read back: waits for the GPU).</summary>
    public RgbaImage Read(Tex t)
    {
        var img = ((GpuTex)t).Texture.GetImage();
        if (img.GetFormat() != Image.Format.Rgba8)
            img.Convert(Image.Format.Rgba8);
        return new RgbaImage(img.GetWidth(), img.GetHeight(), img.GetData());
    }

    /// <summary>The image of <paramref name="t"/> as it is stored (float formats kept: the 16-bit height).</summary>
    public Image ReadImage(Tex t) => ((GpuTex)t).Texture.GetImage();

    /// <summary>A pass's image by its slot (null: no such pass).</summary>
    public Image? ReadSlot(string slot) => _passes.TryGetValue(slot, out var p) ? p.Vp.GetTexture().GetImage() : null;

    /// <summary>One pixel of a 1x1 image (the colour check's means).</summary>
    public Color Pixel(Tex t) => ((GpuTex)t).Texture.GetImage() is { } img && !img.IsEmpty() ? img.GetPixel(0, 0) : new Color(0, 0, 0, 0);

    /// <summary>Every pass draws again at the next commit (the check's warm bake: the targets kept).</summary>
    public void Invalidate()
    {
        foreach (var p in _passes.Values)
            p.Drawn = "";
    }

    /// <summary>Frees every pass (a new texture: another size).</summary>
    public void Clear()
    {
        foreach (var p in _passes.Values)
            p.Vp.QueueFree();
        _passes.Clear();
        _lastOrder.Clear();
        _inputs.Clear();
    }
}
