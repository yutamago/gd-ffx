using System;
using System.Linq;
using Godot;

namespace GdFfx;

/// <summary>
/// Where the player's game is and where its settings and saves go.
///
/// The game folder (the one with <c>FFX.exe</c> and <c>data/</c>), first found of:
/// the command line's <c>--game-dir=PATH</c> (after <c>--</c>), the environment's
/// <c>FFX_GAME_DIR</c>, the folder the player picked in the launcher (remembered in the user
/// settings file, <see cref="SettingsPath"/>), the project setting <c>ffx/game/game_dir</c>.
/// It is only read.
///
/// The documents folder (the game's <c>GameSetting.ini</c> and saves): <c>--documents=PATH</c>,
/// else <c>ffx/game/documents_dir</c> (default <c>user://documents</c>): a sandbox of
/// gd-ffx's own, never the player's documents folder.
/// </summary>
public static class GameSettings
{
    /// <summary>The value of <c>--name=VALUE</c> on the command line, if any.</summary>
    public static string? Arg(string name)
    {
        var prefix = $"--{name}=";
        return OS.GetCmdlineUserArgs()
            .Concat(OS.GetCmdlineArgs())
            .Where(a => a.StartsWith(prefix, StringComparison.Ordinal))
            .Select(a => a[prefix.Length..])
            .FirstOrDefault();
    }

    /// <summary>Every value of <c>--name=VALUE</c> on the command line, in order.</summary>
    public static string[] Args(string name)
    {
        var prefix = $"--{name}=";
        return OS.GetCmdlineUserArgs()
            .Concat(OS.GetCmdlineArgs())
            .Where(a => a.StartsWith(prefix, StringComparison.Ordinal))
            .Select(a => a[prefix.Length..])
            .Distinct()
            .ToArray();
    }

