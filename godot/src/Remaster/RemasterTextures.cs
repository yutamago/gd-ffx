using System.Collections.Generic;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Remaster;

/// <summary>A texture's remaster merged over every layer: the replacement and the material each the last layer's that sets one.</summary>
public sealed record TextureEdit(string Id, string Replacement, TexturePreset Preset, string Material, PbrRecipe? Recipe, string Layer)
{
    public bool Replaced => Replacement.Length > 0;
    public bool HasMaterial => Material.Length > 0;
}

/// <summary>
/// The remaster's textures and materials by texture ID (docs/remaster-editor.md, "Textures"),
/// project-wide: a texture is shared across maps. Layers, in order:
/// <list type="number">
/// <item><c>res://remaster/textures/textures.tres</c>, the project's (layer <c>base</c>);</item>
/// <item>each mounted pack's <c>res://mods/&lt;pack&gt;/remaster/textures/textures.tres</c>, in load order;</item>
/// <item>sets registered at run time (<see cref="Register(string, string)"/>; the editor's store, the checks).</item>
/// </list>
/// A later layer wins per texture ID and per field (the replacement, the material); an entry with
/// <see cref="TextureMode.Classic"/> puts the classic texture back. <c>--no-overrides</c> or
/// <c>--no-textures</c> turns them all off.
/// <para>Merged on the main thread (<see cref="Current"/>: it loads the index files); read on any
/// thread (the classic content loads on workers): the merge is immutable and the images load
/// on first use, cached (<see cref="Texture"/>).</para>
/// </summary>
public static class RemasterTextures
{
    public const string BaseDir = "res://remaster/textures";
    public const string FileName = "textures.tres";

    /// <summary>Metadata on a classic <see cref="ShaderMaterial"/>: its texture parameters and their texture IDs (parallel string arrays).</summary>
    public const string ParamsMeta = "ffx_tex_params", IdsMeta = "ffx_tex_ids";

    /// <summary>Metadata prefix on a classic material: the classic texture of a parameter (<c>ffx_tex_classic_tex</c>).</summary>
    public const string ClassicMeta = "ffx_tex_classic_";

    private static readonly List<(string Layer, string Path, RemasterTextureSet? Set)> Registered = new();
    private static readonly Dictionary<string, Texture2D?> Images = new();
    private static readonly Dictionary<string, Material?> Materials = new();
    private static readonly HashSet<string> Logged = new();
    private static volatile IReadOnlyDictionary<string, TextureEdit>? _merged;

    /// <summary>Bumped on every change (a register, an invalidate): views compare it to refresh.</summary>
    public static int Version { get; private set; }

    public static bool Disabled => GameSettings.Flag("no-overrides") || GameSettings.Flag("no-textures");

    /// <summary>The project's index (<see cref="BaseDir"/>; the editor's store may point it elsewhere for its check).</summary>
    public static string ProjectIndex { get; set; } = $"{BaseDir}/{FileName}";

    /// <summary>The index files in layer order (whether or not they exist), and the sets registered in memory.</summary>
    public static IEnumerable<(string Layer, string Path, RemasterTextureSet? Set)> Sources()
    {
        yield return ("base", ProjectIndex, null);
        foreach (var p in ModLoader.Packs)
            yield return (p.Name, $"res://mods/{p.Name}/remaster/textures/{FileName}", null);
        foreach (var r in Registered)
            yield return r;
    }

    /// <summary>Adds the index at <paramref name="path"/> as the last layer, named <paramref name="layer"/>.</summary>
    public static void Register(string path, string layer = "runtime") => Register(layer, path, null);

    /// <summary>Adds <paramref name="set"/> (made in code; its paths absolute or relative to <paramref name="dir"/>) as the last layer.</summary>
    public static void Register(RemasterTextureSet set, string layer, string dir = "") => Register(layer, dir.TrimEnd('/') + "/" + FileName, set);

    private static void Register(string layer, string path, RemasterTextureSet? set)
    {
        Registered.RemoveAll(e => e.Layer == layer);
        Registered.Add((layer, path, set));
        Invalidate();
    }

    public static void Unregister(string layer)
    {
        if (Registered.RemoveAll(e => e.Layer == layer) > 0)
            Invalidate();
    }

    /// <summary>Drops the merge and the loaded images and materials: the next <see cref="Current"/> reads the files again.</summary>
    public static void Invalidate()
    {
        _merged = null;
        lock (Images)
            Images.Clear();
        lock (Materials)
            Materials.Clear();
        Version++;
    }

    /// <summary>The merge (main thread to make: it loads the index files; afterwards any thread). Empty when disabled.</summary>
    public static IReadOnlyDictionary<string, TextureEdit> Current
    {
        get
        {
            if (_merged is { } m)
                return m;
            if (OS.GetThreadCallerId() != OS.GetMainThreadId())
                return Empty;
            _merged = m = Disabled ? Empty : Merge();
            return m;
        }
    }

