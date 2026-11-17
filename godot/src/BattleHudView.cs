using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// G3's battle HUD, from the game-state API's <c>Battlers</c>: the party taking
/// part (the game's battle indices 0..0x11) with HP and MP, a KO'd one in red,
/// low HP in yellow (a quarter or less, as the game colours it), and the turn
/// order: everyone on the field by the turn counter (<c>current_ctb</c>; the
/// lowest acts next). The game keeps the battle; this only draws it. Who a
/// battler is comes from the field actor with its index (its model name until
/// the API gives the characters' own names). The damage numbers are the game's
/// own (API 12's <c>Effects.numbers</c>), popped over the battler's body.
/// </summary>
public partial class BattleHudView : Control
{
    /// <summary>The first monster's battle index.</summary>
    public const int FirstMonster = 0x14;

    private PanelContainer _panel = null!;
    private VBoxContainer _rows = null!;
    private Label _order = null!;

    /// <summary>The party rows shown now, and the turn order (for tests).</summary>
    public List<string> Shown { get; } = new();
    public string Order { get; private set; } = "";

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.08f, 0.22f, 0.8f),
            BorderColor = new Color(0.8f, 0.85f, 1f, 0.9f),
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 6, ContentMarginBottom = 8,
        });
        AddChild(_panel);
        _rows = new VBoxContainer();
        _panel.AddChild(_rows);
        _order = new Label { Visible = false };
        _order.AddThemeFontSizeOverride("font_size", 16);
        _order.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 1f));
        _order.AddThemeColorOverride("font_outline_color", Colors.Black);
        _order.AddThemeConstantOverride("outline_size", 4);
        AddChild(_order);
    }

    private readonly Dictionary<(string, string), string> _names = new();
    private readonly List<Pop> _pops = new();
    private readonly HashSet<NumberKey> _known = new();
    private readonly List<NumberKey> _stale = new();
    private readonly Dictionary<int, int> _lastHp = new();
    private uint _lastUpdate;

    /// <summary>
    /// A game damage number's identity across snapshots: the update it was put in (the snapshot's
    /// update less its age) with its battler, colour, word and value.
    /// </summary>
    public readonly record struct NumberKey(uint Born, int Battler, int Colour, int Word, int Value);

    /// <summary>The key of <paramref name="n"/> in snapshot <paramref name="s"/>.</summary>
    public static NumberKey KeyOf(GdFfx.Api.Snapshot s, GdFfx.Api.BattleNumber n) =>
        new(s.Update - n.Age, n.Battler, n.Colour, n.Word, n.Value);

    private sealed class Pop
    {
        public required NumberKey Key;
        public required Vector3 At;
        /// <summary>Its characters as the game puts them: the digits lowest first, then the word.</summary>
        public required List<Label> Chars;
        public int Age, Life;
        public bool Seen;
    }

    /// <summary>Damage numbers popped so far (for tests).</summary>
    public int Popped { get; private set; }

    /// <summary>The game's numbers popped so far, in order (for tests).</summary>
    public List<NumberKey> PoppedNumbers { get; } = new();

    /// <summary>Of <see cref="Popped"/>, the HP losses above the HP the battler had: overkill (for tests).</summary>
    public int PoppedKills { get; private set; }

    /// <summary>The PS2 screen's height, in which the game moves its numbers.</summary>
    public const float Ps2Height = 416f;

    /// <summary>The field actor drawing battler <paramref name="index"/>, or null.</summary>
    public static GdFfx.Api.Actor? ActorOf(GdFfx.Api.Snapshot s, int index)
    {
        if (index < FirstMonster)
        {
            foreach (var a in s.Actors)
                if (a.Kind == 0 && a.Id == index + 1)
                    return a;
            return null;
        }
        int k = index - FirstMonster;
        foreach (var a in s.Actors)
            if (a.Kind == 1 && k-- == 0)
                return a;
        return null;
    }

    /// <summary>
    /// The word a number ends with (a glyph of the game's damage font; ffx-rust's
    /// docs/state-api.md, <c>BattleNumber.word</c>): 0x3a MP, 0x3d Overkill. 0x3b and 0x3c are
    /// taken to be the font's other two words, Miss and Immune, by their order in the PS2
    /// font (MP, MISS, IMMUNE, OVERKILL); not seen in a run yet.
    /// </summary>
    public static string WordText(int word) => word switch
    {
        0 => "",
        0x3a => "MP",
        0x3b => "Miss",
        0x3c => "Immune",
        0x3d => "Overkill",
        _ => $"?{word:x2}",
    };

    private Label NewChar(string text, int colour, int size)
    {
        var l = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        l.AddThemeFontSizeOverride("font_size", size);
        // the game's colours: 0 and 2 grey (white on the PS2's scale), 1 green
        Ui.SetColour(l, colour == 1 ? new Color(0.58f, 1f, 0.69f) : new Color(1f, 1f, 1f));
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 6);
        AddChild(l);
        return l;
    }

    private void Popup(GdFfx.Api.Snapshot s, GdFfx.Api.BattleNumber n, NumberKey key)
    {
        var a = ActorOf(s, n.Battler);
        if (a == null)
            return;
        var chars = new List<Label>();
        if (n.Digits > 0)
        {
            var digits = n.Value.ToString();
            for (int k = digits.Length - 1; k >= 0; k--)
                chars.Add(NewChar(digits[k].ToString(), n.Colour, 30));
        }
        if (n.Word != 0)
            chars.Add(NewChar(WordText(n.Word), n.Colour, n.Digits > 0 ? 22 : 26));
        // over the body: its position in Godot's space, about a character's height up (the
        // game's number point; the overkill word starts where the number does and rises away)
        _pops.Add(new Pop
        {
            Key = key,
            At = GameStateView.FieldPoint(a.Position) + Vector3.Up * 14f,
            Chars = chars,
            Age = n.Age,
            Life = n.Life,
        });
        Popped++;
        PoppedNumbers.Add(key);
        // an HP loss above the HP the battler had in the last update: overkill
        if (n.Colour == 0 && n.Word == 0 && n.Digits > 0 && _lastHp.TryGetValue(n.Battler, out var hp) && n.Value > hp)
            PoppedKills++;
    }

    /// <summary>The game's frame table (<c>g_BtlNumberFrames</c>, 0x00c424b0 in FFX.exe).</summary>
    public const long FramesAddress = 0x00c424b0;

    /// <summary>
    /// The rows of the game's frame table, read once from the running game: by a character's age
    /// less one less its place, an x and a y offset (signed, PS2 pixels; a loss bounces by them)
    /// and an alpha (0x80 opaque), as <c>MsNumberDrawProcess</c> reads them. Without a game, rows
    /// that stay put, opaque.
    /// </summary>
    private (sbyte X, sbyte Y, byte Alpha)[]? _frames;

    private (sbyte X, sbyte Y, byte Alpha) Frame(int row)
    {
        if (_frames == null && GameHost.Instance?.Game is { } game)
        {
            var b = game.Peek(FramesAddress, 3 * FrameRows);
            if (b.Length == 3 * FrameRows)
            {
                _frames = new (sbyte, sbyte, byte)[FrameRows];
                for (int i = 0; i < FrameRows; i++)
                    _frames[i] = ((sbyte)b[3 * i], (sbyte)b[3 * i + 1], b[3 * i + 2]);
            }
        }
        return _frames != null && row >= 0 && row < FrameRows ? _frames[row] : ((sbyte)0, (sbyte)0, (byte)0x80);
    }

    /// <summary>Rows of the frame table: a number's life (30) and a little more.</summary>
    public const int FrameRows = 32;

    /// <summary>
    /// The game's own damage numbers (API 12's <c>Effects.numbers</c>): each pops once, the first
    /// time a snapshot lists it, with the game's value (a killing blow's overkill included:
    /// <c>MsSubHP</c> registers the loss before the HP is clamped at 0), its colour (green a
    /// heal) and word, and follows the game's timing: its digits show lowest first, one an
    /// update (digit k from age k + 1), a loss bounces, a heal rises by half its age, a word by
    /// its age (in PS2 pixels), and it fades over the last 5 of its life's updates and leaves
    /// when the game drops it.
    /// </summary>
    private void UpdatePops(GdFfx.Api.Snapshot? s, double delta)
    {
        if (s != null && s.World.Battle && s.Update != _lastUpdate)
        {
            _lastUpdate = s.Update;
            foreach (var p in _pops)
                p.Seen = false;
            foreach (var n in s.Effects.Numbers)
            {
                var key = KeyOf(s, n);
                if (_known.Add(key))
                    Popup(s, n, key);
                foreach (var p in _pops)
                    if (p.Key == key)
                    {
                        p.Seen = true;
                        p.Age = n.Age;
                        p.Life = n.Life;
                    }
            }
            // the game dropped them: gone
            for (int i = _pops.Count - 1; i >= 0; i--)
                if (!_pops[i].Seen)
                {
                    foreach (var l in _pops[i].Chars)
                        l.QueueFree();
                    _pops.RemoveAt(i);
                }
            // keys older than any number's life
            _stale.Clear();
            foreach (var k in _known)
                if (s.Update - k.Born > 256)
                    _stale.Add(k);
            foreach (var k in _stale)
                _known.Remove(k);
            _lastHp.Clear();
            foreach (var b in s.Battlers)
                _lastHp[b.Index] = b.Hp;
        }
        else if (s == null || !s.World.Battle)
        {
            foreach (var p in _pops)
                foreach (var l in p.Chars)
                    l.QueueFree();
            _pops.Clear();
            _known.Clear();
            _lastHp.Clear();
        }
        var cam = GetViewport().GetCamera3D();
        float px = GetViewportRect().Size.Y / Ps2Height;
        foreach (var p in _pops)
        {
            bool behind = cam == null || cam.IsPositionBehind(p.At);
            // the characters side by side, centred over the body, the lowest digit rightmost
            float width = 0;
            foreach (var l in p.Chars)
                width += l.GetCombinedMinimumSize().X;
            var centre = behind ? Vector2.Zero : cam!.UnprojectPosition(p.At);
            float right = centre.X + width / 2;
            // as MsNumberDrawProcess: the age less one; a character in place k shows from row 0
            // (age k + 1); the last 5 updates of its life fade
            int age = p.Age - 1;
            for (int place = 0; place < p.Chars.Count; place++)
            {
                var l = p.Chars[place];
                var size = l.GetCombinedMinimumSize();
                // the word sits left of the digits, in the place after them
                var x = right - size.X;
                right -= size.X;
                int row = age - place;
                if (behind || row < 0)
                {
                    l.Visible = false;
                    continue;
                }
                var f = Frame(row);
                float dx = 0, rise = 0;
                switch (p.Key.Colour)
                {
                    case 0:
                        dx = f.X;
                        rise = f.Y;
                        break;
                    case 1:
                        rise = age / 2;
                        break;
                    case 2:
                        rise = age;
                        break;
                }
                int alpha = f.Alpha;
                if (age > p.Life - 5)
                    alpha = ((p.Life - age) << 5) & 0xff;
                l.Visible = true;
                l.Position = new Vector2(x + dx * px, centre.Y - size.Y / 2 - rise * px);
                l.Modulate = new Color(1, 1, 1, Mathf.Clamp(alpha / 128f, 0, 1));
            }
        }
    }

    /// <summary>The colour of an HP value: red KO'd, yellow at a quarter or less.</summary>
    public static Color HpColour(bool dead, int hp, int max) =>
        dead || hp <= 0 ? new Color(1f, 0.35f, 0.3f)
        : max > 0 && hp * 4 <= max ? new Color(1f, 0.9f, 0.35f)
        : new Color(0.92f, 0.94f, 1f);

    public override void _Process(double delta)
    {
        var t0 = Perf.Start();
        Tick(delta);
        Perf.End(Perf.Section.BattleHud, t0);
    }

    private void Tick(double delta)
    {
        var s = GameHost.Instance?.View.Current;
        Shown.Clear();
        UpdatePops(s, delta);
        if (s == null || !s.World.Battle || s.Battlers.Count == 0)
        {
            _panel.Visible = false;
            _order.Visible = false;
            Order = "";
            return;
        }
        var game = GameHost.Instance?.Game;
        string Name(int index) =>
            index < FirstMonster && game != null && index < s.Save.Members.Count && s.Save.Members[index].Name.Length > 0
                ? FieldMenuView.MemberName(game, s, index, _names)
                : ActorOf(s, index) is { Model.Length: > 0 } a ? a.Model : $"#{index:x}";

        var party = new List<(string Text, Color Colour)>();
        var turns = new List<(int Ctb, int Index)>();
        foreach (var b in s.Battlers)
        {
            if (!b.Exists)
                continue;
            if (!b.Dead)
                turns.Add((b.Ctb, b.Index));
            if (b.Index >= FirstMonster)
                continue;
            var text = $"{Name(b.Index),-6}  HP {b.Hp,5}/{b.MaxHp,-5}  MP {b.Mp,4}/{b.MaxMp,-4}{(b.Dead ? "  KO" : "")}";
            party.Add((text, HpColour(b.Dead, b.Hp, b.MaxHp)));
        }
        while (_rows.GetChildCount() < party.Count)
        {
            var l = new Label();
            l.AddThemeFontSizeOverride("font_size", 20);
            _rows.AddChild(l);
        }
        for (int i = 0; i < _rows.GetChildCount(); i++)
        {
            var l = (Label)_rows.GetChild(i);
            l.Visible = i < party.Count;
            if (i >= party.Count)
                continue;
            Ui.SetText(l, party[i].Text);
            Ui.SetColour(l, party[i].Colour);
            Shown.Add(l.Text);
        }
        _panel.Visible = party.Count > 0;
        var view = GetViewportRect().Size;
        var size = _panel.GetCombinedMinimumSize();
        _panel.Size = size;
        _panel.Position = new Vector2(view.X - size.X - view.X * 0.04f, view.Y - size.Y - view.Y * 0.05f);

        // the turn order: the lowest counter acts next (ties by index, as the game walks them)
        turns.Sort();
        var order = new List<string>();
        foreach (var (_, index) in turns)
            if (order.Count < 8)
                order.Add(index >= FirstMonster ? $"[{Name(index)}]" : Name(index));
        Order = string.Join(" > ", order);
        Ui.SetText(_order, "Next: " + Order);
        _order.Visible = order.Count > 0;
        _order.Position = new Vector2(view.X - _order.GetCombinedMinimumSize().X - view.X * 0.04f, view.Y * 0.04f);
    }
}