    /// <summary>Whether the command line has the flag <c>--name</c>.</summary>
    public static bool Flag(string name) =>
        OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).Contains($"--{name}");

    /// <summary>A number from <c>--name=N</c>, else <paramref name="fallback"/>.</summary>
    public static double Number(string name, double fallback) =>
        double.TryParse(Arg(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public static string GameDir() => Globalize(GameDirFrom().Dir);

    /// <summary>
    /// The game folder and where it came from: <c>--game-dir</c>, <c>FFX_GAME_DIR</c>, the
    /// folder the player picked in the launcher (<see cref="SavedGameDir"/>), else the project
    /// setting (a default for development); the source is empty when none is set.
    /// </summary>
    public static (string Dir, string Source) GameDirFrom()
    {
        var dir = Arg("game-dir");
        if (!string.IsNullOrEmpty(dir))
            return (dir, "--game-dir");
        dir = OS.GetEnvironment("FFX_GAME_DIR");
        if (!string.IsNullOrEmpty(dir))
            return (dir, "FFX_GAME_DIR");
        dir = SavedGameDir();
        if (!string.IsNullOrEmpty(dir))
            return (dir, SettingsPath);
        dir = ProjectSettings.GetSetting("ffx/game/game_dir", "").AsString();
        if (!string.IsNullOrEmpty(dir))
            return (dir, "the project setting ffx/game/game_dir");
        return ("", "");
    }

    /// <summary>
    /// The user settings file (the launcher's choices): <c>--settings=PATH</c>, else
    /// <c>user://settings.cfg</c>; settable (the launcher's self-test).
    /// </summary>
    public static string SettingsPath
    {
        get => _settingsPath ??= Arg("settings") is { Length: > 0 } p ? p : "user://settings.cfg";
        set => _settingsPath = value;
    }

    private static string? _settingsPath;

    private static ConfigFile LoadSettings()
    {
        var cfg = new ConfigFile();
        if (FileAccess.FileExists(Globalize(SettingsPath)))
            cfg.Load(Globalize(SettingsPath));
        return cfg;
    }

    /// <summary>The game folder the launcher remembered (<c>[game] dir</c> in <see cref="SettingsPath"/>), or "".</summary>
    public static string SavedGameDir() => LoadSettings().GetValue("game", "dir", "").AsString();

    /// <summary>Remembers <paramref name="dir"/> as the game folder; false when the file cannot be written.</summary>
    public static bool SaveGameDir(string dir)
    {
        var cfg = LoadSettings();
        cfg.SetValue("game", "dir", dir);
        var path = Globalize(SettingsPath);
        var parent = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            System.IO.Directory.CreateDirectory(parent);
        return cfg.Save(path) == Error.Ok;
    }

    /// <summary>
    /// Why <paramref name="dir"/> is not a game folder (it needs <c>FFX.exe</c> and
    /// <c>data/FFX_Data.vbf</c>), or null when it is one. Only looks; reads nothing.
    /// </summary>
    public static string? CheckGameDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return "no folder chosen";
        dir = Globalize(dir);
        if (!System.IO.Directory.Exists(dir))
            return "the folder is not there";
        if (!System.IO.File.Exists(System.IO.Path.Combine(dir, "FFX.exe")))
            return "no FFX.exe in it";
        if (!System.IO.File.Exists(System.IO.Path.Combine(dir, "data", "FFX_Data.vbf")))
            return "no data/FFX_Data.vbf in it";
        return null;
    }

    /// <summary>The game's settings file in <paramref name="documents"/>, where the game reads and writes it.</summary>
    public static string GameSettingIni(string documents) =>
        System.IO.Path.Combine(documents, "Square Enix", "FINAL FANTASY X&X-2 HD Remaster", "GameSetting.ini");

    /// <summary>
    /// The game's language from <c>GameSetting.ini</c> in <paramref name="documents"/>
    /// (<c>Language=</c>: jp en fr de it es kr ch), or null when the file or the key is
    /// missing (the game then starts in Japanese and writes its default file).
    /// </summary>
    public static string? GameLanguage(string documents)
    {
        var ini = GameSettingIni(documents);
        if (!System.IO.File.Exists(ini))
            return null;
        // the game splits the file at white space into key=value words (keys in any case)
        foreach (var word in System.IO.File.ReadAllText(ini).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = word.IndexOf('=');
            if (eq > 0 && word[..eq].Equals("Language", StringComparison.OrdinalIgnoreCase))
                return word[(eq + 1)..];
        }
        return null;
    }

    public static string DocumentsDir()
    {
        var dir = Arg("documents");
        if (string.IsNullOrEmpty(dir))
            dir = ProjectSettings.GetSetting("ffx/game/documents_dir", "user://documents").AsString();
        return Globalize(dir);
    }

    /// <summary>The classic importer's cache (<c>--classic=PATH</c>, else <c>ffx/classic/cache_dir</c>, default <c>user://classic</c>).</summary>
    public static string ClassicDir()
    {
        var dir = Arg("classic");
        if (string.IsNullOrEmpty(dir))
            dir = ProjectSettings.GetSetting("ffx/classic/cache_dir", "user://classic").AsString();
        return Globalize(dir);
    }

    /// <summary>A whole number from <c>--name=N</c>, else <paramref name="fallback"/>.</summary>
    public static int Int(string name, int fallback) =>
        int.TryParse(Arg(name), out var v) ? v : fallback;

    /// <summary>
    /// Whether the game's sound plays through Godot: off with <c>--mute</c> on the
    /// command line or the project setting <c>ffx/audio/enabled</c> false.
    /// </summary>
    /// <summary>
    /// The quality preset: <c>--quality=high|deck</c>, else <c>deck</c> on a Steam Deck (Steam sets
    /// <c>SteamDeck=1</c> there), else the project setting <c>ffx/render/quality</c> (default
    /// <c>high</c>). <c>deck</c> is the plan's lighter preset (<see cref="ApplyQuality"/>).
    /// </summary>
    public static string Quality()
    {
        var q = Arg("quality");
        if (q is "high" or "deck")
            return q;
        if (System.Environment.GetEnvironmentVariable("SteamDeck") == "1")
            return "deck";
        var p = ProjectSettings.GetSetting("ffx/render/quality", "high").AsString();
        return p == "deck" ? "deck" : "high";
    }

    /// <summary>
    /// Applies <see cref="Quality"/> to <paramref name="viewport"/>: <c>deck</c> draws the 3D at
    /// 75 % of the screen (scaled up), without MSAA, at most 60 frames a second (the Deck's
    /// screen); <c>high</c> leaves the project's settings. <c>--no-vsync</c> turns vsync off. The Mobile renderer
    /// (<c>--rendering-method mobile</c>) is a launch option, as Godot picks it at start.
    /// </summary>
    public static void ApplyQuality(Viewport viewport)
    {
        // --no-vsync: frames not held to the display's refresh (the frame rate unlocked, as the
        // benchmark and the original's unlocked mode measure it)
        if (Flag("no-vsync"))
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        if (Quality() != "deck")
            return;
        viewport.Scaling3DScale = 0.75f;
        viewport.Msaa3D = Viewport.Msaa.Disabled;
        if (Engine.MaxFps == 0 || Engine.MaxFps > 60)
            Engine.MaxFps = 60;
        GD.Print("ffx: quality deck (3D at 75 %, no MSAA, 60 fps at most)");
    }

    public static bool AudioEnabled() =>
        !Flag("mute") && ProjectSettings.GetSetting("ffx/audio/enabled", true).AsBool();

    /// <summary>A path with <c>res://</c> or <c>user://</c> made absolute; others as they are.</summary>
    public static string Globalize(string path) =>
        path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(path)
            : path;
}
