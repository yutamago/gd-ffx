using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R7's movie check (<c>--selftest-movie --devsave=22 --movie-video=FILE.ogv</c>;
/// docs/remaster-editor.md, "Checks"): developer save 22 (Luca) plays the movie <c>LCI_us</c>
/// (the Japanese or English file by the voice language: <c>LCI_jp</c>, <c>LCI_us</c>; <c>--movie-id=</c> another) as its event starts. An original test pattern
/// (<c>tools/make-test-movie.sh</c> makes one with Godot's movie writer) with a subtitle file made
/// here replaces it (or the editor's file: <c>--movies-file=FILE</c>). It checks: the game opens the
/// movie and the replacement plays over the picture (its video advancing with the game's movie
/// frame), the subtitle shows, the game's movie sound is silent in its mix (against its level
/// with F8: <see cref="Replacements"/> off brings the game's movie and its sound back and hides
/// the replacement). Movies the game opens without a replacement stay its own.
/// </summary>
public sealed class MovieTest(double timeout) : ITest
{
    private enum Phase { Setup, Wait, Playing, Off, Done }

    private Phase _phase = Phase.Setup;
    private double _phaseTime, _total;
    private bool _acted;
    private readonly List<string> _seen = new();
    private readonly string _dir = ProjectSettings.GlobalizePath("user://selftest-movie");
    private readonly string _id = GameSettings.Arg("movie-id") is { Length: > 0 } i ? i : "LCI";
    private readonly string? _video = GameSettings.Arg("movie-video");
    private readonly string? _file = GameSettings.Arg("movies-file");
    private readonly List<double> _levels = new();
    private double _levelReplaced, _levelClassic;
    private long _frame0 = -1, _frame1 = -1;
    private double _pos0, _pos1;
    private bool _subtitle;

    public string Note { get; private set; } = "";

    private static MovieView? View =>
        (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("MovieView", true, false) as MovieView;

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        var mv = View;
        if (_total > timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)}; movies opened: {string.Join(", ", mv?.Opened ?? new())}; {string.Join("; ", _seen)})");
        switch (_phase)
        {
            case Phase.Setup:
                if (Setup() is { } err)
                    return Done(false, err);
                Next(Phase.Wait);
                break;
            case Phase.Wait when mv != null:
                if (mv.Log.Count == 0)
                {
                    if (mv.Opened.Count > 0 && !mv.Opened.Any(Matches) && _phaseTime > 60)
                        return Done(false, $"the game opened {string.Join(", ", mv.Opened)}, not {_id}");
                    break;
                }
                var shown = mv.Log[0];
                if (!Matches(shown.Movie))
                    return Done(false, $"{shown.Movie} was replaced, not {_id}");
                if (!shown.Silenced)
                    return Done(false, "the game's movie sound was not silenced");
                _seen.Add($"the game opened {shown.Movie}: replaced by {shown.Video}");
                Next(Phase.Playing);
                break;
            case Phase.Playing:
            {
                if (mv!.Playing == null)
                    return Done(false, $"the replacement stopped after {_phaseTime:F1} s (the game's frame {mv.GameFrame})");
                if (!mv.Visible || !mv.Video.IsPlaying())
                    return Done(false, "the replacement's video is not playing");
                _levels.Add(game.AudioMixLevel());
                _subtitle |= mv.SubtitleText.Length > 0;
                if (_frame0 < 0 && _phaseTime > 0.5)
                    (_frame0, _pos0) = (mv.GameFrame, mv.Video.StreamPosition);
                if (_phaseTime < 3.5)
                    break;
                (_frame1, _pos1) = (mv.GameFrame, mv.Video.StreamPosition);
                if (_frame1 <= _frame0)
                    return Done(false, $"the game's movie did not advance (frame {_frame0} -> {_frame1})");
                if (_pos1 <= _pos0)
                    return Done(false, $"the replacement did not advance ({_pos0:F2} -> {_pos1:F2} s)");
                if (_file == null && !_subtitle)
                    return Done(false, "the subtitle was not shown");
                _levelReplaced = _levels.Skip(_levels.Count / 3).Average();
                _seen.Add($"playing: the game's movie frames {_frame0} -> {_frame1}, the replacement {_pos0:F2} -> {_pos1:F2} s"
                    + $"{(_subtitle ? ", the subtitle shown" : "")}; the game's mix at {_levelReplaced:F5} RMS");
                Next(Phase.Off);
                break;
            }
            case Phase.Off when !_acted:
                _acted = true;
                _levels.Clear();
                Replacements.Toggle();
                break;
            case Phase.Off:
                _levels.Add(game.AudioMixLevel());
                if (_phaseTime < 3.0)
                    break;
                if (mv!.Playing != null || mv.Visible)
                    return Done(false, "F8: the replacement is still shown");
                _levelClassic = _levels.Skip(_levels.Count / 3).Average();
                _seen.Add($"F8: the game's movie and its sound back (the mix at {_levelClassic:F5} RMS)");
                if (_levelClassic < 1e-4)
                    return Done(false, $"F8: the game's movie sound is silent too ({_levelClassic:F6})");
                if (_levelReplaced > _levelClassic * 0.5)
                    return Done(false, $"the game's movie sound was not silent while replaced: {_levelReplaced:F5} against {_levelClassic:F5}");
                Replacements.Toggle();
                Next(Phase.Done);
                break;
            case Phase.Done:
                Cleanup();
                return Done(true, string.Join("; ", _seen));
        }
        return null;
    }

    /// <summary>Whether the game's movie <paramref name="name"/> is the one checked (any language's when the id has none).</summary>
    private bool Matches(string name)
    {
        string k = AudioReplacements.MovieKey(name), id = AudioReplacements.MovieKey(_id);
        return k == id || AudioReplacements.BaseMovieKey(k) == id;
    }

    private string? Setup()
    {
        if (_file != null)
        {
            AudioReplacements.Register(_file, true, "selftest-movie");
            return AudioReplacements.Movies.ContainsKey(AudioReplacements.MovieKey(_id)) ? null : $"the editor's file does not replace {_id}";
        }
        if (_video == null || !FileAccess.FileExists(_video))
            return "--movie-video=FILE.ogv is required (tools/make-test-movie.sh makes one)";
        DirAccess.MakeDirRecursiveAbsolute(_dir);
        var srt = $"{_dir}/test.srt";
        using (var f = FileAccess.Open(srt, FileAccess.ModeFlags.Write))
            f.StoreString("1\n00:00:00,200 --> 00:00:30,000\nR7 test subtitle\n");
        var set = new RemasterMovieSet();
        set.Movies.Add(new RemasterMovie { Movie = _id, Video = _video, Sound = MovieSound.Video, Subtitles = srt });
        var path = $"{_dir}/movies.tres";
        if (ResourceSaver.Save(set, path) != Error.Ok)
            return $"cannot write {path}";
        AudioReplacements.Register(path, true, "selftest-movie");
        _seen.Add($"{_id} replaced by {_video} with a subtitle");
        return null;
    }

    private void Cleanup()
    {
        if (!DirAccess.DirExistsAbsolute(_dir))
            return;
        foreach (var f in DirAccess.GetFilesAt(_dir))
            DirAccess.RemoveAbsolute($"{_dir}/{f}");
        DirAccess.RemoveAbsolute(_dir);
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx selftest: {_phase} -> {p} ({_total:0.0} s)");
        _phase = p;
        _phaseTime = 0;
        _acted = false;
    }

    private bool Done(bool pass, string note)
    {
        if (!pass)
            Cleanup();
        Note = note;
        return pass;
    }
}
