using Godot;

namespace GdFfx;

/// <summary>
/// The FPS overlay (G5; F9, or the options' "FPS overlay", saved): Godot's frames a second
/// (<c>Engine.GetFramesPerSecond</c>), the last frame's time and the worst of the last second,
/// a graph of the last 240 frame times (16.7 and 33.3 ms marked), and the game's updates a
/// second (its own pace is 30; the snapshots' update numbers over the last second) with the
/// game's speed. Drawn in the top right corner; costs nothing while hidden.
/// </summary>
public partial class FpsView : Control
{
    private const int Frames = 240;
    private readonly float[] _ms = new float[Frames];
    private int _at;
    private Label _text = null!;
    private double _sinceText;
    private readonly System.Collections.Generic.Queue<(double At, uint Update)> _updates = new();
    private double _clock;

    /// <summary>The graph's place and size.</summary>
    private static readonly Vector2 GraphSize = new(240, 60);

    /// <summary>The text shown now (for tests).</summary>
    public string Shown => _text.Text;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _text = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _text.AddThemeFontOverride("font", new SystemFont { FontNames = ["monospace", "Consolas", "DejaVu Sans Mono"] });
        _text.AddThemeFontSizeOverride("font_size", 13);
        _text.AddThemeColorOverride("font_outline_color", Colors.Black);
        _text.AddThemeConstantOverride("outline_size", 4);
        AddChild(_text);
        Visible = UserSettings.Current.FpsOverlay;
        UserSettings.Changed += OnSettings;
    }

    public override void _ExitTree() => UserSettings.Changed -= OnSettings;

    private void OnSettings() => Visible = UserSettings.Current.FpsOverlay;

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k && k.PhysicalKeycode == GameHost.FpsKey)
        {
            var u = UserSettings.Current;
            u.FpsOverlay = !u.FpsOverlay;
            Visible = u.FpsOverlay;
            u.Save();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>The game's updates a second over the last second (0 before two snapshots).</summary>
    public double UpdatesPerSecond { get; private set; }

    public override void _Process(double delta)
    {
        _clock += delta;
        _ms[_at] = (float)(delta * 1000);
        _at = (_at + 1) % Frames;
        if (GameHost.Instance?.View.Current is { } s && (_updates.Count == 0 || _updates.Peek().Update != s.Update))
            _updates.Enqueue((_clock, s.Update));
        while (_updates.Count > 1 && _clock - _updates.Peek().At > 1.0)
            _updates.Dequeue();
        if (!Visible)
            return;
        _sinceText += delta;
        if (_sinceText >= 0.25)
        {
            _sinceText = 0;
            float worst = 0;
            int n = (int)Mathf.Clamp(Engine.GetFramesPerSecond(), 1, Frames);
            for (int i = 1; i <= n; i++)
                worst = Mathf.Max(worst, _ms[(_at - i + Frames) % Frames]);
            UpdatesPerSecond = 0;
            if (_updates.Count > 1)
            {
                var first = _updates.Peek();
                (double at, uint up) last = (first.At, first.Update);
                foreach (var u in _updates)
                    last = u;
                if (last.at > first.At)
                    UpdatesPerSecond = (last.up - first.Update) / (last.at - first.At);
            }
            var game = GameHost.Instance?.Game;
            var speed = game != null ? $"  speed x{game.Speed:0.##}" : "";
            Ui.SetText(_text, $"{Engine.GetFramesPerSecond(),4:0} fps  {delta * 1000,5:0.0} ms (worst {worst:0.0})\n"
                + $"game {UpdatesPerSecond,4:0.0} updates/s{speed}{(Engine.MaxFps > 0 ? $"  cap {Engine.MaxFps}" : "")}");
        }
        var view = GetViewportRect().Size;
        _text.Position = new Vector2(view.X - GraphSize.X - 12, 10);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var view = GetViewportRect().Size;
        var origin = new Vector2(view.X - GraphSize.X - 12, 48);
        DrawRect(new Rect2(origin, GraphSize), new Color(0, 0, 0, 0.55f));
        // 50 ms at the top; lines at 16.7 and 33.3 ms
        float Y(float ms) => origin.Y + GraphSize.Y - Mathf.Min(ms / 50f, 1f) * GraphSize.Y;
        DrawLine(new Vector2(origin.X, Y(16.7f)), new Vector2(origin.X + GraphSize.X, Y(16.7f)), new Color(0.4f, 1f, 0.4f, 0.5f));
        DrawLine(new Vector2(origin.X, Y(33.3f)), new Vector2(origin.X + GraphSize.X, Y(33.3f)), new Color(1f, 0.8f, 0.3f, 0.5f));
        for (int i = 0; i < Frames; i++)
        {
            float ms = _ms[(_at + i) % Frames];
            float x = origin.X + i * GraphSize.X / Frames;
            var colour = ms > 33.3f ? new Color(1f, 0.35f, 0.3f) : ms > 16.7f ? new Color(1f, 0.85f, 0.35f) : new Color(0.55f, 0.9f, 1f);
            DrawLine(new Vector2(x, origin.Y + GraphSize.Y), new Vector2(x, Y(ms)), colour);
        }
    }
}
