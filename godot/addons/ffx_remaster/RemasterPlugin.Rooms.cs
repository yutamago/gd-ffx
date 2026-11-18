#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// R7b's tools (docs/remaster-editor.md, "2D rooms rebuilt in 3D", "UI art"): <b>Rebuild in 3D</b>
/// for a 2D-layer map (its remade map scene made and opened: <see cref="RebuiltRoom"/>), the rooms'
/// pictures and game data handed to the scenes (<see cref="RebuiltRoom.Provider"/>), and the
/// <b>UI art</b> dock (<see cref="UiArtDock"/>): a movie's art assigned an image, given back to the
/// classic, one undo step each (the layer file's text before and after).
/// </summary>
public partial class RemasterPlugin
{
    private UiArtDock? _uiArt;
    private RoomsEditorTest? _roomsTest;

    /// <summary>The 2D-layer imports loaded for the rooms, by level (kept while the editor runs).</summary>
    private readonly Dictionary<string, Node3D?> _roomLayers = new();

    public UiArtDock? UiArtDock => _uiArt;

    /// <summary>Where rebuilt rooms are written: the convention's folder (the checks: <c>--remaster-rooms=DIR</c>, in the project).</summary>
    public static string RoomsDir => GameSettings.Arg("remaster-rooms") is { Length: > 0 } d ? d.TrimEnd('/') : "res://remake/map";

    /// <summary>The editor's UI art layer (the project's; the checks: <c>--remaster-ui-art=FILE</c>).</summary>
    public static string UiArtFile => GameSettings.Arg("remaster-ui-art") is { Length: > 0 } f ? f : UiArt.ProjectFile;

    private void EnterRooms()
    {
        RebuiltRoom.Provider = RoomSources;
        if (UiArtFile != UiArt.ProjectFile)
            UiArt.Register(UiArtFile, "editor");
        _uiArt = new UiArtDock(this);
        AddDock(_uiArt);
        if (GameSettings.Flag("remaster-editor-rooms"))
            _roomsTest = new RoomsEditorTest(this);
    }

    private void ExitRooms()
    {
        if (RebuiltRoom.Provider == RoomSources)
            RebuiltRoom.Provider = null;
        foreach (var n in _roomLayers.Values)
            if (n != null && GodotObject.IsInstanceValid(n))
                n.Free();
        _roomLayers.Clear();
        if (_uiArt == null)
            return;
        RemoveDock(_uiArt);
        _uiArt.QueueFree();
        _uiArt = null;
    }

    private void StepRooms(double delta) => _roomsTest?.Step(delta);

    /// <summary>A level's 2D-layer import and game data (null while the import is under way; asked for then).</summary>
    public (Node3D? Layers, MapGameData? Data)? RoomSources(string level)
    {
        if (!Session.IsOpen || Session.Classic is not { } lib)
            return null;
        if (!_roomLayers.TryGetValue(level, out var layers))
        {
            if (lib.Path("map2d", level) is not { } path)
                return lib.Failed("map2d", level) ? (null, null) : null;
            layers = ClassicLibrary.Load(path);
            if (layers != null)
                ClassicMaterials.FixLayers(layers);
            _roomLayers[level] = layers;
        }
        return (layers, Session.Game != null ? MapGameData.Read(Session.Game, level) : null);
    }

    /// <summary>The rebuilt room's scene for <paramref name="key"/>.</summary>
    public static string RoomScenePath(string key) => $"{RoomsDir}/{key}.tscn";

