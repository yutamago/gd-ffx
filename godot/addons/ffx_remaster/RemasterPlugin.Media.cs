#if TOOLS
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// R7's tools (docs/remaster-editor.md, "Effects, audio and movies"): the Effects, Audio and Movies
/// tabs of the <see cref="MediaDock"/>; an effect opened as an edit scene (its classic recording
/// replayed beside the remake); a remade scene assigned to an effect id, a file to a music track,
/// sound effect or voice line, a video to a movie. Each change writes its file at once and is one
/// step of the editor's undo history (the file's text before and after).
/// </summary>
public partial class RemasterPlugin
{
    private MediaDock? _media;
    private MediaEditorTest? _mediaTest;

    public MediaDock? MediaDock => _media;

    /// <summary>The effect edit scene being edited (null: another scene, or none).</summary>
    public EffectEditRoot? CurrentEffect => EditorInterface.Singleton.GetEditedSceneRoot() as EffectEditRoot;

    private void EnterMedia()
    {
        _media = new MediaDock(this);
        AddDock(_media);
        if (GameSettings.Flag("remaster-editor-media"))
            _mediaTest = new MediaEditorTest(this);
    }

    private void ExitMedia()
    {
        if (_media == null)
            return;
        RemoveDock(_media);
        _media.QueueFree();
        _media = null;
    }

    private void StepMedia(double delta) => _mediaTest?.Step(delta);

