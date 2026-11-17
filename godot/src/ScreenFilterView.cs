using Godot;

namespace GdFfx;

/// <summary>
/// G3: the game's screen filters from the game-state API (<c>Effects.Filters</c>, the game's
/// <c>g_FadeSlots</c>): the fade to and from black or white, the flash and the colour filters,
/// each slot a full-screen colour over the scene in slot order, as the game's <c>Sg_DrawFilter</c>
/// (<c>0x0082c0c0</c>, ffx-rust's <c>ffx_sg::control::draw_filter</c>) hands them to
/// <c>graphicAddScreenFadeTask</c> (<see cref="Alpha"/>). The cross-fade (mode 0) and the blur (1)
/// need the previous picture and are left to post-processing later.
/// </summary>
public partial class ScreenFilterView : ColorRect
{
    /// <summary>One quad a slot (<c>g_FadeSlots</c> has five), drawn in slot order.</summary>
    private readonly ColorRect[] _slots = new ColorRect[5];
    private readonly GdFfx.Api.ScreenFilter?[] _bySlot = new GdFfx.Api.ScreenFilter?[5];

    private static readonly CanvasItemMaterial Mix = new() { BlendMode = CanvasItemMaterial.BlendModeEnum.Mix };
    private static readonly CanvasItemMaterial Sub = new() { BlendMode = CanvasItemMaterial.BlendModeEnum.Sub };

    /// <summary>The slots drawn in the last frame: (mode, alpha) each, alpha -1 for none (for tests).</summary>
    public (int Mode, float Alpha)[] Shown { get; } = new (int, float)[5];

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Color = new Color(0, 0, 0, 0);
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i] = new ColorRect { Visible = false, MouseFilter = MouseFilterEnum.Ignore, Material = Mix };
            _slots[i].SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(_slots[i]);
        }
    }

    public override void _Process(double delta)
    {
        var t0 = Perf.Start();
        Tick();
        Perf.End(Perf.Section.Filter, t0);
    }

    /// <summary>
    /// The alpha <c>Sg_DrawFilter</c> draws a slot with, from the slot as the snapshot holds it
    /// (read after the update, so after the draw's own step of the frame counter): <c>n</c> the
    /// frames left, <c>total</c> the frames in all, the integer quotients as the game takes them.
    /// Mode 2 (from black): <c>1 - (127 - n' 127 / total) / 127</c> with <c>n'</c> the count after
    /// the step (the snapshot's). Mode 3 (to black): <c>1 - (n' 127 / total) / 127</c>; at
    /// <c>n' = 0</c> the game writes 1 back and draws 1.0, so a held fade (the snapshot's 1) is
    /// fully black. Mode 4 (from white): <c>(n 255 / total) / 127</c> with <c>n</c> the count before
    /// its step (the snapshot's + 1), up to 2, clamped to 1 by the target. Mode 5 (to white):
    /// <c>(255 - n' 255 / total) / 127</c>, 255 when held. Mode 6 (a flash): <c>(n 127 / total) /
    /// 127</c>, <c>n</c> before its step. Modes 7 and 8 (the filters): <c>level / 127</c>, 8 subtracted
    /// (the task's flag 0x10). Others: not drawn (-1). A total of 0 counts as a quotient of 0.
    /// </summary>
    public static float Alpha(int mode, int frames, int total)
    {
        int Div(int x) => total == 0 ? 0 : x / total;
        float a = mode switch
        {
            2 => 1f - (127 - Div(frames * 127)) / 127f,
            3 => frames <= 1 ? 1f : 1f - Div(frames * 127) / 127f,
            4 => Div((frames + 1) * 255) / 127f,
            5 => (frames <= 1 ? 255 : 255 - Div(frames * 255)) / 127f,
            6 => Div((frames + 1) * 127) / 127f,
            7 or 8 => total / 127f,
            _ => -1f,
        };
        return a < 0 ? -1f : Mathf.Clamp(a, 0f, 1f);
    }

    private void Tick()
    {
        var e = GameHost.Instance?.View.Current?.Effects;
        System.Array.Clear(_bySlot);
        if (e != null)
            foreach (var x in e.Filters)
                if (x.Slot < _bySlot.Length)
                    _bySlot[x.Slot] = x;
        for (int i = 0; i < _slots.Length; i++)
        {
            var f = _bySlot[i];
            float a = f is { Active: true } ? Alpha(f.Mode, f.Frames, f.Level) : -1f;
            Shown[i] = (f?.Mode ?? -1, a);
            var q = _slots[i];
            if (a <= 0f)
            {
                if (q.Visible)
                    q.Visible = false;
                continue;
            }
            // modes 2 and 3 draw black; the others the slot's colour, bytes / 256
            var rgb = f!.Mode is 2 or 3 ? Colors.Black : new Color(f.Color[0] / 256f, f.Color[1] / 256f, f.Color[2] / 256f);
            var c = new Color(rgb.R, rgb.G, rgb.B, a);
            if (q.Color != c)
                q.Color = c;
            var m = f.Mode == 8 ? Sub : Mix;
            if (q.Material != m)
                q.Material = m;
            if (!q.Visible)
                q.Visible = true;
        }
    }
}
