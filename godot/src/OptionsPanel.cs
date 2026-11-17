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
/// The controls are reachable by key (<see cref="Controls"/>) for the self-checks. They wear the
/// game-like look (<see cref="GameTheme"/>): framed checkboxes that read ticked or not, and the
/// control with the keyboard or pad focus framed in gold, its caption lit with the menus' cursor.
/// </summary>
public partial class OptionsPanel : VBoxContainer
{
    /// <summary>The controls by setting: <c>preset</c>, <c>window</c>, <c>scale</c>, <c>upscaler</c>, <c>msaa</c>, <c>aa</c>, <c>taa</c>, <c>vsync</c>, <c>fps</c>, <c>glow</c>, <c>volume</c>, <c>speed</c>, <c>fps_overlay</c>, <c>render_path</c>, <c>lighting</c>.</summary>
    public Dictionary<string, Control> Controls { get; } = new();

    private bool _filling;

    /// <summary>Whether the game's speed is offered (in game; the launcher saves it for the next start).</summary>
    public bool InGame { get; init; }

    public override void _Ready()
    {
        Theme = GameTheme.Get();
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
        Choice(grid, "render_path", "Render path (F7 compares)", ["As each map says", "Classic everywhere", "Remastered everywhere"],
            u => Math.Max(0, Array.IndexOf(Remaster.RenderPaths.Choices, u.RenderPath)), (u, i) =>
            {
                u.RenderPath = Remaster.RenderPaths.Choices[i];
                Remaster.RenderPaths.Set(null);
            });
        Choice(grid, "lighting", fwd ? "Lighting quality (remastered maps)" : "Lighting quality (remastered; GI needs Forward+)",
            ["High (GI, volumetric fog, SSAO/SSIL)", "Medium (no SDFGI, lighter fog)", "Low (no GI or fog; Steam Deck)"],
            u => Math.Max(0, Array.IndexOf(UserSettings.LightingQualities, u.Lighting)), (u, i) => u.Lighting = UserSettings.LightingQualities[i]);
        var buttons = new HBoxContainer();
        var defaults = new Button { Name = "Defaults", Text = "Defaults" };
        defaults.Pressed += () =>
        {
            var d = UserSettings.Defaults();
            var u = UserSettings.Current;
            (u.WindowMode, u.Scale3D, u.Upscaler, u.Msaa, u.ScreenAA, u.Taa, u.VSync, u.MaxFps, u.Glow, u.Quality, u.Volume, u.Speed, u.FpsOverlay, u.RenderPath, u.Lighting) =
                (d.WindowMode, d.Scale3D, d.Upscaler, d.Msaa, d.ScreenAA, d.Taa, d.VSync, d.MaxFps, d.Glow, d.Quality, d.Volume, d.Speed, d.FpsOverlay, d.RenderPath, d.Lighting);
            Remaster.RenderPaths.Set(null);
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

    /// <summary>The captions by setting, with their cursor (lit while the control has the focus).</summary>
    private readonly Dictionary<string, (CursorMark Mark, Label Label)> _captions = new();

    /// <summary>The caption whose control has the focus now (for tests and pictures; "" none).</summary>
    public string Focused => _captions.FirstOrDefault(c => c.Value.Mark.On).Key ?? "";

    private void Caption(GridContainer grid, string key, string text)
    {
        var row = new HBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 4);
        var mark = new CursorMark { CustomMinimumSize = new Vector2(16, 16), Colour = GameTheme.Focus };
        var l = new Label { Text = text, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(mark);
        row.AddChild(l);
        grid.AddChild(row);
        _captions[key] = (mark, l);
    }

    /// <summary>The caption lit while <paramref name="c"/> has the focus.</summary>
    private void Follow(string key, Control c)
    {
        var (mark, label) = _captions[key];
        c.FocusEntered += () =>
        {
            mark.On = true;
            label.AddThemeColorOverride("font_color", MenuList.LitColour);
        };
        c.FocusExited += () =>
        {
            mark.On = false;
            label.RemoveThemeColorOverride("font_color");
        };
    }

    private void Choice(GridContainer grid, string key, string label, string[] items, Func<UserSettings, int> get, Action<UserSettings, int> set)
    {
        Caption(grid, key, label);
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
        Follow(key, o);
        _fill.Add(() => o.Select(Math.Clamp(get(UserSettings.Current), 0, items.Length - 1)));
    }

    private void Check(GridContainer grid, string key, string label, Func<UserSettings, bool> get, Action<UserSettings, bool> set, bool enabled = true)
    {
        Caption(grid, key, label);
        var c = new CheckBox { Name = key, Disabled = !enabled, Text = "", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        c.Toggled += on =>
        {
            if (_filling)
                return;
            set(UserSettings.Current, on);
            Changed();
        };
        grid.AddChild(c);
        Controls[key] = c;
        Follow(key, c);
        _fill.Add(() => c.ButtonPressed = get(UserSettings.Current));
    }

    private void Slider(GridContainer grid, string key, string label, Func<UserSettings, double> get, Action<UserSettings, double> set)
    {
        Caption(grid, key, label);
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
        Follow(key, s);
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
