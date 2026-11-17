using System.Collections.Generic;
using GdFfx.Api;
using Godot;

namespace GdFfx;

/// <summary>
/// The PC version's boosters (G5; API 11's <c>Snapshot.boosters</c>): F1 the speed-up (x1, x2,
/// x4), F2 the power-up ("Supercharge"), F3 the encounter mode (none, normal, frequent), F4
/// the auto-battle. The keys go to the game, which keeps the modes; this shows a badge for
/// each one that is on (the speed-up dimmed where the game does not apply it, e.g. in an
/// event) and, when one changes, a short toast with the game's own tip (its localisation kit:
/// lines 0x2fd..0x2ff, 0x2f6/0x2f7, 0x2f9..0x2fb, 0x37f/0x380), as the game's
/// <c>TipSystem</c> shows it. gd-ffx's debugging speed (the options, <c>--speed=</c>) gets a
/// badge too while it is not 1.
/// </summary>
public partial class BoosterView : Control
{
    private HBoxContainer _badges = null!;
    private PanelContainer _toast = null!;
    private Label _toastText = null!;
    private double _toastAge = double.MaxValue;
    private (byte Speed, bool Powerup, byte Encounters, bool Auto)? _last;
    private readonly Dictionary<string, (PanelContainer Panel, Label Label)> _badge = new();

    /// <summary>Seconds a toast stays.</summary>
    public const double ToastTime = 2.5;

    /// <summary>The badges shown now and the toasts shown so far (for tests).</summary>
    public List<string> Shown { get; } = new();
    public List<string> Toasts { get; } = new();

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _badges = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _badges.AddThemeConstantOverride("separation", 6);
        AddChild(_badges);
        _toast = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _toast.AddThemeStyleboxOverride("panel", Panels.Window(0.88f, 16, 6));
        _toastText = Panels.Text("", 20, null, MessageView.UiFont);
        _toast.AddChild(_toastText);
        AddChild(_toast);
    }

    /// <summary>The game's tip <paramref name="id"/> in its language, else <paramref name="english"/>.</summary>
    private static string Tip(FfxGame? game, Snapshot s, int id, string english)
    {
        if (game == null)
            return english;
        var t = game.FlashText(id, MessageView.LanguageCode(s.World.Language));
        return t.Length > 0 ? t : english;
    }

    /// <summary>The toasts a change from <paramref name="was"/> to <paramref name="now"/> makes (the game's tips).</summary>
    public static List<(int Id, string English)> TipsFor((byte Speed, bool Powerup, byte Encounters, bool Auto) was,
        (byte Speed, bool Powerup, byte Encounters, bool Auto) now)
    {
        var tips = new List<(int, string)>();
        if (was.Speed != now.Speed)
            tips.Add(now.Speed switch { 1 => (0x2fd, "Turbo x2"), 2 => (0x2fe, "Turbo x4"), _ => (0x2ff, "Normal") });
        if (was.Powerup != now.Powerup)
            tips.Add(now.Powerup ? (0x2f6, "Supercharge (On)") : (0x2f7, "Supercharge (Off)"));
        if (was.Encounters != now.Encounters)
            tips.Add(now.Encounters switch
            {
                0 => (0x2fa, "Enemy Encounters (None)"),
                2 => (0x2fb, "Enemy Encounters (Frequent)"),
                _ => (0x2f9, "Enemy Encounters (Normal)"),
            });
        if (was.Auto != now.Auto)
            tips.Add(now.Auto ? (0x37f, "Auto Battle (On)") : (0x380, "Auto Battle (Off)"));
        return tips;
    }

    public override void _Process(double delta)
    {
        var host = GameHost.Instance;
        var s = host?.View.Current;
        Shown.Clear();
        var want = new List<(string Key, string Text, Color Colour, bool Dim)>();
        if (s != null)
        {
            var b = s.Boosters;
            var now = (b.SpeedLevel, b.Powerup, b.Encounters, b.AutoBattle);
            if (_last is { } was && was != now)
            {
                var tips = TipsFor(was, now);
                if (tips.Count > 0)
                {
                    var text = string.Join("\n", tips.ConvertAll(t => Tip(host?.Game, s, t.Id, t.English)));
                    Ui.SetText(_toastText, text);
                    _toastAge = 0;
                    Toasts.Add(text.Replace('\n', ' '));
                    GD.Print($"ffx: booster: {text.Replace('\n', ' ')}");
                }
            }
            _last = now;
            if (b.SpeedLevel > 0)
                want.Add(("speed", b.SpeedLevel == 2 ? "F1 Turbo x4" : "F1 Turbo x2", new Color(0.55f, 0.85f, 1f), !b.Speeding));
            if (b.Powerup)
                want.Add(("powerup", "F2 Supercharge", new Color(1f, 0.6f, 0.35f), false));
            if (b.Encounters != 1)
                want.Add(("encounters", b.Encounters == 0 ? "F3 No encounters" : "F3 Encounters x10", new Color(0.7f, 1f, 0.6f), false));
            if (b.AutoBattle)
                want.Add(("auto", "F4 Auto battle", new Color(1f, 0.9f, 0.45f), false));
        }
        var speed = UserSettings.Current.Speed;
        if (System.Math.Abs(speed - 1) > 1e-6 && host?.Game != null)
            want.Add(("gdspeed", $"Speed x{speed:0.##}", new Color(1f, 0.55f, 0.8f), false));
        var keys = new HashSet<string>();
        foreach (var (key, text, colour, dim) in want)
        {
            keys.Add(key);
            if (!_badge.TryGetValue(key, out var badge))
            {
                var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
                panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
                {
                    BgColor = new Color(0.03f, 0.05f, 0.12f, 0.8f),
                    BorderColor = colour,
                    BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
                    CornerRadiusTopLeft = 9, CornerRadiusTopRight = 9, CornerRadiusBottomLeft = 9, CornerRadiusBottomRight = 9,
                    ContentMarginLeft = 9, ContentMarginRight = 9, ContentMarginTop = 2, ContentMarginBottom = 3,
                });
                var label = Panels.Text(text, 15, colour);
                panel.AddChild(label);
                _badges.AddChild(panel);
                badge = (panel, label);
                _badge[key] = badge;
            }
            Ui.SetText(badge.Label, text);
            badge.Panel.Modulate = new Color(1, 1, 1, dim ? 0.45f : 1f);
            Shown.Add(dim ? $"({text})" : text);
        }
        foreach (var key in new List<string>(_badge.Keys))
            if (!keys.Contains(key))
            {
                _badge[key].Panel.QueueFree();
                _badge.Remove(key);
            }
        var view = GetViewportRect().Size;
        _badges.Size = _badges.GetCombinedMinimumSize();
        // under the battle's turn order, and under the FPS overlay while it is shown
        _badges.Position = new Vector2(view.X - _badges.Size.X - view.X * 0.04f,
            UserSettings.Current.FpsOverlay ? 120 : view.Y * 0.09f);
        _toastAge += delta;
        _toast.Visible = _toastAge < ToastTime;
        if (_toast.Visible)
        {
            _toast.Size = _toast.GetCombinedMinimumSize();
            _toast.Position = new Vector2((view.X - _toast.Size.X) / 2, view.Y * 0.08f);
            _toast.Modulate = new Color(1, 1, 1, (float)Mathf.Clamp((ToastTime - _toastAge) / 0.4, 0, 1));
        }
    }
}
