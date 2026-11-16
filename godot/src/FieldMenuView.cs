using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx;

/// <summary>
/// G4's field menu panel: while the game's main menu is open (<c>Menus.MainMask</c> bit 1),
/// the party as the game's menu shows it beside the options (<see cref="BattleMenuView"/>
/// draws those): each member of the front line, then the back line, by the name the player
/// gave (the game-state API's <c>Save</c>, decoded with the game's character set), with HP,
/// MP and the sphere levels; the gil and the play time below. The game keeps the menu; this
/// only draws it. While the game's item screen is open (menu slot 3, <see cref="ItemsMask"/>,
/// measured: Items in the main menu opens it), the inventory instead: each used slot in the
/// game's order, the item named from the kernel tables (as the battle commands), and its
/// count, in two columns, the one under the game's cursor marked (<see cref="ItemList"/>). While the status screen is open (slot 6), every party member's
/// HP, MP, the 8 stats, AP, sphere levels and overdrive gauge side by side (which member
/// the game shows is not in the API yet). The abilities screen (slot 2): each member's
/// own learnt commands (<see cref="Learnt"/>, without the shared ones) named from the kernel's
/// command table; the overdrive
/// screen (slot 0x15): each member's mode, the modes obtained and the gauge. The other
/// screens' bits are in <see cref="Screens"/>.
/// </summary>
public partial class FieldMenuView : Control
{
    /// <summary>The item screen's bit in <c>Menus.MainMask</c> (menu slot 3).</summary>
    public const uint ItemsMask = 0x8;

    /// <summary>
    /// The main menu's screens by their bit in <c>Menus.MainMask</c> (measured: each option
    /// picked, a character chosen where it asks): sphere grid slot 0x13, items 3, abilities 2,
    /// overdrive 0x15, status 6, config 0xa, help 0xb; the main menu itself slot 1.
    /// </summary>
    public static readonly (uint Mask, string Screen)[] Screens =
    [
        (0x2, "main"), (0x80000, "sphere grid"), (ItemsMask, "items"), (0x4, "abilities"),
        (0x200000, "overdrive"), (0x40, "status"), (0x400, "config"), (0x800, "help"),
    ];

    /// <summary>The status screen's bit (menu slot 6).</summary>
    public const uint StatusMask = 0x40;

    /// <summary>The abilities screen's bit (menu slot 2) and the overdrive screen's (slot 0x15).</summary>
    public const uint AbilitiesMask = 0x4;
    public const uint OverdriveMask = 0x200000;

    /// <summary>
    /// The commands a member has learnt: <c>0x3000 + i</c> for each bit i of its own words
    /// (<c>PartyMember.commands</c>), then <c>0x3060 + i</c> for each of the shared ones
    /// (<c>Save.shared_commands</c>), as <c>MsSetSaveCommand</c> keeps them.
    /// </summary>
    public static List<ushort> Learnt(GdFfx.Api.PartyMember m, byte[]? shared = null)
    {
        var ids = new List<ushort>();
        static bool Bit(byte[] b, int i) => i / 8 < b.Length && (b[i / 8] >> (i % 8) & 1) != 0;
        for (int i = 0; i < m.Commands.Length * 8 && i < 0x60; i++)
            if (Bit(m.Commands, i))
                ids.Add((ushort)(0x3000 + i));
        for (int i = 0; shared != null && i < shared.Length * 8; i++)
            if (Bit(shared, i))
                ids.Add((ushort)(0x3060 + i));
        return ids;
    }

    /// <summary>The screen open now ("" none): the first of <see cref="Screens"/> past the main menu, else the main menu.</summary>
    public static string OpenScreen(uint mask)
    {
        foreach (var (m, name) in Screens)
            if (m != 0x2 && (mask & m) != 0)
                return name;
        return (mask & 0x2) != 0 ? "main" : "";
    }

    private PanelContainer _statusPanel = null!;
    private GridContainer _statusGrid = null!;

    /// <summary>The status rows shown now (for tests): one a stat, the members in columns.</summary>
    public List<string> Status { get; } = new();

