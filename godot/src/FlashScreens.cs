using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx;

/// <summary>
/// The remaster's Flash (Iggy) screens, natively (docs/architecture.md, "The Flash screens").
/// The extension notes every movie the game opens and every AS3 call it makes on one
/// (<c>rust/ffx-gd/src/flash.rs</c>); <see cref="Poll"/> takes them once a frame and keeps what
/// the native screens draw: the tips the game plays (<c>OnPlay</c> on <c>box*.swf</c>), the save
/// and load list (<c>saveload_*.swf</c>: <see cref="SaveLoad"/>), the autosave prompts opened
/// (<c>chocobo.swf</c>). <see cref="Native"/> names each movie's native counterpart, and
/// <see cref="Check"/> fails a run in which the game opened or showed a movie without one.
/// </summary>
public static class FlashScreens
{
    /// <summary>
    /// The 19 movies of the game's archive (<c>ffx_data/gamedata/ps3data/flash/</c>) and what draws
    /// each natively. <c>autobattle_disable</c>, <c>freecamera</c> and <c>time_stop</c> are never
    /// shown by the original (its icon panel refuses icons 6, 8 and 9).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Native = new Dictionary<string, string>
    {
        ["escmenu.swf"] = "PauseView (the ESC menu)",
        ["pausemenu.swf"] = "PauseView (the pause)",
        ["saveload_jp.swf"] = "SaveLoadView",
        ["saveload_kr.swf"] = "SaveLoadView",
        ["saveload_ch.swf"] = "SaveLoadView",
        ["box.swf"] = "BoosterView (the tips)",
        ["box_kr.swf"] = "BoosterView (the tips)",
        ["box_ch.swf"] = "BoosterView (the tips)",
        ["openingscreen.swf"] = "OpeningView",
        ["chocobo.swf"] = "AutosaveView",
        ["speed_2.swf"] = "BoosterView (badge)",
        ["speed_4.swf"] = "BoosterView (badge)",
        ["invincible.swf"] = "BoosterView (badge)",
        ["enemy_0.swf"] = "BoosterView (badge)",
        ["enemy_1.swf"] = "BoosterView (badge)",
        ["autobattle.swf"] = "BoosterView (badge)",
        ["autobattle_disable.swf"] = "BoosterView (badge)",
        ["freecamera.swf"] = "BoosterView (badge)",
        ["time_stop.swf"] = "BoosterView (badge)",
    };

    /// <summary>The tips the game played, not shown yet (the text as the game passed it).</summary>
    public static readonly Queue<string> Tips = new();

    /// <summary>Every tip played so far (for tests).</summary>
    public static readonly List<string> TipsPlayed = new();

    /// <summary>The save and load list as the game set it up.</summary>
    public static readonly SaveLoadState SaveLoad = new();

    /// <summary>Autosave prompts the game opened so far (<c>chocobo.swf</c>).</summary>
    public static int AutosavePrompts { get; private set; }

    /// <summary>The movies the game opened (name, open order, draws), refreshed twice a second.</summary>
    public static List<(string Name, int Seq, long Draws)> Movies { get; private set; } = new();

    /// <summary>The AS3 calls taken so far, by movie and function (for tests and the check's note).</summary>
    public static readonly Dictionary<string, int> CallCounts = new();

    private static double _moviesAt = -1;
    private static int _lastMovieSeq;

    /// <summary>Takes the game's movie calls and, twice a second, its movies.</summary>
    public static void Poll(FfxGame game, double now)
    {
        if (game.FlashCallsPending() > 0)
            foreach (var c in game.FlashCalls())
                Take(c);
        if (now - _moviesAt >= 0.5)
        {
            _moviesAt = now;
            Movies = game.FlashMovies();
            foreach (var m in Movies)
            {
                if (m.Seq <= _lastMovieSeq)
                    continue;
                _lastMovieSeq = m.Seq;
                if (GameSettings.Flag("flash-log"))
                    GD.Print($"ffx flash: movie {m.Seq} opened: {m.Name}");
                if (m.Name == "chocobo.swf")
                    AutosavePrompts++;
            }
        }
    }

