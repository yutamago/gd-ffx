using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Remaster;

/// <summary>A profile found in a layer: its name (the file's, without <c>.tres</c>), its path, the layer, its title.</summary>
public sealed record ProfileRef(string Name, string Path, string Layer, string Title);

/// <summary>
/// The material profiles (docs/remaster-materials.md, "Profiles"; R2b phase 3) by name, over
/// layers as the textures are (<see cref="RemasterTextures"/>):
/// <list type="number">
/// <item><c>res://remaster/profiles/</c>, the project's (layer <c>base</c>; the starter profiles are committed there);</item>
/// <item>the editor's own folder when it is elsewhere (<see cref="ProjectDir"/>: the editor check's);</item>
/// <item>each mounted pack's <c>res://mods/&lt;pack&gt;/remaster/profiles/</c>, in load order;</item>
/// <item>folders registered at run time (<see cref="Register"/>).</item>
/// </list>
/// A later layer wins per name. A texture's entry names its profile (<see cref="RemasterTexture.Profile"/>)
/// and keeps the profile's <see cref="Hash"/> at bake time, so a changed profile shows
/// (<see cref="Stale"/>). The settings a profile holds are <see cref="Props"/>: every
/// <see cref="PbrRecipe"/> setting but the imported files, the working size and the version.
/// Main thread (it lists folders and loads resources); the settings helpers are plain C#.
/// </summary>
public static class MaterialProfiles
{
    public const string BaseDir = "res://remaster/profiles";

    /// <summary>The folder the editor writes profiles into (<see cref="BaseDir"/>, or <c>--remaster-profiles=DIR</c>: a layer after the base).</summary>
    public static string ProjectDir { get; set; } = BaseDir;

    private static readonly List<(string Layer, string Dir)> Registered = new();
    private static SortedDictionary<string, ProfileRef>? _all;
    private static readonly Dictionary<string, MaterialProfile?> Loaded = new();

    /// <summary>Bumped on every change (views compare it to refresh).</summary>
    public static int Version { get; private set; }

    /// <summary>The profile folders in layer order (whether or not they exist).</summary>
    public static IEnumerable<(string Layer, string Dir)> Sources()
    {
        yield return ("base", BaseDir);
        if (ProjectDir.TrimEnd('/') != BaseDir)
            yield return ("project", ProjectDir.TrimEnd('/'));
        foreach (var p in ModLoader.Packs)
            yield return (p.Name, $"res://mods/{p.Name}/remaster/profiles");
        foreach (var r in Registered)
            yield return r;
    }

    /// <summary>Adds folder <paramref name="dir"/> as the last layer, named <paramref name="layer"/>.</summary>
    public static void Register(string dir, string layer)
    {
        Registered.RemoveAll(e => e.Layer == layer);
        Registered.Add((layer, dir.TrimEnd('/')));
        Invalidate();
    }

    public static void Unregister(string layer)
    {
        if (Registered.RemoveAll(e => e.Layer == layer) > 0)
            Invalidate();
    }

    /// <summary>Drops the listing and the loaded profiles (after a profile was written, a pack mounted).</summary>
    public static void Invalidate()
    {
        _all = null;
        Loaded.Clear();
        Version++;
    }

    /// <summary>Every profile by name, the winning layer's (main thread).</summary>
    public static IReadOnlyDictionary<string, ProfileRef> All
    {
        get
        {
            if (_all != null)
                return _all;
            var all = new SortedDictionary<string, ProfileRef>(StringComparer.Ordinal);
            foreach (var (layer, dir) in Sources())
            {
                if (!DirAccess.DirExistsAbsolute(dir))
                    continue;
                foreach (var f in DirAccess.GetFilesAt(dir))
                {
                    // an export converts text resources (rock.tres.remap)
                    var file = f.EndsWith(".remap", StringComparison.Ordinal) ? f[..^".remap".Length] : f;
                    if (!file.EndsWith(".tres", StringComparison.Ordinal))
                        continue;
                    var name = file[..^".tres".Length];
                    var path = $"{dir}/{file}";
                    var title = LoadPath(path)?.Title is { Length: > 0 } t ? t : name;
                    all[name] = new ProfileRef(name, path, layer, title);
                }
            }
            return _all = all;
        }
    }

    /// <summary><paramref name="name"/>'s winning layer (null: no such profile).</summary>
    public static ProfileRef? Find(string? name) =>
        name is { Length: > 0 } && All.TryGetValue(name, out var r) ? r : null;

