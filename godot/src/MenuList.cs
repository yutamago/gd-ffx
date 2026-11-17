using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// The cursor of gd-ffx's native menus (G5): a pointing hand drawn as a shape (a filled
/// triangle with an outline), so it shows whatever font the text has (the <c>▶</c> glyph is
/// missing from some fonts, Godot's default among them).
/// </summary>
public partial class CursorMark : Control
{
    /// <summary>Whether it is drawn (the row is the highlighted one).</summary>
    public bool On
    {
        get => _on;
        set
        {
            if (_on == value)
                return;
            _on = value;
            QueueRedraw();
        }
    }

    private bool _on;

    /// <summary>The fill (the outline is black).</summary>
    public Color Colour { get; set; } = new(1f, 0.93f, 0.55f);

    public CursorMark()
    {
        CustomMinimumSize = new Vector2(18, 18);
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public override void _Draw()
    {
        if (!_on)
            return;
        var s = Size;
        float h = Mathf.Min(s.Y, 18), w = h * 0.85f;
        var o = new Vector2(1, (s.Y - h) / 2);
        Vector2[] tri = [o, o + new Vector2(w, h / 2), o + new Vector2(0, h)];
        DrawColoredPolygon(tri, Colour);
        DrawPolyline([tri[0], tri[1], tri[2], tri[0]], new Color(0, 0, 0, 0.85f), 1.5f, true);
    }
}

/// <summary>
/// A list of rows with one highlighted (G5): the row's bar lit, the cursor drawn before it
/// (<see cref="CursorMark"/>), its text bright; rows that cannot be chosen dimmed. The battle
/// command menu, the message choices and the ESC menu draw their rows with it. A row's text after
/// a tab is a button at its right (the ESC menu's Change parameters rows: "Get").
/// </summary>
public partial class MenuList : VBoxContainer
{
    private readonly List<(PanelContainer Bar, CursorMark Mark, Label Label, PanelContainer Button, Label ButtonText)> _rows = new();
    private readonly StyleBoxFlat _lit, _unlit, _button;
    private readonly int _fontSize;
    private readonly Font? _font;

    /// <summary>The rows shown now, the highlighted one marked <c>▶ </c> (for tests and logs).</summary>
    public List<string> Shown { get; } = new();

    /// <summary>The highlighted row (-1 none).</summary>
    public int Cursor { get; private set; } = -1;

    public static readonly Color TextColour = new(0.92f, 0.94f, 1f);
    public static readonly Color LitColour = new(1f, 0.96f, 0.7f);
    public static readonly Color DimColour = new(0.55f, 0.58f, 0.66f);

    public MenuList(int fontSize = 22, Font? font = null)
    {
        _fontSize = fontSize;
        _font = font;
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeConstantOverride("separation", 2);
        _lit = new StyleBoxFlat
        {
            BgColor = new Color(0.35f, 0.5f, 0.95f, 0.55f),
            BorderColor = new Color(0.75f, 0.85f, 1f, 0.9f),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = 4, ContentMarginRight = 10, ContentMarginTop = 1, ContentMarginBottom = 1,
        };
        _unlit = new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            ContentMarginLeft = 4, ContentMarginRight = 10, ContentMarginTop = 1, ContentMarginBottom = 1,
        };
        _button = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.1f, 0.3f, 0.9f),
            BorderColor = new Color(0.8f, 0.85f, 1f, 0.9f),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 0, ContentMarginBottom = 0,
        };
    }

    /// <summary>Shows <paramref name="rows"/> with row <paramref name="cursor"/> highlighted; <paramref name="enabled"/> null: all can be chosen.</summary>
    public void SetRows(IReadOnlyList<string> rows, int cursor, IReadOnlyList<bool>? enabled = null)
    {
        Cursor = cursor >= 0 && cursor < rows.Count ? cursor : -1;
        while (_rows.Count < rows.Count)
        {
            var bar = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
            bar.AddThemeStyleboxOverride("panel", _unlit);
            var box = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            box.AddThemeConstantOverride("separation", 6);
            var mark = new CursorMark { CustomMinimumSize = new Vector2(_fontSize * 0.8f, _fontSize * 0.8f) };
            var label = new Label();
            if (_font != null)
                label.AddThemeFontOverride("font", _font);
            label.AddThemeFontSizeOverride("font_size", _fontSize);
            label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var button = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            button.AddThemeStyleboxOverride("panel", _button);
            var buttonText = new Label();
            if (_font != null)
                buttonText.AddThemeFontOverride("font", _font);
            buttonText.AddThemeFontSizeOverride("font_size", (int)(_fontSize * 0.8f));
            button.AddChild(buttonText);
            box.AddChild(mark);
            box.AddChild(label);
            box.AddChild(button);
            bar.AddChild(box);
            AddChild(bar);
            _rows.Add((bar, mark, label, button, buttonText));
        }
        Shown.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            var (bar, mark, label, button, buttonText) = _rows[i];
            bar.Visible = i < rows.Count;
            if (i >= rows.Count)
                continue;
            bool on = i == Cursor, can = enabled == null || i >= enabled.Count || enabled[i];
            if (bar.HasMeta("lit") != on)
            {
                bar.AddThemeStyleboxOverride("panel", on ? _lit : _unlit);
                if (on)
                    bar.SetMeta("lit", true);
                else
                    bar.RemoveMeta("lit");
            }
            mark.On = on;
            int tab = rows[i].IndexOf('\t');
            Ui.SetText(label, tab < 0 ? rows[i] : rows[i][..tab]);
            var colour = !can ? DimColour : on ? LitColour : TextColour;
            Ui.SetColour(label, colour);
            button.Visible = tab >= 0;
            if (tab >= 0)
            {
                Ui.SetText(buttonText, rows[i][(tab + 1)..]);
                Ui.SetColour(buttonText, colour);
            }
            Shown.Add((on ? "▶ " : "   ") + (tab < 0 ? rows[i] : $"{rows[i][..tab]} [{rows[i][(tab + 1)..]}]"));
        }
    }
}

/// <summary>The look shared by gd-ffx's native panels.</summary>
public static class Panels
{
    /// <summary>The game-like blue window: a translucent fill, a light border, rounded corners.</summary>
    public static StyleBoxFlat Window(float alpha = 0.85f, int marginX = 14, int marginY = 8) => new()
    {
        BgColor = new Color(0.04f, 0.08f, 0.22f, alpha),
        BorderColor = new Color(0.8f, 0.85f, 1f, 0.9f),
        BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
        CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        ContentMarginLeft = marginX, ContentMarginRight = marginX + 4, ContentMarginTop = marginY, ContentMarginBottom = marginY + 2,
    };

    /// <summary>A label in the given size and colour (outlined, readable over the picture).</summary>
    public static Label Text(string text, int size, Color? colour = null, Font? font = null)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        if (font != null)
            l.AddThemeFontOverride("font", font);
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", colour ?? MenuList.TextColour);
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        return l;
    }
}
