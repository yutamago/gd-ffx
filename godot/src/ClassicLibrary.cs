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

    public ClassicLibrary(FfxGame game, string gameDir, string cacheDir)
    {
        _game = game;
        Open = game.ClassicOpen(gameDir, cacheDir);
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
        return doc.GenerateScene(state) as Node3D;
    }
}
