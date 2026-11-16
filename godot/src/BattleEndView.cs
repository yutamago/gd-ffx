using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// G4's battle results screen, from the game-state API's <c>Menus.battle_end</c> (API 10; the
/// game's <c>TkBtlEndCtrl</c>, main-menu mask bit 0x20000, slot 0x11): while it runs, each
/// character row the game draws (kinds 0 and 5 are not drawn) by the name the player gave, the
/// AP it still has to count (the game counts them down) and its sphere levels, a "Level up!"
/// that flashes while the row's flash counter runs (0xf when the AP reach a sphere level, down
/// by one each draw); the gil won and the share counted so far (the game pays it in steps); the
/// items won named from the kernel tables with their counts, and the gear won by id (gear has no
/// kernel name: its ids are <c>MsGetSaveWeapon</c>'s). FFX has no EXP. The game keeps the
/// screen; this only draws it.
/// </summary>
public partial class BattleEndView : Control
{
    /// <summary>The results screen's bit in <c>Menus.MainMask</c> (slot 0x11).</summary>
    public const uint Mask = 0x20000;

    private PanelContainer _panel = null!;
    private Label _title = null!;
    private GridContainer _rows = null!;
    private Label _gil = null!;
    private Label _spoils = null!;
    private readonly Dictionary<(string, string), string> _names = new();
    private readonly Dictionary<(ushort, string), string> _items = new();

    /// <summary>The character rows shown now (for tests): name, AP left, sphere levels, a level-up flash.</summary>
    public List<string> Shown { get; } = new();

    /// <summary>The gil line and the spoils line shown now (for tests).</summary>
    public string Gil => _gil.Text;
    public string Spoils => _spoils.Text;

    /// <summary>Level-up flashes seen so far and the most AP a row showed (for tests).</summary>
    public int Flashes { get; private set; }
    public uint MostAp { get; private set; }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.08f, 0.22f, 0.88f),
            BorderColor = new Color(0.8f, 0.85f, 1f, 0.9f),
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 22, ContentMarginRight = 22, ContentMarginTop = 12, ContentMarginBottom = 12,
        });
        AddChild(_panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(box);
        _title = new Label { Text = "Battle results" };
        _title.AddThemeFontSizeOverride("font_size", 24);
        _title.AddThemeColorOverride("font_color", new Color(1f, 0.95f, 0.6f));
        box.AddChild(_title);
        _rows = new GridContainer { Columns = 4 };
        _rows.AddThemeConstantOverride("h_separation", 36);
        box.AddChild(_rows);
        _gil = new Label();
        _gil.AddThemeFontSizeOverride("font_size", 20);
        _gil.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 1f));
        box.AddChild(_gil);
        _spoils = new Label();
        _spoils.AddThemeFontSizeOverride("font_size", 20);
        _spoils.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 1f));
        box.AddChild(_spoils);
    }

    /// <summary>Whether the results screen runs in <paramref name="s"/>.</summary>
    public static bool Open(GdFfx.Api.Snapshot? s) => s != null && s.Menus.BattleEnd.Open && (s.Menus.MainMask & Mask) != 0;

    /// <summary>The AP character <paramref name="c"/> still has to count (<c>BattleEnd.ap</c>: 18 u32, little-endian).</summary>
    public static uint Ap(GdFfx.Api.BattleEnd e, int c) =>
        c >= 0 && 4 * c + 4 <= e.Ap.Length ? System.BitConverter.ToUInt32(e.Ap, 4 * c) : 0;

    /// <summary>The gear ids won (<c>BattleEnd.gear</c>: u16 little-endian each).</summary>
    public static List<ushort> Gear(GdFfx.Api.BattleEnd e)
    {
        var ids = new List<ushort>();
        for (int i = 0; i + 2 <= e.Gear.Length; i += 2)
            ids.Add(System.BitConverter.ToUInt16(e.Gear, i));
        return ids;
    }

    public override void _Process(double delta)
    {
        var t0 = Perf.Start();
        Tick();
        Perf.End(Perf.Section.BattleEnd, t0);
    }

    private void Tick()
    {
        var host = GameHost.Instance;
        var s = host?.View.Current;
        Shown.Clear();
        if (host?.Game == null || !Open(s))
        {
            _panel.Visible = false;
            return;
        }
        var e = s!.Menus.BattleEnd;
        var lang = MessageView.LanguageCode(s.World.Language);
        var cells = new List<(string Text, Color Colour)>();
        var white = new Color(0.92f, 0.94f, 1f);
        var flashColour = new Color(1f, 0.9f, 0.35f);
        foreach (var r in e.Rows)
        {
            if (r.Kind is 0 or 5 || r.Character < 0)
                continue;
            var name = FieldMenuView.MemberName(host.Game, s, r.Character, _names);
            var ap = Ap(e, r.Character);
            if (ap > MostAp)
                MostAp = ap;
            int levels = r.Character < s.Save.Members.Count ? s.Save.Members[r.Character].SphereLevels : 0;
            bool flash = r.Flash > 0;
            if (flash)
                Flashes++;
            // the flash fades with its counter (0xf down to 0), as the game's does
            var fade = new Color(flashColour, Mathf.Clamp(r.Flash / 15f, 0.25f, 1f));
            var nameColour = r.InParty ? white : white.Darkened(0.3f);
            cells.Add((name, nameColour));
            cells.Add(($"AP {ap,5}", white));
            cells.Add(($"S.Lv {levels,2}", flash ? flashColour : white));
            cells.Add((flash ? "Level up!" : "", flash ? fade : white));
            Shown.Add($"{name} AP {ap} S.Lv {levels}{(flash ? " Level up!" : "")}");
        }
        while (_rows.GetChildCount() < cells.Count)
        {
            var l = new Label();
            l.AddThemeFontSizeOverride("font_size", 20);
            _rows.AddChild(l);
        }
        for (int i = 0; i < _rows.GetChildCount(); i++)
        {
            var l = (Label)_rows.GetChild(i);
            l.Visible = i < cells.Count;
            if (i >= cells.Count)
                continue;
            Ui.SetText(l, cells[i].Text);
            Ui.SetColour(l, cells[i].Colour);
        }
        // the gil: won, and what the game has counted into the party's so far
        var counted = e.GilWon >= e.GilLeft ? e.GilWon - e.GilLeft : 0;
        Ui.SetText(_gil, $"Gil {counted,6} / {e.GilWon}    (party {s.Save.Gil})");
        var spoils = new List<string>();
        foreach (var it in e.Items)
        {
            if (!_items.TryGetValue((it.Id, lang), out var n))
            {
                n = MessageView.Clean(host.Game.KernelName(it.Id, lang));
                if (n.Length == 0)
                    n = $"0x{it.Id:x4}";
                _items[(it.Id, lang)] = n;
            }
            spoils.Add($"{n} x{it.Count}");
        }
        foreach (var g in Gear(e))
            spoils.Add($"gear #{g}");
        Ui.SetText(_spoils, spoils.Count == 0 ? "Spoils: none" : $"Spoils: {string.Join(", ", spoils)}{(e.GearFull ? " (gear full)" : "")}");
        _panel.Visible = true;
        var view = GetViewportRect().Size;
        var size = _panel.GetCombinedMinimumSize();
        _panel.Size = size;
        _panel.Position = new Vector2((view.X - size.X) / 2, view.Y * 0.1f);
    }
}