    /// <summary>The profile <paramref name="name"/> as its winning layer has it (null: none).</summary>
    public static MaterialProfile? Load(string? name) => Find(name) is { } r ? LoadPath(r.Path) : null;

    /// <summary>The profile file at <paramref name="path"/> (cached until <see cref="Invalidate"/>).</summary>
    public static MaterialProfile? LoadPath(string path)
    {
        if (Loaded.TryGetValue(path, out var known))
            return known;
        MaterialProfile? p = null;
        if (ResourceLoader.Exists(path) || FileAccess.FileExists(path))
            p = ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) as MaterialProfile;
        if (p == null)
            GD.PrintErr($"ffx: profiles: {path} is not a MaterialProfile");
        return Loaded[path] = p;
    }

    /// <summary>
    /// Whether <paramref name="t"/> was made with a profile that changed since (its hash differs);
    /// false without a profile or when the profile is gone (<see cref="Missing"/>).
    /// </summary>
    public static bool Stale(RemasterTexture? t) =>
        t is { Profile.Length: > 0, Material.Length: > 0 } && Load(t.Profile) is { } p && Hash(p) != t.ProfileHash;

    /// <summary>Whether <paramref name="t"/> names a profile no layer has.</summary>
    public static bool Missing(RemasterTexture? t) => t is { Profile.Length: > 0 } && Find(t.Profile) == null;

    // ---- the settings ------------------------------------------------------------------------------

    /// <summary>Every setting a profile holds, in <see cref="PbrRecipe"/>'s order: all its exported properties but the files, the working size and the version.</summary>
    public static readonly string[] Props = typeof(PbrRecipe).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(p => p.GetCustomAttribute<ExportAttribute>() != null && p.CanWrite
            && p.Name is not ("Version" or "WorkingSide") && !p.Name.EndsWith("File", StringComparison.Ordinal))
        .OrderBy(p => p.MetadataToken).Select(p => p.Name).ToArray();

    private static readonly Dictionary<string, PropertyInfo> PropInfo =
        typeof(PbrRecipe).GetProperties(BindingFlags.Public | BindingFlags.Instance).ToDictionary(p => p.Name);

    private static readonly PbrRecipe Defaults = new();

    /// <summary>
    /// The settings added after phase 3 (declared after <see cref="PbrRecipe.EmissionEnergy"/>):
    /// the hash counts them only when they differ from their defaults, so a texture made before
    /// keeps its profile's hash.
    /// </summary>
    private static readonly HashSet<string> Added = Props.SkipWhile(p => p != nameof(PbrRecipe.EmissionEnergy)).Skip(1).ToHashSet();

    /// <summary>A setting's value as invariant text (the hash's; any thread).</summary>
    public static string Text(PbrRecipe r, string prop) => Text(PropInfo[prop].GetValue(r));

    private static string Text(object? v) => v switch
    {
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        Color c => string.Join(",", new[] { c.R, c.G, c.B, c.A }.Select(x => x.ToString("R", CultureInfo.InvariantCulture))),
        Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        null => "",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>
    /// The content hash of a profile's settings (<see cref="Props"/>, by value: the title, the
    /// note and how the file is written do not count): 12 hex digits (any thread).
    /// </summary>
    public static string Hash(PbrRecipe r)
    {
        var sb = new StringBuilder();
        foreach (var p in Props)
        {
            // R2b phase 4's settings count only when set: a profile without them has the hash it had
            if (Added.Contains(p) && Text(r, p) == Text(Defaults, p))
                continue;
            sb.Append(p).Append('=').Append(Text(r, p)).Append('\n');
        }
        var h = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(h, 0, 6).ToLowerInvariant();
    }

    /// <summary>Copies <paramref name="props"/> (default: <see cref="Props"/>) from <paramref name="from"/> to <paramref name="to"/> (any thread).</summary>
    public static void Copy(PbrRecipe from, PbrRecipe to, IEnumerable<string>? props = null)
    {
        foreach (var p in props ?? Props)
            PropInfo[p].SetValue(to, PropInfo[p].GetValue(from));
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> have the same <paramref name="prop"/>.</summary>
    public static bool Same(PbrRecipe a, PbrRecipe b, string prop) => Text(a, prop) == Text(b, prop);

    /// <summary>
    /// A profile of <paramref name="settings"/>: its <see cref="Props"/>, a step on Import made
    /// Generate (an imported file is a texture's own: pinned to it, not the profile's).
    /// </summary>
    public static MaterialProfile From(PbrRecipe settings, string title, string note = "")
    {
        var p = new MaterialProfile { Title = title, Note = note, Version = 3 };
        Copy(settings, p);
        foreach (var s in MaterialPipeline.Steps)
            if (MaterialPipeline.ModeOf(p, s) == StepMode.Import)
                PropInfo[MaterialPipeline.ModeProps(s).Mode].SetValue(p, StepMode.Generate);
        return p;
    }

    /// <summary>
    /// The settings a texture is made with: <paramref name="profile"/>'s, then its
    /// <paramref name="exceptions"/> (setting → value) over them. Main thread (the exceptions are
    /// Godot variants).
    /// </summary>
    public static PbrRecipe Effective(PbrRecipe profile, Godot.Collections.Dictionary? exceptions)
    {
        var r = new PbrRecipe { Version = 3 };
        Copy(profile, r);
        if (exceptions != null)
            foreach (var (k, v) in exceptions)
                r.Set(k.AsString(), v);
        return r;
    }

    /// <summary>
    /// What a texture made with <paramref name="settings"/> keeps as its exceptions to
    /// <paramref name="profile"/>: every <see cref="Props"/> setting that differs (setting →
    /// value), and the file of a step on Import (a profile has none). Main thread.
    /// </summary>
    public static Godot.Collections.Dictionary Differences(PbrRecipe settings, PbrRecipe profile)
    {
        var d = new Godot.Collections.Dictionary();
        foreach (var p in Props)
            if (!Same(settings, profile, p))
                d[p] = settings.Get(p);
        foreach (var s in MaterialPipeline.Steps)
            if (MaterialPipeline.ModeOf(settings, s) == StepMode.Import)
                d[MaterialPipeline.ModeProps(s).File] = MaterialPipeline.FileOf(settings, s);
        // the detail's own files (a texture's, as the imports)
        foreach (var f in new[] { nameof(PbrRecipe.DetailAlbedoFile), nameof(PbrRecipe.DetailNormalFile) })
            if (settings.DetailOn && settings.DetailFrom == DetailFrom.Files && (string)PropInfo[f].GetValue(settings)! is { Length: > 0 } file)
                d[f] = file;
        var sorted = new Godot.Collections.Dictionary();
        foreach (var k in d.Keys.Select(k => k.AsString()).OrderBy(k => k, StringComparer.Ordinal))
            sorted[k] = d[k];
        return sorted;
    }

    /// <summary>A file name for a profile titled <paramref name="title"/>: lower case letters, digits, '-' and '_' (<c>Stone and concrete</c> → <c>stone-and-concrete</c>).</summary>
    public static string FileName(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title.Trim().ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-');
        var s = sb.ToString();
        while (s.Contains("--", StringComparison.Ordinal))
            s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Trim('-');
    }

    /// <summary>
    /// A profile as Godot's text resource: the title and the note, then every setting that
    /// differs from <see cref="PbrRecipe"/>'s defaults, in <see cref="Props"/>' order, so the same
    /// settings give the same bytes and a diff shows what a change did.
    /// </summary>
    public static string ToText(MaterialProfile p)
    {
        var sb = new StringBuilder();
        sb.Append("[gd_resource type=\"Resource\" script_class=\"MaterialProfile\" load_steps=2 format=3]\n\n");
        sb.Append("[ext_resource type=\"Script\" path=\"res://src/Remaster/MaterialProfile.cs\" id=\"1\"]\n\n");
        sb.Append("[resource]\nscript = ExtResource(\"1\")\n");
        sb.Append("Title = ").Append(GD.VarToStr(p.Title)).Append('\n');
        if (p.Note.Length > 0)
            sb.Append("Note = ").Append(GD.VarToStr(p.Note)).Append('\n');
        sb.Append("Version = 3\n");
        foreach (var name in Props)
        {
            if (Text(p, name) == Text(Defaults, name))
                continue;
            var v = PropInfo[name].GetValue(p);
            if (v is Color col)
            {
                // the components' shortest decimals, as for floats
                sb.Append(name).Append(" = Color(").Append(string.Join(", ", new[] { col.R, col.G, col.B, col.A }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)))).Append(")\n");
                continue;
            }
            Variant value = v switch
            {
                // the float's shortest decimal (1.65, not 1.64999997615814): it reads back as the same float
                float f => double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                int i => i,
                bool b => b,
                string s => s,
                Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"MaterialProfile: no text for {name}"),
            };
            sb.Append(name).Append(" = ").Append(GD.VarToStr(value)).Append('\n');
        }
        return sb.ToString();
    }
}
