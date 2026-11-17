using System.Runtime.CompilerServices;
using Godot;

namespace GdFfx;

/// <summary>
/// Label updates that do nothing when nothing changed (G5). The views set their texts each
/// frame; a colour override set again, even the same, makes Godot re-theme the label and lay
/// out its container again (in battle 2.8 ms a frame), and each text set crosses into Godot.
/// </summary>
public static class Ui
{
    private sealed class Last
    {
        public string? Text;
        public Color? Colour;
    }

    private static readonly ConditionalWeakTable<Label, Last> Seen = new();

    /// <summary>Sets <paramref name="l"/>'s text when it differs from the last one set here.</summary>
    public static void SetText(Label l, string text)
    {
        var last = Seen.GetOrCreateValue(l);
        if (last.Text == text)
            return;
        last.Text = text;
        l.Text = text;
    }

    /// <summary>Sets <paramref name="l"/>'s <c>font_color</c> override when it differs from the last one set here.</summary>
    public static void SetColour(Label l, Color colour)
    {
        var last = Seen.GetOrCreateValue(l);
        if (last.Colour == colour)
            return;
        last.Colour = colour;
        l.AddThemeColorOverride("font_color", colour);
    }
}

/// <summary>
/// The game-like look of gd-ffx's settings controls (G5; the options overlay and the launcher's
/// settings): the blue window's colours, checkboxes drawn as framed boxes that read on the dark
/// window whether ticked or not (a ticked one filled in the cursor's gold with a dark tick; a
/// disabled one dimmed), and a bright gold frame on the control with the keyboard or pad focus,
/// as the menus light their cursor row (<see cref="MenuList"/>).
/// </summary>
public static class GameTheme
{
    /// <summary>The window's frame colour, the focus frame's (the cursor's gold), the controls' fill.</summary>
    public static readonly Color Frame = new(0.8f, 0.85f, 1f, 0.9f);
    public static readonly Color Focus = new(1f, 0.85f, 0.35f);
    public static readonly Color Fill = new(0.05f, 0.1f, 0.3f, 0.92f);
    public static readonly Color FillLit = new(0.2f, 0.32f, 0.7f, 0.95f);

    private static Theme? _theme;

    /// <summary>The theme (made once).</summary>
    public static Theme Get() => _theme ??= Make();

    private static StyleBoxFlat Box(Color bg, Color border, int width, bool center = true, int margin = 6) => new()
    {
        BgColor = bg,
        DrawCenter = center,
        BorderColor = border,
        BorderWidthLeft = width, BorderWidthRight = width, BorderWidthTop = width, BorderWidthBottom = width,
        CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        ContentMarginLeft = margin, ContentMarginRight = margin, ContentMarginTop = 3, ContentMarginBottom = 3,
    };

    /// <summary>The focus frame: drawn over the control, a little outside it.</summary>
    private static StyleBoxFlat FocusFrame()
    {
        var f = Box(new Color(0, 0, 0, 0), Focus, 2, center: false);
        f.ExpandMarginLeft = f.ExpandMarginRight = f.ExpandMarginTop = f.ExpandMarginBottom = 2;
        f.ShadowColor = new Color(Focus, 0.35f);
        f.ShadowSize = 4;
        return f;
    }

