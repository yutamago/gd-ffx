using System.Collections.Generic;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// A map's overrides applied to its loaded scene (docs/remake.md, "Overrides in the game"):
/// <see cref="Apply"/> edits the scene once (on the loading worker for a classic map, before it
/// is in the tree), then <see cref="StateView3D"/> keeps the edits each frame: the game places
/// its script-moved objects every frame (<see cref="Place"/> puts an offset on top and keeps a
/// hidden or removed part hidden), and <see cref="Update"/> holds the rest.
/// <list type="bullet">
/// <item>A part ID of one surface (<c>#&lt;node&gt;/&lt;k&gt;</c>) is first split off: the surface
/// becomes a child <see cref="MeshInstance3D"/> of its instance (same material, morph targets and
/// mesh fields but the instance number, so the game's object index skips it and its morph is its
/// own), and the instance's surface draws nothing (<see cref="NoDraw"/>). The split part follows
/// its instance (the game's placement, visibility); its own edits then apply to it.</item>
/// <item>Remove: the part and everything under it is hidden for good and its surfaces draw
/// nothing (its node stays, so the game's object index keeps its numbers).</item>
/// <item>Hide: hidden for good (the editor shows it greyed); Shown undoes an earlier layer's.</item>
/// <item>Offset: <c>W' = (B W.basis, W.origin + t)</c> on the game's world <c>W</c> each frame;
/// absolute: <c>W' = X</c>.</item>
/// <item>Replace: the part's surfaces draw nothing, the scene sits under it (follows its
/// placement and visibility) in render units.</item>
/// <item>Add: each node of a layer's additions scene under its anchor (<c>ffx_anchor</c>: a part
/// ID, else the map's root), in render units relative to the anchor's origin and axes.</item>
/// </list>
/// Nothing is freed, so the object index, morphs and the 2D layers built before stay valid; an
/// ID the map lacks is counted in <see cref="Unknown"/> and logged.
/// </summary>
public sealed class MapOverrideRuntime
{
    private sealed class Entry
    {
        public required Node3D Node;
        public bool Hidden;
        public TransformOp Mode;
        public Transform3D X = Transform3D.Identity;
        public Transform3D OrigLocal;
        public ulong PlacedFrame = ulong.MaxValue;
        public bool EverPlaced;
        public Transform3D LastBase;
    }

    private readonly Dictionary<Node3D, Entry> _entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>The holders of added and replacing nodes and their anchors (kept in render units each frame).</summary>
    private readonly List<(Node3D Holder, Node3D Anchor)> _holders = new();

    /// <summary>The map's parts and added nodes by ID (after the splits and additions).</summary>
    public PartIndex Index { get; }

    public MergedOverrides Overrides { get; }

    /// <summary>What was applied (for checks and the log).</summary>
    public int Removed { get; private set; }
    public int Hidden { get; private set; }
    public int Moved { get; private set; }
    public int Replaced { get; private set; }
    public int Added { get; private set; }
    public int Split { get; private set; }

    /// <summary>IDs edited or anchored to that the map lacks.</summary>
    public List<string> Unknown { get; } = new();

    /// <summary>The split parts (child mesh instances), by part ID.</summary>
    public Dictionary<string, MeshInstance3D> SplitParts { get; } = new();

    /// <summary>The replacements' wrappers, by part ID.</summary>
    public Dictionary<string, Node3D> Replacements { get; } = new();

    /// <summary>The added nodes, by ID.</summary>
    public Dictionary<string, Node3D> AddedNodes { get; } = new();

    private MapOverrideRuntime(PartIndex index, MergedOverrides overrides)
    {
        Index = index;
        Overrides = overrides;
    }

    /// <summary>
    /// The material of a surface that draws nothing: its vertices go outside the clip volume
    /// (no fragments, no shadow), so a surface is taken out without rebuilding its mesh.
    /// </summary>
    public static ShaderMaterial NoDraw => _noDraw ??= MakeNoDraw();

    private static ShaderMaterial? _noDraw;

    private static ShaderMaterial MakeNoDraw()
    {
        var shader = new Shader
        {
            Code = "shader_type spatial;\nrender_mode unshaded, cull_disabled, depth_draw_never;\n"
                + "void vertex() { POSITION = vec4(2.0, 2.0, 2.0, 1.0); }\nvoid fragment() { ALBEDO = vec3(0.0); }\n",
        };
        var m = new ShaderMaterial { Shader = shader, ResourceName = "ffx_no_draw" };
        return m;
    }

