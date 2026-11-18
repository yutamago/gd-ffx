#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// R7's editor check (docs/remaster-editor.md, "Checks"): run in the editor with
/// <c>-- --remaster-editor-media --remaster-effects=DIR</c> (the game's recordings, from its
/// <c>--selftest-effects</c>) <c>--remaster-effect-scenes=DIR --remaster-audio=FILE
/// --remaster-movies=FILE --media-video=FILE.ogv</c> (everything written there, not the project).
/// Through the code the Media dock uses: <b>Effects</b>: the recorded effects listed, Besaid's save
/// sphere glow (<c>bsil00-fp15</c>) opened (its recording replayed with the game's textures,
/// scrubbed), the original example assigned as its remake, undone (classic), redone, the scene
/// opened again with the remake; <b>Audio</b>: the music tracks named by the game, the battle
/// music's and the sea effect's classic sounds decoded, generated stand-ins assigned (the music
/// with loop points), undone, redone, read back; <b>Movies</b>: the game's movies listed by name and
/// language, the test video assigned to <c>LCI</c> with a subtitle file, undone, redone, read back,
/// previewed. Exits the editor: 0 on PASS, 1 on FAIL. The game then runs with what it saved
/// (tools/remaster-editor-check.sh, step 6).
/// </summary>
public sealed class MediaEditorTest
{
    public const string Effect = "bsil00-fp15";
    public const string Example = "res://remake/examples/save_sphere_sparkle.tscn";
    public const int Music = 0x10, Sea = 0xc355;

    private enum Phase { Archive, Effects, Open, Scrub, Assign, Undo, Redo, Reopen, Reopened, Audio, Movies, Preview, Done }

    private readonly RemasterPlugin _plugin;
    private readonly double _timeout = GameSettings.Number("editor-test-timeout", 300);
    private readonly List<string> _seen = new();
    private readonly string? _video = GameSettings.Arg("media-video");
    private Phase _phase = Phase.Archive;
    private double _t, _phaseT;
    private bool _finished, _inStep, _acted;

    public MediaEditorTest(RemasterPlugin plugin)
    {
        _plugin = plugin;
        GD.Print($"ffx editortest: media (effects {MediaStore.EffectScenesDir}, audio {MediaStore.AudioFile}, movies {MediaStore.MoviesFile})");
        if (MediaStore.InProject)
            Finish(false, "--remaster-effect-scenes=DIR, --remaster-audio=FILE and --remaster-movies=FILE are required (the check must not write the project's)");
    }

    private UndoRedo Global => _plugin.GetUndoRedo().GetHistoryUndoRedo((int)EditorUndoRedoManager.SpecialHistory.GlobalHistory);

    public void Step(double delta)
    {
        if (_finished || _inStep)
            return;
        _inStep = true;
        try
        {
            _t += delta;
            _phaseT += delta;
            if (_t > _timeout)
                Finish(false, $"timed out in {_phase}");
            else if (Run() is { } fail)
                Finish(false, $"{_phase}: {fail}");
        }
        catch (System.Exception e)
        {
            Finish(false, $"{_phase}: {e}");
        }
        finally
        {
            _inStep = false;
        }
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx editortest: {_phase} -> {p} ({_t:F1} s)");
        _phase = p;
        _phaseT = 0;
        _acted = false;
    }

