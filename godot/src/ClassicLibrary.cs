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
    private readonly HashSet<(string Kind, string Name)> _asked = new();

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
    /// Why the launcher must not clear <paramref name="dir"/>'s <c>map/</c> and <c>chr/</c>, or
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

    /// <summary>Picks up the finished imports (once a frame).</summary>
    public void Poll()
    {
        foreach (var d in _game.ClassicFinished())
        {
            var key = (d["kind"].AsString(), d["name"].AsString());
            var path = d["path"].AsString();
            if (path.Length > 0)
                _paths[key] = path;
            else
            {
                _errors[key] = d["error"].AsString();
                GD.PrintErr($"ffx: classic {key.Item1} {key.Item2}: {_errors[key]}");
            }
        }
    }

    /// <summary>The .glb of <paramref name="name"/> when it is imported; asks for it otherwise.</summary>
    public string? Path(string kind, string name)
    {
        var key = (kind, name);
        if (_paths.TryGetValue(key, out var p))
            return p;
        if (Open && !_errors.ContainsKey(key) && _asked.Add(key))
            _game.ClassicRequest(kind, name);
        return null;
    }

    /// <summary>Whether <paramref name="name"/> failed to import.</summary>
    public bool Failed(string kind, string name) => _errors.ContainsKey((kind, name));

    /// <summary>A loaded copy of the .glb at <paramref name="path"/> (null on failure).</summary>
    public static Node3D? Load(string path)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state) != Error.Ok)
        {
            GD.PrintErr($"ffx: cannot load {path}");
            return null;
        }
        var scene = doc.GenerateScene(state) as Node3D;
        if (scene != null)
            ClassicMaterials.Apply(state, scene);
        return scene;
    }
}