    /// <summary>Whether <paramref name="m"/> is <see cref="NoDraw"/>.</summary>
    public static bool IsNoDraw(Material? m) => m != null && ReferenceEquals(m, _noDraw);

    /// <summary>
    /// Applies <paramref name="overrides"/> to the map scene <paramref name="map"/> (not in the
    /// tree yet; a worker thread may do it). <paramref name="mapScale"/>: the scale the view
    /// gives the map's root (10 for a classic map, 1 for a remade one), so added and replacing
    /// scenes are in render units.
    /// </summary>
    public static MapOverrideRuntime Apply(Node3D map, MergedOverrides overrides, float mapScale)
    {
        lock (NoDrawLock)
            _ = NoDraw;
        var rt = new MapOverrideRuntime(PartIndex.Build(map), overrides);
        rt.ApplyAll(map, mapScale);
        GD.Print($"ffx: overrides: {overrides.Key} applied: {rt.Removed} removed, {rt.Hidden} hidden, {rt.Moved} moved, "
            + $"{rt.Replaced} replaced, {rt.Added} added ({rt.Split} part(s) split off)"
            + (rt.Unknown.Count > 0 ? $"; not on the map: {string.Join(", ", rt.Unknown)}" : ""));
        return rt;
    }

    private static readonly object NoDrawLock = new();

    private void ApplyAll(Node3D map, float mapScale)
    {
        // 1. one-surface parts with edits: split off their instance
        foreach (var (id, _) in Overrides.Parts)
            if (!Index.Nodes.ContainsKey(id) && Index.Surfaces.TryGetValue(id, out var s))
                SplitOff(id, s.Mesh, s.Surface);
        // 2. the layers' added nodes under their anchors
        foreach (var layer in Overrides.Layers)
            if (layer.Additions != null)
                AddFrom(map, layer, mapScale);
        // 3. the edits
        foreach (var (id, p) in Overrides.Parts)
        {
            if (!Index.Nodes.TryGetValue(id, out var node))
            {
                Unknown.Add(id);
                continue;
            }
            if (p.Removed)
            {
                NoDrawUnder(node);
                EntryOf(node).Hidden = true;
                node.Visible = false;
                Removed++;
                continue;
            }
            if (p.Hidden)
            {
                EntryOf(node).Hidden = true;
                node.Visible = false;
                Hidden++;
            }
            if (p.Moved)
            {
                var e = EntryOf(node);
                e.Mode = p.TransformMode;
                e.X = p.Transform;
                Moved++;
            }
            if (p.Replaced)
                ReplaceWith(id, node, p.Scene!, map, mapScale);
        }
        // the added and replacing nodes, findable too
        foreach (var (id, n) in AddedNodes)
            Index.Nodes.TryAdd(id, n);
    }

    private Entry EntryOf(Node3D node)
    {
        if (!_entries.TryGetValue(node, out var e))
            _entries[node] = e = new Entry { Node = node, OrigLocal = node.Transform };
        return e;
    }

    /// <summary>Surface <paramref name="surface"/> of <paramref name="mi"/> as a child of its own (see the class).</summary>
    private void SplitOff(string id, MeshInstance3D mi, int surface)
    {
        if (SplitSurface(mi, surface, id) is not { } part)
        {
            Unknown.Add(id);
            return;
        }
        mi.SetSurfaceOverrideMaterial(surface, NoDraw);
        Index.Nodes[id] = part;
        SplitParts[id] = part;
        Split++;
    }

