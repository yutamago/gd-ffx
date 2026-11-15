using Godot;

namespace GdFfx;

/// <summary>
/// G3: the game's screen filters from the game-state API (<c>Effects.Filters</c>,
/// the game's <c>g_FadeSlots</c>): the fade to and from black or white, the flash
/// and the colour filter drawn as a full-screen colour over the scene, as the
/// game's <c>Sg_DrawFilter</c> does. (The cross-fade and the blur need the
/// previous picture and are left to post-processing later.)
/// </summary>
public partial class ScreenFilterView : ColorRect
{
    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Color = new Color(0, 0, 0, 0);
    }

    public override void _Process(double delta)
    {
        var e = GameHost.Instance?.View.Current?.Effects;
        var c = new Color(0, 0, 0, 0);
        if (e != null)
            foreach (var f in e.Filters)
            {
                if (!f.Active)
                    continue;
                float a = 0;
                var rgb = new Color(f.Color[0] / 255f, f.Color[1] / 255f, f.Color[2] / 255f);
                switch (f.Mode)
                {
                    // the fade: 2 from black, 3 to black (held), 4 from white, 5 to white
                    case >= 2 and <= 5 when f.Level > 0:
                    {
                        float t = Mathf.Clamp((float)f.Frames / f.Level, 0, 1);
                        a = f.Mode is 2 or 4 ? t : 1 - t;
                        rgb = f.Mode <= 3 ? Colors.Black : Colors.White;
                        break;
                    }
                    // a flash: its colour, fading with the frames left
                    case 6 when f.Level > 0:
                        a = Mathf.Clamp((float)f.Frames / f.Level, 0, 1);
                        break;
                    // a colour filter: its level / 127
                    case 7 or 8:
                        a = Mathf.Clamp(f.Level / 127f, 0, 1) * 0.5f;
                        break;
                }
                if (a > c.A)
                    c = new Color(rgb.R, rgb.G, rgb.B, a);
            }
        Color = c;
    }
}
