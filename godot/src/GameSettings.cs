using System;
using System.Linq;
using Godot;

namespace GdFfx;

/// <summary>
/// Where the player's game is and where its settings and saves go.
///
/// The game folder (the one with <c>FFX.exe</c> and <c>data/</c>), first found of:
/// the command line's <c>--game-dir=PATH</c> (after <c>--</c>), the environment's
/// <c>FFX_GAME_DIR</c>, the project setting <c>ffx/game/game_dir</c>. It is only read.
///
/// The documents folder (the game's <c>GameSetting.ini</c> and saves): <c>--documents=PATH</c>,
/// else <c>ffx/game/documents_dir</c> (default <c>user://documents</c>): a sandbox of
/// gd-ffx's own, never the player's documents folder.
/// </summary>
public static class GameSettings
{
    private static string? Arg(string name)
    {
        var prefix = $"--{name}=";
        return OS.GetCmdlineUserArgs()
            .Concat(OS.GetCmdlineArgs())
            .Where(a => a.StartsWith(prefix, StringComparison.Ordinal))
            .Select(a => a[prefix.Length..])
            .FirstOrDefault();
    }

    /// <summary>Whether the command line has the flag <c>--name</c>.</summary>
    public static bool Flag(string name) =>
        OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).Contains($"--{name}");

    /// <summary>A number from <c>--name=N</c>, else <paramref name="fallback"/>.</summary>
    public static double Number(string name, double fallback) =>
        double.TryParse(Arg(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public static string GameDir()
    {
        var dir = Arg("game-dir");
        if (string.IsNullOrEmpty(dir))
            dir = OS.GetEnvironment("FFX_GAME_DIR");
        if (string.IsNullOrEmpty(dir))
            dir = ProjectSettings.GetSetting("ffx/game/game_dir", "").AsString();
        return Globalize(dir);
    }

    public static string DocumentsDir()
    {
        var dir = Arg("documents");
        if (string.IsNullOrEmpty(dir))
            dir = ProjectSettings.GetSetting("ffx/game/documents_dir", "user://documents").AsString();
        return Globalize(dir);
    }

    /// <summary>
    /// Whether the game's sound plays through Godot: off with <c>--mute</c> on the
    /// command line or the project setting <c>ffx/audio/enabled</c> false.
    /// </summary>
    public static bool AudioEnabled() =>
        !Flag("mute") && ProjectSettings.GetSetting("ffx/audio/enabled", true).AsBool();

    private static string Globalize(string path) =>
        path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(path)
            : path;
}
