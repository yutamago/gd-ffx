using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// Classic content (docs/importer.md): the game's own maps and characters,
/// imported in the background from the player's archive by the extension
/// (rust/ffx-import) into gd-ffx's cache folder, then loaded here as glTF
/// scenes. Nothing of the game ships with gd-ffx; this cache is made on the
/// player's machine.
/// </summary>
public sealed class ClassicLibrary
{
    /// <summary>The game places a map's instances at the file's node worlds times 10.</summary>
    public const float MapScale = 10f;

    private readonly FfxGame _game;
    private readonly Dictionary<(string Kind, string Name), string> _paths = new();
    private readonly Dictionary<(string Kind, string Name), string> _errors = new();
    private readonly HashSet<(string, string)> _quiet = new();
    private readonly HashSet<(string Kind, string Name)> _asked = new();
    /// <summary>The imports asked for and not finished yet: <see cref="Poll"/> asks the extension only while there are some.</summary>
    private readonly HashSet<(string Kind, string Name)> _pending = new();

    public bool Open { get; }

    /// <summary>Imports finished, failed, waiting.</summary>
    public (int Ready, int Failed, int Waiting) Counts =>
        (_paths.Count, _errors.Count, _asked.Count - _paths.Count - _errors.Count);

    /// <summary>The file that marks a folder as gd-ffx's classic cache (written when the cache is opened).</summary>
    public const string Marker = ".gd-ffx-classic-cache";

    public ClassicLibrary(FfxGame game, string gameDir, string cacheDir)
    {
        _game = game;
        Open = game.ClassicOpen(gameDir, cacheDir);
        if (Open)
            WriteMarker(cacheDir);
    }

