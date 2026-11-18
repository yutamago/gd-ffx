#if TOOLS
using System.Collections.Generic;
using GdFfx.Characters;
using GdFfx.Remake;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The project's remade characters as the editor edits them (docs/remaster-editor.md,
/// "Characters"): per character id the scene <c>&lt;dir&gt;/&lt;id&gt;.tscn</c> and its rig
/// <c>&lt;dir&gt;/&lt;id&gt;.rig.tres</c> (<see cref="CharacterScene"/>), <c>dir</c> the remake
/// convention's <c>res://remake/chr</c> (the checks: <c>--remaster-characters=DIR</c>). The rigs
/// are held in memory while edited and written by <see cref="Save"/>.
/// </summary>
public static class CharacterStore
{
    public static string Dir => GameSettings.Arg("remaster-characters") is { Length: > 0 } d ? d : CharacterScene.ProjectDir;

    private static readonly Dictionary<string, CharacterRig> Rigs = new();

    /// <summary>The remade scene of <paramref name="id"/>: the store's, else (in the project) the registry's (packs' and convention's); null none.</summary>
    public static string? Scene(string id)
    {
        var path = CharacterScene.ScenePath(Dir, id);
        if (FileAccess.FileExists(path))
            return path;
        return Dir == CharacterScene.ProjectDir ? RemakeRegistry.Find("chr", id) : null;
    }

    /// <summary>The rig being edited (loaded from its file on first use; null without one).</summary>
    public static CharacterRig? Rig(string id)
    {
        if (Rigs.TryGetValue(id, out var r))
            return r;
        var path = CharacterScene.RigPath(Dir, id);
        if (!FileAccess.FileExists(path) || ResourceLoader.Load(path, null, ResourceLoader.CacheMode.Replace) is not CharacterRig loaded)
            return null;
        Rigs[id] = loaded;
        return loaded;
    }

    /// <summary>Takes <paramref name="rig"/> as <paramref name="id"/>'s rig (after an assignment).</summary>
    public static void Put(string id, CharacterRig rig) => Rigs[id] = rig;

    /// <summary>Forgets the rig in memory (read again from its file).</summary>
    public static void Forget(string id) => Rigs.Remove(id);

    /// <summary>Writes <paramref name="id"/>'s rig; an error or null.</summary>
    public static string? Save(string id)
    {
        if (!Rigs.TryGetValue(id, out var rig))
            return null;
        var path = CharacterScene.RigPath(Dir, id);
        rig.Character = id;
        var e = ResourceSaver.Save(rig, path);
        if (e != Error.Ok)
            return $"save {path}: {e}";
        rig.TakeOverPath(path);
        return null;
    }

    /// <summary>The files of a character (scene and rig) as they are (null: no file), for the undo history.</summary>
    public static (byte[]? Scene, byte[]? Rig) Files(string id) =>
        (Read(CharacterScene.ScenePath(Dir, id)), Read(CharacterScene.RigPath(Dir, id)));

    public static void Restore(string id, byte[]? scene, byte[]? rig)
    {
        Write(CharacterScene.ScenePath(Dir, id), scene);
        Write(CharacterScene.RigPath(Dir, id), rig);
        Forget(id);
    }

    private static byte[]? Read(string path) => FileAccess.FileExists(path) ? FileAccess.GetFileAsBytes(path) : null;

    private static void Write(string path, byte[]? bytes)
    {
        if (bytes == null)
        {
            if (FileAccess.FileExists(path))
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
            return;
        }
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        f?.StoreBuffer(bytes);
    }
}
#endif