    private string? Run()
    {
        var dock = _plugin.MediaDock!;
        var root = _plugin.CurrentEffect;
        var path = MediaStore.EffectScenePath(Effect);
        switch (_phase)
        {
            case Phase.Archive when _plugin.Session.IsOpen && _phaseT > 0.5:
                Next(Phase.Effects);
                break;
            case Phase.Effects:
            {
                dock.Refresh();
                var ids = dock.ListedEffects();
                if (!ids.Contains(Effect))
                    return $"{Effect} not listed ({string.Join(", ", ids.Take(20))}; recordings in {EffectRecorder.Dir})";
                _seen.Add($"{ids.Count} recorded effects listed");
                dock.SelectEffect(Effect);
                if (_plugin.OpenEffect(Effect) is { } openErr)
                    return openErr;
                Next(Phase.Open);
                break;
            }
            case Phase.Open when root is { Effect: Effect } r && r.Recording != null && _phaseT > 0.5:
                if (r.FrameDraws == 0 || r.TexturesFound == 0)
                    return $"the replay drew {r.FrameDraws} draws, {r.TexturesFound} textures";
                if (r.Remake != null)
                    return "a remake before one was assigned";
                _seen.Add($"{Effect} opened: {r.Recording.Frames.Count} frames, {r.FrameDraws} draws, {r.TexturesFound} textures from the archive");
                Next(Phase.Scrub);
                break;
            case Phase.Scrub:
            {
                var r = root!;
                r.Playing = false;
                int target = System.Math.Min(60, r.Recording!.Frames.Count - 1);
                r.ShowFrame(target);
                if (r.Frame != target || r.FrameDraws == 0)
                    return $"scrubbed to {r.Frame} ({r.FrameDraws} draws), not {target}";
                _seen.Add($"scrubbed to frame {target}");
                Next(Phase.Assign);
                break;
            }
            case Phase.Assign when !_acted:
                _acted = true;
                if (_plugin.AssignEffect(Effect, Example) is { } assignErr)
                    return assignErr;
                break;
            case Phase.Assign when _phaseT > 0.3:
                if (!FileAccess.FileExists(path))
                    return $"{path} not written";
                if (root?.Remake == null || root.Remake.FindChildren("*", "GPUParticles3D", true, false).Count == 0)
                    return "the remake is not shown beside the classic effect";
                _seen.Add($"remade by the example ({path})");
                Next(Phase.Undo);
                break;
            case Phase.Undo when !_acted:
                _acted = true;
                Global.Undo();
                break;
            case Phase.Undo when _phaseT > 0.3:
                if (FileAccess.FileExists(path) || root?.Remake != null)
                    return "undo left the remake";
                Next(Phase.Redo);
                break;
            case Phase.Redo when !_acted:
                _acted = true;
                Global.Redo();
                break;
            case Phase.Redo when _phaseT > 0.3:
                if (!FileAccess.FileExists(path) || root?.Remake == null)
                    return "redo did not bring the remake back";
                _seen.Add("undo: classic; redo: remade");
                Next(Phase.Reopen);
                break;
            case Phase.Reopen when !_acted:
                _acted = true;
                EditorInterface.Singleton.CloseScene();
                break;
            case Phase.Reopen when _phaseT > 0.5:
                if (_plugin.OpenEffect(Effect) is { } err2)
                    return err2;
                Next(Phase.Reopened);
                break;
            case Phase.Reopened when root is { Effect: Effect } r2 && r2.Recording != null && _phaseT > 0.5:
                if (r2.Remake == null)
                    return "opened again without its remake";
                dock.Refresh();
                _seen.Add("opened again with its remake");
                Next(Phase.Audio);
                break;
            case Phase.Audio:
                if (Audio(dock) is { } a)
                    return a;
                Next(Phase.Movies);
                break;
            case Phase.Movies:
                if (Movies(dock) is { } m)
                    return m;
                Next(Phase.Preview);
                break;
            case Phase.Preview when !_acted:
                _acted = true;
                if (dock.PreviewMovie("LCI") is { } p)
                    return p;
                break;
            case Phase.Preview when _phaseT > 0.5:
                if (!dock.MoviePreview.IsPlaying())
                    return "the movie preview does not play";
                dock.MoviePreview.Stop();
                _seen.Add("the replacement previewed");
                Next(Phase.Done);
                break;
            case Phase.Done:
                Finish(true, string.Join("; ", _seen));
                break;
        }
        return null;
    }

    private string? Audio(MediaDock dock)
    {
        var music = dock.Classic(SoundKind.Music);
        if (music.Count < 80)
            return $"{music.Count} music tracks";
        var battle = music.FirstOrDefault(m => m.Id == Music);
        if (battle.Name == null || battle.Name.Length == 0)
            return $"music {Music:x} has no name";
        var sfx = dock.Classic(SoundKind.Sfx);
        if (!sfx.Any(s => s.Id == Sea))
            return $"the sea effect {Sea:x} is not among the {sfx.Count} sound effects";
        var voice = dock.Classic(SoundKind.Voice);
        var classic = dock.ClassicStream(SoundKind.Music, Music);
        var sea = dock.ClassicStream(SoundKind.Sfx, Sea);
        if (classic == null || classic.GetLength() < 5 || sea == null || sea.GetLength() <= 0)
            return $"the classic sounds were not decoded ({classic?.GetLength():F1} s, {sea?.GetLength():F1} s)";
        _seen.Add($"{music.Count} music tracks named by the game (music {Music:x}: {battle.Name}, {classic.GetLength():F0} s decoded{(classic.LoopMode != AudioStreamWav.LoopModeEnum.Disabled ? ", looping" : "")}), "
            + $"{sfx.Count} sound effects ({Sea:x}: {sea.GetLength():F1} s), {voice.Count} voice lines");
        dock.Kind = SoundKind.Music;
        dock.SetSoundSearch(Music.ToString("x"));
        if (!dock.ListedSounds().Contains(Music))
            return "searching the music id does not list it";
        // the stand-ins, generated beside the audio file
        var dir = MediaStore.AudioFile.GetBaseDir();
        DirAccess.MakeDirRecursiveAbsolute(dir);
        if (AudioTest.Tone($"{dir}/battle.wav", 4.0, [196, 294], 0.25f, true) is { } e1 || AudioTest.Tone($"{dir}/sea.wav", 2.0, [140, 147], 0.3f, true) is { } e2)
            return "cannot write the stand-ins";
        if (_plugin.AssignSound(SoundKind.Music, Music, $"{dir}/battle.wav", true, 1.0, 3.0) is { } a1)
            return a1;
        if (_plugin.AssignSound(SoundKind.Sfx, Sea, $"{dir}/sea.wav", true) is { } a2)
            return a2;
        if (Read(SoundKind.Music, Music) is not { Loop: true, LoopStart: 1.0, LoopEnd: 3.0 })
            return "the music's replacement was not saved";
        Global.Undo();
        if (Read(SoundKind.Sfx, Sea) != null || Read(SoundKind.Music, Music) == null)
            return "undo did not take back the sea's replacement alone";
        Global.Redo();
        if (Read(SoundKind.Sfx, Sea) == null)
            return "redo did not bring the sea's replacement back";
        dock.SelectSound(Music);
        if (dock.ClassicStream(SoundKind.Music, Music) == null)
            return "no classic stream to play";
        _seen.Add($"music {Music:x} and the sea {Sea:x} replaced by stand-ins (loop 1..3 s), undone, redone, read back from {MediaStore.AudioFile}");
        return null;
    }

