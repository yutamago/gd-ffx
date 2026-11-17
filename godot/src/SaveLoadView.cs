using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// The PC save and load screen (<c>saveload_jp.swf</c>, <c>_kr</c>, <c>_ch</c>; G5), natively.
/// The game's save machine (<c>SaveDataMachine_*</c>) reads the keys itself and drives the movie
/// only by calls (<see cref="SaveLoadState"/>): this view draws what those calls set up, laid out
/// as the original draws it at 1280 x 720 (measured on its picture): the help bar ("HELP" and
/// the help text) with the count under its right end, five rows of 1038 x 100 from y 118, 110
/// apart (the label, gold for one such as "Autosave", else the slot number; the place; the date
/// at the right; up to three party portraits; the player's name and play time; the map's
/// thumbnail at the right end), the cursor at the focused row's left, the scroll bar at the
/// right, and the pop-ups: a message box, and the question with Yes and No and the cursor on
/// the button the game says. Portraits (the movie's <c>face</c> sprite at labels <c>_1</c> ..
/// <c>_8</c>), thumbnails (classes <c>m&lt;n&gt;</c>) and the cursor are drawn from the player's
/// own movie. Shown while the game's save screen is up (<c>Menus.save_state</c>).
/// </summary>
public partial class SaveLoadView : Control
{
    /// <summary>Rows on the screen.</summary>
    public const int Rows = 5;
    private static readonly Vector2 RowSize = new(1038, 100);
    private const float RowTop = 118, RowStep = 110, RowLeft = 108;

    private readonly Dictionary<string, ImageTexture?> _art = new();
    private int _top;
    private double _time;

