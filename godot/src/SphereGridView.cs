using System.Linq;
using Godot;

namespace GdFfx;

/// <summary>
/// G4's sphere grid screen, from the game-state API's <c>SphereGrid</c> while the game has the
/// grid up (main menu slot 0x13, <see cref="FieldMenuView.Screens"/>): the links and the nodes
/// fitted to the screen, empty nodes dim, the nodes the shown character has activated (the
/// node's flags, bit 8 + the character; observed) marked, the game's cursor ringed with its
/// node's name (the kernel's <c>panel.bin</c>: <see cref="FfxGame.GridNodeName"/>), and the
/// character's name. The game
/// keeps the grid's logic (moving, activating); this only draws it.
/// </summary>
public partial class SphereGridView : Control
{
    /// <summary>Nodes, links and nodes marked for the shown character in the last frame drawn (for tests).</summary>
    public (int Nodes, int Links, int Marked) Shown { get; private set; }

    /// <summary>The name of the node under the game's cursor ("" none; for tests).</summary>
    public string CursorName { get; private set; } = "";

    private Label _title = null!;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _title = new Label { Visible = false };
        _title.AddThemeFontSizeOverride("font_size", 24);
        _title.AddThemeColorOverride("font_outline_color", Colors.Black);
        _title.AddThemeConstantOverride("outline_size", 5);
        _title.Position = new Vector2(24, 16);
        AddChild(_title);
    }

    /// <summary>Whether node flags mark character <paramref name="chr"/> (bit 8 + chr).</summary>
    public static bool Marked(ushort flags, int chr) => chr is >= 0 and < 8 && (flags >> (8 + chr) & 1) != 0;

    private static bool Open(GdFfx.Api.Snapshot? s) =>
        s != null && s.SphereGrid.Open && FieldMenuView.OpenScreen(s.Menus.MainMask) == "sphere grid";

    private bool _wasOpen;

    public override void _Process(double delta)
    {
        var t0 = Perf.Start();
        Tick(delta);
        Perf.End(Perf.Section.SphereGrid, t0);
    }

    private void Tick(double delta)
    {
        var s = GameHost.Instance?.View.Current;
        var open = Open(s);
        _title.Visible = open;
        if (open && GameHost.Instance?.Game is { } game)
        {
            var g = s!.SphereGrid;
            CursorName = "";
            if (g.Cursor < g.Nodes.Count)
            {
                var kind = g.Nodes[g.Cursor].Kind;
                var lang = MessageView.LanguageCode(s.World.Language);
                CursorName = kind == 0xffff ? "" : MessageView.Clean(game.GridNodeName(kind, lang));
            }
            _title.Text = $"Sphere Grid: {FieldMenuView.MemberName(game, s, g.Current)}" + (CursorName.Length > 0 ? $"   ▶ {CursorName}" : "");
        }
        // drawn while open, and once more when it closes
        if (open || _wasOpen)
            QueueRedraw();
        _wasOpen = open;
    }

    public override void _Draw()
    {
        var s = GameHost.Instance?.View.Current;
        if (!Open(s))
        {
            Shown = (0, 0, 0);
            return;
        }
        var g = s!.SphereGrid;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.02f, 0.03f, 0.08f, 0.92f));
        int minX = g.Nodes.Min(n => (int)n.X), maxX = g.Nodes.Max(n => (int)n.X);
        int minY = g.Nodes.Min(n => (int)n.Y), maxY = g.Nodes.Max(n => (int)n.Y);
        var margin = 40f;
        float scale = Mathf.Min((Size.X - 2 * margin) / Mathf.Max(1, maxX - minX), (Size.Y - 2 * margin) / Mathf.Max(1, maxY - minY));
        var origin = new Vector2(Size.X / 2 - (minX + maxX) / 2f * scale, Size.Y / 2 - (minY + maxY) / 2f * scale);
        Vector2 At(GdFfx.Api.GridNode n) => origin + new Vector2(n.X, n.Y) * scale;
        foreach (var l in g.Links)
            if (l.A < g.Nodes.Count && l.B < g.Nodes.Count)
                DrawLine(At(g.Nodes[l.A]), At(g.Nodes[l.B]), new Color(0.35f, 0.38f, 0.6f), 1.5f);
        int marked = 0;
        float r = Mathf.Max(2f, 7f * scale);
        foreach (var n in g.Nodes)
        {
            Color c;
            if (Marked(n.Flags, g.Current))
            {
                c = new Color(1f, 0.85f, 0.3f);
                marked++;
            }
            else if (n.Kind == 0xffff)
                c = new Color(0.3f, 0.3f, 0.35f);
            else
                c = Color.FromHsv(n.Kind * 0.137f % 1f, 0.45f, 0.95f);
            DrawCircle(At(n), r, c);
        }
        if (g.Cursor < g.Nodes.Count)
            DrawArc(At(g.Nodes[g.Cursor]), r * 2.2f, 0, Mathf.Tau, 24, new Color(1f, 1f, 1f), 2.5f);
        Shown = (g.Nodes.Count, g.Links.Count, marked);
    }
}
