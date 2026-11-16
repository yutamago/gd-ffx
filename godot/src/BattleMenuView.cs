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
    private VBoxContainer _rows = null!;
    private readonly Dictionary<(ushort Id, byte Lang), string> _names = new();

    /// <summary>The rows shown now, the highlighted one marked (for tests).</summary>
    public List<string> Shown { get; } = new();

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
            ContentMarginLeft = 14, ContentMarginRight = 18, ContentMarginTop = 8, ContentMarginBottom = 10,
        });
        AddChild(_panel);
        var box = new VBoxContainer();
        _panel.AddChild(box);
        _title = new Label();
        _title.AddThemeFontSizeOverride("font_size", 16);
        _title.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.95f));
        box.AddChild(_title);
        _rows = new VBoxContainer();
        box.AddChild(_rows);
    }

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
        var host = GameHost.Instance;
        var s = host?.View.Current;
        Shown.Clear();
        if (host?.Game == null || s == null)
        {
            _panel.Visible = false;
            return;
        }
        var m = s.Menus;
        var rows = new List<string>();
        int cursor = -1;
        string title;
        if (s.World.Battle && m.BattleWindow != 0 && m.BattleDepth >= 0 && m.BattleIds.Length >= 2)
        {
            var who = "";
            foreach (var a in s.Actors)
                if (a.Kind == 0 && a.Slot == m.BattleCharacter)
                    who = a.Model;
            title = $"{(m.BattleDepth == 0 ? "Commands" : $"Level {m.BattleDepth}")}{(who.Length > 0 ? $" ({who})" : "")}";
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
        _title.Text = title;
        while (_rows.GetChildCount() < rows.Count)
        {
            var l = new Label();
            l.AddThemeFontSizeOverride("font_size", 22);
            _rows.AddChild(l);
        }
        for (int i = 0; i < _rows.GetChildCount(); i++)
        {
            var l = (Label)_rows.GetChild(i);
            l.Visible = i < rows.Count;
            if (i >= rows.Count)
                continue;
            var on = i == cursor;
            l.Text = (on ? "▶ " : "   ") + rows[i];
            l.AddThemeColorOverride("font_color", on ? new Color(1f, 0.95f, 0.6f) : new Color(0.92f, 0.94f, 1f));
            Shown.Add(l.Text);
        }
        _panel.Visible = true;
        var view = GetViewportRect().Size;
        var size = _panel.GetCombinedMinimumSize();
        _panel.Position = new Vector2(view.X * 0.06f, view.Y - size.Y - view.Y * 0.06f);
    }
}