    private static RemasterSound? Read(SoundKind kind, int id)
    {
        if (!FileAccess.FileExists(MediaStore.AudioFile))
            return null;
        var set = ResourceLoader.Load(MediaStore.AudioFile, "", ResourceLoader.CacheMode.Replace) as RemasterSoundSet;
        return set?.Sounds.FirstOrDefault(s => s != null && s.Key == (kind, id));
    }

    private string? Movies(MediaDock dock)
    {
        var movies = dock.ClassicMovies();
        if (!movies.TryGetValue("LCI", out var langs) || langs.Count == 0)
            return $"the movie LCI is not listed ({movies.Count} movies)";
        if (_video == null || !FileAccess.FileExists(_video))
            return "--media-video=FILE.ogv is required (tools/make-test-movie.sh)";
        dock.Refresh();
        if (!dock.ListedMovies().Contains("LCI"))
            return "the dock does not list LCI";
        var dir = MediaStore.MoviesFile.GetBaseDir();
        DirAccess.MakeDirRecursiveAbsolute(dir);
        var srt = $"{dir}/lci.srt";
        using (var f = FileAccess.Open(srt, FileAccess.ModeFlags.Write))
            f.StoreString("1\n00:00:00,200 --> 00:00:30,000\nR7 test subtitle\n");
        if (_plugin.AssignMovie("LCI", _video, MovieSound.Video, "", srt) is { } err)
            return err;
        if (ReadMovie() is not { } m || m.Video != _video || m.Subtitles != srt)
            return "the movie's replacement was not saved";
        Global.Undo();
        if (ReadMovie() != null)
            return "undo left the movie's replacement";
        Global.Redo();
        if (ReadMovie() == null)
            return "redo did not bring the movie's replacement back";
        _seen.Add($"{movies.Count} movies listed (LCI in {string.Join(", ", langs)}); LCI replaced by the test video with a subtitle, undone, redone, read back from {MediaStore.MoviesFile}");
        return null;
    }

    private static RemasterMovie? ReadMovie()
    {
        if (!FileAccess.FileExists(MediaStore.MoviesFile))
            return null;
        var set = ResourceLoader.Load(MediaStore.MoviesFile, "", ResourceLoader.CacheMode.Replace) as RemasterMovieSet;
        return set?.Movies.FirstOrDefault(m => m != null && AudioReplacements.MovieKey(m.Movie) == "lci");
    }

    private void Finish(bool pass, string note)
    {
        if (_finished)
            return;
        _finished = true;
        GD.Print($"ffx editortest: {(pass ? "PASS" : "FAIL")} {note}{(pass ? "" : $" [{string.Join("; ", _seen)}]")}");
        var scene = EffectEditRoot.EditScenePath(Effect);
        if (FileAccess.FileExists(scene))
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(scene));
        var dir = ProjectSettings.GlobalizePath(EffectEditRoot.EditDir);
        if (DirAccess.DirExistsAbsolute(dir) && DirAccess.GetFilesAt(dir).Length == 0)
            DirAccess.RemoveAbsolute(dir);
        var tree = (Engine.GetMainLoop() as SceneTree)!;
        tree.CreateTimer(0.5).Timeout += () => tree.Quit(pass ? 0 : 1);
    }
}
#endif
