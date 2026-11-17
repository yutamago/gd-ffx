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
    /// <summary>
    /// The command line (the user's arguments, after <c>--</c>, then Godot's), read once: views
    /// ask for flags every frame, and each read from Godot made two new string arrays.
    /// </summary>
    private static string[] CmdLine => _cmdLine ??= OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();

    private static string[]? _cmdLine;

    /// <summary>Whether <paramref name="a"/> is <c>--name</c>, followed by <paramref name="then"/> (if any).</summary>
    private static bool IsOption(string a, string name, char? then) =>
        a.Length >= name.Length + (then == null ? 2 : 3) && a.StartsWith("--", StringComparison.Ordinal)
        && string.CompareOrdinal(a, 2, name, 0, name.Length) == 0
        && (then == null ? a.Length == name.Length + 2 : a[name.Length + 2] == then);

    /// <summary>The value of <c>--name=VALUE</c> on the command line, if any.</summary>
    public static string? Arg(string name)
    {
        foreach (var a in CmdLine)
            if (IsOption(a, name, '='))
                return a[(name.Length + 3)..];
        return null;
    }

    /// <summary>Every value of <c>--name=VALUE</c> on the command line, in order.</summary>
    public static string[] Args(string name) =>
        CmdLine.Where(a => IsOption(a, name, '=')).Select(a => a[(name.Length + 3)..]).Distinct().ToArray();

    /// <summary>
    /// Whether the command line has the flag <c>--name</c>, or it is set at run time
    /// (<see cref="SetFlag"/>: the options overlay's switches that the views read as flags,
    /// e.g. <c>no-glow</c>).
    /// </summary>
    public static bool Flag(string name)
    {
        if (RunFlags.Count > 0 && RunFlags.Contains(name))
            return true;
        foreach (var a in CmdLine)
            if (IsOption(a, name, null))
                return true;
        return false;
    }

    private static readonly System.Collections.Generic.HashSet<string> RunFlags = new();

    /// <summary>Whether <c>--name</c> is on the command line itself (not set at run time).</summary>
    public static bool CommandLineFlag(string name)
    {
        foreach (var a in CmdLine)
            if (IsOption(a, name, null))
                return true;
        return false;
    }

    /// <summary>Sets or clears the run-time flag <c>name</c> (<see cref="Flag"/>); a command-line flag stays set.</summary>
    public static void SetFlag(string name, bool on)
    {
        if (on)
            RunFlags.Add(name);
        else
            RunFlags.Remove(name);
    }

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

    /// <summary>The languages <c>GameSetting.ini</c>'s <c>Language=</c> takes (the game's key table).</summary>
    public static readonly string[] GameLanguages = ["jp", "en", "fr", "de", "it", "es", "kr", "ch"];

    /// <summary>
    /// Sets the game's language in <c>GameSetting.ini</c> in <paramref name="documents"/> (the
    /// sandbox; the game reads it at boot): the <c>Language=</c> line replaced, every other line
    /// kept as it is; a missing file is made with that line only (the game fills in its
    /// defaults for the other keys and writes them back). Null when written, else why not.
    /// </summary>
    public static string? SetGameLanguage(string documents, string code)
    {
        code = code.ToLowerInvariant();
        if (System.Array.IndexOf(GameLanguages, code) < 0)
            return $"not a language of the game: {code}";
        var ini = GameSettingIni(documents);
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ini)!);
            if (!System.IO.File.Exists(ini))
            {
                System.IO.File.WriteAllText(ini, $"Language={code}\r\n");
                return null;
            }
            var text = System.IO.File.ReadAllText(ini);
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var eq = lines[i].IndexOf('=');
                if (eq > 0 && lines[i][..eq].Trim().Equals("Language", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"Language={code}";
                    found = true;
                }
            }
            if (!found)
                lines.Insert(0, $"Language={code}");
            System.IO.File.WriteAllText(ini, string.Join(nl, lines) + nl);
            return null;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
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
    /// The quality preset in use (<see cref="UserSettings.Quality"/>: <c>--quality=high|deck</c>,
    /// else the one saved by the options, else <c>deck</c> on a Steam Deck, where Steam sets
    /// <c>SteamDeck=1</c>, or with the project setting <c>ffx/render/quality</c>, else <c>high</c>).
    /// </summary>
    public static string Quality() => UserSettings.Current.Quality;

    /// <summary>
    /// Applies the graphics settings to <paramref name="viewport"/> (<see cref="UserSettings"/>:
    /// the player's, saved in <see cref="SettingsPath"/>, with the command line's for this run:
    /// <c>--quality=high|deck</c>, <c>--no-vsync</c>). The <c>deck</c> preset draws the 3D at
    /// 75 % of the screen (scaled up), without MSAA, at most 60 frames a second (the Deck's
    /// screen). The Mobile renderer (<c>--rendering-method mobile</c>) is a launch option, as
    /// Godot picks it at start.
    /// </summary>
    public static void ApplyQuality(Viewport viewport) => UserSettings.Current.Apply(viewport);

    /// <summary>The user settings file, read (empty when there is none).</summary>
    internal static ConfigFile LoadSettingsFile() => LoadSettings();

    /// <summary>Writes <paramref name="cfg"/> as the user settings file; false when it cannot be written.</summary>
    internal static bool SaveSettingsFile(ConfigFile cfg)
    {
        var path = Globalize(SettingsPath);
        var parent = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            System.IO.Directory.CreateDirectory(parent);
        return cfg.Save(path) == Error.Ok;
    }

    public static bool AudioEnabled() =>
        !Flag("mute") && ProjectSettings.GetSetting("ffx/audio/enabled", true).AsBool();

    /// <summary>A path with <c>res://</c> or <c>user://</c> made absolute; others as they are.</summary>
    public static string Globalize(string path) =>
        path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(path)
            : path;
}
