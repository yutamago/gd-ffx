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
    private TextureDock? _textures;
    private CheckButton? _lookToggle;
    private PartInspector? _inspector;
    private EditorSelfTest? _test;

    /// <summary>The map edit scene being edited (null: another scene, or none).</summary>
    public MapEditRoot? Current => EditorInterface.Singleton.GetEditedSceneRoot() as MapEditRoot;

    public MapBrowserDock? Dock => _dock;

    public TextureDock? Textures => _textures;

    /// <summary>The where-used index (built after the archive opens, cached in <c>user://</c>).</summary>
    public WhereUsed Uses { get; } = new();

    public override void _EnterTree()
    {
        Instance = this;
        _dock = new MapBrowserDock(this);
        AddDock(_dock);
        TextureStore.Attach();
        ComfyUpscaler.RegisterSettings();
        var fs = EditorInterface.Singleton.GetResourceFilesystem();
        fs.ResourcesReimported += OnReimported;
        fs.FilesystemChanged += OnFilesystemChanged;
        _textures = new TextureDock(this);
        AddDock(_textures);
        // the 3D view's compare: the open map classic or remastered (its render path when opened)
        _lookToggle = new CheckButton { Text = "Remastered", TooltipText = "Draw the open map remastered (PBR materials, the map's lighting) or classic" };
        _lookToggle.Toggled += on => SetEditorLook(on);
        AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _lookToggle);
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
        var efs = EditorInterface.Singleton.GetResourceFilesystem();
        efs.ResourcesReimported -= OnReimported;
        efs.FilesystemChanged -= OnFilesystemChanged;
        if (_inspector != null)
            RemoveInspectorPlugin(_inspector);
        if (_lookToggle != null)
        {
            RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _lookToggle);
            _lookToggle.QueueFree();
        }
        if (_textures != null)
        {
            RemoveDock(_textures);
            _textures.QueueFree();
        }
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
        if (TextureStore.Poll())
        {
            RemasterTextures.Invalidate();
            TexturesChanged();
        }
        if (Uses.Building && Uses.Step())
            _textures?.UsesChanged();
        _textures?.Tick(delta);
        _test?.Step(delta);
    }

    /// <summary>Opens the player's archive (<paramref name="dir"/>: a folder chosen in the dock) and shows the catalogue.</summary>
    public bool OpenArchive(string? dir = null)
    {
        bool ok = Session.Open(dir);
        _dock?.ArchiveChanged();
        if (ok)
            Uses.Start(Session);
        _textures?.UsesChanged();
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
        // the classic import asked for now (the scene waits for it; a 2D-layer map has none)
        if (entry.Model3D != null)
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

    /// <summary>The edit scene finished building (the dock shows its parts; drawn as its render path says).</summary>
    public void MapBuilt(MapEditRoot root)
    {
        _dock?.CurrentChanged();
        bool remastered = MapStore.Find(root.Key)?.RenderPath == RenderPath.Remastered;
        _lookToggle?.SetPressedNoSignal(remastered);
        SetEditorLook(remastered);
        _textures?.MapChanged();
        _textures?.SelectPendingPart();
    }

    // ---- the render path in the editor --------------------------------------------------------

    /// <summary>Whether the open map is drawn remastered in the 3D view.</summary>
    public bool EditorRemastered => _lookToggle?.ButtonPressed == true;

    /// <summary>Draws the open map remastered or classic (the 3D view's toggle; no undo: a view, not an edit).</summary>
    public int SetEditorLook(bool remastered)
    {
        if (_lookToggle != null && _lookToggle.ButtonPressed != remastered)
            _lookToggle.SetPressedNoSignal(remastered);
        if (Current is not { Built: true, Map: { } map })
            return 0;
        int n = MapLook.Of(map).Set(remastered, Current.Lighting?.Res.BlendedParts == BlendedLook.Lit);
        if (n > 0)
            GD.Print($"ffx remaster: {Current.Key} drawn {(remastered ? "remastered" : "classic")} in the editor ({n} surface(s))");
        return n;
    }

    // ---- textures and materials, each one undo step --------------------------------------------

    /// <summary>Sets <paramref name="id"/>'s entry in the project's texture layer (an empty entry removes it): one undo step.</summary>
    public void SetTexture(string id, RemasterTexture after, string action) => SetTextures([(id, after)], action);

    /// <summary>Sets several textures' entries as one undo step (the batch).</summary>
    public void SetTextures(IReadOnlyList<(string Id, RemasterTexture After)> edits, string action)
    {
        if (edits.Count == 0)
            return;
        Undo.CreateAction(action, UndoRedo.MergeMode.Disable, this);
        // the entries as dictionaries: a resource without a path would count as the edited scene's
        // (Godot's per-scene histories), the action is the global history's
        foreach (var (id, after) in edits)
        {
            Undo.AddDoMethod(this, MethodName.ApplyTexture, id, TextureStore.ToDictionary(after));
            Undo.AddUndoMethod(this, MethodName.ApplyTexture, id, TextureStore.ToDictionary(TextureStore.Copy(id)));
        }
        Undo.CommitAction();
    }

    /// <summary>Writes <paramref name="id"/>'s entry (the undo history calls it) and shows it on the open map.</summary>
    public void ApplyTexture(string id, Godot.Collections.Dictionary entry)
    {
        TextureStore.Put(id, TextureStore.FromDictionary(id, entry));
        TexturesChanged();
    }

    /// <summary>Godot imported files: the project's textures among them load imported now (they loaded from their PNGs until then).</summary>
    private void OnReimported(string[] files)
    {
        var dir = TextureStore.Dir + "/";
        if (System.Array.Exists(files, f => f.StartsWith(dir, System.StringComparison.Ordinal)))
        {
            RemasterTextures.Invalidate();
            TexturesChanged();
        }
    }

    private void OnFilesystemChanged()
    {
        // a scan's first import of the project's new PNGs: load them imported now
        if (TextureStore.Set.Textures.Count > 0)
        {
            RemasterTextures.Invalidate();
            TexturesChanged();
        }
    }

    /// <summary>The project's textures changed: the open map binds them again (classic and lit), the docks list them again.</summary>
    public void TexturesChanged()
    {
        if (Current is { Built: true, Map: { } map })
        {
            RemasterTextures.Refresh(map);
            if (MapLook.Find(map) is { } look)
                look.Set(look.Remastered, look.LitBlended);
        }
        _textures?.TexturesChanged();
    }

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
    public static readonly string[] Primitives = ["Box", "Sphere", "Cylinder", "Omni light", "Spot light", "Directional light", "Reflection probe", "Fog volume"];

    /// <summary>A new node of kind <paramref name="kind"/> (<see cref="Primitives"/>), sized for the map (render units: a character is about 16 tall).</summary>
    public static Node3D MakePrimitive(string kind) => kind switch
    {
        "Sphere" => new MeshInstance3D { Name = "Sphere", Mesh = new SphereMesh { Radius = 5, Height = 10 } },
        "Cylinder" => new MeshInstance3D { Name = "Cylinder", Mesh = new CylinderMesh { TopRadius = 4, BottomRadius = 4, Height = 16 } },
        // lights with shadows; they light the remastered path only (the classic shaders are unshaded)
        "Omni light" => new OmniLight3D { Name = "OmniLight", OmniRange = 60, ShadowEnabled = true },
        "Spot light" => new SpotLight3D { Name = "SpotLight", SpotRange = 80, ShadowEnabled = true },
        "Directional light" => new DirectionalLight3D { Name = "DirectionalLight", ShadowEnabled = true, LightEnergy = 0.5f },
        // a box probe for reflections around a spot (render units)
        "Reflection probe" => new ReflectionProbe { Name = "ReflectionProbe", Size = new Vector3(200, 100, 200), BoxProjection = true, UpdateMode = ReflectionProbe.UpdateModeEnum.Once },
        // Godot's local volumetric fog (Forward+, the map's volumetric fog on)
        "Fog volume" => new FogVolume { Name = "FogVolume", Size = new Vector3(100, 40, 100), Material = new FogMaterial { Density = 0.05f } },
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

    /// <summary>Adds the lighting volume <paramref name="node"/> at <paramref name="world"/> (one undo step); selects it.</summary>
    public void AddVolume(MapEditRoot root, VolumeNode node, Transform3D world)
    {
        var group = root.Lighting!.Volumes;
        node.Transform = group.GlobalTransform.AffineInverse() * world;
        Undo.CreateAction($"Add the lighting volume {node.Name}", UndoRedo.MergeMode.Disable, root);
        Undo.AddDoMethod(group, Node.MethodName.AddChild, node, true);
        Undo.AddDoMethod(root, MapEditRoot.MethodName.Own, node);
        Undo.AddDoReference(node);
        Undo.AddUndoMethod(group, Node.MethodName.RemoveChild, node);
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
        // the open map follows its render path
        if (Current is { Built: true } root && root.Key == key)
            SetEditorLook((RenderPath)path == RenderPath.Remastered);
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