    /// <summary>The names of the 8 stats in the API's order.</summary>
    public static readonly string[] StatNames = ["Strength", "Defence", "Magic", "Magic def.", "Agility", "Luck", "Evasion", "Accuracy"];

    private PanelContainer _itemsPanel = null!;
    private GridContainer _itemGrid = null!;
    private readonly Dictionary<(ushort Id, string Lang), string> _itemNames = new();

    /// <summary>The item rows shown now (for tests), and the one under the game's cursor ("" none).</summary>
    public List<string> Items { get; } = new();
    public string ItemAtCursor { get; private set; } = "";

    /// <summary>
    /// The item screen's list window (<c>Menus.Windows</c>): the open one listing items (kind 0)
    /// with the most rows (the inventory's 0x70 slots, two a row; measured: 56 rows). Its
    /// cursor is taken as the slot: it starts at 0 and one Down makes it 1 (measured). ffx-rust's
    /// menu.md has row = cursor >> 1 in two-column lists, where Down would add 2; which entry
    /// the game's own picture highlights is not checked.
    /// </summary>
    public static GdFfx.Api.MenuWindow? ItemList(GdFfx.Api.Snapshot s) =>
        s.Menus.Windows.Where(w => w.State != 0 && w.Kind == 0 && w.Rows >= 8).OrderByDescending(w => w.Rows).FirstOrDefault();

    private PanelContainer _panel = null!;
    private VBoxContainer _rows = null!;
    private Label _footer = null!;
    private readonly Dictionary<(string Name, string Lang), string> _names = new();

