using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace GdFfx;

/// <summary>
/// gd-ffx's settings as controls (G5): each change goes into <see cref="UserSettings.Current"/>,
/// is applied at once (<see cref="UserSettings.Apply"/>) and saved. The in-game overlay
/// (<see cref="OptionsView"/>, F10 or the ESC menu's System setting) and the launcher show it.
/// The controls are reachable by key (<see cref="Controls"/>) for the self-checks.
/// </summary>
public partial class OptionsPanel : VBoxContainer
{
    /// <summary>The controls by setting: <c>preset</c>, <c>window</c>, <c>scale</c>, <c>upscaler</c>, <c>msaa</c>, <c>aa</c>, <c>taa</c>, <c>vsync</c>, <c>fps</c>, <c>glow</c>, <c>volume</c>, <c>speed</c>, <c>fps_overlay</c>.</summary>
    public Dictionary<string, Control> Controls { get; } = new();

    private bool _filling;

    /// <summary>Whether the game's speed is offered (in game; the launcher saves it for the next start).</summary>
    public bool InGame { get; init; }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 4);
        AddChild(grid);
        bool fwd = UserSettings.ForwardPlus();
        string Pct(float f) => $"{f * 100:0} %";
        Choice(grid, "preset", "Quality preset", ["High", "Deck (lighter: 3D at 75 %, 60 fps)"], u => Array.IndexOf(UserSettings.Presets, u.Quality),
            (u, i) => u.ApplyPreset(UserSettings.Presets[i]));
        Choice(grid, "window", "Window", ["Windowed", "Fullscreen (borderless)", "Exclusive fullscreen"],
            u => Array.IndexOf(UserSettings.WindowModes, u.WindowMode), (u, i) => u.WindowMode = UserSettings.WindowModes[i]);
        Choice(grid, "scale", "3D resolution", UserSettings.Scales.Select(Pct).ToArray(),
            u => Nearest(UserSettings.Scales, u.Scale3D), (u, i) => u.Scale3D = UserSettings.Scales[i]);
        Choice(grid, "upscaler", "Upscaler", fwd ? ["Bilinear", "FSR 1.0", "FSR 2.2"] : ["Bilinear (FSR needs Forward+)"],
            u => fwd ? Math.Max(0, Array.IndexOf(UserSettings.Upscalers, u.Upscaler)) : 0, (u, i) => u.Upscaler = UserSettings.Upscalers[i]);
        Choice(grid, "msaa", "MSAA (3D)", ["Off", "2x", "4x", "8x"], u => Math.Max(0, Array.IndexOf(UserSettings.MsaaSamples, u.Msaa)),
            (u, i) => u.Msaa = UserSettings.MsaaSamples[i]);
        Choice(grid, "aa", "Screen-space AA", ["Off", "FXAA", "SMAA"], u => Math.Max(0, Array.IndexOf(UserSettings.ScreenAAs, u.ScreenAA)),
            (u, i) => u.ScreenAA = UserSettings.ScreenAAs[i]);
        Check(grid, "taa", fwd ? "Temporal AA (TAA)" : "Temporal AA (needs Forward+)", u => u.Taa, (u, on) => u.Taa = on, enabled: fwd);
        Choice(grid, "vsync", "VSync", ["On", "Off", "Adaptive", "Mailbox"], u => Math.Max(0, Array.IndexOf(UserSettings.VSyncs, u.VSync)),
            (u, i) => u.VSync = UserSettings.VSyncs[i]);
        Choice(grid, "fps", "Frame rate cap", UserSettings.FpsCaps.Select(f => f == 0 ? "None" : $"{f} fps").ToArray(),
            u => Math.Max(0, Array.IndexOf(UserSettings.FpsCaps, u.MaxFps)), (u, i) => u.MaxFps = UserSettings.FpsCaps[i]);
        Check(grid, "glow", "Glow", u => u.Glow, (u, on) => u.Glow = on);
        Slider(grid, "volume", "Sound volume", u => u.Volume * 100, (u, v) => u.Volume = (float)(v / 100));
        Choice(grid, "speed", InGame ? "Game speed (debugging)" : "Game speed (debugging; from the start)",
            UserSettings.Speeds.Select(s => $"x{s.ToString("0.##", CultureInfo.InvariantCulture)}").ToArray(),
            u => Nearest(UserSettings.Speeds.Select(s => (float)s).ToArray(), (float)u.Speed), (u, i) => u.Speed = UserSettings.Speeds[i]);
        Check(grid, "fps_overlay", "FPS overlay (F9)", u => u.FpsOverlay, (u, on) => u.FpsOverlay = on);
        var buttons = new HBoxContainer();
        var defaults = new Button { Name = "Defaults", Text = "Defaults" };
        defaults.Pressed += () =>
        {
            var d = UserSettings.Defaults();
            var u = UserSettings.Current;
            (u.WindowMode, u.Scale3D, u.Upscaler, u.Msaa, u.ScreenAA, u.Taa, u.VSync, u.MaxFps, u.Glow, u.Quality, u.Volume, u.Speed, u.FpsOverlay) =
                (d.WindowMode, d.Scale3D, d.Upscaler, d.Msaa, d.ScreenAA, d.Taa, d.VSync, d.MaxFps, d.Glow, d.Quality, d.Volume, d.Speed, d.FpsOverlay);
            Changed();
        };
        buttons.AddChild(defaults);
        Controls["defaults"] = defaults;
        AddChild(buttons);
        Fill();
    }

    private static int Nearest(float[] choices, float v)
    {
        int best = 0;
        for (int i = 1; i < choices.Length; i++)
            if (Math.Abs(choices[i] - v) < Math.Abs(choices[best] - v))
                best = i;
        return best;
    }

    private readonly List<Action> _fill = new();

    private static Label Caption(GridContainer grid, string text)
    {
        var l = new Label { Text = text, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        grid.AddChild(l);
        return l;
    }

    private void Choice(GridContainer grid, string key, string label, string[] items, Func<UserSettings, int> get, Action<UserSettings, int> set)
    {
        Caption(grid, label);
        var o = new OptionButton { Name = key, CustomMinimumSize = new Vector2(260, 0) };
        foreach (var item in items)
            o.AddItem(item);
        o.ItemSelected += i =>
        {
            if (_filling)
                return;
            set(UserSettings.Current, (int)i);
            Changed();
        };
        grid.AddChild(o);
        Controls[key] = o;
        _fill.Add(() => o.Select(Math.Clamp(get(UserSettings.Current), 0, items.Length - 1)));
    }

    private void Check(GridContainer grid, string key, string label, Func<UserSettings, bool> get, Action<UserSettings, bool> set, bool enabled = true)
    {
        Caption(grid, label);
        var c = new CheckBox { Name = key, Disabled = !enabled, Text = "" };
        c.Toggled += on =>
        {
            if (_filling)
                return;
            set(UserSettings.Current, on);
            Changed();
        };
        grid.AddChild(c);
        Controls[key] = c;
        _fill.Add(() => c.ButtonPressed = get(UserSettings.Current));
    }

    private void Slider(GridContainer grid, string key, string label, Func<UserSettings, double> get, Action<UserSettings, double> set)
    {
        Caption(grid, label);
        var s = new HSlider { Name = key, MinValue = 0, MaxValue = 100, Step = 5, CustomMinimumSize = new Vector2(260, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        s.ValueChanged += v =>
        {
            if (_filling)
                return;
            set(UserSettings.Current, v);
            Changed();
        };
        grid.AddChild(s);
        Controls[key] = s;
        _fill.Add(() => s.Value = get(UserSettings.Current));
    }

    /// <summary>Shows the settings' values (after a preset or the defaults, or opening).</summary>
    public void Fill()
    {
        _filling = true;
        foreach (var f in _fill)
            f();
        _filling = false;
    }

    private void Changed()
    {
        var u = UserSettings.Current;
        u.Apply();
        u.Save();
        Fill();
        GD.Print($"ffx: settings: {u}");
    }

    /// <summary>The first control (keyboard focus on opening).</summary>
    public Control? First => Controls.GetValueOrDefault("preset");
}
