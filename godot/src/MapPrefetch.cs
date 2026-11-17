using System.Collections.Generic;
using System.Linq;
using GdFfx.Api;
using GdFfx.Remake;
using Godot;

namespace GdFfx;

/// <summary>
/// Loads the maps the player is likely to enter next while the current one plays
/// (docs/architecture.md, "Map transitions"), so a map change waits less for its map:
/// <list type="bullet">
/// <item>where it goes: the map change the game asked for (API 11's <c>World.map_change</c>:
/// <c>World.map</c> is then the target) first, else the exits of the current map, the maps its
/// event script jumps to with constant arguments (the extension's <c>map_exits</c>), in the
/// script's order;</item>
/// <item>the import (the archive to the cache folder, on the extension's threads) of each
/// candidate's map and 2D layers, one at a time;</item>
/// <item>the Godot scene of the first <see cref="Keep"/> candidates, built on a worker thread
/// (<see cref="ContentResolver.Start"/>) and taken by the 3D view's next <c>Resolve</c> of that
/// level; the oldest is dropped beyond <see cref="Keep"/>. Under the headless dummy renderer,
/// whose loads run on the main thread (<see cref="Background"/>), only the imports;</item>
/// <item>the battle map of the next random encounter: where the game can start one (encounters
/// not switched off with F3, no battle running), the battle maps of the current map's encounter
/// groups with a rate (the game's encounter table, <see cref="FfxGame.EncounterMaps"/>), imported
/// and the first one's scene held as well (<see cref="KeepBattle"/>, beside the exits').</item>
/// </list>
/// <c>--no-prefetch</c> turns it off.
/// </summary>
public sealed class MapPrefetch
{
    /// <summary>Map scenes held ready at most (each a whole map's meshes and textures).</summary>
    public const int Keep = 1;

    /// <summary>Exits imported ahead at most per map.</summary>
    public const int MostExits = 6;

    /// <summary>Battle map scenes held ready at most (beside <see cref="Keep"/>).</summary>
    public const int KeepBattle = 1;

    private readonly List<string> _battle = new();
    private readonly List<string> _battleLoads = new();
    private int _battleOf = -2;
    private double _battleAsked = -10;

    /// <summary>The battle maps the current map's random encounters use (for tests and the log).</summary>
    public IReadOnlyList<string> Battle => _battle;

    /// <summary>Battle map scenes started ahead (and not taken yet).</summary>
    public IReadOnlyList<string> BattleLoads => _battleLoads;

    private readonly Dictionary<int, (string Level, int[] Maps, string[] Levels)> _exits = new();
    private readonly List<string> _loads = new();
    private readonly List<string> _candidates = new();
    private readonly HashSet<string> _imported = new();
    private int _of = -1;
    private bool _change;

    /// <summary>Levels whose scene was started ahead (and not taken yet), oldest first.</summary>
    public IReadOnlyList<string> Loads => _loads;

    /// <summary>The candidates now, most likely first (for tests and the log).</summary>
    public IReadOnlyList<string> Candidates => _candidates;