    /// <summary>Opens effect <paramref name="id"/>'s edit scene (made when missing); an error or null.</summary>
    public string? OpenEffect(string id)
    {
        if (id.Length == 0)
            return "no effect";
        var path = EffectEditRoot.EditScenePath(id);
        if (!ResourceLoader.Exists(path))
        {
            DirAccess.MakeDirRecursiveAbsolute(EffectEditRoot.EditDir);
            var root = new EffectEditRoot { Name = "Effect_" + id.Replace('-', '_'), Effect = id };
            var packed = new PackedScene();
            var err = packed.Pack(root);
            root.Free();
            if (err != Error.Ok || ResourceSaver.Save(packed, path) != Error.Ok)
                return $"cannot write {path}";
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);
        }
        EditorInterface.Singleton.OpenSceneFromPath(path);
        return null;
    }

    /// <summary>The effect edit scene built (the dock shows its frames).</summary>
    public void EffectBuilt(EffectEditRoot root) => _media?.EffectChanged();

    /// <summary>Assigns <paramref name="scene"/> as effect <paramref name="id"/>'s remake (the convention's scene written); one undo step; an error or null.</summary>
    public string? AssignEffect(string id, string scene)
    {
        var text = MediaStore.EffectSceneText(id, scene);
        if (text == null)
            return $"{scene} is not a scene";
        return FileStep($"Remake effect {id}", MediaStore.EffectScenePath(id), text);
    }

    /// <summary>Removes effect <paramref name="id"/>'s remade scene (the editor's file); one undo step.</summary>
    public string? ClearEffect(string id) => FileStep($"Classic effect {id}", MediaStore.EffectScenePath(id), null);

    /// <summary>Replaces sound (<paramref name="kind"/>, <paramref name="id"/>) by <paramref name="file"/> (loop points in seconds); one undo step.</summary>
    public string? AssignSound(SoundKind kind, int id, string file, bool loop, double loopStart = 0, double loopEnd = 0, float volumeDb = 0, string note = "")
    {
        if (AudioReplacements.LoadStream(file) == null)
            return $"{file} is not an audio file Godot can play (Ogg Vorbis, WAV, MP3)";
        var set = MediaStore.LoadSounds();
        var keep = set.Sounds.Where(s => s != null && s.Key != (kind, id)).ToList();
        keep.Add(new RemasterSound { Kind = kind, Id = id, Stream = file, Loop = loop, LoopStart = loopStart, LoopEnd = loopEnd, VolumeDb = volumeDb, Note = note });
        keep.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : a.Id.CompareTo(b.Id));
        set.Sounds = new Godot.Collections.Array<RemasterSound>(keep);
        return FileStep($"Replace {kind} {id:x}", MediaStore.AudioFile, MediaStore.ResourceText(set));
    }

    /// <summary>Gives sound (<paramref name="kind"/>, <paramref name="id"/>) back to the game; one undo step.</summary>
    public string? ClearSound(SoundKind kind, int id)
    {
        var set = MediaStore.LoadSounds();
        var keep = set.Sounds.Where(s => s != null && s.Key != (kind, id)).ToList();
        if (keep.Count == set.Sounds.Count)
            return null;
        set.Sounds = new Godot.Collections.Array<RemasterSound>(keep);
        return FileStep($"Classic {kind} {id:x}", MediaStore.AudioFile, MediaStore.ResourceText(set));
    }

    /// <summary>Replaces movie <paramref name="movie"/> (its name, <c>LCI</c> for every language or <c>LCI_us</c>) by <paramref name="video"/>; one undo step.</summary>
    public string? AssignMovie(string movie, string video, MovieSound sound = MovieSound.Video, string soundFile = "", string subtitles = "", string note = "")
    {
        if (AudioReplacements.LoadVideo(video) == null)
            return $"{video} is not an Ogg Theora video";
        var set = MediaStore.LoadMovies();
        var key = AudioReplacements.MovieKey(movie);
        var keep = set.Movies.Where(m => m != null && AudioReplacements.MovieKey(m.Movie) != key).ToList();
        keep.Add(new RemasterMovie { Movie = movie, Video = video, Sound = sound, SoundFile = soundFile, Subtitles = subtitles, Note = note });
        keep.Sort((a, b) => string.CompareOrdinal(a.Movie.ToLowerInvariant(), b.Movie.ToLowerInvariant()));
        set.Movies = new Godot.Collections.Array<RemasterMovie>(keep);
        return FileStep($"Replace movie {movie}", MediaStore.MoviesFile, MediaStore.ResourceText(set));
    }

    /// <summary>Gives movie <paramref name="movie"/> back to the game; one undo step.</summary>
    public string? ClearMovie(string movie)
    {
        var set = MediaStore.LoadMovies();
        var key = AudioReplacements.MovieKey(movie);
        var keep = set.Movies.Where(m => m != null && AudioReplacements.MovieKey(m.Movie) != key).ToList();
        if (keep.Count == set.Movies.Count)
            return null;
        set.Movies = new Godot.Collections.Array<RemasterMovie>(keep);
        return FileStep($"Classic movie {movie}", MediaStore.MoviesFile, MediaStore.ResourceText(set));
    }

    /// <summary>The placeholder for "no file" in the undo history's arguments.</summary>
    private const string NoFile = "\u0001none";

    /// <summary>Writes <paramref name="after"/> to <paramref name="path"/> (null: removes it) as one undo step (its text before restored on undo).</summary>
    private string? FileStep(string name, string path, string? after)
    {
        var before = MediaStore.ReadText(path);
        if (before == after)
            return null;
        var ur = GetUndoRedo();
        ur.CreateAction(name, UndoRedo.MergeMode.Disable, this);
        ur.AddDoMethod(this, "SetFileText", path, after ?? NoFile);
        ur.AddUndoMethod(this, "SetFileText", path, before ?? NoFile);
        ur.CommitAction();
        return null;
    }

    /// <summary>The undo history's step: a file's text (or none), the views refreshed.</summary>
    public void SetFileText(string path, string text)
    {
        MediaStore.WriteText(path, text == NoFile ? null : text);
        if (path == UiArtFile)
        {
            UiArt.Invalidate();
            _uiArt?.Refresh();
            return;
        }
        CurrentEffect?.BuildRemake();
        _media?.Refresh();
    }
}
#endif
