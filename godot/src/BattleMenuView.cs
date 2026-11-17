using System.Linq;
using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// G4's native battle command menu: the command window the game has open (the
/// game-state API's <c>Menus</c>: whose it is, its level, its ids and the
/// game's cursor), each id named from the player's kernel tables (battle
/// commands, items, monster abilities) in the game's language. The game keeps
/// the menu's logic; this only draws it. Also a small panel for the PS2 main
/// menu and the PC save screen while they are open (their own screens come
/// next).
/// </summary>
public partial class BattleMenuView : Control
{
    private PanelContainer _panel = null!;
    private Label _title = null!;
    private MenuList _rows = null!;
    private readonly Dictionary<(ushort Id, byte Lang), string> _names = new();

    /// <summary>The rows shown now, the highlighted one marked (for tests).</summary>
    public List<string> Shown { get; } = new();

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _panel.AddThemeStyleboxOverride("panel", Panels.Window());
        AddChild(_panel);
        var box = new VBoxContainer();
        _panel.AddChild(box);
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 16);
        _title.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.95f));
        box.AddChild(_title);
        _rows = new MenuList(22, MessageView.UiFont);
        box.AddChild(_rows);
        _target = Panels.Text("", 18, MenuList.LitColour, MessageView.UiFont);
        _target.Visible = false;
        box.AddChild(_target);
        _hands = new TargetHands { Name = "TargetHands" };
        AddChild(_hands);
    }

    private Label _target = null!;
    private TargetHands _hands = null!;

    /// <summary>The target selection shown now ("" none; for tests): the target's name and the targets marked.</summary>
    public string TargetShown { get; private set; } = "";

    /// <summary>The hand cursors drawn over the battlers now (for tests).</summary>
    public int HandsShown => _hands.Count;

    private List<SaveFiles.Header?>? _saveList;
    private bool _saveListSaving;
    private double _saveListAt;

    /// <summary>A save list row: the player's name, the party, the play time, gil, the location id.</summary>
    public static string SaveRow(FfxGame game, SaveFiles.Header? h, string lang)
    {
        if (h == null)
            return "New save";
        string Decode(byte[] b) => b.Length == 0 ? "" : MessageView.Clean(game.DecodeText(b, lang));
        var party = string.Join(" ", h.Party.Where(i => i != 0xff).Select(i => Decode(h.Name(i))));
        var label = h.Slot == 0 ? "Autosave" : $"File {h.Slot}";
        var place = MessageView.Clean(game.PlaceName(h.Location, lang));
        return $"{label}  {Decode(h.PlayerName)}  [{party}]  {FieldMenuView.Time(h.PlayTime)}  {h.Gil} gil  {(place.Length > 0 ? place : $"@{h.Location:x}")}";
    }

    private string CommandName(FfxGame game, ushort id, byte language)
    {
        if (!_names.TryGetValue((id, language), out var n))
        {
            n = MessageView.Clean(game.KernelName(id, MessageView.LanguageCode(language)));
            if (n.Length == 0)
                n = $"0x{id:x4}";
            _names[(id, language)] = n;
        }
        return n;
    }

    public override void _Process(double delta)
    {
        var t0 = Perf.Start();
        Tick(delta);
        Perf.End(Perf.Section.BattleMenu, t0);
    }

    private void Tick(double delta)
    {
        var host = GameHost.Instance;
        var s = host?.View.Current;
        Shown.Clear();
        _hands.Clear();
        TargetShown = "";
        if (host?.Game == null || s == null)
        {
            _panel.Visible = false;
            return;
        }
        var m = s.Menus;
        var rows = new List<string>();
        int cursor = -1;
        string title;
        bool targeting = s.World.Battle && m.BattleTarget.Open;
        if (s.World.Battle && (targeting || (m.BattleWindow != 0 && m.BattleDepth >= 0 && m.BattleIds.Length >= 2)))
        {
            var who = "";
            foreach (var a in s.Actors)
                if (a.Kind == 0 && a.Slot == m.BattleCharacter)
                    who = a.Model;
            title = $"{(targeting ? "Target" : m.BattleDepth == 0 ? "Commands" : $"Level {m.BattleDepth}")}{(who.Length > 0 ? $" ({who})" : "")}";
            for (int i = 0; i + 1 < m.BattleIds.Length; i += 2)
                rows.Add(CommandName(host.Game, (ushort)(m.BattleIds[i] | (m.BattleIds[i + 1] << 8)), s.World.Language));
            cursor = m.BattleCursor;
        }
        else if (m.SaveState != 0)
        {
            var saving = !(m.SaveState >= 0xb && m.SaveState <= 0x11);
            title = saving ? "Save" : "Load";
            var lang = MessageView.LanguageCode(s.World.Language);
            if (_saveList == null || _saveListSaving != saving || _saveListAt + 2.0 < Time.GetTicksMsec() / 1000.0)
            {
                _saveList = SaveFiles.List(GameSettings.DocumentsDir(), saving);
                _saveListSaving = saving;
                _saveListAt = Time.GetTicksMsec() / 1000.0;
            }
            // the screen shows five rows from save_top
            for (int i = m.SaveTop; i < m.SaveTop + 5 && i < _saveList.Count; i++)
                rows.Add(SaveRow(host.Game, _saveList[i], lang));
            cursor = m.SaveCursor;
        }
        else if ((m.MainMask & 0x2) != 0)
        {
            title = "Menu";
            var lang = MessageView.LanguageCode(s.World.Language);
            foreach (var o in m.MainOptionList)
            {
                var name = o.Name.Length > 0 ? MessageView.Clean(host.Game.DecodeText(o.Name, lang)) : $"0x{o.Id:x}";
                rows.Add(o.Enabled ? name : $"({name})");
            }
            cursor = m.MainCursor;
        }
        else
        {
            _panel.Visible = false;
            return;
        }
        Ui.SetText(_title, title);
        _rows.SetRows(rows, cursor);
        Shown.AddRange(_rows.Shown);
        Targets(host.Game, s);
        _panel.Visible = true;
        var view = GetViewportRect().Size;
        var size = _panel.GetCombinedMinimumSize();
        _panel.Size = size;
        _panel.Position = new Vector2(view.X * 0.06f, view.Y - size.Y - view.Y * 0.06f);
    }

    private readonly Dictionary<(string, string), string> _memberNames = new();

    /// <summary>
    /// The target selection (API 11's <c>Menus.battle_target</c>, while the command window's
    /// level chooses a target): the target's name under the commands, and a hand over each
    /// battler selected (<c>mask</c>; the one the cursor is on brightest), as the game's
    /// <c>TOBtlDrawTargetCursor</c> puts its cursors. None while the auto-battle runs.
    /// </summary>
    private void Targets(FfxGame game, GdFfx.Api.Snapshot s)
    {
        var t = s.Menus.BattleTarget;
        _hands.Clear();
        if (!s.World.Battle || !t.Open || s.Boosters.AutoBattle)
        {
            _target.Visible = false;
            TargetShown = "";
            return;
        }
        string Name(int index) =>
            index < BattleHudView.FirstMonster && index < s.Save.Members.Count && s.Save.Members[index].Name.Length > 0
                ? FieldMenuView.MemberName(game, s, index, _memberNames)
                : BattleHudView.ActorOf(s, index) is { Model.Length: > 0 } a ? a.Model : $"#{index:x}";
        var marked = new List<string>();
        for (int i = 0; i < 28; i++)
        {
            if ((t.Mask & (1u << i)) == 0)
                continue;
            marked.Add(Name(i));
            if (BattleHudView.ActorOf(s, i) is { } actor)
                _hands.Add(GameStateView.FieldPoint(actor.Position) + Vector3.Up * 16f, i == t.Character);
        }
        var side = t.Side == 1 ? "enemies" : "party";
        var text = marked.Count > 1
            ? $"Target: {string.Join(", ", marked)} ({side})"
            : $"Target: {Name(t.Character)} ({side}{(t.Count > 1 ? $", {t.Cursor + 1}/{t.Count}" : "")})";
        Ui.SetText(_target, text + (t.Switchable ? "   [Left/Right, Up/Down]" : ""));
        _target.Visible = true;
        TargetShown = $"{text}; {_hands.Count} hand(s)";
    }
}

