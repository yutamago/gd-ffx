#if TOOLS
using System.Collections.Generic;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>What a part is: the model (the import's root), a <c>PNode</c>, an instance (<c>/*</c>) or one segment of one (<c>/k</c>).</summary>
public enum PartKind
{
    Model,
    Node,
    Instance,
    Segment,
}

/// <summary>A classic part in the edit scene: its ID, node, classic parent and placement, and its edits not held by the node itself.</summary>
public sealed class PartInfo
{
    public required string Id { get; init; }
    public required Node3D Node { get; init; }
    public required PartKind Kind { get; init; }

    /// <summary>The part it hangs under in the import (null: the model).</summary>
    public PartInfo? Parent { get; init; }

    /// <summary>
    /// Its transform relative to <see cref="Parent"/> as the game places it: the import's (the
    /// file's), or where the game's scripts had it in the map's capture (<see cref="GamePlaced"/>).
    /// </summary>
    public Transform3D BaseLocal { get; set; }

    /// <summary>Placed where the game had it (the map's capture, <see cref="GameDataEdit.PlaceFromCapture"/>), not at its file place.</summary>
    public bool GamePlaced { get; set; }

    /// <summary>The surfaces it has (an instance: its segments).</summary>
    public int Surfaces { get; init; }

    /// <summary>Its entry in the project's overrides when the map was opened (kept: fields the editor does not show).</summary>
    public PartOverride? Loaded { get; set; }

    /// <summary>Removed by the editor (<see cref="MapEditRoot.ParkPart"/>): out of the tree, held here.</summary>
    public bool Parked { get; set; }
    public Node? ParkedParent { get; set; }
    public int ParkedIndex { get; set; }

    /// <summary>The scene replacing it (empty: none).</summary>
    public string Replacement { get; set; } = "";

    public Node3D? Preview { get; set; }

    /// <summary>How a move is written: <see cref="TransformOp.Offset"/> (the default) or <see cref="TransformOp.Absolute"/> (kept from the file).</summary>
    public TransformOp Mode { get; set; } = TransformOp.Offset;

    public string Note { get; set; } = "";

    /// <summary>The model-relative ID (<c>#...</c>) when it is one of the model's parts.</summary>
    public string Short(string? model) => model != null && Id.StartsWith(model + "#", System.StringComparison.Ordinal) ? Id[model.Length..] : Id;
}

/// <summary>A part's edits as the scene has them now (<see cref="MapEditRoot.MarksOf"/>).</summary>
[System.Flags]
public enum PartMarks
{
    None = 0,
    Moved = 1,
    Hidden = 2,
    Removed = 4,
    Replaced = 8,
    Noted = 16,
}

/// <summary>
/// The root of a map's edit scene (<c>res://remaster/edit/&lt;level&gt;.tscn</c>, made by the
/// plugin; docs/remaster-editor.md). The file holds this node and its level only: when the
/// editor opens it, the root builds the classic map from the player's import (every part a node
/// tagged with its ID, a multi-segment instance's segments as child nodes of their own, the
/// game's classic materials), then the project's overrides and additions over it. Nothing of
/// the classic map is ever saved with the scene: on save its children are kept out
/// (<see cref="Node.NotificationEditorPreSave"/>), and the plugin writes the edits as
/// <c>overrides.tres</c> and <c>additions.tscn</c> instead (<see cref="MapEditSave"/>).
/// <para>The scene is in render space: the root at the origin, the import scaled by 10 under
/// it as the game places it (<see cref="ClassicLibrary.MapScale"/>).</para>
/// </summary>
[Tool]
public partial class MapEditRoot : Node3D
{
    public const string EditDir = "res://remaster/edit";

    /// <summary>Metadata on the editor's own nodes (the additions group, previews): never a part, never an addition.</summary>
    public const string EditorNode = "ffx_editor";

    /// <summary>The game's level (<c>/map/bsil/bsil00/</c>).</summary>
    [Export] public string Level { get; set; } = "";

    public string Key => PartIds.LevelKey(Level);

    /// <summary>The ID relative part IDs (<c>#...</c>) are relative to.</summary>
    public string? Model => PartIds.ModelIdOf(Level);

    public bool Built { get; private set; }

    public string Status { get; private set; } = "waiting for the archive";

    /// <summary>The import's root (scaled by 10).</summary>
    public Node3D? Map { get; private set; }

    /// <summary>The map's remastered lighting as nodes (R3; null until built, and on a 2D-layer map).</summary>
    public LightingEdit? Lighting { get; private set; }

    /// <summary>The game's data on the map as read-only nodes (R5; null until built).</summary>
    public GameDataEdit? GameData { get; private set; }

    /// <summary>A map of 2D layers only: no classic 3D map (<see cref="Map"/> null), the game's data only.</summary>
    public bool Is2D { get; private set; }

    /// <summary>Where added nodes without an anchor go.</summary>
    public Node3D? AdditionsGroup { get; private set; }

    /// <summary>The parts by full ID, in the import's order (parents before children).</summary>
    public readonly Dictionary<string, PartInfo> Parts = new();
    public readonly List<PartInfo> Order = new();
    private readonly Dictionary<Node, PartInfo> _byNode = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every node the import made (parts and the rest, e.g. an animation player): never an added node.</summary>
    private readonly HashSet<Node> _imported = new(ReferenceEqualityComparer.Instance);

    /// <summary>The project's entries for IDs this map does not have (kept as they are).</summary>
    public readonly List<PartOverride> Foreign = new();

    /// <summary>Anchors of added nodes that are not on the map (kept as they are).</summary>
    public int UnknownAnchors { get; private set; }

    private readonly List<(MeshInstance3D Mesh, int Surface, Material? Material)> _previewMaterials = new();
    private readonly List<Node> _unowned = new();

    /// <summary>Each added node's place in its anchor's frame as the file had it (a save keeps it while the scene still shows it).</summary>
    private readonly Dictionary<Node, Transform3D> _loadedPlace = new(ReferenceEqualityComparer.Instance);

    /// <summary>The place <paramref name="added"/> had in the additions file when the map opened (null: added since).</summary>
    public Transform3D? LoadedPlace(Node added) => _loadedPlace.TryGetValue(added, out var t) ? t : null;

    public static string EditScenePath(string key) => $"{EditDir}/{key}.tscn";

    public override void _Ready()
    {
        if (!Engine.IsEditorHint())
            return;
        // a C# reload made a new script instance: what was built is gone from it
        if (GetNodeOrNull("ClassicMap") is { } stale)
        {
            RemoveChild(stale);
            stale.QueueFree();
        }
        foreach (var name in new[] { "Additions", "Lighting", "LightingPreview", "RemasterLighting", "GameData" })
            if (GetNodeOrNull(name) is { } staleAdd && staleAdd.HasMeta(EditorNode))
            {
                RemoveChild(staleAdd);
                staleAdd.QueueFree();
            }
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (!Engine.IsEditorHint())
            return;
        if (!Built)
            TryBuild();
        else
        {
            KeepPreviews();
            Lighting?.Tick(RemasterPlugin.Instance?.EditorRemastered == true, delta);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationEditorPreSave)
        {
            // nothing of the classic map in the scene file: the root's children are kept out
            _unowned.Clear();
            for (int i = 0, k = GetChildCount(); i < k; i++)
                if (GetChild(i) is { } c && c.Owner == this)
                {
                    c.Owner = null;
                    _unowned.Add(c);
                }
        }
        else if (what == NotificationEditorPostSave)
        {
            foreach (var c in _unowned)
                if (IsInstanceValid(c) && c.GetParent() == this)
                    c.Owner = this;
            _unowned.Clear();
        }
        else if (what == NotificationPredelete)
        {
            foreach (var p in Order)
                if (p.Parked && IsInstanceValid(p.Node))
                    p.Node.Free();
        }
    }

    private void TryBuild()
    {
        if (RemasterPlugin.Instance?.Session is not { IsOpen: true } session)
        {
            Status = "the archive is not open (the dock: Open)";
            return;
        }
        var classic = session.Classic!;
        session.Poll();
        if (session.Catalogue?.Find(Level) is { Model3D: null, Kind: "field" })
        {
            Build2D(session);
            return;
        }
        if (classic.Path("map", Level) is not { } path)
        {
            Status = classic.Failed("map", Level) ? $"{Level} did not import (see the log)" : $"importing {Level}...";
            return;
        }
        var t0 = Time.GetTicksMsec();
        var map = ClassicLibrary.Load(path, $"PS3Data{Level}tex/");
        if (map == null)
        {
            Status = $"{path} did not load";
            Built = true;
            return;
        }
        ClassicMaterials.FixLayers(map);
        map.Name = "ClassicMap";
        map.Scale = Vector3.One * ClassicLibrary.MapScale;
        AddChild(map);
        Register(map, null);
        MarkImported(map);
        AdditionsGroup = new Node3D { Name = "Additions" };
        AdditionsGroup.SetMeta(EditorNode, true);
        AddChild(AdditionsGroup);
        AdditionsGroup.Owner = this;
        Map = map;
        // the parts the game's scripts move, where the game had them (R5), before the overrides
        var capture = MapCapture.Read(Level);
        var (placed, morphed) = capture != null ? GameDataEdit.PlaceFromCapture(this, capture) : (0, 0);
        ApplyProject();
        Lighting = LightingEdit.Build(this);
        GameData = GameDataEdit.Build(this, session.Game, session.Catalogue, capture, placed, morphed);
        Built = true;
        Status = $"{Key}: {Parts.Count} parts ({Time.GetTicksMsec() - t0} ms)";
        GD.Print($"ffx remaster: opened {Level} for editing: {Parts.Count} parts, {CountAdded()} added node(s), "
            + $"{Foreign.Count} entr(ies) for IDs not on the map, {UnknownAnchors} anchor(s) not on it ({Time.GetTicksMsec() - t0} ms)");
        RemasterPlugin.Instance?.MapBuilt(this);
    }

    /// <summary>A map of 2D layers only: no 3D map to edit; the game's data (walk mesh, triggers, cameras) and the additions group.</summary>
    private void Build2D(EditorClassic session)
    {
        var t0 = Time.GetTicksMsec();
        Is2D = true;
        AdditionsGroup = new Node3D { Name = "Additions" };
        AdditionsGroup.SetMeta(EditorNode, true);
        AddChild(AdditionsGroup);
        AdditionsGroup.Owner = this;
        GameData = GameDataEdit.Build(this, session.Game, session.Catalogue);
        Built = true;
        Status = $"{Key}: 2D layers only (the game's data shown; nothing 3D to edit)";
        GD.Print($"ffx remaster: opened {Level} (2D layers only): {GameData.Describe()} ({Time.GetTicksMsec() - t0} ms)");
        RemasterPlugin.Instance?.MapBuilt(this);
    }

    /// <summary>Tags <paramref name="n"/> and everything under it; splits multi-segment instances into a node a segment.</summary>
    private void Register(Node n, PartInfo? parent)
    {
        var info = parent;
        if (n is Node3D n3 && PartIds.Of(n3) is { } id && !Parts.ContainsKey(id))
        {
            var parts = n3 is MeshInstance3D mi0 ? PartIds.GetStrings(mi0, PartIds.Parts) : null;
            info = Add(new PartInfo
            {
                Id = id,
                Node = n3,
                Kind = parent == null ? PartKind.Model : id.EndsWith("/*") ? PartKind.Instance : IsSegment(id) ? PartKind.Segment : PartKind.Node,
                Parent = parent,
                BaseLocal = n3.Transform,
                Surfaces = parts?.Length ?? 0,
            });
            if (n3 is MeshInstance3D { Mesh: { } } mi && parts is { Length: > 0 })
            {
                // every segment its own node, so each can be picked (a click picks the segment, the
            // scene dock its instance); the instance keeps its ID and no mesh
                for (int k = 0; k < parts.Length; k++)
                    if (MapOverrideRuntime.SplitSurface(mi, k, parts[k]) is { } seg)
                    {
                        seg.Name = k.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        Add(new PartInfo
                        {
                            Id = parts[k], Node = seg, Kind = PartKind.Segment, Parent = info, BaseLocal = seg.Transform, Surfaces = 1,
                        });
                    }
                mi.Mesh = null;
                return;
            }
        }
        for (int i = 0, k = n.GetChildCount(); i < k; i++)
            Register(n.GetChild(i), info);
    }

    /// <summary>Notes <paramref name="n"/> and its descendants as the import's and owns them (the glTF scene's root owned them).</summary>
    private void MarkImported(Node n)
    {
        _imported.Add(n);
        n.Owner = this;
        for (int i = 0, k = n.GetChildCount(); i < k; i++)
            MarkImported(n.GetChild(i));
    }

    /// <summary>Whether the import made <paramref name="n"/> (a part or not).</summary>
    public bool IsImported(Node n) => _imported.Contains(n);

    private PartInfo Add(PartInfo p)
    {
        Parts[p.Id] = p;
        Order.Add(p);
        _byNode[p.Node] = p;
        return p;
    }

    private static bool IsSegment(string id)
    {
        int slash = id.LastIndexOf('/');
        int hash = id.LastIndexOf('#');
        return slash > hash && hash >= 0 && slash + 1 < id.Length && char.IsAsciiDigit(id[slash + 1]);
    }

    /// <summary>
    /// Sets this root as the owner of <paramref name="n"/> and its descendants whose owner is not
    /// in this scene (none, or the scene they were loaded from); a node of an instanced scene
    /// keeps that instance as its owner. Not the editor's previews.
    /// </summary>
    public void Own(Node n)
    {
        if (n.HasMeta(EditorNode) && n != AdditionsGroup)
            return;
        if (n != this && n.Owner != this && (n.Owner == null || !IsAncestorOf(n.Owner)))
            n.Owner = this;
        for (int i = 0, k = n.GetChildCount(); i < k; i++)
            Own(n.GetChild(i));
    }

    // ---- the project's overrides over the classic map ---------------------------------------

    private void ApplyProject()
    {
        var res = MapStore.Find(Key);
        if (res == null)
            return;
        var byId = new Dictionary<string, PartOverride>();
        foreach (var p in res.Parts)
            if (p != null && !string.IsNullOrWhiteSpace(p.Id))
            {
                var full = PartIds.Full(p.Id.Trim(), Model);
                if (Parts.ContainsKey(full))
                    byId[full] = p;
                else
                    Foreign.Add(p);
            }
        var removed = new List<PartInfo>();
        // parents first: a child's classic placement hangs on its parent's edited one
        foreach (var info in Order)
        {
            if (!byId.TryGetValue(info.Id, out var p))
                continue;
            info.Loaded = p;
            info.Note = p.Note;
            if (p.TransformMode is TransformOp.Offset or TransformOp.Absolute)
            {
                info.Mode = p.TransformMode;
                var b = BaseWorld(info);
                info.Node.GlobalTransform = p.TransformMode == TransformOp.Absolute
                    ? p.Transform
                    : new Transform3D(p.Transform.Basis * b.Basis, b.Origin + p.Transform.Origin);
            }
            if (p.Visibility == VisibilityOp.Hidden)
                info.Node.Visible = false;
            if (p.Replace == ReplaceOp.Scene && p.Scene is { ResourcePath: { Length: > 0 } scene })
                SetReplacementNow(info, scene);
            if (p.Remove == RemoveOp.Remove)
                removed.Add(info);
        }
        LoadAdditions(res);
        foreach (var info in removed)
            Park(info);
    }

    private void LoadAdditions(MapOverrides res)
    {
        var scene = res.Additions;
        if (scene == null && MapStore.HasAdditions(Key))
            scene = ResourceLoader.Load(MapStore.AdditionsPathOf(Key), "", ResourceLoader.CacheMode.Replace) as PackedScene;
        if (scene?.Instantiate() is not { } root)
            return;
        var children = new List<Node>();
        for (int i = 0, k = root.GetChildCount(); i < k; i++)
            children.Add(root.GetChild(i));
        foreach (var c in children)
        {
            root.RemoveChild(c);
            if (c is not Node3D added)
            {
                c.Free();
                continue;
            }
            var saved = added.Transform;
            PartInfo? anchor = null;
            if (PartIds.GetString(added, "ffx_anchor") is { Length: > 0 } a)
            {
                if (Parts.TryGetValue(PartIds.Full(a, Model), out var found))
                {
                    anchor = found;
                    added.RemoveMeta("ffx_anchor");
                }
                else
                    UnknownAnchors++;
            }
            (anchor?.Node ?? AdditionsGroup!).AddChild(added);
            added.GlobalTransform = Frame(anchor) * saved;
            _loadedPlace[added] = saved;
            Own(added);
        }
        root.Free();
    }

    // ---- placement --------------------------------------------------------------------------

    /// <summary>The world a part has without its own edit: its classic parent's world now times its classic local.</summary>
    public Transform3D BaseWorld(PartInfo p)
    {
        if (p.Parent == null)
            return GlobalTransform * new Transform3D(Basis.FromScale(Vector3.One * ClassicLibrary.MapScale), Vector3.Zero);
        var parent = p.Parent.Node.IsInsideTree() ? p.Parent.Node.GlobalTransform : BaseWorld(p.Parent);
        return parent * p.BaseLocal;
    }

    /// <summary>
    /// An anchor's frame as the game holds added and replacing nodes: the part's origin and axes,
    /// unscaled (render units); null: the map's root in render space.
    /// </summary>
    public Transform3D Frame(PartInfo? anchor)
    {
        if (anchor == null || !anchor.Node.IsInsideTree())
            return GlobalTransform;
        var a = anchor.Node.GlobalTransform;
        return new Transform3D(a.Basis.Orthonormalized(), a.Origin);
    }

    // ---- what is what -----------------------------------------------------------------------

    public PartInfo? PartOf(Node? n) => n != null && _byNode.TryGetValue(n, out var p) ? p : null;

    /// <summary>Whether <paramref name="p"/> is out of the map: removed by the editor or deleted in the scene (not under a removed part).</summary>
    public bool IsRemoved(PartInfo p)
    {
        if (p.Parked)
            return true;
        bool gone = !IsInstanceValid(p.Node) || !p.Node.IsInsideTree();
        return gone && (p.Parent == null || !IsGone(p.Parent));
    }

    /// <summary>Whether <paramref name="p"/> is not in the scene (removed itself or under a removed part).</summary>
    public bool IsGone(PartInfo p) => p.Parked || !IsInstanceValid(p.Node) || !p.Node.IsInsideTree();

    /// <summary>The edits a part has now.</summary>
    public PartMarks MarksOf(PartInfo p)
    {
        var m = PartMarks.None;
        if (IsRemoved(p))
            m |= PartMarks.Removed;
        else if (!IsGone(p))
        {
            if (!p.Node.Visible)
                m |= PartMarks.Hidden;
            if (!p.Node.GlobalTransform.IsEqualApprox(BaseWorld(p)))
                m |= PartMarks.Moved;
        }
        if (p.Replacement.Length > 0)
            m |= PartMarks.Replaced;
        if (p.Note.Length > 0)
            m |= PartMarks.Noted;
        return m;
    }

    /// <summary>
    /// The added nodes now: each node of the scene owned by this root that is neither a part nor
    /// the editor's, whose parent is a part, the root or the additions group (a node under an
    /// added one goes with it), with its anchor (null: the map's root).
    /// </summary>
    public List<(Node3D Node, PartInfo? Anchor)> Added()
    {
        var r = new List<(Node3D, PartInfo?)>();
        void Walk(Node n)
        {
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
            {
                var c = n.GetChild(i);
                if (c.HasMeta(EditorNode))
                {
                    if (c == AdditionsGroup)
                        Walk(c);
                    continue;
                }
                if (_imported.Contains(c))
                {
                    Walk(c);
                    continue;
                }
                if (c.Owner != this || c is not Node3D c3)
                    continue;
                r.Add((c3, PartOf(n)));
            }
        }
        Walk(this);
        return r;
    }

    private int CountAdded() => Added().Count;

    // ---- edits (undoable: the plugin's actions call these) ----------------------------------

    /// <summary>Removes part <paramref name="id"/> from the map (held, so it can come back).</summary>
    public void ParkPart(string id)
    {
        if (Parts.TryGetValue(id, out var p) && !p.Parked && p.Node.IsInsideTree())
            Park(p);
    }

    private void Park(PartInfo p)
    {
        if (p.Node.GetParent() is not { } parent)
            return;
        p.ParkedParent = parent;
        p.ParkedIndex = p.Node.GetIndex();
        parent.RemoveChild(p.Node);
        p.Parked = true;
    }

    /// <summary>Puts part <paramref name="id"/> back (removed by the editor, or deleted in the scene and not freed yet).</summary>
    public void UnparkPart(string id)
    {
        if (!Parts.TryGetValue(id, out var p) || !IsInstanceValid(p.Node) || p.Node.IsInsideTree())
            return;
        var parent = p.ParkedParent ?? p.Parent?.Node ?? (Node)this;
        if (!IsInstanceValid(parent) || !parent.IsInsideTree())
            return;
        parent.AddChild(p.Node);
        if (p.Parked && p.ParkedIndex < parent.GetChildCount())
            parent.MoveChild(p.Node, p.ParkedIndex);
        p.Parked = false;
        p.ParkedParent = null;
        Own(p.Node);
    }

    /// <summary>Replaces part <paramref name="id"/> by the scene at <paramref name="scene"/> (empty: the classic part again).</summary>
    public void SetReplacement(string id, string scene)
    {
        if (Parts.TryGetValue(id, out var p))
            SetReplacementNow(p, scene);
    }

    private void SetReplacementNow(PartInfo p, string scene)
    {
        if (p.Preview != null)
        {
            if (IsInstanceValid(p.Preview))
                p.Preview.QueueFree();
            p.Preview = null;
            for (int i = _previewMaterials.Count - 1; i >= 0; i--)
                if (IsUnder(_previewMaterials[i].Mesh, p.Node))
                {
                    var (mesh, s, m) = _previewMaterials[i];
                    if (IsInstanceValid(mesh))
                        mesh.SetSurfaceOverrideMaterial(s, m);
                    _previewMaterials.RemoveAt(i);
                }
        }
        p.Replacement = scene;
        if (scene.Length == 0)
            return;
        if (ResourceLoader.Load(scene) is not PackedScene packed || packed.Instantiate() is not Node3D inst)
        {
            GD.PrintErr($"ffx remaster: {scene} is not a 3D scene");
            return;
        }
        NoDrawUnder(p.Node, p.Node);
        var holder = new Node3D { Name = "Replacement" };
        holder.SetMeta(EditorNode, true);
        holder.AddChild(inst);
        p.Node.AddChild(holder);
        p.Preview = holder;
        KeepPreviews();
    }

    private static bool IsUnder(Node n, Node top)
    {
        for (Node? x = n; x != null; x = x.GetParent())
            if (x == top)
                return true;
        return false;
    }

    /// <summary>The classic surfaces under <paramref name="n"/> draw nothing (not added nodes, not previews), as the game does for a replaced part.</summary>
    private void NoDrawUnder(Node n, Node top)
    {
        if (n != top && (n.HasMeta(EditorNode) || !_imported.Contains(n)))
            return;
        if (n is MeshInstance3D { Mesh: { } m } mi)
            for (int s = 0; s < m.GetSurfaceCount(); s++)
            {
                _previewMaterials.Add((mi, s, mi.GetSurfaceOverrideMaterial(s)));
                mi.SetSurfaceOverrideMaterial(s, MapOverrideRuntime.NoDraw);
            }
        for (int i = 0, k = n.GetChildCount(); i < k; i++)
            NoDrawUnder(n.GetChild(i), top);
    }

    /// <summary>The replacements sit at their part's origin and axes, unscaled, as in the game.</summary>
    private void KeepPreviews()
    {
        foreach (var p in Order)
            if (p.Preview is { } h && IsInstanceValid(h) && h.IsInsideTree())
            {
                var want = Frame(p);
                if (!h.GlobalTransform.IsEqualApprox(want))
                    h.GlobalTransform = want;
            }
    }

    /// <summary>Sets part <paramref name="id"/>'s note.</summary>
    public void SetPartNote(string id, string note)
    {
        if (Parts.TryGetValue(id, out var p))
            p.Note = note;
    }

    /// <summary>Sets part <paramref name="id"/>'s world transform (render space) and visibility (the revert's undo).</summary>
    public void SetPartState(string id, Transform3D world, bool visible)
    {
        if (!Parts.TryGetValue(id, out var p) || !p.Node.IsInsideTree())
            return;
        p.Node.GlobalTransform = world;
        p.Node.Visible = visible;
    }

    /// <summary>Part <paramref name="id"/> as the classic import has it: placed by the game, shown, not replaced (not its removal: <see cref="UnparkPart"/>).</summary>
    public void ResetPart(string id)
    {
        if (!Parts.TryGetValue(id, out var p) || !p.Node.IsInsideTree())
            return;
        p.Node.GlobalTransform = BaseWorld(p);
        p.Node.Visible = true;
    }
}
#endif