    private static void Take(FlashCall c)
    {
        var key = $"{c.Movie} {c.Function}";
        CallCounts[key] = CallCounts.GetValueOrDefault(key) + 1;
        if (GameSettings.Flag("flash-log"))
            GD.Print($"ffx flash: {c.Movie}#{c.Seq} {c.Function}({string.Join(", ", c.Args.Select(a => $"\"{a}\""))})");
        if (c.Movie.StartsWith("box") && c.Function == "OnPlay")
        {
            // OnPlay(0, text): TipSystem_PlayTip
            var text = c.Arg(1);
            Tips.Enqueue(text);
            TipsPlayed.Add(text);
            GD.Print($"ffx: tip: {MessageView.Clean(text.Replace("@el@", " "))}");
        }
        else if (c.Movie.StartsWith("saveload"))
            SaveLoad.Apply(c);
    }

    /// <summary>
    /// A picture of the screen into <c>--shot-dir</c> as <c>NAME.png</c> (under a renderer, not
    /// headless), once a name: the native screens' moments for <c>tools/ux-shots.sh</c>.
    /// </summary>
    public static void Shot(string name)
    {
        if (GameSettings.Arg("shot-dir") is not { Length: > 0 } dir || DisplayServer.GetName() == "headless" || !_shots.Add(name))
            return;
        System.IO.Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, name + ".png");
        (Engine.GetMainLoop() as SceneTree)?.Root.GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"ffx: picture {file}");
    }

    private static readonly HashSet<string> _shots = new();

    /// <summary>
    /// The movie check (<c>--check-flash</c>): every movie the game opened and every one it drew,
    /// and whether each has a native counterpart; fails on one without.
    /// </summary>
    public static (bool Pass, string Note) Check(FfxGame game)
    {
        Movies = game.FlashMovies();
        var opened = Movies.Select(m => m.Name).Distinct().OrderBy(n => n).ToList();
        var shown = Movies.Where(m => m.Draws > 0).Select(m => m.Name).Distinct().OrderBy(n => n).ToList();
        var missing = opened.Where(n => !Native.ContainsKey(n)).ToList();
        // the native screens drew what the game showed
        var undrawn = new List<string>();
        if (shown.Contains("chocobo.swf") && AutosaveView.TotalPlayed == 0)
            undrawn.Add("chocobo.swf (no autosave prompt played)");
        if (shown.Any(n => n.StartsWith("saveload")) && !SaveLoadView.EverShown)
            undrawn.Add("saveload (no list drawn)");
        if (GameHost.Instance?.NativeOpening == true && opened.Contains("openingscreen.swf") && OpeningView.Instance is not { LogoShown: true })
            undrawn.Add("openingscreen.swf (no logo drawn)");
        if (Tips.Count > 0)
            undrawn.Add($"{Tips.Count} tip(s) not shown");
        var note = $"flash movies opened: {string.Join(" ", opened)}; shown: {(shown.Count > 0 ? string.Join(" ", shown) : "none")}"
            + $"; {TipsPlayed.Count} tip(s), {AutosaveView.TotalPlayed} autosave prompt(s)"
            + (missing.Count > 0 ? $"; WITHOUT a native screen: {string.Join(" ", missing)}" : "; all native")
            + (undrawn.Count > 0 ? $"; NOT DRAWN: {string.Join(", ", undrawn)}" : "");
        return (missing.Count == 0 && undrawn.Count == 0, note);
    }
}

/// <summary>
/// The save and load list as the game sets it up on <c>saveload_*.swf</c> (its
/// <c>SaveDataMachine</c> reads the keys itself and drives the movie by calls; the movie calls
/// nothing back): <c>showSaveLoad</c>, <c>clearSaveLoad</c>, <c>addSaveLoadItem</c> (12 values:
/// index, slot, place, date, the player's name, play time, three party members <c>_n</c>, the
/// thumbnail <c>m&lt;n&gt;</c>, a label such as "Autosave", and a row text: the new file's row
/// "Create new save data" has only its slot 0 and that), <c>setSaveLoadHelpText</c>
/// (help, count), <c>setSaveLoadFocus</c>, <c>showPopUpStd</c> / <c>hidePopUpStd</c> (a message),
/// <c>showPopUpWithBtn</c> (question, yes, no, which button) / <c>changeSelectedBtn</c> /
/// <c>hidePopUpWithBtn</c>.
/// </summary>
public sealed class SaveLoadState
{
    public sealed record Item(int Index, string Slot, string Place, string Date, string Name, string PlayTime,
        string[] Party, string Thumbnail, string Label, string Extra);

    /// <summary>The rows by index.</summary>
    public SortedDictionary<int, Item> Items { get; } = new();
    public string Help { get; private set; } = "";
    public string Count { get; private set; } = "";
    public int Focus { get; private set; } = -1;
    /// <summary>The message pop-up's text (null: none).</summary>
    public string? Message { get; private set; }
    /// <summary>The question pop-up (null: none), its buttons and the one the cursor is on (0 yes, 1 no).</summary>
    public (string Text, string Yes, string No)? Question { get; private set; }
    public int Button { get; private set; }
    /// <summary>Counts the changes (a view redraws when it differs).</summary>
    public int Version { get; private set; }
    /// <summary>The calls' order of the last opening (<c>showSaveLoad</c>), for tests.</summary>
    public int Shown { get; private set; }
    public string Language { get; private set; } = "";
    /// <summary>The movie the calls went to (<c>saveload_jp.swf</c>, <c>_kr</c>, <c>_ch</c>).</summary>
    public string Movie { get; private set; } = "";

    public void Apply(FlashCall c)
    {
        Version++;
        Movie = c.Movie;
        switch (c.Function)
        {
            case "showSaveLoad":
                Shown++;
                break;
            case "SetLanguage":
                Language = c.Arg(0);
                break;
            case "clearSaveLoad":
                Items.Clear();
                Focus = -1;
                break;
            case "addSaveLoadItem":
                if (double.TryParse(c.Arg(0), System.Globalization.CultureInfo.InvariantCulture, out var i))
                    Items[(int)i] = new Item((int)i, c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5),
                        [c.Arg(6), c.Arg(7), c.Arg(8)], c.Arg(9), c.Arg(10), c.Arg(11));
                break;
            case "setSaveLoadHelpText":
                Help = c.Arg(0);
                Count = c.Arg(1);
                break;
            case "setSaveLoadFocus":
                Focus = int.TryParse(c.Arg(0), out var f) ? f : -1;
                break;
            case "showPopUpStd":
                Message = c.Arg(0);
                break;
            case "hidePopUpStd":
                Message = null;
                break;
            case "showPopUpWithBtn":
                Question = (c.Arg(0), c.Arg(1), c.Arg(2));
                // the movie's cursor starts on No: the game moves it to Yes at once for "Save?"
                // (changeSelectedBtn in the same frame) and leaves it on No for "Overwrite?"
                // (measured: the calls of a save and an overwrite; the M5 pilot's load "Load
                // this file?" likewise, its picture on Yes after the change). The fourth value
                // was false each time; true is taken to start on Yes (not seen).
                Button = c.Arg(3) == "true" ? 0 : 1;
                break;
            case "changeSelectedBtn":
                Button ^= 1;
                break;
            case "hidePopUpWithBtn":
                Question = null;
                break;
        }
    }
}