    /// <summary>Marks <paramref name="cacheDir"/> as a classic cache (<see cref="Marker"/>), so the launcher may clear it.</summary>
    public static void WriteMarker(string cacheDir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(cacheDir);
            var m = System.IO.Path.Combine(cacheDir, Marker);
            if (!System.IO.File.Exists(m))
                System.IO.File.WriteAllText(m, "gd-ffx's classic cache: imported from the player's copy; safe to delete\n");
        }
        catch (System.Exception e) when (e is System.IO.IOException or System.UnauthorizedAccessException)
        {
            GD.PrintErr($"ffx: classic cache {cacheDir}: {e.Message}");
        }
    }

    /// <summary>
    /// Why the launcher must not clear <paramref name="dir"/>'s <c>map/</c>, <c>map2d/</c> and <c>chr/</c>, or
    /// null when it may: the folder is absolute, not a filesystem root, the home folder or
    /// gd-ffx's user data folder itself, and lies inside gd-ffx's user data folder
    /// (<c>user://</c>) or holds the <see cref="Marker"/> that gd-ffx writes into a cache it used.
    /// </summary>
    public static string? WhyNotClearable(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return "no cache folder";
        if (!System.IO.Path.IsPathFullyQualified(dir))
            return "not an absolute path";
        static string Norm(string p) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p));
        var full = Norm(dir);
        if (full == Norm(System.IO.Path.GetPathRoot(full) ?? full) || full.Length <= 1)
            return "a filesystem root";
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && string.Equals(full, Norm(home), System.StringComparison.OrdinalIgnoreCase))
            return "the home folder";
        var user = Norm(OS.GetUserDataDir());
        if (string.Equals(full, user, System.StringComparison.OrdinalIgnoreCase))
            return "gd-ffx's user data folder itself";
        if (full.StartsWith(user + System.IO.Path.DirectorySeparatorChar, System.StringComparison.Ordinal))
            return null;
        if (System.IO.File.Exists(System.IO.Path.Combine(full, Marker)))
            return null;
        return $"outside gd-ffx's user data folder and without {Marker}";
    }

    /// <summary>
    /// Picks up the finished imports (once a frame, while any is pending: the extension's answer is
    /// a Godot array, and each such wrapper and variant is finalizable, so it outlives its first
    /// collection; a few a frame made every young collection long: docs/running.md, "Benchmark").
    /// </summary>
    public void Poll()
    {
        if (_pending.Count == 0)
            return;
        foreach (var d in _game.ClassicFinished())
        {
            var key = (d["kind"].AsString(), d["name"].AsString());
            _pending.Remove(key);
            var path = d["path"].AsString();
            if (path.Length > 0)
                _paths[key] = path;
            else
            {
                _errors[key] = d["error"].AsString();
                // "none: ..." is no fault: a map of 2D layers only has no 3D model, most maps no 2D layers
                if (!_errors[key].StartsWith("none: ", System.StringComparison.Ordinal) && !_quiet.Contains(key))
                    GD.PrintErr($"ffx: classic {key.Item1} {key.Item2}: {_errors[key]}");
            }
        }
    }

    /// <summary>
    /// The .glb of <paramref name="name"/> when it is imported; asks for it otherwise.
    /// <paramref name="quiet"/>: a guess (the next map loaded ahead, MapPrefetch), whose failure
    /// is no fault and is not logged (exits to the developers' test maps the retail archive
    /// lacks); asked for again without it, a failure is logged then.
    /// </summary>
    public string? Path(string kind, string name, bool quiet = false)
    {
        var key = (kind, name);
        if (_paths.TryGetValue(key, out var p))
            return p;
        if (quiet)
            _quiet.Add(key);
        else if (_quiet.Remove(key) && _errors.TryGetValue(key, out var why) && !why.StartsWith("none: ", System.StringComparison.Ordinal))
            GD.PrintErr($"ffx: classic {kind} {name}: {why}");
        if (Open && !_errors.ContainsKey(key) && _asked.Add(key) && _game.ClassicRequest(kind, name))
            _pending.Add(key);
        return null;
    }

    /// <summary>Whether <paramref name="name"/> failed to import.</summary>
    public bool Failed(string kind, string name) => _errors.ContainsKey((kind, name));

    /// <summary>
    /// A loaded copy of the .glb at <paramref name="path"/> (null on failure); <paramref name="texDir"/>
    /// is a map's texture folder as the models name it (<c>PS3Data/map/bsil/bsil00/tex/</c>),
    /// where its cube maps are.
    /// </summary>
    /// <summary>
    /// <c>EditorSceneFormatImporter.IMPORT_FORCE_DISABLE_MESH_COMPRESSION</c> (64; the editor's
    /// enum is not in the game's assembly). Without it Godot's glTF loader compresses the
    /// vertex attributes of every surface with normals and no skin or morph: positions as
    /// 16-bit fractions of the surface's own bounding box. Two map pieces then round the
    /// vertices of an edge they share differently, and the cracks between them let the clear
    /// colour and the sky through (single bright pixels along the seams of Besaid's sand: 110
    /// of the map's 138 surfaces were compressed). The game draws the pieces' float positions
    /// with one shared world matrix (ffx-rust's docs/formats/scene.md, question 4), so its
    /// edges meet; uncompressed, Godot's do too.
    /// </summary>
    public const uint ForceDisableMeshCompression = 64;

    public static Node3D? Load(string path, string? texDir = null)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state, ForceDisableMeshCompression) != Error.Ok)
        {
            GD.PrintErr($"ffx: cannot load {path}");
            return null;
        }
        var scene = doc.GenerateScene(state) as Node3D;
        // the import's extras read once from the JSON (GltfExtras: no Godot variant a value)
        if (scene != null && GltfExtras.Read(ProjectSettings.GlobalizePath(path)) is { } extras)
        {
            MarkMeshes(state, extras);
            ClassicMaterials.Apply(state, extras, scene, texDir);
        }
        return scene;
    }

    /// <summary>
    /// Sets each glTF mesh's extras (<see cref="MeshExtras"/>) on the Godot mesh the scene uses:
    /// an <see cref="ImporterMesh"/> keeps the mesh it made, which the generated scene shows.
    /// </summary>
    private static void MarkMeshes(GltfState state, GltfExtras extras)
    {
        var meshes = state.GetMeshes();
        for (int i = 0; i < meshes.Count && i < extras.Meshes.Length; i++)
        {
            // their C# wrappers released at once (finalizable; nothing else holds these two)
            using var gltf = meshes[i];
            using var importer = gltf.Mesh;
            if (importer?.GetMesh() is { } mesh)
                extras.Meshes[i].SetOn(mesh);
        }
    }
}
