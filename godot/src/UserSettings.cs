using System;
using System.Globalization;
using Godot;

namespace GdFfx;

/// <summary>
/// gd-ffx's own settings (G5, docs/running.md "Options"): the graphics (window mode, 3D scale
/// and upscaler, MSAA, screen-space AA, TAA, vsync, the frame cap, the glow), the sound's
/// volume, the game's speed for debugging and the FPS overlay. Kept in the user settings file
/// (<see cref="GameSettings.SettingsPath"/>, sections <c>[video]</c>, <c>[audio]</c>,
/// <c>[debug]</c>), changed by the options overlay (<see cref="OptionsPanel"/>, in game and in
/// the launcher) and applied at once. The command line wins for its run, unsaved:
/// <c>--quality=high|deck</c> (the preset's values), <c>--no-vsync</c>, <c>--no-glow</c>,
/// <c>--speed=X</c>. The self-checks and the benchmark (<c>--selftest*</c>, <c>--bench</c>)
/// start from the defaults, not the saved file, unless <c>--settings=PATH</c> names one, so a
/// player's settings never change a check's picture.
/// </summary>
public sealed class UserSettings
{
    /// <summary>The settings in use (read once, at the first use).</summary>
    public static UserSettings Current => _current ??= Load();

    private static UserSettings? _current;

    /// <summary>Whether <see cref="Apply"/> ran before (the start's leaves Godot's own defaults).</summary>
    private static bool _applied;

    /// <summary>Raised after a setting changed and was applied (the views that read one refresh).</summary>
    public static event Action? Changed;

    // ---- the values (the names are the file's keys) -----------------------------------------

    /// <summary><c>windowed</c>, <c>fullscreen</c> (borderless) or <c>exclusive</c>.</summary>
    public string WindowMode = "windowed";
    /// <summary>The 3D picture's size against the window's, 0.5 .. 2 (<c>Viewport.Scaling3DScale</c>).</summary>
    public float Scale3D = 1f;
    /// <summary><c>bilinear</c>, <c>fsr</c> (FSR 1.0) or <c>fsr2</c> (FSR 2.2; Forward+ only, both).</summary>
    public string Upscaler = "bilinear";
    /// <summary>MSAA samples for the 3D: 0, 2, 4 or 8.</summary>
    public int Msaa;
    /// <summary>Screen-space AA: <c>off</c>, <c>fxaa</c> or <c>smaa</c>.</summary>
    public string ScreenAA = "off";
    /// <summary>Temporal AA (Forward+ only).</summary>
    public bool Taa;
    /// <summary><c>on</c>, <c>off</c>, <c>adaptive</c> or <c>mailbox</c>.</summary>
    public string VSync = "on";
    /// <summary>Frames a second at most, 0 for no cap.</summary>
    public int MaxFps;
    /// <summary>The game's glow pass (off: as <c>--no-glow</c>).</summary>
    public bool Glow = true;
    /// <summary>The preset last chosen (<c>high</c>, <c>deck</c>; the values may differ since).</summary>
    public string Quality = "high";
    /// <summary>The sound's volume, 0 .. 1 (Godot's Master bus, which plays the game's mix).</summary>
    public float Volume = 1f;
    /// <summary>The game's speed, 0.25 .. 8 (ffx-host's clock; 1 is the game's own pace).</summary>
    public double Speed = 1;
    /// <summary>The FPS / frame-time overlay shown (F9).</summary>
    public bool FpsOverlay;
    /// <summary>The render path (docs/remaster-editor.md, "The render path"): <c>map</c> (as each map says), <c>classic</c> or <c>remastered</c> for all.</summary>
    public string RenderPath = "map";

    // ---- the choices the overlay offers ------------------------------------------------------

    public static readonly string[] WindowModes = ["windowed", "fullscreen", "exclusive"];
    public static readonly float[] Scales = [0.5f, 0.67f, 0.75f, 0.85f, 1f, 1.25f, 1.5f, 2f];
    public static readonly string[] Upscalers = ["bilinear", "fsr", "fsr2"];
    public static readonly int[] MsaaSamples = [0, 2, 4, 8];
    public static readonly string[] ScreenAAs = ["off", "fxaa", "smaa"];
    public static readonly string[] VSyncs = ["on", "off", "adaptive", "mailbox"];
    public static readonly int[] FpsCaps = [0, 30, 60, 120, 144, 240];
    public static readonly string[] Presets = ["high", "deck"];
    public static readonly double[] Speeds = [0.25, 0.5, 1, 2, 4, 8];