    /// <summary>The rows shown now and the footer (for tests).</summary>
    public List<string> Shown { get; } = new();
    public string Footer => _footer.Text;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.08f, 0.22f, 0.85f),
            BorderColor = new Color(0.8f, 0.85f, 1f, 0.9f),
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        AddChild(_panel);
        _itemsPanel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _itemsPanel.AddThemeStyleboxOverride("panel", (StyleBox)_panel.GetThemeStylebox("panel").Duplicate());
        AddChild(_itemsPanel);
        _statusPanel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _statusPanel.AddThemeStyleboxOverride("panel", (StyleBox)_panel.GetThemeStylebox("panel").Duplicate());
        AddChild(_statusPanel);
        _statusGrid = new GridContainer();
        _statusGrid.AddThemeConstantOverride("h_separation", 36);
        _statusPanel.AddChild(_statusGrid);
        _itemGrid = new GridContainer { Columns = 2 };
        _itemGrid.AddThemeConstantOverride("h_separation", 48);
        _itemsPanel.AddChild(_itemGrid);
        var box = new VBoxContainer();
        _panel.AddChild(box);
        _rows = new VBoxContainer();
        box.AddChild(_rows);
        _footer = new Label();
        _footer.AddThemeFontSizeOverride("font_size", 18);
        _footer.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.95f));
        box.AddChild(_footer);
    }

    /// <summary>A member's name as the player set it (the game's text, decoded), or <c>#i</c>.</summary>
    public static string MemberName(FfxGame game, GdFfx.Api.Snapshot s, int index, Dictionary<(string, string), string>? cache = null)
    {
        if (index >= s.Save.Members.Count || s.Save.Members[index].Name.Length == 0)
            return $"#{index}";
        var lang = MessageView.LanguageCode(s.World.Language);
        var raw = System.Convert.ToHexString(s.Save.Members[index].Name);
        if (cache != null && cache.TryGetValue((raw, lang), out var n))
            return n;
        n = MessageView.Clean(game.DecodeText(s.Save.Members[index].Name, lang));
        if (n.Length == 0)
            n = $"#{index}";
        if (cache != null)
            cache[(raw, lang)] = n;
        return n;
    }

    private string Command(FfxGame game, ushort id, string lang)
    {
        if (!_itemNames.TryGetValue((id, lang), out var n))
        {
            n = MessageView.Clean(game.KernelName(id, lang));
            if (n.Length == 0)
                n = $"0x{id:x4}";
            _itemNames[(id, lang)] = n;
        }
        return n;
    }

    private void ShowStatus(FfxGame game, GdFfx.Api.Snapshot s)
    {
        Status.Clear();
        var screen = OpenScreen(s.Menus.MainMask);
        if (screen is not ("status" or "abilities" or "overdrive"))
        {
            _statusPanel.Visible = false;
            return;
        }
        var sv = s.Save;
        var who = sv.Frontline.Concat(sv.Backline).Where(i => i != 0xff && i < sv.Members.Count).Distinct().ToList();
        var cells = new List<string> { "" };
        cells.AddRange(who.Select(i => MemberName(game, s, i, _names)));
        if (screen != "status")
        {
            var lang = MessageView.LanguageCode(s.World.Language);
            // the member's own commands (the shared ones, from 0x3060, are the party's
            // system commands: summons, aeons' and Yojimbo's, which this screen leaves out)
            var lists = who.Select(i => screen == "abilities"
                ? Learnt(sv.Members[i]).Select(id => Command(game, id, lang)).ToList()
                : new List<string> { $"Mode {sv.Members[i].OverdriveMode}", $"{System.Numerics.BitOperations.PopCount(sv.Members[i].OverdriveModes)} modes",
                    $"Gauge {sv.Members[i].LimitCharge}/{sv.Members[i].LimitChargeMax}" }).ToList();
            int rows = lists.Count == 0 ? 0 : lists.Max(l => l.Count);
            for (int r = 0; r < rows; r++)
            {
                cells.Add("");
                cells.AddRange(lists.Select(l => r < l.Count ? l[r] : ""));
            }
            for (int k = 0; k < who.Count; k++)
                Status.Add($"{cells[k + 1]}: {string.Join(", ", lists[k])}");
            Fill(cells, who.Count + 1);
            return;
        }
        void Row(string label, System.Func<GdFfx.Api.PartyMember, string> value)
        {
            cells.Add(label);
            cells.AddRange(who.Select(i => value(sv.Members[i])));
            Status.Add($"{label}: {string.Join(" / ", who.Select(i => value(sv.Members[i])))}");
        }
        Row("HP", m => $"{m.Hp}/{m.MaxHp}");
        Row("MP", m => $"{m.Mp}/{m.MaxMp}");
        for (int k = 0; k < StatNames.Length; k++)
        {
            int kk = k;
            Row(StatNames[k], m => kk < m.Stats.Length ? m.Stats[kk].ToString() : "-");
        }
        Row("AP", m => m.Ap.ToString());
        Row("S.Lv", m => m.SphereLevels.ToString());
        Row("Overdrive", m => $"{m.LimitCharge}/{m.LimitChargeMax}");
        Fill(cells, who.Count + 1);
    }

    private void Fill(List<string> cells, int columns)
    {
        _statusGrid.Columns = columns;
        while (_statusGrid.GetChildCount() < cells.Count)
        {
            var l = new Label();
            l.AddThemeFontSizeOverride("font_size", 19);
            l.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 1f));
            _statusGrid.AddChild(l);
        }
        for (int i = 0; i < _statusGrid.GetChildCount(); i++)
        {
            var l = (Label)_statusGrid.GetChild(i);
            l.Visible = i < cells.Count;
            if (i < cells.Count)
                l.Text = cells[i];
        }
        _statusPanel.Visible = true;
        var view = GetViewportRect().Size;
        var size = _statusPanel.GetCombinedMinimumSize();
        _statusPanel.Size = size;
        _statusPanel.Position = new Vector2((view.X - size.X) / 2, view.Y * 0.12f);
    }

    private void ShowItems(FfxGame game, GdFfx.Api.Snapshot s)
    {
        if ((s.Menus.MainMask & ItemsMask) == 0)
        {
            _itemsPanel.Visible = false;
            return;
        }
        var lang = MessageView.LanguageCode(s.World.Language);
        foreach (var it in s.Save.Items)
        {
            if (!_itemNames.TryGetValue((it.Id, lang), out var n))
            {
                n = MessageView.Clean(game.KernelName(it.Id, lang));
                if (n.Length == 0)
                    n = $"0x{it.Id:x4}";
                _itemNames[(it.Id, lang)] = n;
            }
            Items.Add($"{n,-16} {it.Count,3}");
        }
        var list = ItemList(s);
        int at = list == null ? -1 : s.Save.Items.FindIndex(it => it.Slot == list.Cursor);
        ItemAtCursor = at >= 0 ? Items[at] : "";
        while (_itemGrid.GetChildCount() < Items.Count)
        {
            var l = new Label();
            l.AddThemeFontSizeOverride("font_size", 20);
            l.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 1f));
            _itemGrid.AddChild(l);
        }
        for (int i = 0; i < _itemGrid.GetChildCount(); i++)
        {
            var l = (Label)_itemGrid.GetChild(i);
            l.Visible = i < Items.Count;
            if (i < Items.Count)
            {
                l.Text = (i == at ? "▶ " : "   ") + Items[i];
                l.AddThemeColorOverride("font_color", i == at ? new Color(1f, 0.95f, 0.6f) : new Color(0.92f, 0.94f, 1f));
            }
        }
        _itemsPanel.Visible = true;
        var view = GetViewportRect().Size;
        var size = _itemsPanel.GetCombinedMinimumSize();
        _itemsPanel.Size = size;
        _itemsPanel.Position = new Vector2((view.X - size.X) / 2, view.Y * 0.18f);
    }

    /// <summary>Play time as the game's menu shows it (hours:minutes).</summary>
    public static string Time(uint seconds) => $"{seconds / 3600}:{seconds / 60 % 60:00}";

    public override void _Process(double delta)
    {
        var host = GameHost.Instance;
        var s = host?.View.Current;
        Shown.Clear();
        Items.Clear();
        if (host?.Game == null || s == null || !s.Save.Valid || s.World.Battle)
        {
            _panel.Visible = false;
            _itemsPanel.Visible = false;
            _statusPanel.Visible = false;
            return;
        }
        ShowItems(host.Game, s);
        ShowStatus(host.Game, s);
        if ((s.Menus.MainMask & 0x2) == 0)
        {
            _panel.Visible = false;
            return;
        }
        var sv = s.Save;
        var order = sv.Frontline.Concat(sv.Backline).Where(i => i != 0xff && i < sv.Members.Count).Distinct().ToList();
        var rows = new List<(string Text, Color Colour)>();
        foreach (var i in order)
        {
            var m = sv.Members[i];
            var front = sv.Frontline.Contains((byte)i);
            var text = $"{MemberName(host.Game, s, i, _names),-10} HP {m.Hp,5}/{m.MaxHp,-5} MP {m.Mp,4}/{m.MaxMp,-4} S.Lv {m.SphereLevels,2}";
            var colour = BattleHudView.HpColour(false, (int)m.Hp, (int)m.MaxHp);
            rows.Add((text, front ? colour : colour.Darkened(0.35f)));
        }
        while (_rows.GetChildCount() < rows.Count)
        {
            var l = new Label();
            l.AddThemeFontSizeOverride("font_size", 20);
            _rows.AddChild(l);
        }
        for (int r = 0; r < _rows.GetChildCount(); r++)
        {
            var l = (Label)_rows.GetChild(r);
            l.Visible = r < rows.Count;
            if (r >= rows.Count)
                continue;
            l.Text = rows[r].Text;
            l.AddThemeColorOverride("font_color", rows[r].Colour);
            Shown.Add(l.Text);
        }
        _footer.Text = $"Gil {sv.Gil}    Time {Time(sv.PlayTime)}";
        _panel.Visible = true;
        var view = GetViewportRect().Size;
        var size = _panel.GetCombinedMinimumSize();
        _panel.Size = size;
        _panel.Position = new Vector2(view.X - size.X - view.X * 0.05f, view.Y * 0.08f);
    }
}
