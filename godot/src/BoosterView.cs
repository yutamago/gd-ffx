using System.Collections.Generic;
using GdFfx.Api;
using Godot;

namespace GdFfx;

/// <summary>
/// The PC version's boosters (G5; API 11's <c>Snapshot.boosters</c>): F1 the speed-up (x1, x2,
/// x4), F2 the power-up ("Supercharge"), F3 the encounter mode (none, normal, frequent), F4
/// the auto-battle. The keys go to the game, which keeps the modes; this shows a badge for
/// each one that is on (the speed-up dimmed where the game does not apply it, e.g. in an
/// event). The tips box (<c>box.swf</c>, <c>_kr</c>, <c>_ch</c>): every tip the game plays
/// (<c>TipSystem_PlayTip</c>'s <c>OnPlay(0, text)</c>, taken by <see cref="FlashScreens"/>) is
/// shown as a short toast, whatever sends it: the boosters' (localisation lines 0x2fd..0x2ff,
/// 0x2f6..0x2f8, 0x2f9..0x2fb, 0x37d..0x380), the free camera's and time stop's (0x2ed/0x2ee,
/// 0x2f3..0x2f5), the HUD's (0x2f0..0x2f2), "You get 50000 Gils!" (0x2fc). The free camera
/// (<c>freecamera.swf</c>) and time stop (<c>time_stop.swf</c>) get a badge with the movie's
/// own icon while on: the game asks its icon panel for them (icons 8 and 9), which the original
/// refuses (<c>IconPanel_showIcon</c> takes only 0..6), so these two are gd-ffx's; their state is
/// API 13's <c>Snapshot.free_camera</c> (<c>free</c>, <c>time_stop</c>). gd-ffx's debugging
/// speed (the options, <c>--speed=</c>) gets a badge too while it is not 1.
/// </summary>
public partial class BoosterView : Control
{
    private HBoxContainer _badges = null!;
    private PanelContainer _toast = null!;
    private Label _toastText = null!;
    private double _toastAge = double.MaxValue;
    private readonly Dictionary<string, (PanelContainer Panel, Label Label)> _badge = new();
    private readonly Dictionary<string, ImageTexture?> _icons = new();

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

    /// <summary>A movie's icon (its first bitmap), cached.</summary>
    private ImageTexture? Icon(FfxGame game, string movie)
    {
        if (!_icons.TryGetValue(movie, out var t))
        {
            t = game.FlashBitmap(movie, 1) is { } img ? ImageTexture.CreateFromImage(img) : null;
            _icons[movie] = t;
        }
        return t;
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
            if (b.SpeedLevel > 0)
                want.Add(("speed", b.SpeedLevel == 2 ? "F1 Turbo x4" : "F1 Turbo x2", new Color(0.55f, 0.85f, 1f), !b.Speeding));
            if (b.Powerup)
                want.Add(("powerup", "F2 Supercharge", new Color(1f, 0.6f, 0.35f), false));
            if (b.Encounters != 1)
                want.Add(("encounters", b.Encounters == 0 ? "F3 No encounters" : "F3 Encounters x10", new Color(0.7f, 1f, 0.6f), false));
            if (b.AutoBattle)
                want.Add(("auto", "F4 Auto battle", new Color(1f, 0.9f, 0.45f), false));
        }
        // the tips the game played (its own text, in its language)
        if (FlashScreens.Tips.Count > 0)
        {
            var lines = new List<string>();
            while (FlashScreens.Tips.Count > 0)
                lines.Add(MessageView.Clean(FlashScreens.Tips.Dequeue().Replace("@el@", "\n")));
            var text = string.Join("\n", lines);
            Ui.SetText(_toastText, text);
            _toastAge = 0;
            Toasts.Add(text.Replace('\n', ' '));
        }
        string? iconOf(string key) => key switch { "freecamera" => "freecamera.swf", "timestop" => "time_stop.swf", _ => null };
        if (s != null)
        {
            var (free, stop) = (s.FreeCamera.Free, s.FreeCamera.TimeStop);
            if (free)
                want.Add(("freecamera", "Free camera", new Color(0.6f, 1f, 0.6f), false));
            if (stop)
                want.Add(("timestop", "Time stop", new Color(1f, 0.8f, 0.4f), false));
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
                var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                row.AddThemeConstantOverride("separation", 4);
                if (iconOf(key) is { } movie && host?.Game is { } ig && Icon(ig, movie) is { } icon)
                    row.AddChild(new TextureRect
                    {
                        Texture = icon, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        CustomMinimumSize = new Vector2(22, 22), MouseFilter = MouseFilterEnum.Ignore,
                    });
                row.AddChild(label);
                panel.AddChild(row);
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
