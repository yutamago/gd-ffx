using System.Collections.Generic;
using System.Text.RegularExpressions;
using GdFfx.Api;
using Godot;

namespace GdFfx;

/// <summary>
/// G4's native message boxes: the event script's open message windows from the
/// game-state API (dialogue, captions, choices), drawn as Godot panels at the
/// window's place on the PS2 screen (512 x 448, scaled to the viewport). The text
/// is the game's, decoded with the character set of the game's language from the
/// player's archive. In a choice the highlighted option is marked.
/// </summary>
public partial class MessageView : Control
{
    /// <summary>The PS2 screen the windows are placed on.</summary>
    private static readonly Vector2 Ps2Screen = new(512, 448);

    private readonly Dictionary<int, (PanelContainer Panel, Label Label, MenuList Options)> _windows = new();
    private readonly Dictionary<(int Window, string Bytes), string> _decoded = new();

    /// <summary>
    /// The system's UI font with fallbacks for Japanese, Korean and Chinese (the
    /// game's text in those languages); Godot's default font has no CJK glyphs.
    /// </summary>
    public static readonly SystemFont UiFont = new()
    {
        FontNames =
        [
            "Noto Sans", "Segoe UI", "DejaVu Sans", "Noto Sans CJK JP", "Noto Sans JP", "Yu Gothic UI",
            "Meiryo", "MS Gothic", "Noto Sans CJK KR", "Malgun Gothic", "Noto Sans CJK SC", "Microsoft YaHei",
        ],
        AllowSystemFallback = true,
    };

    /// <summary>The texts shown now (for tests).</summary>
    public List<string> Shown { get; } = new();

    /// <summary>
    /// The text's size on the 1280 x 720 canvas (project.godot's stretch mode scales the canvas
    /// with the window's height, the 16:9 picture kept with bars): 24, the game's ~36 px glyph at
    /// 1080 lines.
    /// </summary>
    public const int FontSize = 24;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        // the system font found and loaded now, with the scene, not at the first message (a
        // 20 to 45 ms frame)
        UiFont.GetStringSize("Ag", HorizontalAlignment.Left, -1, FontSize);
    }

    /// <summary>The ffx-text code of the API's language number (<c>TOGetFFXLang</c>).</summary>
    public static string LanguageCode(byte language) => language switch
    {
        0 => "jp",
        1 => "us",
        2 => "fr",
        3 => "sp",
        4 => "de",
        5 => "it",
        9 => "kr",
        10 => "ch",
        _ => "us",
    };

    /// <summary>
    /// A window's text split for display: the lines above the options, the options (the last
    /// <paramref name="options"/> lines, when the window lays out a choice: <paramref name="choice"/>
    /// 0 or more) and the option the cursor is on.
    /// </summary>
    public static (string Body, List<string> Options, int Cursor) Layout(string text, int options, int choice)
    {
        var lines = new List<string>(text.Split('\n'));
        if (choice < 0 || options <= 0 || options > lines.Count)
            return (text, new List<string>(), -1);
        int first = lines.Count - options;
        var rows = lines.GetRange(first, options).ConvertAll(l => l.Trim());
        return (string.Join("\n", lines.GetRange(0, first)).TrimEnd('\n', ' '), rows, choice < options ? choice : -1);
    }

    /// <summary>The choices shown now: the window, its options and the row marked (for tests).</summary>
    public List<string> Choices { get; } = new();

    /// <summary>The decoded text for display: the game's control codes (<c>{..}</c>) left out.</summary>
    public static string Clean(string text) =>
        Regex.Replace(text, @"\{[^}]*\}", "").Trim('\n', ' ');

    public override void _Process(double delta)
    {
        var t0 = Perf.Start();
        Tick(delta);
        Perf.End(Perf.Section.Messages, t0);
    }

    private void Tick(double delta)
    {
        var host = GameHost.Instance;
        var s = host?.View.Current;
        Shown.Clear();
        Choices.Clear();
        var open = new HashSet<int>();
        if (host?.Game != null && s != null)
        {
            var lang = LanguageCode(s.World.Language);
            foreach (var m in s.Messages)
            {
                if (m.State == 0 || m.Text.Length == 0)
                    continue;
                open.Add(m.Window);
                var key = (m.Window, System.Convert.ToHexString(m.Text) + lang);
                if (!_decoded.TryGetValue(key, out var text))
                {
                    text = Clean(host.Game.DecodeText(m.Text, lang));
                    if (_decoded.Count > 256)
                        _decoded.Clear();
                    _decoded[key] = text;
                }
                Show(m, text);
                Shown.Add(text);
                if (_windows.TryGetValue(m.Window, out var win) && win.Options.Visible)
                    Choices.Add($"window {m.Window}: {string.Join(" | ", win.Options.Shown)} (choice {m.Choice}, picked {m.Picked})");
            }
        }
        foreach (var w in new List<int>(_windows.Keys))
            if (!open.Contains(w))
            {
                _windows[w].Panel.QueueFree();
                _windows.Remove(w);
            }
    }

    private void Show(Message m, string text)
    {
        if (!_windows.TryGetValue(m.Window, out var w))
        {
            var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.05f, 0.1f, 0.25f, 0.82f),
                BorderColor = new Color(0.75f, 0.8f, 0.95f, 0.9f),
                BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
                ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 10, ContentMarginBottom = 10,
            });
            var label = new Label();
            label.AddThemeFontOverride("font", UiFont);
            label.AddThemeFontSizeOverride("font_size", FontSize);
            label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
            var options = new MenuList(FontSize, UiFont) { Visible = false };
            var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            box.AddChild(label);
            box.AddChild(options);
            panel.AddChild(box);
            AddChild(panel);
            w = (panel, label, options);
            _windows[m.Window] = w;
        }
        // a choice: the last (flags >> 8) lines are the options, drawn as a list with the
        // cursor (API 11's live Message.choice) on its row
        var (body, rows, cursor) = Layout(text, m.Flags >> 8, m.Choice);
        Ui.SetText(w.Label, body);
        w.Label.Visible = body.Length > 0;
        w.Options.Visible = rows.Count > 0;
        if (rows.Count > 0)
            w.Options.SetRows(rows, cursor);
        // the window's place: x its centre, y its bottom edge on the PS2 screen
        var view = GetViewportRect().Size;
        var size = w.Panel.GetCombinedMinimumSize();
        var at = new Vector2(m.X / Ps2Screen.X * view.X - size.X / 2, m.Y / Ps2Screen.Y * view.Y - size.Y - 16);
        // a window larger than the screen keeps its top left on screen
        var max = (view - size - Vector2.One * 8).Max(Vector2.One * 8);
        w.Panel.Position = at.Clamp(Vector2.One * 8, max);
        w.Panel.Modulate = new Color(1, 1, 1, m.State == 2 ? 1f : 0.6f);
    }
}