    /// <summary>
    /// A new child <see cref="MeshInstance3D"/> of <paramref name="mi"/> (named <c>Part&lt;k&gt;</c>,
    /// at its origin) that draws surface <paramref name="surface"/> alone: its arrays, morph
    /// targets, material and drawn material, the mesh's extras but the instance number (so the
    /// game's object index skips it), tagged with part ID <paramref name="id"/>. The instance's
    /// own surface is left as it is (the game makes it draw nothing, the remaster editor drops
    /// the instance's mesh). Null when <paramref name="mi"/> has no such surface.
    /// </summary>
    public static MeshInstance3D? SplitSurface(MeshInstance3D mi, int surface, string id)
    {
        if (mi.Mesh is not ArrayMesh src || surface >= src.GetSurfaceCount())
            return null;
        var mesh = new ArrayMesh { BlendShapeMode = src.BlendShapeMode };
        for (int b = 0; b < src.GetBlendShapeCount(); b++)
            mesh.AddBlendShape(src.GetBlendShapeName(b));
        using (var arrays = src.SurfaceGetArrays(surface))
        {
            var blends = src.SurfaceGetBlendShapeArrays(surface);
            // the custom channels' formats (CUSTOM0..3) and the vertex flags travel with the arrays
            const long customBits = 0x1ffe000; // ARRAY_FORMAT_CUSTOM0..3 (3 bits each from bit 13)
            var format = (long)src.SurfaceGetFormat(surface);
            var flags = (Mesh.ArrayFormat)(format & (customBits | (long)Mesh.ArrayFormat.FlagUse2DVertices
                | (long)Mesh.ArrayFormat.FlagUse8BoneWeights));
            mesh.AddSurfaceFromArrays(src.SurfaceGetPrimitiveType(surface), arrays, blends, NoLods, flags);
        }
        mesh.SurfaceSetMaterial(0, src.SurfaceGetMaterial(surface));
        (MeshExtras.Of(src) with { Instance = null }).SetOn(mesh);
        var part = new MeshInstance3D
        {
            Name = $"Part{surface}",
            Mesh = mesh,
            CastShadow = mi.CastShadow,
            Layers = mi.Layers,
            SortingOffset = mi.SortingOffset,
        };
        part.SetSurfaceOverrideMaterial(0, mi.GetSurfaceOverrideMaterial(surface));
        PartIds.SetString(part, PartIds.Part, id);
        PartIds.SetStrings(part, PartIds.Parts, [id]);
        mi.AddChild(part);
        return part;
    }

    private static readonly Godot.Collections.Dictionary NoLods = new();

    /// <summary>Every original surface under <paramref name="node"/> draws nothing (not the added or replacing nodes).</summary>
    private static void NoDrawUnder(Node node)
    {
        if (node.HasMeta(Variants.Name(PartIds.Added)) || node.HasMeta(Variants.Name(PartIds.Replacement)))
            return;
        if (node is MeshInstance3D { Mesh: { } m } mi)
            for (int s = 0; s < m.GetSurfaceCount(); s++)
                mi.SetSurfaceOverrideMaterial(s, NoDraw);
        for (int i = 0, k = node.GetChildCount(); i < k; i++)
            NoDrawUnder(node.GetChild(i));
    }

    /// <summary>
    /// A holder under <paramref name="anchor"/> that undoes the anchor's scale within the map and
    /// the map's own (<paramref name="mapScale"/>): what it holds is in render units, at the
    /// anchor's origin and axes.
    /// </summary>
    private Node3D Holder(Node3D anchor, Node3D map, float mapScale, string name)
    {
        var scale = Vector3.One * mapScale;
        for (Node? n = anchor; n != null && n != map; n = n.GetParent())
            if (n is Node3D n3)
                scale *= n3.Transform.Basis.Scale;
        static float Inv(float v) => Mathf.Abs(v) > 1e-6f ? 1f / v : 1f;
        var holder = new Node3D { Name = name, Basis = Basis.FromScale(new Vector3(Inv(scale.X), Inv(scale.Y), Inv(scale.Z))) };
        anchor.AddChild(holder);
        _holders.Add((holder, anchor));
        return holder;
    }

    private void ReplaceWith(string id, Node3D node, PackedScene scene, Node3D map, float mapScale)
    {
        if (scene.Instantiate() is not Node3D inst)
        {
            GD.PrintErr($"ffx: overrides: {id}: the replacement {scene.ResourcePath} is not a 3D scene");
            return;
        }
        NoDrawUnder(node);
        var holder = Holder(node, map, mapScale, "Replacement");
        holder.SetMeta(Variants.Name(PartIds.Replacement), true);
        holder.AddChild(inst);
        Replacements[id] = holder;
        Replaced++;
    }

