#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The project's material profiles as the editor writes them (docs/remaster-materials.md,
/// "Profiles"): <c>&lt;dir&gt;/&lt;name&gt;.tres</c>, the text of
/// <see cref="MaterialProfiles.ToText"/> (only the settings that differ from the defaults, in a
/// stable order). The folder is <c>res://remaster/profiles</c> (the committed starter profiles
/// are there), or <c>--remaster-profiles=DIR</c> (the editor check's: then a layer after the
/// project's, so the starter profiles are still listed and never written). Every change goes
/// through <see cref="RemasterPlugin"/> as one undo step that swaps the file's text
/// (<see cref="Write"/>), so undo and redo give the same bytes back.
/// </summary>
public static class ProfileStore
{
    public static string Dir => GameSettings.Arg("remaster-profiles") is { Length: > 0 } d ? d.TrimEnd('/') : MaterialProfiles.BaseDir;

    /// <summary>Points the profile layers at this store (the plugin, at start).</summary>
    public static void Attach()
    {
        MaterialProfiles.ProjectDir = Dir;
        MaterialProfiles.Invalidate();
    }

    public static string PathOf(string name) => $"{Dir}/{name}.tres";

    /// <summary>The text of the profile file at <paramref name="path"/> (empty: none).</summary>
    public static string Text(string path) => FileAccess.FileExists(path) ? FileAccess.GetFileAsString(path) : "";

    /// <summary>Whether <paramref name="name"/>'s winning layer is the store's own file (so Save writes what is used).</summary>
    public static bool Own(string name) => MaterialProfiles.Find(name)?.Path == PathOf(name);

    /// <summary>Writes <paramref name="text"/> as the file at <paramref name="path"/> (empty: removes it); the layers read again.</summary>
    public static void Write(string path, string text)
    {
        if (text.Length == 0)
        {
            if (FileAccess.FileExists(path))
                DirAccess.RemoveAbsolute(path);
        }
        else
        {
            DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            f?.StoreString(text);
        }
        MaterialProfiles.Invalidate();
        if (path.StartsWith("res://", System.StringComparison.Ordinal))
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);
    }

    /// <summary>The textures of the project's index made with profile <paramref name="name"/>.</summary>
    public static List<RemasterTexture> Users(string name) =>
        TextureStore.Set.Textures.Where(t => t != null && (t.Profile == name
            || t.Regions.Any(v => v.VariantType == Variant.Type.Dictionary && MaterialRegionStore.ProfileOf(v.AsGodotDictionary()) == name))).ToList();

    /// <summary>The project's textures whose profile changed since they were made, by profile name.</summary>
    public static SortedDictionary<string, List<string>> Stale()
    {
        var r = new SortedDictionary<string, List<string>>(System.StringComparer.Ordinal);
        foreach (var t in TextureStore.Set.Textures)
            if (t != null)
                // the texture's own profile and its regions' (R2b phase 6)
                foreach (var name in MaterialProfiles.StaleNames(t))
                {
                    if (!r.TryGetValue(name, out var l))
                        r[name] = l = new List<string>();
                    l.Add(t.Id);
                }
        return r;
    }

    /// <summary>"Rock changed: 37 textures; Wood changed: 2 textures" (empty: none).</summary>
    public static string StaleText(SortedDictionary<string, List<string>> stale) =>
        string.Join("; ", stale.Select(kv => $"{MaterialProfiles.Find(kv.Key)?.Title ?? kv.Key} changed: {kv.Value.Count} texture{(kv.Value.Count == 1 ? "" : "s")}"));
}
#endif