    private static readonly IReadOnlyDictionary<string, TextureEdit> Empty = new Dictionary<string, TextureEdit>();

    /// <summary>Makes the merge now (main thread), so worker threads see it.</summary>
    public static void Ensure() => _ = Current;

    private static Dictionary<string, TextureEdit> Merge()
    {
        var merged = new Dictionary<string, TextureEdit>();
        var names = new List<string>();
        foreach (var (layer, path, given) in Sources())
        {
            var set = given;
            if (set == null)
            {
                // a text index is a file (in the project or a pack): Godot's file cache may still name a removed one
                if (!FileAccess.FileExists(path))
                    continue;
                // read fresh, never into the cached instances: Replace updates them in place with what the
                // file says, and a text resource leaves out fields at their defaults, so a field emptied by
                // an edit (an undo) would keep its old value in the editor's live index
                set = ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) as RemasterTextureSet;
                if (set == null)
                {
                    if (Logged.Add(path))
                        GD.PrintErr($"ffx: textures: {path} is not a RemasterTextureSet");
                    continue;
                }
            }
            var dir = path.GetBaseDir();
            int n = 0;
            foreach (var t in set.Textures)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.Id))
                    continue;
                var id = t.Id.Trim().ToLowerInvariant();
                n++;
                if (t.Mode == TextureMode.Classic)
                {
                    merged.Remove(id);
                    continue;
                }
                merged.TryGetValue(id, out var was);
                var rep = t.Replacement.Length > 0 ? Resolve(t.Replacement, dir) : was?.Replacement ?? "";
                var preset = t.Replacement.Length > 0 ? t.Preset : was?.Preset ?? t.Preset;
                var mat = t.Material.Length > 0 ? Resolve(t.Material, dir) : was?.Material ?? "";
                var recipe = t.Material.Length > 0 ? t.Recipe : was?.Recipe ?? t.Recipe;
                merged[id] = new TextureEdit(id, rep, preset, mat, recipe, layer);
            }
            names.Add($"{layer} ({path}: {n})");
        }
        if (names.Count > 0)
            GD.Print($"ffx: textures: {merged.Count} texture(s) over {names.Count} layer(s): {string.Join(", ", names)}");
        return merged;
    }

    /// <summary><paramref name="path"/> as an absolute path: <c>res://</c>, <c>user://</c>, <c>uid://</c> and absolute ones as they are, others relative to <paramref name="dir"/>.</summary>
    public static string Resolve(string path, string dir)
    {
        if (path.Contains("://") || path.StartsWith('/') || (path.Length > 2 && path[1] == ':'))
            return path;
        return $"{dir.TrimEnd('/')}/{path}";
    }

    /// <summary><paramref name="id"/>'s merged edit (null: none).</summary>
    public static TextureEdit? For(string? id) =>
        id != null && Current.TryGetValue(id.ToLowerInvariant(), out var e) ? e : null;

    /// <summary><paramref name="id"/>'s replacement image, or <paramref name="classic"/> (any thread).</summary>
    public static Texture2D? ReplacementOr(string? id, Texture2D? classic) =>
        For(id) is { Replaced: true } e && Texture(e.Replacement) is { } t ? t : classic;

    /// <summary><paramref name="id"/>'s material (null: none; any thread).</summary>
    public static Material? MaterialFor(string? id) =>
        For(id) is { HasMaterial: true } e ? LoadMaterial(e.Material) : null;

    /// <summary>
    /// The image at <paramref name="path"/> (any thread; cached): an imported resource, else the
    /// file itself (a PNG outside the project or not imported yet) with mipmaps.
    /// </summary>
    public static Texture2D? Texture(string path)
    {
        lock (Images)
            if (Images.TryGetValue(path, out var known))
                return known;
        Texture2D? t = null;
        if (ResourceLoader.Exists(path) && !Unimported(path))
            t = ResourceLoader.Load(path) as Texture2D;
        // not imported (yet): the file itself
        if (t == null && FileAccess.FileExists(path) && path.GetExtension().ToLowerInvariant() is "png" or "webp" or "jpg" or "jpeg"
            && LoadImage(path) is { } img && !img.IsEmpty())
        {
            if (img.IsCompressed())
                img.Decompress();
            img.GenerateMipmaps();
            t = ImageTexture.CreateFromImage(img);
            t.ResourceName = path.GetFile();
        }
        if (t == null && Logged.Add(path))
            GD.PrintErr($"ffx: textures: cannot load {path}");
        lock (Images)
        {
            if (Images.TryGetValue(path, out var kept))
                return kept;
            Images[path] = t;
        }
        return t;
    }

    /// <summary>
    /// Whether <paramref name="path"/> has an <c>.import</c> without an import result yet (the
    /// editor wrote it, Godot has not imported the file): loaded from the file itself meanwhile.
    /// </summary>
    public static bool Unimported(string path)
    {
        var import = path + ".import";
        if (!FileAccess.FileExists(import))
            return false;
        var text = FileAccess.GetFileAsString(import);
        return !text.Contains("\npath=", System.StringComparison.Ordinal) && !text.Contains("\npath.", System.StringComparison.Ordinal);
    }

    /// <summary>An image file by any of Godot's paths (<c>res://</c>, also inside a pack; <c>user://</c>; absolute).</summary>
    private static Image? LoadImage(string path)
    {
        var bytes = FileAccess.GetFileAsBytes(path);
        if (bytes.Length == 0)
            return null;
        var img = new Image();
        var err = path.GetExtension().ToLowerInvariant() switch
        {
            "png" => img.LoadPngFromBuffer(bytes),
            "webp" => img.LoadWebpFromBuffer(bytes),
            _ => img.LoadJpgFromBuffer(bytes),
        };
        return err == Error.Ok ? img : null;
    }

    /// <summary>
    /// The maps <paramref name="m"/> names that did not load (empty: none): an ORM material without
    /// its ORM map (it would draw as metal: its metallic multiplier is 1), a normal or height map
    /// switched on without its texture (what Godot leaves of a material whose files are gone or not
    /// imported yet). A later <see cref="RemasterTextures.Version"/> (the import done) makes it again.
    /// </summary>
    public static List<string> MissingMaps(BaseMaterial3D m)
    {
        var missing = new List<string>();
        if (m is OrmMaterial3D { OrmTexture: null })
            missing.Add("orm");
        if (m.NormalEnabled && m.NormalTexture == null)
            missing.Add("normal");
        if (m.HeightmapEnabled && m.HeightmapTexture == null)
            missing.Add("height");
        return missing;
    }

    private static Material? LoadMaterial(string path)
    {
        lock (Materials)
            if (Materials.TryGetValue(path, out var known))
                return known;
        // its maps being imported still (the editor wrote them just now): not yet, not remembered
        if (FileAccess.FileExists(path))
            foreach (var dep in ResourceLoader.GetDependencies(path))
            {
                var p = dep[(dep.LastIndexOf("::", System.StringComparison.Ordinal) + 2)..];
                if (p.Length > 0 && Unimported(p))
                    return null;
            }
        // its maps loaded again too (Godot's cache may hold a copy loaded before they were imported)
        var m = ResourceLoader.Exists(path) || FileAccess.FileExists(path)
            ? ResourceLoader.Load(path, "", ResourceLoader.CacheMode.ReplaceDeep) as Material : null;
        if (m == null && Logged.Add(path))
            GD.PrintErr($"ffx: textures: cannot load the material {path}");
        lock (Materials)
        {
            if (Materials.TryGetValue(path, out var kept))
                return kept;
            Materials[path] = m;
        }
        return m;
    }

    // ---- the classic materials --------------------------------------------------------------

    /// <summary>
    /// Notes on classic material <paramref name="m"/> that its parameter <paramref name="param"/>
    /// samples texture <paramref name="id"/> (classic texture <paramref name="classic"/>), and
    /// returns the texture to bind: the replacement when there is one.
    /// </summary>
    public static Texture? Bind(ShaderMaterial m, string param, string? id, Texture? classic)
    {
        if (id == null)
            return classic;
        var ps = PartIds.GetStrings(m, ParamsMeta) ?? [];
        var ids = PartIds.GetStrings(m, IdsMeta) ?? [];
        PartIds.SetStrings(m, ParamsMeta, [.. ps, param]);
        PartIds.SetStrings(m, IdsMeta, [.. ids, id.ToLowerInvariant()]);
        if (classic != null)
            m.SetMeta(Variants.Name(ClassicMeta + param), classic);
        // a cube map is not replaced (2D images only)
        return classic is null or Texture2D ? ReplacementOr(id, classic as Texture2D) : classic;
    }

    /// <summary>The texture IDs classic material <paramref name="m"/> samples, by parameter (empty: none).</summary>
    public static IEnumerable<(string Param, string Id)> BoundTextures(Material? m)
    {
        if (m is not ShaderMaterial sm)
            yield break;
        var ps = PartIds.GetStrings(sm, ParamsMeta);
        var ids = PartIds.GetStrings(sm, IdsMeta);
        if (ps == null || ids == null)
            yield break;
        for (int i = 0; i < ps.Length && i < ids.Length; i++)
            yield return (ps[i], ids[i]);
    }

    /// <summary>The texture ID classic material <paramref name="m"/>'s colour texture (<c>tex</c>, else the water's <c>tex0</c>) samples (null: none).</summary>
    public static string? ColourId(Material? m)
    {
        string? first = null;
        foreach (var (p, id) in BoundTextures(m))
        {
            if (p == "tex")
                return id;
            first ??= p == "tex0" ? id : null;
        }
        return first;
    }

    /// <summary>The classic texture of <paramref name="m"/>'s parameter <paramref name="param"/> (null: none noted).</summary>
    public static Texture? ClassicOf(ShaderMaterial m, string param)
    {
        var name = Variants.Name(ClassicMeta + param);
        if (!m.HasMeta(name))
            return null;
        using var v = m.GetMeta(name);
        return v.AsGodotObject() as Texture;
    }

    /// <summary>
    /// Binds the current replacements (or the classic textures again) on every classic material
    /// under <paramref name="root"/> (main thread): after an edit in the editor, a registered
    /// layer, the toggle. Returns how many texture parameters it set.
    /// </summary>
    public static int Refresh(Node root)
    {
        var done = new HashSet<Material>(ReferenceEqualityComparer.Instance);
        int n = 0;
        void Fix(Material? m)
        {
            for (; m != null && done.Add(m); m = m.NextPass)
            {
                if (m is not ShaderMaterial sm)
                    continue;
                foreach (var (param, id) in BoundTextures(sm))
                {
                    var classic = ClassicOf(sm, param);
                    var t = classic is Texture2D or null ? ReplacementOr(id, classic as Texture2D) : classic;
                    if (t != null)
                    {
                        Variants.SetParameter(sm, param, t);
                        n++;
                    }
                }
            }
        }
        void Walk(Node node)
        {
            if (node is MeshInstance3D { Mesh: { } mesh } mi)
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    Fix(mi.GetSurfaceOverrideMaterial(s));
                    Fix(mesh.SurfaceGetMaterial(s));
                }
            for (int i = 0, k = node.GetChildCount(); i < k; i++)
                Walk(node.GetChild(i));
        }
        Walk(root);
        // the classic materials a map drawn remastered holds aside (MapLook)
        if (MapLook.Find(root) is { } look)
            foreach (var m in look.Classics)
                Fix(m);
        return n;
    }

    /// <summary>
    /// The folder of a texture's files under an index folder (<paramref name="baseDir"/>): its
    /// archive path without <c>x:/ffx_data/gamedata/ps3data/</c>, <c>tex/d3d11/</c> and
    /// <c>.dds.phyre</c> (<c>map/bsil/bsil00/sand</c>).
    /// </summary>
    public static string Folder(string id, string baseDir = BaseDir)
    {
        var p = id.ToLowerInvariant();
        foreach (var prefix in new[] { "x:/ffx_data/gamedata/ps3data/", "x:/" })
            if (p.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                p = p[prefix.Length..];
                break;
            }
        p = p.Replace("/tex/d3d11/", "/").Replace("/d3d11/", "/");
        if (p.EndsWith(".dds.phyre", System.StringComparison.Ordinal))
            p = p[..^".dds.phyre".Length];
        var safe = new System.Text.StringBuilder();
        foreach (var c in p)
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.' ? c : '_');
        return $"{baseDir.TrimEnd('/')}/{safe.ToString().Trim('/')}";
    }

    /// <summary>
    /// The path a model stores for texture <paramref name="id"/> (<c>ps3data/map/bsil/bsil00/tex/sand.dds</c>),
    /// as the extension's texture reads take it; null when the ID is not an archive texture's.
    /// </summary>
    public static string? StoredPath(string id)
    {
        const string prefix = "x:/ffx_data/gamedata/";
        var p = id.ToLowerInvariant();
        if (!p.StartsWith(prefix, System.StringComparison.Ordinal) || !p.EndsWith(".phyre", System.StringComparison.Ordinal))
            return null;
        p = p[prefix.Length..^".phyre".Length];
        int d = p.LastIndexOf("/d3d11/", System.StringComparison.Ordinal);
        return d < 0 ? p : p[..d] + "/" + p[(d + "/d3d11/".Length)..];
    }

    /// <summary>The classic texture <paramref name="id"/>'s pixels from the player's archive (its top level; null: not readable). Main thread is quickest.</summary>
    public static Image? ClassicImage(string id) =>
        StoredPath(id) is { } path && TextureFetcher.Fetch("face", path, 0) is { Faces: 1 } px ? px.ToImage() : null;

    /// <summary>A texture ID's short name (<c>sand</c> of <c>.../tex/d3d11/sand.dds.phyre</c>).</summary>
    public static string ShortName(string id)
    {
        var f = id[(id.LastIndexOf('/') + 1)..];
        return f.EndsWith(".dds.phyre", System.StringComparison.Ordinal) ? f[..^".dds.phyre".Length] : f;
    }
}
