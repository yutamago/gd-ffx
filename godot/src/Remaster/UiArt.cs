using System.Collections.Generic;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// The native screens' art and fonts replaced (R7b; docs/remaster-editor.md, "UI art"). The
/// screens draw art from the game's Flash movies (the small SWF renderer: <see cref="FfxGame.FlashRender"/>);
/// each piece they ask for has an id in its movie (<see cref="ArtId"/>): a <c>SymbolClass</c> name
/// (<c>saveload_jp_fla.arrow_21</c>, the thumbnails' <c>m12</c>) or <c>#&lt;character id&gt;</c>, with
/// <c>@&lt;label&gt;</c> or <c>@&lt;frame&gt;</c> for one frame of a sprite (the portraits:
/// <c>saveload_jp_fla.face_565@_1</c>; the opening's logo <c>#0@34</c>). A replacement is an image
/// drawn where the classic art would be, at its size. Layers as the textures and sounds: the
/// project's <c>res://remaster/ui/ui.tres</c>, each pack's <c>res://mods/&lt;pack&gt;/remaster/ui/ui.tres</c>,
/// then files registered at run time (<c>--ui-art-file=</c>); a later layer wins per movie and id,
/// <c>Classic</c> undoes. An entry's movie is a file name (<c>saveload_kr.swf</c>) or the name without
/// its language (<c>saveload</c>: every language's movie); the file name wins. The fonts by role:
/// <c>ui</c> (every screen's text, <see cref="MessageView.UiFont"/>) and <c>message</c> (the message
/// boxes', <see cref="MessageView.MessageFont"/>; the <c>ui</c> font when not set), with the system
/// fonts behind them for the glyphs they lack. F8 (<see cref="Replacements"/>) turns them off and on.
/// </summary>
public static class UiArt
{
    public const string ProjectFile = "res://remaster/ui/ui.tres";

    private static readonly List<(string Layer, string Path)> Registered = new();
    private static Dictionary<(string Movie, string Art), (RemasterUiArt Entry, string Image, string Layer)>? _art;
    private static Dictionary<string, (string File, string Layer)>? _fonts;
    private static readonly Dictionary<string, Texture2D?> Images = new();
    private static readonly HashSet<string> Logged = new();
    private static int _fontsFor = -1;

    /// <summary>Counts the merges and F8's flips (views drop their cached art by it).</summary>
    public static int Version => _version + Replacements.Changed * 1000003;
    private static int _version;

    /// <summary>The art id for a class or character, optionally one frame of it (<c>name</c>, <c>#12</c>, <c>name@_1</c>, <c>#0@34</c>).</summary>
    public static string ArtId(string classOrId, string? frame = null) => frame is { Length: > 0 } ? $"{classOrId}@{frame}" : classOrId;

    /// <summary>A movie's key: its file name without folder and extension, lower case.</summary>
    public static string MovieKey(string movie) => AudioReplacements.MovieKey(movie);

    /// <summary>Adds a layer file at run time (a later one wins).</summary>
    public static void Register(string path, string layer = "runtime")
    {
        Registered.RemoveAll(e => e.Layer == layer);
        Registered.Add((layer, path));
        Invalidate();
    }

    public static void Unregister(string layer)
    {
        if (Registered.RemoveAll(e => e.Layer == layer) > 0)
            Invalidate();
    }

    /// <summary>Reads the layers again (the editor after an edit).</summary>
    public static void Invalidate()
    {
        _art = null;
        _fonts = null;
        lock (Images)
            Images.Clear();
        _version++;
        ApplyFonts();
    }

    /// <summary>The layer files in order (whether or not they exist).</summary>
    public static IEnumerable<(string Layer, string Path)> Sources()
    {
        yield return ("base", ProjectFile);
        foreach (var p in ModLoader.Packs)
            yield return (p.Name, $"res://mods/{p.Name}/remaster/ui/ui.tres");
        foreach (var r in Registered)
            yield return r;
        foreach (var a in GameSettings.Args("ui-art-file"))
            yield return ("command line", a);
    }