    /// <summary>What is drawn now, one line a row, the focused one marked (for tests).</summary>
    public List<string> Shown { get; } = new();
    public static SaveLoadView? Instance { get; private set; }
    /// <summary>Whether it drew the game's list in this run.</summary>
    public static bool EverShown { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>
    /// Whether the game's save screen is up (its machine's state, <c>g_SaveMachineState</c>: not 0,
    /// and not 0xa, the idle state a load leaves it in) and has set up the movie.
    /// </summary>
    public static bool Active =>
        GameHost.Instance?.View.Current is { } s && s.Menus.SaveState is not (0 or 0xa) && FlashScreens.SaveLoad.Shown > 0;

    public override void _Process(double delta)
    {
        _time += delta;
        var st = FlashScreens.SaveLoad;
        Visible = Active;
        Shown.Clear();
        if (!Visible)
            return;
        if (st.Focus >= 0)
        {
            if (st.Focus < _top)
                _top = st.Focus;
            if (st.Focus >= _top + Rows)
                _top = st.Focus - Rows + 1;
        }
        _top = Mathf.Clamp(_top, 0, Mathf.Max(0, Last(st) - Rows + 1));
        for (int i = _top; i < _top + Rows; i++)
            if (st.Items.TryGetValue(i, out var it))
                Shown.Add(it.Extra.Length > 0 && it.Date.Length == 0 ? $"{(i == st.Focus ? ">" : " ")}[{it.Extra}]" : $"{(i == st.Focus ? ">" : " ")}{Label(it)} | {it.Place} | {it.Date} | {it.Name} | {it.PlayTime} | {string.Join(",", it.Party)} | {it.Thumbnail}");
        if (st.Message != null)
            Shown.Add($"[message] {st.Message}");
        if (Shown.Count > 0)
            EverShown = true;
        if (st.Question is { } q)
            Shown.Add($"[question] {q.Text} ({(st.Button == 0 ? ">" : "")}{q.Yes} / {(st.Button == 1 ? ">" : "")}{q.No})");
        QueueRedraw();
    }

    private static int Last(SaveLoadState st)
    {
        int last = -1;
        foreach (var k in st.Items.Keys)
            last = Mathf.Max(last, k);
        return last;
    }

    private static string Label(SaveLoadState.Item it) => it.Label.Length > 0 ? it.Label : it.Slot;

    /// <summary>A piece of the movie (a class or a sprite at a label) as a texture, cached.</summary>
    private ImageTexture? Art(string movie, string key, int id, int frame, float scale)
    {
        if (_art.TryGetValue(key, out var t))
            return t;
        t = null;
        var game = GameHost.Instance?.Game;
        if (game != null && id >= 0 && frame >= 1)
        {
            var b = game.FlashBounds(movie, id, frame);
            if (b.Size.X > 0 && game.FlashRender(movie, id, frame, b.Position,
                    (int)Mathf.Ceil(b.Size.X * scale), (int)Mathf.Ceil(b.Size.Y * scale), scale) is { } img)
                t = ImageTexture.CreateFromImage(img);
        }
        _art[key] = t;
        return t;
    }

    private string Movie => FlashScreens.SaveLoad.Movie.Length > 0 ? FlashScreens.SaveLoad.Movie : "saveload_jp.swf";

    private ImageTexture? Thumbnail(string name)
    {
        var game = GameHost.Instance?.Game;
        if (game == null || name.Length == 0)
            return null;
        if (!_art.ContainsKey("m:" + name))
            return Art(Movie, "m:" + name, game.FlashClass(Movie, name), 1, 1f);
        return _art["m:" + name];
    }

    private ImageTexture? Face(string label)
    {
        var game = GameHost.Instance?.Game;
        if (game == null || label.Length == 0 || label == "_0")
            return null;
        if (!_art.ContainsKey("f:" + label))
        {
            var face = game.FlashClass(Movie, "saveload_jp_fla.face_565");
            return Art(Movie, "f:" + label, face, face >= 0 ? game.FlashLabel(Movie, face, label) : -1, 0.5f);
        }
        return _art["f:" + label];
    }

    private ImageTexture? Arrow()
    {
        var game = GameHost.Instance?.Game;
        if (game == null)
            return null;
        if (!_art.ContainsKey("arrow"))
            return Art(Movie, "arrow", game.FlashClass(Movie, "saveload_jp_fla.arrow_21"), 1, 1f);
        return _art["arrow"];
    }

    private static readonly Color Ink = new(0.96f, 0.97f, 1f);
    private static readonly Color Gold = new(1f, 0.82f, 0.25f);
    private static readonly Color Back = new(0.42f, 0.45f, 0.64f);
    private static readonly Color BandColour = new(0.08f, 0.08f, 0.2f, 0.92f);
    private static readonly Color RowFill = new(0.5f, 0.53f, 0.72f, 0.55f);
    private static readonly Color Border = new(0.85f, 0.88f, 1f, 0.9f);

    private void Text(string s, Vector2 at, int size, Color colour, HorizontalAlignment align = HorizontalAlignment.Left, float width = -1)
    {
        var f = MessageView.UiFont;
        // a shadow, as the movie's bitmap text has one
        DrawString(f, at + new Vector2(1.5f, 1.5f), s, align, width, size, new Color(0, 0, 0, 0.6f));
        DrawString(f, at, s, align, width, size, colour);
    }

    private void Band(Rect2 r)
    {
        // dark at the left, fading out to the right
        var c0 = BandColour;
        var c1 = new Color(BandColour, BandColour.A * 0.15f);
        DrawPolygon([r.Position, r.Position + new Vector2(r.Size.X, 0), r.End, r.Position + new Vector2(0, r.Size.Y)], [c0, c1, c1, c0]);
    }

    private void CursorAt(Vector2 tip)
    {
        float bob = (float)(Mathf.Sin(_time * 6) * 2);
        if (Arrow() is { } a)
        {
            var size = a.GetSize() * 0.85f;
            DrawTextureRect(a, new Rect2(tip + new Vector2(-size.X + bob, -size.Y / 2), size), false);
        }
        else
            DrawColoredPolygon([tip + new Vector2(bob, 0), tip + new Vector2(bob - 22, -11), tip + new Vector2(bob - 22, 11)], Gold);
    }

    public override void _Draw()
    {
        var st = FlashScreens.SaveLoad;
        var view = GetViewportRect().Size;
        // the backdrop: the movie's blue-grey, lighter at the top
        for (int i = 0; i < 12; i++)
        {
            float k = i / 12f;
            DrawRect(new Rect2(0, view.Y * k, view.X, view.Y / 12 + 1), Back.Lightened(0.08f * (1 - k)).Darkened(0.12f * k));
        }
        // the help bar
        Text("HELP", new Vector2(102, 64), 13, Ink);
        Band(new Rect2(100, 66, 1045, 31));
        Text(st.Help, new Vector2(118, 90), 22, Ink);
        Text(st.Count, new Vector2(1000, 104), 18, Ink, HorizontalAlignment.Right, 143);
        // the rows
        for (int r = 0; r < Rows; r++)
        {
            int i = _top + r;
            if (!st.Items.TryGetValue(i, out var it))
                continue;
            var at = new Vector2(RowLeft, RowTop + RowStep * r);
            var rect = new Rect2(at, RowSize);
            DrawRect(rect, RowFill);
            Band(new Rect2(at + Vector2.One, new Vector2(860, 30)));
            DrawRect(rect, Border, false, 1.5f);
            if (it.Extra.Length > 0 && it.Date.Length == 0)
            {
                // the new file's row: its text alone
                Text(it.Extra, at + new Vector2(0, 60), 24, Ink, HorizontalAlignment.Center, 860);
                if (i == st.Focus)
                    CursorAt(at + new Vector2(10, 52));
                continue;
            }
            var label = Label(it);
            bool named = it.Label.Length > 0;
            Text(label, at + new Vector2(9, 24), 22, named ? Gold : Ink);
            Text(it.Place, at + new Vector2(named ? 192 : 76, 24), 22, Ink);
            Text(it.Date, at + new Vector2(500, 24), 22, Ink, HorizontalAlignment.Right, 332);
            for (int p = 0; p < 3; p++)
                if (Face(it.Party[p]) is { } face)
                    DrawTextureRect(face, new Rect2(at + new Vector2(2 + 70 * p, 30), new Vector2(68, 68)), false);
            Text(it.Name, at + new Vector2(233, 57), 22, Ink);
            Text(it.PlayTime, at + new Vector2(233, 91), 22, Ink);
            var thumb = new Rect2(at + new Vector2(867, 1), new Vector2(170, 98));
            if (Thumbnail(it.Thumbnail) is { } tex)
                DrawTextureRect(tex, thumb, false);
            else
                DrawRect(thumb, new Color(0, 0, 0, 0.35f));
            if (i == st.Focus)
                CursorAt(at + new Vector2(10, 52));
        }
        // the scroll bar: arrows, track, the visible part
        int count = Last(st) + 1;
        var track = new Rect2(1169, 140, 12, 504);
        DrawColoredPolygon([new(1175, 117), new(1165, 129), new(1185, 129)], new Color(0.8f, 0.82f, 0.95f, 0.8f));
        DrawColoredPolygon([new(1175, 664), new(1165, 652), new(1185, 652)], new Color(0.8f, 0.82f, 0.95f, 0.8f));
        DrawRect(track, new Color(0.05f, 0.05f, 0.1f, 0.9f));
        if (count > 0)
        {
            float h = track.Size.Y * Mathf.Min(1f, Rows / (float)count);
            float y = track.Position.Y + (track.Size.Y - h) * (count > Rows ? _top / (float)(count - Rows) : 0);
            DrawRect(new Rect2(track.Position.X + 1, y + 1, track.Size.X - 2, h - 2), new Color(0.82f, 0.85f, 0.98f, 0.95f));
        }
        // the pop-ups
        if (st.Message is { } m)
            PopUp(m, null);
        if (st.Question is { } q)
            PopUp(q.Text, (q.Yes, q.No, st.Button));
    }

    private void PopUp(string text, (string Yes, string No, int Button)? buttons)
    {
        var lines = text.Replace("@el@", "\n").Split('\n');
        float h = buttons != null ? 135 : 40 + 30 * lines.Length;
        var box = new Rect2(402, buttons != null ? 287 : 360 - h / 2, 475, h);
        DrawRect(box, new Color(0.12f, 0.14f, 0.48f, 0.82f));
        DrawRect(box, Border, false, 1.5f);
        for (int l = 0; l < lines.Length; l++)
            Text(MessageView.Clean(lines[l]), box.Position + new Vector2(0, 45 + 30 * l), 22, Ink, HorizontalAlignment.Center, box.Size.X);
        if (buttons is not { } b)
            return;
        for (int k = 0; k < 2; k++)
        {
            var r = new Rect2(482 + 180 * k, 370, 135, 32);
            DrawRect(r, new Color(0.15f, 0.2f, 0.6f, 0.95f));
            DrawRect(r, Border, false, 1.2f);
            Text(k == 0 ? b.Yes : b.No, r.Position + new Vector2(0, 24), 22, Ink, HorizontalAlignment.Center, r.Size.X);
            if (k == b.Button)
                CursorAt(r.Position + new Vector2(8, 16));
        }
    }
}