/// <summary>
/// The battle's target cursors (G5): a hand over each selected battler, at its body's place on
/// the screen (the game's camera), bobbing as the game's does; the battler the cursor is on
/// drawn bright, the others dimmer.
/// </summary>
public partial class TargetHands : Control
{
    private readonly List<(Vector3 At, bool Current)> _at = new();
    private double _time;

    public int Count => _at.Count;

    public TargetHands()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public void Clear() => _at.Clear();

    public void Add(Vector3 at, bool current) => _at.Add((at, current));

    public override void _Process(double delta)
    {
        _time += delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null)
            return;
        float bob = (float)(Mathf.Sin(_time * 8) * 4);
        foreach (var (at, current) in _at)
        {
            if (cam.IsPositionBehind(at))
                continue;
            var p = cam.UnprojectPosition(at) + new Vector2(0, -12 + bob);
            // a hand pointing down at the battler: the palm a rounded box, the finger a triangle
            var fill = current ? new Color(1f, 0.95f, 0.6f) : new Color(0.75f, 0.78f, 0.85f, 0.85f);
            var line = new Color(0, 0, 0, 0.9f);
            Vector2[] tip = [p + new Vector2(-9, -16), p + new Vector2(9, -16), p];
            var palm = new Rect2(p + new Vector2(-13, -34), new Vector2(26, 18));
            DrawRect(palm, fill);
            DrawRect(palm, line, false, 2f);
            DrawColoredPolygon(tip, fill);
            DrawPolyline([tip[0], tip[2], tip[1]], line, 2f, true);
        }
    }
}
