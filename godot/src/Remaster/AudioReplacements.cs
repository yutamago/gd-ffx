using System.Collections.Generic;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// Replaced music, sound effects, voice lines and movies, merged over their layers (R7;
/// docs/remaster-editor.md, "Audio", "Movies"), as the textures are: the project's
/// (<c>res://remaster/audio/audio.tres</c>, <c>res://remaster/movies/movies.tres</c>), then each
/// mounted pack's (<c>res://mods/&lt;pack&gt;/remaster/...</c>, load order), then files registered at
/// run time (<see cref="Register"/>; the checks: <c>--audio-file=</c>, <c>--movies-file=</c>). A
/// later layer wins per id; an entry with <c>Classic</c> pins the id to the game's own.
/// </summary>
public static class AudioReplacements
{
    public const string ProjectAudio = "res://remaster/audio/audio.tres";
    public const string ProjectMovies = "res://remaster/movies/movies.tres";

    private static readonly List<(string Layer, string Path)> RegisteredAudio = new();
    private static readonly List<(string Layer, string Path)> RegisteredMovies = new();
    private static Dictionary<(SoundKind, int), (RemasterSound Sound, string Layer)>? _sounds;
    private static Dictionary<string, (RemasterMovie Movie, string Layer)>? _movies;

    /// <summary>Counts the merges (views see a change by it).</summary>
    public static int Version { get; private set; }

    /// <summary>Adds a layer file at run time (a later one wins).</summary>
    public static void Register(string path, bool movies, string layer = "runtime")
    {
        var list = movies ? RegisteredMovies : RegisteredAudio;
        list.RemoveAll(e => e.Layer == layer);
        list.Add((layer, path));
        Invalidate();
    }

    /// <summary>Reads the layers again.</summary>
    public static void Invalidate()
    {
        _sounds = null;
        _movies = null;
        Version++;
    }

    /// <summary>The layer files, in order (whether or not they exist).</summary>
    public static IEnumerable<(string Layer, string Path)> Sources(bool movies)
    {
        yield return ("base", movies ? ProjectMovies : ProjectAudio);
        foreach (var p in ModLoader.Packs)
            yield return (p.Name, $"res://mods/{p.Name}/remaster/{(movies ? "movies/movies" : "audio/audio")}.tres");
        foreach (var r in movies ? RegisteredMovies : RegisteredAudio)
            yield return r;
        foreach (var a in GameSettings.Args(movies ? "movies-file" : "audio-file"))
            yield return ("command line", a);
    }

    private static Resource? LoadLayer(string path)
    {
        if (path.StartsWith("res://") && !ResourceLoader.Exists(path))
            return null;
        if (!path.StartsWith("res://") && !FileAccess.FileExists(path))
            return null;
        return ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace);
    }

    /// <summary>Every replaced sound by (kind, id), with the layer it came from.</summary>
    public static IReadOnlyDictionary<(SoundKind, int), (RemasterSound Sound, string Layer)> Sounds
    {
        get
        {
            if (_sounds != null)
                return _sounds;
            _sounds = new();
            foreach (var (layer, path) in Sources(false))
                if (LoadLayer(path) is RemasterSoundSet set)
                    foreach (var s in set.Sounds)
                        if (s != null)
                        {
                            if (s.Classic)
                                _sounds.Remove(s.Key);
                            else if (s.Stream.Length > 0)
                                _sounds[s.Key] = (s, layer);
                        }
            if (_sounds.Count > 0)
                GD.Print($"ffx: audio: {_sounds.Count} replaced sound(s)");
            return _sounds;
        }
    }

    /// <summary>Every replaced movie by name (lower case, no extension), with its layer.</summary>
    public static IReadOnlyDictionary<string, (RemasterMovie Movie, string Layer)> Movies
    {
        get
        {
            if (_movies != null)
                return _movies;
            _movies = new();
            foreach (var (layer, path) in Sources(true))
                if (LoadLayer(path) is RemasterMovieSet set)
                    foreach (var m in set.Movies)
                        if (m != null && m.Movie.Length > 0)
                        {
                            var key = MovieKey(m.Movie);
                            if (m.Classic)
                                _movies.Remove(key);
                            else if (m.Video.Length > 0)
                                _movies[key] = (m, layer);
                        }
            if (_movies.Count > 0)
                GD.Print($"ffx: movies: {_movies.Count} replaced movie(s)");
            return _movies;
        }
    }

    /// <summary>A movie's key: its file name without folder and extension, lower case (<c>IWK_us</c> -> <c>iwk_us</c>).</summary>
    public static string MovieKey(string nameOrPath)
    {
        var n = nameOrPath.Replace('\\', '/');
        n = n[(n.LastIndexOf('/') + 1)..];
        int dot = n.LastIndexOf('.');
        return (dot > 0 ? n[..dot] : n).ToLowerInvariant();
    }

    /// <summary>
    /// The replacement of the movie the game opened as <paramref name="nameOrPath"/>: an entry for its
    /// file name (<c>LCI_jp</c>), else for the name without its language (<c>LCI</c>: every language's).
    /// </summary>
    public static RemasterMovie? FindMovie(string nameOrPath)
    {
        var key = MovieKey(nameOrPath);
        if (Movies.TryGetValue(key, out var m))
            return m.Movie;
        return Movies.TryGetValue(BaseMovieKey(key), out m) ? m.Movie : null;
    }

    /// <summary>A movie's key without its language suffix (<c>lci_jp</c> -> <c>lci</c>; the same when it has none).</summary>
    public static string BaseMovieKey(string key)
    {
        int u = key.LastIndexOf('_');
        return u > 0 && key.Length - u - 1 == 2 && char.IsLetter(key[^1]) && char.IsLetter(key[^2]) ? key[..u] : key;
    }

    /// <summary>The replacement of (<paramref name="kind"/>, <paramref name="id"/>), null for the game's own.</summary>
    public static RemasterSound? Find(SoundKind kind, int id) => Sounds.TryGetValue((kind, id), out var s) ? s.Sound : null;

    /// <summary>An audio file as a stream: <c>res://</c> through the importer, else loaded from the file (Ogg Vorbis, WAV, MP3).</summary>
    public static AudioStream? LoadStream(string path)
    {
        if (path.Length == 0)
            return null;
        if (path.StartsWith("res://"))
            return ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;
        if (!FileAccess.FileExists(path))
            return null;
        var ext = path.GetExtension().ToLowerInvariant();
        return ext switch
        {
            "ogg" => AudioStreamOggVorbis.LoadFromFile(path),
            "wav" => AudioStreamWav.LoadFromFile(path),
            "mp3" => AudioStreamMP3.LoadFromFile(path),
            _ => null,
        };
    }

    /// <summary>A video file as a stream (Ogg Theora): <c>res://</c> through the importer, else from the file.</summary>
    public static VideoStream? LoadVideo(string path)
    {
        if (path.Length == 0)
            return null;
        if (path.StartsWith("res://") && ResourceLoader.Exists(path))
            return ResourceLoader.Load<VideoStream>(path);
        if (!FileAccess.FileExists(path))
            return null;
        return new VideoStreamTheora { File = path };
    }
}
