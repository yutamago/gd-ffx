#if TOOLS
using System.Collections.Generic;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// gd-ffx's remaster editor (docs/remaster-editor.md; docs/remaster-plan.md R1b): the player's
/// archive in the editor (<see cref="EditorClassic"/>), the map browser dock
/// (<see cref="MapBrowserDock"/>), a map opened as an editable scene of its classic parts
/// (<see cref="MapEditRoot"/>), the part inspector (<see cref="PartInspector"/>), the edits saved
/// as the map's override layer (<see cref="MapEditSave"/>), and play from the editor. Every
/// edit the plugin makes is one step of the editor's undo history; moves and hiding are
/// Godot's own (gizmos, inspector, the scene dock), read back from the scene when saving.
/// </summary>
[Tool]
public partial class RemasterPlugin : EditorPlugin
{
    public static RemasterPlugin? Instance { get; private set; }

    public EditorClassic Session { get; } = new();

    private MapBrowserDock? _dock;
    private PartInspector? _inspector;
    private EditorSelfTest? _test;

    /// <summary>The map edit scene being edited (null: another scene, or none).</summary>
    public MapEditRoot? Current => EditorInterface.Singleton.GetEditedSceneRoot() as MapEditRoot;

    public MapBrowserDock? Dock => _dock;

    public override void _EnterTree()
    {
        Instance = this;
        _dock = new MapBrowserDock(this);
        AddDock(_dock);
        _inspector = new PartInspector(this);
        AddInspectorPlugin(_inspector);
        SceneSaved += OnSceneSaved;
        SceneChanged += OnSceneChanged;
        if (GameSettings.Flag("remaster-editor-test"))
            _test = new EditorSelfTest(this);
        // the archive opens by itself when a folder is known (a fraction of a second)
        Callable.From(() =>
        {
            if (EditorClassic.FindGameDir().Dir.Length > 0)
                OpenArchive();
        }).CallDeferred();
    }

    public override void _ExitTree()
    {
        SceneSaved -= OnSceneSaved;
        SceneChanged -= OnSceneChanged;
        if (_inspector != null)
            RemoveInspectorPlugin(_inspector);
        if (_dock != null)
        {
            RemoveDock(_dock);
            _dock.QueueFree();
        }
        if (GameHost.EditorGame == Session.Game)
            GameHost.EditorGame = null;
        Session.Game?.Node.Free();
        if (Instance == this)
            Instance = null;
    }

    public override string _GetPluginName() => "FFX Remaster";

    public override void _Process(double delta)
    {
        Session.Poll();
        _test?.Step(delta);
    }

    /// <summary>Opens the player's archive (<paramref name="dir"/>: a folder chosen in the dock) and shows the catalogue.</summary>
    public bool OpenArchive(string? dir = null)
    {
        bool ok = Session.Open(dir);
        _dock?.ArchiveChanged();
        return ok;
    }

    // ---- maps -------------------------------------------------------------------------------

