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
/// the API gives the characters' own names).
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

    private readonly Dictionary<int, int> _lastHp = new();
    private readonly Dictionary<(string, string), string> _names = new();
    private readonly List<(Label Label, Vector3 At, double Age)> _pops = new();
    private uint _lastUpdate;

    /// <summary>Damage numbers popped so far (for tests).</summary>
    public int Popped { get; private set; }

    /// <summary>Seconds a damage number stays.</summary>
    public const double PopTime = 1.2;

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

    private void Pop(GdFfx.Api.Snapshot s, int index, int change)
    {
        var a = ActorOf(s, index);
        if (a == null)
            return;
        var l = new Label { Text = Mathf.Abs(change).ToString(), MouseFilter = MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", 30);
        Ui.SetColour(l, change < 0 ? new Color(1f, 1f, 1f) : new Color(0.5f, 1f, 0.55f));
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 6);
        AddChild(l);
        // over the body: its position in Godot's space, about a character's height up
        _pops.Add((l, GameStateView.FieldPoint(a.Position) + Vector3.Up * 14f, 0));
        Popped++;
    }

    private void UpdatePops(GdFfx.Api.Snapshot? s, double delta)
    {
        if (s != null && s.World.Battle && s.Update != _lastUpdate)
        {
            _lastUpdate = s.Update;
            foreach (var b in s.Battlers)
            {
                if (_lastHp.TryGetValue(b.Index, out var was) && was != b.Hp)
                    Pop(s, b.Index, b.Hp - was);
                _lastHp[b.Index] = b.Hp;
            }
        }
        else if (s == null || !s.World.Battle)
            _lastHp.Clear();
        var cam = GetViewport().GetCamera3D();
        for (int i = _pops.Count - 1; i >= 0; i--)
        {
            var (l, at, age) = _pops[i];
            age += delta;
            if (age > PopTime || cam == null || cam.IsPositionBehind(at))
            {
                l.QueueFree();
                _pops.RemoveAt(i);
                continue;
            }
            _pops[i] = (l, at, age);
            var p = cam.UnprojectPosition(at) - l.GetCombinedMinimumSize() / 2;
            l.Position = p + Vector2.Up * (float)(age * 40);
            l.Modulate = new Color(1, 1, 1, (float)Mathf.Clamp(2 * (PopTime - age) / PopTime, 0, 1));
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