    private void AddFrom(Node3D map, OverrideLayer layer, float mapScale)
    {
        var root = layer.Additions!.Instantiate();
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
            var id = PartIds.AddedId(Overrides.Key, layer.Name, added.Name);
            var anchorId = PartIds.GetString(added, "ffx_anchor") is { Length: > 0 } a ? PartIds.Full(a, Overrides.Model) : null;
            Node3D anchor = map;
            if (anchorId != null)
            {
                if (Index.Nodes.TryGetValue(anchorId, out var n))
                    anchor = n;
                else if (Index.Surfaces.TryGetValue(anchorId, out var s))
                    anchor = s.Mesh; // a part shares its instance's frame
                else
                    Unknown.Add(anchorId);
            }
            var holder = Holder(anchor, map, mapScale, $"Added_{layer.Name}_{added.Name}");
            PartIds.SetString(added, PartIds.Added, id);
            holder.AddChild(added);
            AddedNodes[id] = added;
            Index.Nodes[id] = added;
            Added++;
        }
        root.Free();
    }

    // ---- each frame -------------------------------------------------------------------------

    /// <summary>
    /// The game places <paramref name="node"/> at <paramref name="t"/> (render space) and shows it
    /// when <paramref name="visible"/>: puts this map's edits of it on top. True when it has some.
    /// </summary>
    public bool Place(Node3D node, ref Transform3D t, ref bool visible)
    {
        if (!_entries.TryGetValue(node, out var e))
            return false;
        e.PlacedFrame = Engine.GetProcessFrames();
        e.EverPlaced = true;
        e.LastBase = t;
        if (e.Hidden)
            visible = false;
        t = Edit(e, t);
        return true;
    }

    /// <summary>
    /// The edits of the parts the game did not place this frame (main thread, the map in the
    /// tree): their placement from their parent's (or the game's last), their visibility.
    /// </summary>
    public void Update()
    {
        var frame = Engine.GetProcessFrames();
        foreach (var e in _entries.Values)
        {
            if (!GodotObject.IsInstanceValid(e.Node) || !e.Node.IsInsideTree())
                continue;
            if (e.PlacedFrame == frame)
                continue;
            if (e.Hidden && e.Node.Visible)
                e.Node.Visible = false;
            if (e.Mode is not (TransformOp.Offset or TransformOp.Absolute))
                continue;
            if (!e.EverPlaced)
                e.LastBase = e.Node.GetParent() is Node3D p ? p.GlobalTransform * e.OrigLocal : e.OrigLocal;
            var w = Edit(e, e.LastBase);
            if (!e.Node.GlobalTransform.IsEqualApprox(w))
                e.Node.GlobalTransform = w;
        }
        // the game's matrix of an anchor may scale it otherwise than the file: the holders stay
        // at the anchor's origin and axes, unscaled (render units)
        foreach (var (holder, anchor) in _holders)
        {
            if (!holder.IsInsideTree() || !holder.IsVisibleInTree())
                continue;
            var a = anchor.GlobalTransform;
            var want = new Transform3D(a.Basis.Orthonormalized(), a.Origin);
            if (!holder.GlobalTransform.IsEqualApprox(want))
                holder.GlobalTransform = want;
        }
    }

    private static Transform3D Edit(Entry e, Transform3D w) => e.Mode switch
    {
        TransformOp.Offset => new Transform3D(e.X.Basis * w.Basis, w.Origin + e.X.Origin),
        TransformOp.Absolute => e.X,
        _ => w,
    };

    /// <summary>The game's (or the file's) world of an edited node before its edit (for checks); null when it has no edits.</summary>
    public Transform3D? BaseOf(Node3D node) => _entries.TryGetValue(node, out var e) ? e.LastBase : null;

    /// <summary>Whether the game placed <paramref name="node"/> this frame or the one before (for checks).</summary>
    public bool PlacedRecently(Node3D node) =>
        _entries.TryGetValue(node, out var e) && e.EverPlaced && e.PlacedFrame + 1 >= Engine.GetProcessFrames();

    /// <summary>
    /// The node a part ID stands for: the part's own node (an instance, a node, a split-off or
    /// added part), else the instance one surface of which it is (null: not on the map).
    /// </summary>
    public Node3D? NodeOf(string id) =>
        Index.Nodes.TryGetValue(id, out var n) ? n : Index.Surfaces.TryGetValue(id, out var s) ? s.Mesh : null;

    /// <summary>Whether <paramref name="node"/> is held hidden (removed or hidden).</summary>
    public bool HeldHidden(Node3D node) => _entries.TryGetValue(node, out var e) && e.Hidden;
}