    /// <summary>
    /// <b>Rebuild in 3D</b>: the open 2D-layer map (or <paramref name="level"/>) made a remade map scene
    /// (<see cref="RebuiltRoom.Create"/>: a proxy a layer, the floor, a group for the model) at
    /// <see cref="RoomScenePath"/> and opened; an existing one is opened, never overwritten. Like
    /// Godot's own new scene, making the file is not an undo step; editing it is Godot's. An error, or
    /// null.
    /// </summary>
    public string? RebuildRoom(string? level = null, bool open = true)
    {
        level ??= Current?.Level;
        if (level is not { Length: > 0 })
            return "open a 2D-layer map first";
        if (Current is { Level: var l, Is2D: false } && l == level)
            return $"{PartIds.LevelKey(level)} is a 3D map: remake it with additions or a remade map scene";
        var path = RoomScenePath(PartIds.LevelKey(level));
        if (!FileAccess.FileExists(path))
        {
            if (RoomSources(level) is not { } src)
                return $"importing {PartIds.LevelKey(level)}'s 2D layers: try again in a moment";
            if (src.Layers == null)
                return $"{PartIds.LevelKey(level)} has no 2D layers";
            var root = RebuiltRoom.Create(level, src.Layers);
            var packed = new PackedScene();
            var err = packed.Pack(root);
            root.Free();
            DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(path.GetBaseDir()));
            if (err != Error.Ok || ResourceSaver.Save(packed, path) != Error.Ok)
                return $"cannot write {path}";
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);
            GD.Print($"ffx remaster: {PartIds.LevelKey(level)} rebuilt in 3D: {path}");
        }
        if (open)
            EditorInterface.Singleton.OpenSceneFromPath(path);
        return null;
    }

    /// <summary>The rebuilt room being edited (null: another scene).</summary>
    public RebuiltRoom? CurrentRoom => EditorInterface.Singleton.GetEditedSceneRoot() as RebuiltRoom;

    // ---- UI art ---------------------------------------------------------------------------------

    /// <summary>The editor's UI art layer as it is on disk (a new one when there is none).</summary>
    public static RemasterUiArtSet LoadUiArt() =>
        FileAccess.FileExists(UiArtFile) && ResourceLoader.Load(UiArtFile, "", ResourceLoader.CacheMode.Replace) is RemasterUiArtSet s ? s : new RemasterUiArtSet();

    /// <summary>Replaces <paramref name="art"/> of <paramref name="movie"/> (a file name, or the name without its language) by <paramref name="image"/>; one undo step.</summary>
    public string? AssignUiArt(string movie, string art, string image, string note = "")
    {
        if (UiArt.Image(image) == null)
            return $"{image} is not an image Godot can read (PNG, WebP, JPEG)";
        var set = LoadUiArt();
        var key = (UiArt.MovieKey(movie), art);
        var keep = set.Art.Where(a => a != null && (UiArt.MovieKey(a.Movie), a.Art) != key).ToList();
        keep.Add(new RemasterUiArt { Movie = UiArt.MovieKey(movie), Art = art, Image = image, Note = note });
        keep.Sort((a, b) => a.Movie != b.Movie ? string.CompareOrdinal(a.Movie, b.Movie) : string.CompareOrdinal(a.Art, b.Art));
        set.Art = new Godot.Collections.Array<RemasterUiArt>(keep);
        return FileStep($"Replace UI art {art}", UiArtFile, MediaStore.ResourceText(set));
    }

    /// <summary>Gives <paramref name="art"/> of <paramref name="movie"/> back to the game's movie; one undo step.</summary>
    public string? ClearUiArt(string movie, string art)
    {
        var set = LoadUiArt();
        var key = (UiArt.MovieKey(movie), art);
        var keep = set.Art.Where(a => a != null && (UiArt.MovieKey(a.Movie), a.Art) != key).ToList();
        if (keep.Count == set.Art.Count)
            return null;
        set.Art = new Godot.Collections.Array<RemasterUiArt>(keep);
        return FileStep($"Classic UI art {art}", UiArtFile, MediaStore.ResourceText(set));
    }

    /// <summary>Gives role <paramref name="role"/> (<c>ui</c>, <c>message</c>) the font <paramref name="file"/> (empty: the system's again); one undo step.</summary>
    public string? AssignUiFont(string role, string file)
    {
        if (file.Length > 0 && UiArt.LoadFont(file) == null)
            return $"{file} is not a font Godot can read (TTF, OTF, WOFF, WOFF2)";
        var set = LoadUiArt();
        var keep = set.Fonts.Where(f => f != null && f.Role != role).ToList();
        if (file.Length > 0)
            keep.Add(new RemasterUiFont { Role = role, File = file });
        set.Fonts = new Godot.Collections.Array<RemasterUiFont>(keep);
        return FileStep($"UI font {role}", UiArtFile, MediaStore.ResourceText(set));
    }
}
#endif