    /// <summary>A checkbox's box: a frame on a dark fill, with a tick when <paramref name="on"/>.</summary>
    private static ImageTexture Check(bool on, bool disabled, int size = 22)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float dim = disabled ? 0.45f : 1f;
        var border = new Color(Frame.R * dim, Frame.G * dim, Frame.B * dim, 1f);
        var fill = on ? new Color(Focus.R * dim, Focus.G * dim, Focus.B * dim, 1f) : new Color(0.01f, 0.03f, 0.1f, 1f);
        var tick = new Color(0.05f, 0.07f, 0.18f, 1f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool edge = x < 2 || y < 2 || x >= size - 2 || y >= size - 2;
                bool corner = (x == 0 || x == size - 1) && (y == 0 || y == size - 1);
                img.SetPixel(x, y, corner ? new Color(0, 0, 0, 0) : edge ? border : fill);
            }
        if (on)
        {
            // a tick: down from (5, 11) to (9, 15), up to (16, 6), three pixels thick
            void Line(Vector2 a, Vector2 b)
            {
                for (float t = 0; t <= 1; t += 0.02f)
                {
                    var p = a.Lerp(b, t) * (size / 22f);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            img.SetPixel(Mathf.Clamp((int)p.X + dx, 2, size - 3), Mathf.Clamp((int)p.Y + dy, 2, size - 3), tick);
                }
            }
            Line(new Vector2(5.5f, 11), new Vector2(9.5f, 15.5f));
            Line(new Vector2(9.5f, 15.5f), new Vector2(16.5f, 6));
        }
        return ImageTexture.CreateFromImage(img);
    }

    private static Theme Make()
    {
        var t = new Theme();
        var text = MenuList.TextColour;
        var lit = MenuList.LitColour;
        var dim = MenuList.DimColour;
        // checkboxes: the boxes, a light bar under the hovered or focused one, the gold frame on focus
        foreach (var (name, on, off) in new[] { ("checked", true, false), ("unchecked", false, false), ("checked_disabled", true, true), ("unchecked_disabled", false, true) })
            t.SetIcon(name, "CheckBox", Check(on, off));
        var clear = Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 0);
        var hover = Box(new Color(0.35f, 0.5f, 0.95f, 0.3f), new Color(0, 0, 0, 0), 0);
        foreach (var s in new[] { "normal", "pressed", "disabled" })
            t.SetStylebox(s, "CheckBox", clear);
        t.SetStylebox("hover", "CheckBox", hover);
        t.SetStylebox("hover_pressed", "CheckBox", hover);
        t.SetStylebox("focus", "CheckBox", FocusFrame());
        // buttons and drop-down lists: the window's fill, lit when hovered, the gold frame on focus
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            t.SetStylebox("normal", type, Box(Fill, new Color(0.55f, 0.62f, 0.85f, 0.8f), 1, margin: 8));
            t.SetStylebox("hover", type, Box(FillLit, Frame, 1, margin: 8));
            t.SetStylebox("pressed", type, Box(FillLit, Focus, 1, margin: 8));
            t.SetStylebox("hover_pressed", type, Box(FillLit, Focus, 1, margin: 8));
            t.SetStylebox("disabled", type, Box(new Color(0.04f, 0.06f, 0.14f, 0.7f), new Color(0.35f, 0.38f, 0.5f, 0.6f), 1, margin: 8));
            t.SetStylebox("focus", type, FocusFrame());
            t.SetColor("font_color", type, text);
            t.SetColor("font_hover_color", type, lit);
            t.SetColor("font_focus_color", type, lit);
            t.SetColor("font_pressed_color", type, lit);
            t.SetColor("font_hover_pressed_color", type, lit);
            t.SetColor("font_disabled_color", type, dim);
        }
        t.SetColor("font_color", "CheckBox", text);
        t.SetColor("font_hover_color", "CheckBox", lit);
        t.SetColor("font_focus_color", "CheckBox", lit);
        t.SetColor("font_disabled_color", "CheckBox", dim);
        // the lists' pop-up: a window, the hovered row lit as the menus' cursor row
        t.SetStylebox("panel", "PopupMenu", Panels.Window(0.97f, 6, 6));
        t.SetStylebox("hover", "PopupMenu", Box(new Color(0.35f, 0.5f, 0.95f, 0.55f), Frame, 1));
        t.SetColor("font_color", "PopupMenu", text);
        t.SetColor("font_hover_color", "PopupMenu", lit);
        // the slider: a framed track, filled up to the grabber, the gold frame on focus
        var track = Box(new Color(0.01f, 0.03f, 0.1f, 1f), Frame, 1, margin: 0);
        track.ContentMarginTop = track.ContentMarginBottom = 3;
        t.SetStylebox("slider", "HSlider", track);
        var area = Box(new Color(0.4f, 0.55f, 0.95f, 1f), new Color(0, 0, 0, 0), 0, margin: 0);
        area.ContentMarginTop = area.ContentMarginBottom = 3;
        t.SetStylebox("grabber_area", "HSlider", area);
        var areaLit = Box(new Color(Focus, 1f), new Color(0, 0, 0, 0), 0, margin: 0);
        areaLit.ContentMarginTop = areaLit.ContentMarginBottom = 3;
        t.SetStylebox("grabber_area_highlight", "HSlider", areaLit);
        t.SetStylebox("focus", "HSlider", FocusFrame());
        return t;
    }
}
