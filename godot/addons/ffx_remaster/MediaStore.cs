#if TOOLS
using System.Collections.Generic;
using GdFfx.Remake;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// Where the editor writes R7's work (docs/remaster-editor.md, "Effects, audio and movies"): remade
/// effects as the convention's scenes (<c>res://remake/effect/&lt;id&gt;.tscn</c>: a
/// <see cref="RemadeEffect"/> root with your scene under it), the replaced sounds
/// (<c>res://remaster/audio/audio.tres</c>) and movies (<c>res://remaster/movies/movies.tres</c>);
/// the checks give folders of their own (<c>--remaster-effect-scenes=DIR</c>,
/// <c>--remaster-audio=FILE</c>, <c>--remaster-movies=FILE</c>), never the project's. Every write
/// is text (a scene or resource Godot saves), one undo step in the plugin.
/// </summary>
public static class MediaStore
{
    public static string EffectScenesDir => GameSettings.Arg("remaster-effect-scenes") is { Length: > 0 } d ? d.TrimEnd('/') : "res://remake/effect";

    public static string AudioFile => GameSettings.Arg("remaster-audio") is { Length: > 0 } f ? f : AudioReplacements.ProjectAudio;

    public static string MoviesFile => GameSettings.Arg("remaster-movies") is { Length: > 0 } f ? f : AudioReplacements.ProjectMovies;

    /// <summary>Whether the check writes outside the project.</summary>
    public static bool InProject => !EffectScenesDir.StartsWith('/') && !AudioFile.StartsWith('/') && !MoviesFile.StartsWith('/');

    public static string EffectScenePath(string id) => $"{EffectScenesDir}/{id}.tscn";

    /// <summary>The remade scene of effect <paramref name="id"/>: the editor's file, else a registry entry (the packs'); null for none.</summary>
    public static string? EffectScene(string id)
    {
        var own = EffectScenePath(id);
        if (FileAccess.FileExists(own))
            return own;
        return RemakeRegistry.Find("effect", id);
    }

    /// <summary>A file's text (null: no file).</summary>
    public static string? ReadText(string path) => FileAccess.FileExists(path) ? FileAccess.GetFileAsString(path) : null;

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> (null: removes the file); tells the editor's file system.</summary>
    public static void WriteText(string path, string? text)
    {
        if (text == null)
        {
            if (FileAccess.FileExists(path))
                DirAccess.RemoveAbsolute(GameSettings.Globalize(path));
        }
        else
        {
            DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(path.GetBaseDir()));
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            f?.StoreString(text);
        }
        if (path.StartsWith("res://"))
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);
        RemakeRegistry.Invalidate();
        AudioReplacements.Invalidate();
    }

    /// <summary>The text of a remade-effect scene: a <see cref="RemadeEffect"/> root named for the id with <paramref name="scene"/> instanced under it.</summary>
    public static string? EffectSceneText(string id, string scene)
    {
        if (ResourceLoader.Load(scene) is not PackedScene ps)
            return null;
        var root = new RemadeEffect { Name = SafeName(id), Note = $"Remade effect {id} ({EffectOwners.Describe(id)}): {scene}" };
        var inst = ps.Instantiate(PackedScene.GenEditState.Instance);
        root.AddChild(inst);
        inst.Owner = root;
        var packed = new PackedScene();
        var err = packed.Pack(root);
        root.Free();
        if (err != Error.Ok)
            return null;
        var tmp = $"user://remaster-media-{System.Environment.ProcessId}.tscn";
        if (ResourceSaver.Save(packed, tmp) != Error.Ok)
            return null;
        var text = FileAccess.GetFileAsString(tmp);
        DirAccess.RemoveAbsolute(GameSettings.Globalize(tmp));
        return text;
    }

    private static string SafeName(string id) => id.Replace('-', '_');

    /// <summary>A resource's text as Godot saves it (stable: the same resource gives the same bytes).</summary>
    public static string? ResourceText(Resource r)
    {
        var tmp = $"user://remaster-media-{System.Environment.ProcessId}.tres";
        if (ResourceSaver.Save(r, tmp) != Error.Ok)
            return null;
        var text = FileAccess.GetFileAsString(tmp);
        DirAccess.RemoveAbsolute(GameSettings.Globalize(tmp));
        return text;
    }

    /// <summary>The editor's sound layer as it is on disk (a new one when there is none).</summary>
    public static RemasterSoundSet LoadSounds() =>
        FileAccess.FileExists(AudioFile) && ResourceLoader.Load(AudioFile, "", ResourceLoader.CacheMode.Replace) is RemasterSoundSet s ? s : new RemasterSoundSet();

    /// <summary>The editor's movie layer as it is on disk (a new one when there is none).</summary>
    public static RemasterMovieSet LoadMovies() =>
        FileAccess.FileExists(MoviesFile) && ResourceLoader.Load(MoviesFile, "", ResourceLoader.CacheMode.Replace) is RemasterMovieSet m ? m : new RemasterMovieSet();

    /// <summary>The effects the game recorded (the index), sorted by id.</summary>
    public static List<(string Id, string What, int Frames, string Level)> RecordedEffects()
    {
        var list = new List<(string, string, int, string)>();
        foreach (var (k, v) in EffectRecorder.ReadIndex())
        {
            var d = v.AsGodotDictionary();
            list.Add((k.AsString(), d.TryGetValue("what", out var w) ? w.AsString() : "",
                d.TryGetValue("frames", out var f) ? (int)f.AsDouble() : 0, d.TryGetValue("level", out var l) ? l.AsString() : ""));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }
}
#endif