    /// <summary>
    /// Opens <paramref name="entry"/> for editing: its edit scene (made when missing: the root
    /// and the level only, <c>res://remaster/edit/&lt;key&gt;.tscn</c>, not in git) opened in the
    /// editor, which then builds the map from the classic import. An error when it could not.
    /// </summary>
    public string? OpenMap(MapEntry entry)
    {
        if (!Session.IsOpen)
            return "the archive is not open";
        if (entry.Model3D == null && entry.Kind != "battle")
            return $"{entry.Key} has no 3D model (2D layers only): nothing to edit in 3D yet";
        var path = MapEditRoot.EditScenePath(entry.Key);
        if (!ResourceLoader.Exists(path))
        {
            DirAccess.MakeDirRecursiveAbsolute(MapEditRoot.EditDir);
            var root = new MapEditRoot { Name = entry.Key, Level = entry.Level };
            var packed = new PackedScene();
            var err = packed.Pack(root);
            root.Free();
            if (err != Error.Ok || ResourceSaver.Save(packed, path) != Error.Ok)
                return $"cannot write {path}";
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);
        }
        // the classic import asked for now (the scene waits for it)
        Session.Classic?.Path("map", entry.Level);
        EditorInterface.Singleton.OpenSceneFromPath(path);
        return null;
    }

    /// <summary>Saves the open map's edits (the scene's state as its override layer).</summary>
    public MapEditSave.Result? SaveMap(MapEditRoot? root = null)
    {
        root ??= Current;
        if (root == null || !root.Built)
            return null;
        var r = MapEditSave.Save(root);
        _dock?.MapsChanged();
        return r;
    }

    private void OnSceneSaved(string path)
    {
        // Ctrl+S on a map's edit scene saves its edits as well
        if (Current is { } root && root.SceneFilePath == path)
            SaveMap(root);
    }

    private void OnSceneChanged(Node? root) => _dock?.CurrentChanged();

    /// <summary>The edit scene finished building (the dock shows its parts).</summary>
    public void MapBuilt(MapEditRoot root) => _dock?.CurrentChanged();

    // ---- edits, each one undo step ------------------------------------------------------------

    private EditorUndoRedoManager Undo => GetUndoRedo();

    /// <summary>Removes <paramref name="part"/> from the map (Delete in the scene dock does the same, read back on save).</summary>
    public void RemovePart(MapEditRoot root, PartInfo part)
    {
        if (root.IsGone(part))
            return;
        EditorInterface.Singleton.GetSelection().RemoveNode(part.Node);
        Undo.CreateAction($"Remove {part.Short(root.Model)}", UndoRedo.MergeMode.Disable, root);
        Undo.AddDoMethod(root, MapEditRoot.MethodName.ParkPart, part.Id);
        Undo.AddUndoMethod(root, MapEditRoot.MethodName.UnparkPart, part.Id);
        Undo.CommitAction();
    }

    /// <summary>Puts <paramref name="part"/> back as the classic import has it: placed by the game, shown, not replaced, not removed.</summary>
    public void RevertPart(MapEditRoot root, PartInfo part)
    {
        Undo.CreateAction($"Revert {part.Short(root.Model)} to classic", UndoRedo.MergeMode.Disable, root);
        if (root.IsRemoved(part))
        {
            Undo.AddDoMethod(root, MapEditRoot.MethodName.UnparkPart, part.Id);
            Undo.AddUndoMethod(root, MapEditRoot.MethodName.ParkPart, part.Id);
        }
        else if (!root.IsGone(part))
        {
            Undo.AddDoMethod(root, MapEditRoot.MethodName.ResetPart, part.Id);
            Undo.AddUndoMethod(root, MapEditRoot.MethodName.SetPartState, part.Id, part.Node.GlobalTransform, part.Node.Visible);
        }
        if (part.Replacement.Length > 0)
        {
            Undo.AddDoMethod(root, MapEditRoot.MethodName.SetReplacement, part.Id, "");
            Undo.AddUndoMethod(root, MapEditRoot.MethodName.SetReplacement, part.Id, part.Replacement);
        }
        Undo.CommitAction();
    }

    /// <summary>Replaces <paramref name="part"/> by the scene at <paramref name="scene"/> (empty: drops the replacement).</summary>
    public void ReplacePart(MapEditRoot root, PartInfo part, string scene)
    {
        Undo.CreateAction(scene.Length > 0 ? $"Replace {part.Short(root.Model)}" : $"Drop the replacement of {part.Short(root.Model)}",
            UndoRedo.MergeMode.Disable, root);
        Undo.AddDoMethod(root, MapEditRoot.MethodName.SetReplacement, part.Id, scene);
        Undo.AddUndoMethod(root, MapEditRoot.MethodName.SetReplacement, part.Id, part.Replacement);
        Undo.CommitAction();
    }

    /// <summary>Sets <paramref name="part"/>'s note.</summary>
    public void SetNote(MapEditRoot root, PartInfo part, string note)
    {
        if (note == part.Note)
            return;
        Undo.CreateAction($"Note on {part.Short(root.Model)}", UndoRedo.MergeMode.Disable, root);
        Undo.AddDoMethod(root, MapEditRoot.MethodName.SetPartNote, part.Id, note);
        Undo.AddUndoMethod(root, MapEditRoot.MethodName.SetPartNote, part.Id, part.Note);
        Undo.CommitAction();
    }

    /// <summary>What "Add" can drop into the map.</summary>
    public static readonly string[] Primitives = ["Box", "Sphere", "Cylinder", "Omni light", "Spot light"];

    /// <summary>A new node of kind <paramref name="kind"/> (<see cref="Primitives"/>), sized for the map (render units: a character is about 16 tall).</summary>
    public static Node3D MakePrimitive(string kind) => kind switch
    {
        "Sphere" => new MeshInstance3D { Name = "Sphere", Mesh = new SphereMesh { Radius = 5, Height = 10 } },
        "Cylinder" => new MeshInstance3D { Name = "Cylinder", Mesh = new CylinderMesh { TopRadius = 4, BottomRadius = 4, Height = 16 } },
        "Omni light" => new OmniLight3D { Name = "OmniLight", OmniRange = 60 },
        "Spot light" => new SpotLight3D { Name = "SpotLight", SpotRange = 80 },
        _ => new MeshInstance3D { Name = "Box", Mesh = new BoxMesh { Size = new Vector3(10, 10, 10) } },
    };

    /// <summary>
    /// Adds <paramref name="node"/> to the map, anchored to <paramref name="anchor"/> (it follows
    /// that part in the game; null: the map's root), at <paramref name="world"/> (render space);
    /// selects it.
    /// </summary>
    public void AddNode(MapEditRoot root, Node3D node, PartInfo? anchor, Transform3D world)
    {
        var parent = anchor != null && !root.IsGone(anchor) ? anchor.Node : root.AdditionsGroup!;
        node.Transform = parent.GlobalTransform.AffineInverse() * world;
        Undo.CreateAction($"Add {node.Name}" + (anchor != null ? $" at {anchor.Short(root.Model)}" : ""), UndoRedo.MergeMode.Disable, root);
        Undo.AddDoMethod(parent, Node.MethodName.AddChild, node, true);
        Undo.AddDoMethod(root, MapEditRoot.MethodName.Own, node);
        Undo.AddDoReference(node);
        Undo.AddUndoMethod(parent, Node.MethodName.RemoveChild, node);
        Undo.CommitAction();
        Select(node);
    }

    /// <summary>Where a new node at <paramref name="anchor"/> goes: the middle of the part's meshes (or its origin), in the anchor's axes.</summary>
    public static Transform3D PlaceAt(MapEditRoot root, PartInfo? anchor)
    {
        var frame = root.Frame(anchor);
        if (anchor == null || root.IsGone(anchor))
            return frame;
        Aabb? box = null;
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } m } mi && !n.HasMeta(MapEditRoot.EditorNode))
            {
                var b = mi.GlobalTransform * m.GetAabb();
                box = box is { } a ? a.Merge(b) : b;
            }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                if (root.PartOf(n.GetChild(i)) != null)
                    Walk(n.GetChild(i));
        }
        Walk(anchor.Node);
        return box is { } bb ? new Transform3D(frame.Basis, bb.GetCenter()) : frame;
    }

    public static void Select(Node node)
    {
        var sel = EditorInterface.Singleton.GetSelection();
        sel.Clear();
        if (node.IsInsideTree())
        {
            sel.AddNode(node);
            EditorInterface.Singleton.EditNode(node);
        }
    }

    // ---- the map's status ---------------------------------------------------------------------

    /// <summary>Sets <paramref name="key"/>'s hand-set status, render path and notes (one undo step; written at once).</summary>
    public void SetMapState(string key, MapStatus status, RenderPath path, string notes)
    {
        var res = MapStore.Find(key);
        var (s0, p0, n0) = res != null ? (res.Status, res.RenderPath, res.Notes) : (MapStatus.Original, RenderPath.Classic, "");
        if ((s0, p0, n0) == (status, path, notes))
            return;
        Undo.CreateAction($"Status of {key}: {status}", UndoRedo.MergeMode.Disable, this);
        Undo.AddDoMethod(this, MethodName.ApplyMapState, key, (int)status, (int)path, notes);
        Undo.AddUndoMethod(this, MethodName.ApplyMapState, key, (int)s0, (int)p0, n0);
        Undo.CommitAction();
    }

    /// <summary>Writes <paramref name="key"/>'s status, render path and notes into its overrides (the undo history calls it).</summary>
    public void ApplyMapState(string key, int status, int path, string notes)
    {
        var res = MapStore.GetOrNew(key);
        res.Status = (MapStatus)status;
        res.RenderPath = (RenderPath)path;
        res.Notes = notes;
        MapStore.Write(key, null, keepAdditions: true);
        _dock?.MapsChanged();
    }

    // ---- play ---------------------------------------------------------------------------------

    /// <summary>
    /// Saves the open map and runs the project's main scene at developer save
    /// <paramref name="save"/> (the launcher is skipped with <c>--devsave</c>): the game shows
    /// the project's overrides as saved. The arguments go into <c>editor/run/main_run_args</c>
    /// for this run only.
    /// </summary>
    public string Play(int save)
    {
        if (save <= 0)
            return "no developer save to start at";
        if (Current is { Built: true } root)
            SaveMap(root);
        const string setting = "editor/run/main_run_args";
        var old = ProjectSettings.GetSetting(setting, "").AsString();
        var args = PlayArguments(old, save);
        ProjectSettings.SetSetting(setting, args);
        EditorInterface.Singleton.PlayMainScene();
        GetTree().CreateTimer(2.0).Timeout += () => ProjectSettings.SetSetting(setting, old);
        return $"started the game with {args}";
    }

    /// <summary>The run's arguments: <paramref name="old"/> plus <c>-- --devsave=N</c> (and the editor's game folder when only the editor knows it).</summary>
    public string PlayArguments(string old, int save)
    {
        var user = new List<string> { $"--devsave={save}" };
        if (EditorClassic.FindGameDir().Source.StartsWith("the editor setting", System.StringComparison.Ordinal))
            user.Add($"--game-dir={Session.GameDir}");
        bool hasSep = old.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) is var parts && System.Array.IndexOf(parts, "--") >= 0;
        return $"{old} {(hasSep ? "" : "-- ")}{string.Join(' ', user)}".Trim();
    }
}
#endif
