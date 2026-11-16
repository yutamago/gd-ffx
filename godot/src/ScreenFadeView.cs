using Godot;

namespace GdFfx;

/// <summary>
/// G3: the game's screen fade draw event (ffx-rust's effect capture, event kind 2,
/// <c>PostProcessManager_renderScreenFade</c>; docs/formats/effects-draw.md "Draw events" there):
/// a full-screen quad of the event's colour (RGBA bytes / 128) over the picture, blended as the
/// event says: 2 additive, 0x10 subtract (the picture minus the colour), else the alpha blend.
/// The game draws it after the effects and before the screen distortion, so it sits below
/// <see cref="DistortionView"/> in the overlay. The battle's fade-in uses it: black at full
/// alpha, then down by 1/16 a frame. Hidden in frames without one.
/// </summary>
public partial class ScreenFadeView : Control
{
    private ColorRect _quad = null!;
    private readonly CanvasItemMaterial[] _blends =
    [
        new() { BlendMode = CanvasItemMaterial.BlendModeEnum.Mix },
        new() { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        new() { BlendMode = CanvasItemMaterial.BlendModeEnum.Sub },
    ];

    /// <summary>The fade shown in the last frame (null none; for tests).</summary>
    public EffectDrawView.ScreenFade? Shown { get; private set; }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _quad = new ColorRect { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _quad.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_quad);
    }

    /// <summary>This frame's fade (null hides it).</summary>
    public void Show(EffectDrawView.ScreenFade? fade)
    {
        Shown = fade;
        if (fade is not { } f || f.Colour.A <= 0f)
        {
            _quad.Visible = false;
            return;
        }
        var c = f.Colour;
        _quad.Color = new Color(Mathf.Min(c.R, 1f), Mathf.Min(c.G, 1f), Mathf.Min(c.B, 1f), Mathf.Min(c.A, 1f));
        _quad.Material = _blends[Mathf.Clamp(f.Blend, 0, 2)];
        _quad.Visible = true;
    }
}