    /// <summary>Whether this run is a self-check or the benchmark (the saved file not read).</summary>
    public static bool IsCheck()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--selftest", StringComparison.Ordinal) || a == "--bench")
                return true;
        return false;
    }

    /// <summary>Whether Forward+ runs (FSR, TAA and the like need it).</summary>
    public static bool ForwardPlus() => RenderingServer.GetCurrentRenderingMethod() == "forward_plus";

    /// <summary>The preset's values (window mode, volume, speed and the overlay kept).</summary>
    public void ApplyPreset(string preset)
    {
        Quality = preset == "deck" ? "deck" : "high";
        if (Quality == "deck")
        {
            (Scale3D, Upscaler, Msaa, ScreenAA, Taa, MaxFps) = (0.75f, "bilinear", 0, "off", false, 60);
        }
        else
        {
            (Scale3D, Upscaler, Msaa, ScreenAA, Taa, MaxFps) = (1f, "bilinear", 0, "off", false, 0);
        }
        Glow = true;
        VSync = "on";
    }

    /// <summary>The defaults: the <c>high</c> preset (the project's own settings), or <c>deck</c> on a Steam Deck.</summary>
    public static UserSettings Defaults()
    {
        var u = new UserSettings();
        u.ApplyPreset(System.Environment.GetEnvironmentVariable("SteamDeck") == "1"
            || ProjectSettings.GetSetting("ffx/render/quality", "high").AsString() == "deck" ? "deck" : "high");
        return u;
    }

    /// <summary>
    /// Reads the settings: the defaults, the saved file over them (not for a check without
    /// <c>--settings=PATH</c>), then the command line's for this run.
    /// </summary>
    public static UserSettings Load()
    {
        var u = Defaults();
        if (!IsCheck() || GameSettings.Arg("settings") != null)
            u.Read(GameSettings.LoadSettingsFile());
        // the command line, for this run only
        if (GameSettings.Arg("quality") is "high" or "deck")
            u.ApplyPreset(GameSettings.Arg("quality")!);
        if (GameSettings.CommandLineFlag("no-vsync"))
            u.VSync = "off";
        if (GameSettings.CommandLineFlag("no-glow"))
            u.Glow = false;
        if (GameSettings.Arg("speed") is { } sp
            && double.TryParse(sp, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            u.Speed = Math.Clamp(speed, 0.25, 8);
        return u;
    }

    /// <summary>Forgets the settings read (the next <see cref="Current"/> reads them again; the self-checks).</summary>
    public static void Reload() => _current = null;

    private static T Pick<T>(T[] choices, T value, T fallback) => Array.IndexOf(choices, value) >= 0 ? value : fallback;

    /// <summary>Reads the file's values over these (unknown values ignored).</summary>
    public void Read(ConfigFile cfg)
    {
        string S(string section, string key, string d) => cfg.GetValue(section, key, d).AsString();
        double D(string section, string key, double d) =>
            double.TryParse(cfg.GetValue(section, key, d).AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        bool B(string section, string key, bool d) => cfg.GetValue(section, key, d).AsBool();
        WindowMode = Pick(WindowModes, S("video", "window_mode", WindowMode), WindowMode);
        Scale3D = (float)Math.Clamp(D("video", "scale_3d", Scale3D), 0.25, 2.0);
        Upscaler = Pick(Upscalers, S("video", "upscaler", Upscaler), Upscaler);
        Msaa = Pick(MsaaSamples, (int)D("video", "msaa", Msaa), Msaa);
        ScreenAA = Pick(ScreenAAs, S("video", "screen_aa", ScreenAA), ScreenAA);
        Taa = B("video", "taa", Taa);
        VSync = Pick(VSyncs, S("video", "vsync", VSync), VSync);
        MaxFps = Math.Max(0, (int)D("video", "max_fps", MaxFps));
        Glow = B("video", "glow", Glow);
        Quality = Pick(Presets, S("video", "quality", Quality), Quality);
        Volume = (float)Math.Clamp(D("audio", "volume", Volume), 0, 1);
        Speed = Math.Clamp(D("debug", "speed", Speed), 0.25, 8);
        FpsOverlay = B("debug", "fps_overlay", FpsOverlay);
        RenderPath = Pick(Remaster.RenderPaths.Choices, S("video", "render_path", RenderPath), RenderPath);
    }

    /// <summary>
    /// Writes the values into the user settings file (its other sections, the game folder's,
    /// kept); false when it cannot be written.
    /// </summary>
    public bool Save()
    {
        var cfg = GameSettings.LoadSettingsFile();
        cfg.SetValue("video", "window_mode", WindowMode);
        cfg.SetValue("video", "scale_3d", Scale3D.ToString("0.###", CultureInfo.InvariantCulture));
        cfg.SetValue("video", "upscaler", Upscaler);
        cfg.SetValue("video", "msaa", Msaa);
        cfg.SetValue("video", "screen_aa", ScreenAA);
        cfg.SetValue("video", "taa", Taa);
        cfg.SetValue("video", "vsync", VSync);
        cfg.SetValue("video", "max_fps", MaxFps);
        cfg.SetValue("video", "glow", Glow);
        cfg.SetValue("video", "quality", Quality);
        cfg.SetValue("audio", "volume", Volume.ToString("0.###", CultureInfo.InvariantCulture));
        cfg.SetValue("debug", "speed", Speed.ToString("0.###", CultureInfo.InvariantCulture));
        cfg.SetValue("debug", "fps_overlay", FpsOverlay);
        cfg.SetValue("video", "render_path", RenderPath);
        var ok = GameSettings.SaveSettingsFile(cfg);
        if (!ok)
            GD.PrintErr($"ffx: cannot write the settings to {GameSettings.SettingsPath}");
        return ok;
    }

    /// <summary>
    /// Applies the values: to <paramref name="viewport"/> (the root's when null; the 3D scale,
    /// the upscaler, MSAA, screen-space AA, TAA), the window (its mode, vsync; not headless),
    /// the frame cap, the glow (as the run-time flag <c>no-glow</c>), the volume and the running
    /// game's speed. Then <see cref="Changed"/>.
    /// </summary>
    public void Apply(Viewport? viewport = null)
    {
        // the first time (the start) a default leaves Godot's own (its command line's
        // --fullscreen, --max-fps and the like, the project's vsync) as it is
        bool first = !_applied;
        _applied = true;
        viewport ??= (Engine.GetMainLoop() as SceneTree)?.Root;
        bool fwd = ForwardPlus();
        if (viewport != null)
        {
            viewport.Scaling3DScale = Scale3D;
            viewport.Scaling3DMode = !fwd ? Viewport.Scaling3DModeEnum.Bilinear : Upscaler switch
            {
                "fsr" => Viewport.Scaling3DModeEnum.Fsr,
                "fsr2" => Viewport.Scaling3DModeEnum.Fsr2,
                _ => Viewport.Scaling3DModeEnum.Bilinear,
            };
            viewport.Msaa3D = Msaa switch
            {
                2 => Viewport.Msaa.Msaa2X,
                4 => Viewport.Msaa.Msaa4X,
                8 => Viewport.Msaa.Msaa8X,
                _ => Viewport.Msaa.Disabled,
            };
            viewport.ScreenSpaceAA = ScreenAA switch
            {
                "fxaa" => Viewport.ScreenSpaceAAEnum.Fxaa,
                "smaa" => Viewport.ScreenSpaceAAEnum.Smaa,
                _ => Viewport.ScreenSpaceAAEnum.Disabled,
            };
            viewport.UseTaa = fwd && Taa;
        }
        if (DisplayServer.GetName() != "headless")
        {
            var mode = WindowMode switch
            {
                "fullscreen" => DisplayServer.WindowMode.Fullscreen,
                "exclusive" => DisplayServer.WindowMode.ExclusiveFullscreen,
                _ => DisplayServer.WindowMode.Windowed,
            };
            var now = DisplayServer.WindowGetMode();
            // a maximised or minimised window counts as windowed
            bool windowed = now is DisplayServer.WindowMode.Windowed or DisplayServer.WindowMode.Maximized or DisplayServer.WindowMode.Minimized;
            if (!(first && WindowMode == "windowed") && (mode == DisplayServer.WindowMode.Windowed ? !windowed : now != mode))
                DisplayServer.WindowSetMode(mode);
            if (!(first && VSync == "on"))
                DisplayServer.WindowSetVsyncMode(VSync switch
            {
                "off" => DisplayServer.VSyncMode.Disabled,
                "adaptive" => DisplayServer.VSyncMode.Adaptive,
                "mailbox" => DisplayServer.VSyncMode.Mailbox,
                _ => DisplayServer.VSyncMode.Enabled,
            });
        }
        if (!(first && MaxFps == 0))
            Engine.MaxFps = MaxFps;
        GameSettings.SetFlag("no-glow", !Glow);
        int master = AudioServer.GetBusIndex("Master");
        if (master >= 0)
            AudioServer.SetBusVolumeDb(master, Volume <= 0.001f ? -80f : Mathf.LinearToDb(Volume));
        GameHost.Instance?.Game?.SetSpeed(Speed);
        Changed?.Invoke();
    }

    /// <summary>One line of the values (the checks' logs, the overlay's title).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"window {WindowMode}, 3D x{Scale3D:0.##} {Upscaler}, MSAA {Msaa}, AA {ScreenAA}{(Taa ? "+TAA" : "")}, vsync {VSync}, "
            + $"fps cap {(MaxFps == 0 ? "none" : MaxFps.ToString(CultureInfo.InvariantCulture))}, glow {(Glow ? "on" : "off")}, preset {Quality}, "
            + $"volume {Volume * 100:0}%, speed x{Speed:0.##}, fps overlay {(FpsOverlay ? "on" : "off")}, render path {RenderPath}");
}