    private static RemasterUiArtSet? LoadLayer(string path)
    {
        if (path.StartsWith("res://") && !ResourceLoader.Exists(path))
            return null;
        if (!path.StartsWith("res://") && !FileAccess.FileExists(path))
            return null;
        return ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace) as RemasterUiArtSet;
    }

    /// <summary>A path in a layer file, relative to its folder unless absolute or <c>res://</c>/<c>user://</c>.</summary>
    public static string Resolve(string layerFile, string path) =>
        path.Length == 0 || path.Contains("://") || path.StartsWith('/') || (path.Length > 1 && path[1] == ':') ? path : $"{layerFile.GetBaseDir()}/{path}";

    private static void Merge()
    {
        _art = new();
        _fonts = new();
        foreach (var (layer, path) in Sources())
        {
            if (LoadLayer(path) is not { } set)
                continue;
            foreach (var a in set.Art)
            {
                if (a == null || a.Movie.Length == 0 || a.Art.Length == 0)
                    continue;
                var key = (MovieKey(a.Movie), a.Art);
                if (a.Classic)
                    _art.Remove(key);
                else if (a.Image.Length > 0)
                    _art[key] = (a, Resolve(path, a.Image), layer);
            }
            foreach (var f in set.Fonts)
            {
                if (f == null || f.Role.Length == 0)
                    continue;
                if (f.Classic)
                    _fonts.Remove(f.Role);
                else if (f.File.Length > 0)
                    _fonts[f.Role] = (Resolve(path, f.File), layer);
            }
        }
        if (_art.Count > 0 || _fonts.Count > 0)
            GD.Print($"ffx: ui art: {_art.Count} replaced piece(s), {_fonts.Count} font(s)");
    }

    /// <summary>Every replaced piece by (movie key, art id), with its resolved image and layer.</summary>
    public static IReadOnlyDictionary<(string Movie, string Art), (RemasterUiArt Entry, string Image, string Layer)> Entries
    {
        get
        {
            if (_art == null)
                Merge();
            return _art!;
        }
    }

    /// <summary>The replaced fonts by role, with their resolved files.</summary>
    public static IReadOnlyDictionary<string, (string File, string Layer)> Fonts
    {
        get
        {
            if (_fonts == null)
                Merge();
            return _fonts!;
        }
    }

    /// <summary>The entry for <paramref name="art"/> of <paramref name="movie"/> (the file name's, else its language-free name's), whether or not F8 has them off.</summary>
    public static (RemasterUiArt Entry, string Image, string Layer)? Find(string movie, string art)
    {
        var key = MovieKey(movie);
        if (Entries.TryGetValue((key, art), out var e))
            return e;
        var family = AudioReplacements.BaseMovieKey(key);
        return family != key && Entries.TryGetValue((family, art), out e) ? e : null;
    }

    /// <summary>The replacement image for <paramref name="art"/> of <paramref name="movie"/> (null: classic, or F8 off).</summary>
    public static Texture2D? Replacement(string movie, string art)
    {
        if (!Replacements.On || Find(movie, art) is not { } e)
            return null;
        return Image(e.Image);
    }

    /// <summary>An image file as a texture (any Godot path; cached).</summary>
    public static Texture2D? Image(string path)
    {
        lock (Images)
            if (Images.TryGetValue(path, out var known))
                return known;
        Texture2D? t = null;
        if (path.StartsWith("res://") && ResourceLoader.Exists(path) && !RemasterTextures.Unimported(path))
            t = ResourceLoader.Load(path) as Texture2D;
        if (t == null && FileAccess.FileExists(path))
        {
            var img = new Image();
            var bytes = FileAccess.GetFileAsBytes(path);
            var err = path.GetExtension().ToLowerInvariant() switch
            {
                "png" => img.LoadPngFromBuffer(bytes),
                "webp" => img.LoadWebpFromBuffer(bytes),
                "jpg" or "jpeg" => img.LoadJpgFromBuffer(bytes),
                _ => Error.FileUnrecognized,
            };
            if (err == Error.Ok && !img.IsEmpty())
                t = ImageTexture.CreateFromImage(img);
        }
        if (t == null && Logged.Add(path))
            GD.PrintErr($"ffx: ui art: cannot load {path} (the classic art is drawn)");
        lock (Images)
            Images[path] = t;
        return t;
    }

    /// <summary>A font file (TTF, OTF, WOFF, WOFF2) by any Godot path (null: not loadable).</summary>
    public static FontFile? LoadFont(string path)
    {
        if (path.StartsWith("res://") && ResourceLoader.Exists(path) && ResourceLoader.Load(path) is FontFile ff)
            return ff;
        if (!FileAccess.FileExists(path))
            return null;
        var f = new FontFile();
        if (f.LoadDynamicFont(path) != Error.Ok)
            return null;
        return f;
    }

    private static readonly Dictionary<string, FontFile?> FontFiles = new();

    /// <summary>
    /// Puts the replaced fonts (or the system's) under <see cref="MessageView.UiFont"/> and
    /// <see cref="MessageView.MessageFont"/>: every label and drawn string using them changes at
    /// once. Called after a merge and each frame by the views (cheap when nothing changed).
    /// </summary>
    public static void ApplyFonts()
    {
        if (_fontsFor == Version)
            return;
        _fontsFor = Version;
        Font? For(string role)
        {
            if (!Replacements.On || !Fonts.TryGetValue(role, out var f))
                return null;
            if (!FontFiles.TryGetValue(f.File, out var file))
            {
                file = LoadFont(f.File);
                if (file == null)
                    GD.PrintErr($"ffx: ui art: cannot load the font {f.File} ({role}; the system's is used)");
                else
                    file.Fallbacks = [MessageView.SystemUiFont];
                FontFiles[f.File] = file;
            }
            return file;
        }
        var ui = For("ui");
        var message = For("message");
        Font uiBase = ui ?? MessageView.SystemUiFont;
        if (MessageView.UiFont.BaseFont != uiBase)
            MessageView.UiFont.BaseFont = uiBase;
        Font messageBase = message ?? MessageView.UiFont;
        if (MessageView.MessageFont.BaseFont != messageBase)
            MessageView.MessageFont.BaseFont = messageBase;
    }
}
