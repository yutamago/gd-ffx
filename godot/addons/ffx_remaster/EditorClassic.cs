#if TOOLS
using System.Collections.Generic;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The player's archive in the editor (docs/remaster-editor.md): the extension's classic
/// functions without booting the game. The game folder is the first set of the editor setting
/// <see cref="GameDirSetting"/>, then the game's own order (<see cref="GameSettings.GameDirFrom"/>:
/// <c>--game-dir</c>, <c>FFX_GAME_DIR</c>, the launcher's remembered folder, the project
/// setting); the classic cache is the game's (<see cref="GameSettings.ClassicDir"/>,
/// <c>user://classic</c>), so the editor and the game import each map once between them.
/// The folder is only read.
/// </summary>
public sealed class EditorClassic
{
    /// <summary>The editor setting with the game folder (empty: the game's own order).</summary>
    public const string GameDirSetting = "ffx_remaster/game_dir";

    /// <summary>The extension's game object (never booted, never in the scene tree).</summary>
    public FfxGame? Game { get; private set; }

    public ClassicLibrary? Classic { get; private set; }

    public MapCatalogue? Catalogue { get; private set; }

    /// <summary>The developers' saves: (save, map number, entrance, scenario).</summary>
    public (int Save, int Map, int Entrance, int Scenario)[] DevSaves { get; private set; } = [];

    /// <summary>The folder opened (empty: none), where it came from, and why it failed.</summary>
    public string GameDir { get; private set; } = "";
    public string Source { get; private set; } = "";
    public string Error { get; private set; } = "";

    public bool IsOpen => Classic?.Open == true;

    /// <summary>The game folder to use and where it came from (see the class).</summary>
    public static (string Dir, string Source) FindGameDir()
    {
        var es = EditorInterface.Singleton.GetEditorSettings();
        if (es.HasSetting(GameDirSetting) && es.GetSetting(GameDirSetting).AsString() is { Length: > 0 } d)
            return (d, $"the editor setting {GameDirSetting}");
        return GameSettings.GameDirFrom();
    }

    /// <summary>
    /// Opens the archive (again: a new folder) and reads the catalogue in every language and the
    /// developers' saves. False with <see cref="Error"/> set when it could not.
    /// </summary>
    public bool Open(string? dir = null)
    {
        var (found, source) = FindGameDir();
        if (dir != null)
            (found, source) = (dir, "chosen in the dock");
        GameDir = GameSettings.Globalize(found);
        Source = source;
        Error = "";
        if (GameDir.Length == 0)
            return Fail("No game folder: set it in the dock (the editor setting " + GameDirSetting
                + "), choose one in the launcher, or set FFX_GAME_DIR.");
        if (GameSettings.CheckGameDir(GameDir) is { } why)
            return Fail($"{GameDir}: {why}");
        if (Game == null)
        {
            if (!ClassDB.ClassExists("FfxGame"))
                return Fail("the ffx-gd extension is not loaded (build it: docs/building.md)");
            Game = new FfxGame();
            GameHost.EditorGame = Game;
        }
        Classic = new ClassicLibrary(Game, GameDir, GameSettings.ClassicDir());
        if (!Classic.Open)
            return Fail($"the archive did not open: {Game.LastError}");
        Catalogue = MapCatalogue.Read(Game);
        DevSaves = Game.ClassicDevSaves();
        GD.Print($"ffx remaster: archive {GameDir} ({Source}): {Catalogue?.Levels.Count ?? 0} levels, "
            + $"{Catalogue?.Areas.Count ?? 0} areas, {DevSaves.Length} developer saves; cache {GameSettings.ClassicDir()}");
        return true;
    }

    private bool Fail(string why)
    {
        Error = why;
        GD.PrintErr($"ffx remaster: {why}");
        return false;
    }

    /// <summary>Picks up finished imports (once a frame).</summary>
    public void Poll() => Classic?.Poll();

    /// <summary>The developers' saves that start on <paramref name="entry"/> (their map number is one of its events').</summary>
    public List<int> DevSavesOn(MapEntry entry)
    {
        var maps = new HashSet<int>();
        foreach (var e in entry.Events)
            maps.Add(e.Map);
        var r = new List<int>();
        foreach (var d in DevSaves)
            if (maps.Contains(d.Map))
                r.Add(d.Save);
        return r;
    }

    /// <summary>
    /// How to start the game at <paramref name="entry"/>: a developer save on the map, else one in
    /// the same area (the nearest the game can start at: it has no jump to a map), else none.
    /// </summary>
    public PlayPlan PlanFor(MapEntry entry)
    {
        if (DevSavesOn(entry) is { Count: > 0 } on)
            return new PlayPlan(on[0], on, true, $"developer save {on[0]} starts on {entry.Key}");
        if (Catalogue != null)
            foreach (var d in DevSaves)
                if (Catalogue.FindMap(d.Map) is { } other && other.Area == entry.Area && other.Kind == "field")
                    return new PlayPlan(d.Save, [d.Save], false,
                        $"no developer save starts on {entry.Key}; save {d.Save} starts nearby on {other.Key} ({other.Title()}), "
                        + "walk there (the game has no jump to a map)");
        return new PlayPlan(0, [], false, $"no developer save starts on {entry.Key} or in its area; "
            + "start the game and walk there (the game has no jump to a map)");
    }
}

/// <summary>How "Play" starts the game at a map: developer save <see cref="Save"/> (0: none), every save on it, whether it is on the map itself.</summary>
public sealed record PlayPlan(int Save, IReadOnlyList<int> Saves, bool OnMap, string Note)
{
    /// <summary>The game's command line for the run (after <c>--</c>).</summary>
    public string Arguments => Save > 0 ? $"--devsave={Save}" : "";
}
#endif