    /// <summary>
    /// Once a frame: the candidates for the snapshot's map, their imports and scenes.
    /// <paramref name="shown"/> is the level the view shows, <paramref name="kept"/> the one it
    /// keeps from before (not loaded twice), <paramref name="prepare"/> its worker-thread step for
    /// a map scene (the object index).
    /// </summary>
    public void Tick(ClassicLibrary? lib, FfxGame? game, Snapshot? s, string shown, string kept, System.Func<Node3D, object?> prepare)
    {
        if (lib is not { Open: true } || game == null || s == null || GameSettings.Flag("no-prefetch"))
            return;
        var w = s.World;
        // taken by the view's Resolve (or dropped): no longer held here
        _loads.RemoveAll(static l => !ContentResolver.Pending("map", l));
        if (w.Map != _of || w.MapChange != _change)
        {
            _of = w.Map;
            _change = w.MapChange;
            _candidates.Clear();
        }
        if (_candidates.Count == 0 && Exits(game, w.Map) is { } x)
        {
            // while a map change is pending World.map is its target: that map's level first
            if (w.MapChange)
                Add(x.Level, shown, w.Level);
            else
                for (int i = 0; i < x.Levels.Length && _candidates.Count < MostExits; i++)
                    Add(x.Levels[i], shown, w.Level);
            if (_candidates.Count > 0)
                GD.Print($"ffx: prefetch: map 0x{w.Map:x4}{(w.MapChange ? " (the map change's target)" : "")}: {string.Join(" ", _candidates)}");
        }
        // the imports, one at a time (each reads and converts a whole map)
        foreach (var level in _candidates)
        {
            if (_imported.Contains(level))
                continue;
            bool map = lib.Path("map", level, quiet: true) != null || lib.Failed("map", level);
            bool layers = lib.Path("map2d", level, quiet: true) != null || lib.Failed("map2d", level);
            if (!map || !layers)
                break;
            _imported.Add(level);
        }
        TickBattle(lib, game, s, shown, kept, prepare);
        if (!Background.Threaded)
            return;
        // the scenes of the first candidates not shown or kept already, built on a worker
        int want = w.MapChange ? 1 : Keep;
        foreach (var level in _candidates)
        {
            if (want == 0)
                break;
            if (level == shown || level == kept || lib.Failed("map", level))
                continue;
            want--;
            if (_loads.Contains(level) || !_imported.Contains(level) || ContentResolver.Pending("map", level))
                continue; // held already, not imported yet, or the view's own load
            if (!ContentResolver.Start(lib, "map", level, prepare: prepare))
                continue;
            _loads.Add(level);
            GD.Print($"ffx: prefetch: {level} loading ahead");
        }
        // at most Keep held: the oldest that is not a candidate now goes first
        while (_loads.Count > Keep)
        {
            int drop = _loads.FindIndex(l => !_candidates.Contains(l));
            if (drop < 0)
                drop = 0;
            var level = _loads[drop];
            if (level == w.Level)
                break;
            _loads.RemoveAt(drop);
            ContentResolver.Forget("map", level);
            GD.Print($"ffx: prefetch: {level} dropped");
        }
    }

    /// <summary>
    /// The next battle's map: the levels of the current map's encounter groups with a rate, read
    /// again when the map the game looks encounters up for changes (and every 2 s until the table
    /// answers); imported, and the first one's scene held while an encounter can come.
    /// </summary>
    private void TickBattle(ClassicLibrary lib, FfxGame game, Snapshot s, string shown, string kept, System.Func<Node3D, object?> prepare)
    {
        _battleLoads.RemoveAll(static l => !ContentResolver.Pending("map", l));
        var w = s.World;
        // in a battle, or during a map change: the battle map is shown or the field not entered yet
        if (w.Battle || w.MapChange || !w.Level.StartsWith("/map/"))
            return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (w.Map != _battleOf && now - _battleAsked > 2)
        {
            _battleAsked = now;
            if (game.EncounterMaps() is { } e)
            {
                _battleOf = w.Map;
                _battle.Clear();
                foreach (var g in e.Groups)
                    if (g.Rate > 0 && g.Level.Length > 0 && !_battle.Contains(g.Level))
                        _battle.Add(g.Level);
                GD.Print($"ffx: prefetch: encounters on map 0x{e.Map:x4}: {(_battle.Count > 0 ? string.Join(" ", _battle) : "none")} (groups: {string.Join(", ", e.Groups.Select(g => $"0x{g.Map:x3} rate {g.Rate} {g.Level}"))})");
            }
        }
        if (_battle.Count == 0 || s.Boosters.Encounters == 0)
            return;
        foreach (var level in _battle)
        {
            if (_imported.Contains(level))
                continue;
            if (lib.Path("map", level, quiet: true) == null && !lib.Failed("map", level))
                break;
            _imported.Add(level);
        }
        if (!Background.Threaded)
            return;
        int want = KeepBattle;
        foreach (var level in _battle)
        {
            if (want == 0)
                break;
            if (level == shown || level == kept || lib.Failed("map", level))
                continue;
            want--;
            if (_battleLoads.Contains(level) || _loads.Contains(level) || !_imported.Contains(level) || ContentResolver.Pending("map", level))
                continue;
            if (!ContentResolver.Start(lib, "map", level, prepare: prepare))
                continue;
            _battleLoads.Add(level);
            GD.Print($"ffx: prefetch: battle map {level} loading ahead");
        }
        while (_battleLoads.Count > KeepBattle)
        {
            int drop = _battleLoads.FindIndex(l => !_battle.Contains(l));
            if (drop < 0)
                drop = 0;
            var level = _battleLoads[drop];
            _battleLoads.RemoveAt(drop);
            ContentResolver.Forget("map", level);
            GD.Print($"ffx: prefetch: battle map {level} dropped");
        }
    }

    /// <summary>Whether <paramref name="level"/> was loaded ahead (an exit's or a battle's).</summary>
    public bool Holds(string level) => _loads.Contains(level) || _battleLoads.Contains(level);

    private void Add(string level, string shown, string current)
    {
        if (level.Length > 0 && level != shown && level != current && !_candidates.Contains(level))
            _candidates.Add(level);
    }

    private (string Level, int[] Maps, string[] Levels)? Exits(FfxGame game, int map)
    {
        if (_exits.TryGetValue(map, out var x))
            return x;
        if (game.MapExits(map) is not { } got)
            return null;
        _exits[map] = got;
        return got;
    }
}
