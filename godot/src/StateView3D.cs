using System.Linq;
using System.Collections.Generic;
using GdFfx.Api;
using GdFfx.Remake;
using Godot;

namespace GdFfx;

/// <summary>
/// The game in 3D from the game-state API: the game's camera (interpolated),
/// the classic map of the level the game's renderer loaded, and each
/// character as its classic model posed by the game's skeleton (element i
/// poses <c>SkeletonNode_i</c>), or as a marker with skeleton points until
/// its model is imported.
/// </summary>
public partial class StateView3D : Node3D
{
    private Camera3D _camera = null!;
    private WorldEnvironment _env = null!;
    private readonly Dictionary<ushort, ActorNode> _actors = new();
    private SphereMesh _boneMesh = null!;
    private Node3D? _map;
    private MapLayers _layers = null!;
    private string _mapLevel = "";
    private string _wantedLevel = "";

    /// <summary>Character space (the field's, y down) to Godot's: half a turn about z.</summary>
    private static readonly Basis FlipXY = new(new Vector3(-1, 0, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1));

    /// <summary>The level whose map is shown ("" none yet).</summary>
    public string MapLevel => _mapLevel;

    /// <summary>The map's 2D layers.</summary>
    public MapLayers Layers => _layers;

    /// <summary>Characters drawn with their classic model, and how many of their bones are posed.</summary>
    public (int Models, int PosedBones) Posed { get; private set; }

    private sealed class ActorNode
    {
        public required Node3D Root;
        public required Node3D Marker;
        public required MultiMeshInstance3D Bones;
        public required Label3D Label;
        public ushort Id;
        public string ModelName = "";
        public Node3D? Model;
        public Skeleton3D? Skeleton;
        /// <summary>
        /// The skeleton's bones named <c>SkeletonNode_i</c> with their game element i, parents
        /// before children (each global pose is set against its parent's, already set).
        /// </summary>
        public (int Bone, int Element)[] BoneMap = [];
        /// <summary>The skeleton's transform in the model's space (inverted).</summary>
        public Transform3D SkeletonInModelInv = Transform3D.Identity;
        /// <summary>
        /// A model without a skin (rigid parts, e.g. the save sphere f001): each part node
        /// <c>MeshNodeK</c> with the game element it follows and its parent's transform in the
        /// model (inverted).
        /// </summary>
        public (Node3D Part, int Element, Transform3D ParentInv)[] Rigid = [];
    }

    public override void _Ready()
    {
        GameSettings.ApplyQuality(GetViewport());
        _camera = new Camera3D { Current = true, Near = 0.5f, Far = 20000f };
        AddChild(_camera);
        _env = new WorldEnvironment
        {
            Environment = new Environment
            {
                BackgroundMode = Environment.BGMode.Color,
                BackgroundColor = new Color(0.05f, 0.06f, 0.09f),
                AmbientLightSource = Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.75f, 0.75f, 0.78f),
                AmbientLightEnergy = 1.0f,
                // the game draws its own colours (ClassicMaterials): no tone mapping
                TonemapMode = Environment.ToneMapper.Linear,
            },
        };
        AddChild(_env);
        _layers = new MapLayers { Name = "MapLayers" };
        AddChild(_layers);
        AddChild(new EffectView { Name = "EffectView" });
        AddChild(new EffectDrawView { Name = "EffectDrawView" });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, 30, 0), LightEnergy = 0.8f });
        _boneMesh = new SphereMesh { Radius = 0.6f, Height = 1.2f, RadialSegments = 6, Rings = 3 };
    }

    private static Color KindColor(int kind) => kind switch
    {
        0 => new Color(0.95f, 0.75f, 0.3f), // party
        1 => new Color(0.9f, 0.3f, 0.3f),   // monster
        2 => new Color(0.4f, 0.8f, 0.5f),   // NPC
        3 => new Color(0.7f, 0.5f, 0.95f),  // aeon
        4 => new Color(0.7f, 0.7f, 0.75f),  // weapon
        _ => new Color(0.5f, 0.7f, 0.95f),  // object, skeleton
    };

    private ActorNode Make(ushort slot, int kind)
    {
        var root = new Node3D { Name = $"Actor{slot}" };
        var mat = new StandardMaterial3D { AlbedoColor = KindColor(kind) };
        var marker = new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(3f, 6f, 3f) }, MaterialOverride = mat };
        root.AddChild(marker);
        AddChild(root);
        var bones = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _boneMesh },
            MaterialOverride = mat,
        };
        AddChild(bones);
        var label = new Label3D { Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.05f, FontSize = 48 };
        AddChild(label);
        return new ActorNode { Root = root, Marker = marker, Bones = bones, Label = label };
    }

    public override void _Process(double delta)
    {
        var host = GameHost.Instance;
        if (host == null)
            return;
        var view = host.View;
        if (view.Camera() is { } cam)
        {
            _camera.GlobalTransform = cam.Transform;
            _camera.Fov = Mathf.RadToDeg(cam.FovY);
            _camera.Far = Mathf.Clamp(cam.Far, 100f, 20000f);
            // the game's screen shake (Effects.ShakeX/Y, in the PS2 screen's pixels, 448 high):
            // as the same share of the view at a subject 100 units away
            var fx = view.Current?.Effects;
            float unit = 2f * Mathf.Tan(Mathf.DegToRad(_camera.Fov) / 2f) * 100f / 448f;
            _camera.HOffset = (fx?.ShakeX ?? 0f) * unit;
            _camera.VOffset = -(fx?.ShakeY ?? 0f) * unit;
        }
        ClassicMaterials.SetLook(view.Current?.Look);
        // the game clears to the map's background colour (Look.background), drawn as it is
        if (view.Current?.Look is { Valid: true } look)
        {
            _env.Environment.BackgroundColor = new Color(look.Background.X, look.Background.Y, look.Background.Z);
            ApplyGlow(look);
        }
        UpdateMap(host.Classic, view.Current);
        _layers.Tick(host.Game, view.Current?.World.Map ?? -1, _camera.Far);
        UpdateObjects(view);
        UpdateActors(host.Classic, view);
    }

    // ---- the map ----------------------------------------------------------------

    /// <summary>
    /// The game's glow pass (docs/formats/postfx.md in ffx-rust, "Glow"): a glow map of what is
    /// brighter than <c>NoGlowThreshold</c> (full at <c>FullGlowThreshold</c>), blurred in three
    /// levels and added with <c>GlowGain</c>. Godot's environment glow is the same kind of pass:
    /// threshold, three blurred levels, additive mix; the map's values drive it (the shapes of
    /// the blurs and the threshold ramp are Godot's own). <c>--no-glow</c> turns it off.
    /// </summary>
    private void ApplyGlow(GdFfx.Api.Look look)
    {
        var env = _env.Environment;
        bool on = look.GlowGain > 0 && !GameSettings.Flag("no-glow");
        env.GlowEnabled = on;
        if (!on)
            return;
        env.GlowBlendMode = Environment.GlowBlendModeEnum.Additive;
        env.GlowHdrThreshold = Mathf.Max(look.GlowNone, 0.35f);
        env.GlowHdrScale = 1f / Mathf.Max(look.GlowFull - look.GlowNone, 0.05f);
        env.GlowIntensity = look.GlowGain;
        env.GlowBloom = 0f;
        for (int i = 0; i < 7; i++)
            env.SetGlowLevel(i, i is >= 1 and <= 3 ? 1f : 0f);
    }

    // ---- the map's objects ------------------------------------------------------

    /// <summary>The classic map's mesh nodes by object id and mesh index (<c>N_kShape</c>).</summary>
    private Dictionary<(uint Id, int K), Node3D>? _objectNodes;

    /// <summary>Objects placed by the game this frame (for tests).</summary>
    public int ObjectsPlaced { get; private set; }

    private static readonly System.Text.RegularExpressions.Regex Fbx = new("FBXASC(\\d{3})");
    private static readonly System.Text.RegularExpressions.Regex MeshName = new("^(\\d+)_(\\d+)Shape$");

    /// <summary>A node name with FBX's <c>FBXASCnnn</c> escapes decoded.</summary>
    public static string FbxName(string n) => Fbx.Replace(n, m => ((char)int.Parse(m.Groups[1].Value)).ToString());

    private void IndexObjects()
    {
        _objectNodes = new();
        if (_map == null || _map.Name != "ClassicMap")
            return;
        foreach (var n in _map.FindChildren("*", nameof(Node3D), true, false))
            if (n is Node3D n3 && MeshName.Match(FbxName(n3.Name)) is { Success: true } m)
                _objectNodes.TryAdd((uint.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)), n3);
    }

    /// <summary>
    /// The map's objects where the game has them (API 8's <c>Snapshot.objects</c>): mesh k of
    /// object N (the node <c>N_kShape</c>) at its live world matrix, interpolated between the
    /// last two snapshots, shown while the game draws it. Scripts move objects this way, e.g.
    /// Besaid's birds, which the file places at the origin.
    /// </summary>
    private void UpdateObjects(GameStateView view)
    {
        ObjectsPlaced = 0;
        var s = view.Current;
        if (s == null || _map == null)
            return;
        if (_objectNodes == null)
            IndexObjects();
        if (_objectNodes!.Count == 0)
            return;
        Dictionary<uint, GdFfx.Api.SceneObject>? prev = null;
        if (view.Previous is { } p && p.World.Level == s.World.Level)
        {
            prev = new();
            foreach (var o in p.Objects)
                prev.TryAdd(o.Id, o);
        }
        foreach (var o in s.Objects)
            for (int k = 0; k < o.Meshes.Count; k++)
            {
                if (!_objectNodes.TryGetValue((o.Id, k), out var node))
                    continue;
                var m = o.Meshes[k];
                var t = GameStateView.CameraTransform(m.World);
                if (prev != null && prev.TryGetValue(o.Id, out var po) && k < po.Meshes.Count)
                {
                    var a = GameStateView.CameraTransform(po.Meshes[k].World);
                    if (a.Origin.DistanceTo(t.Origin) < GameStateView.WarpDistance * 4)
                        t = a.InterpolateWith(t, view.Alpha);
                }
                if (t.Basis.Determinant() != 0)
                    node.GlobalTransform = t;
                node.Visible = m.Drawn;
                ObjectsPlaced++;
            }
    }

    private void UpdateMap(ClassicLibrary? lib, Snapshot? s)
    {
        var level = s?.World.Level ?? "";
        if (level.Length == 0 || !(level.StartsWith("/map/") || level.StartsWith("/btlmap/")))
            return;
        // the 2D layers, also on a map that has a 3D model (MapLayers)
        if (level.StartsWith("/map/"))
            _layers.Load(lib, level, s!.World.Map);
        if (level == _mapLevel)
            return;
        _wantedLevel = level;
        if (ContentResolver.Resolve(lib, "map", level) is not { } content)
        {
            // a map of 2D layers only has no 3D model: shown once its layers are
            if (lib?.Failed("map", level) == true && _layers.Level == level)
            {
                _map?.QueueFree();
                _map = null;
                _objectNodes = null;
                _mapLevel = level;
                GD.Print($"ffx: map {level} shown (2D layers only)");
            }
            return;
        }
        var map = content.Node;
        _map?.QueueFree();
        _map = map;
        _objectNodes = null;
        _mapLevel = level;
        if (map == null)
            return;
        map.Name = content.Remade ? "RemadeMap" : "ClassicMap";
        map.Scale = Vector3.One * content.MapScale;
        AddChild(map);
        GD.Print($"ffx: map {level} shown{(content.Remade ? $" (remade: {content.Path})" : "")}");
    }

    // ---- the characters -------------------------------------------------------------

    private void UpdateActors(ClassicLibrary? lib, GameStateView view)
    {
        var seen = new HashSet<ushort>();
        int models = 0, posed = 0;
        foreach (var f in view.Actors())
        {
            var a = f.Actor;
            seen.Add(a.Slot);
            if (!_actors.TryGetValue(a.Slot, out var n) || n.Id != a.Id)
            {
                if (n != null)
                    Free(n);
                n = Make(a.Slot, a.Kind);
                n.Id = a.Id;
                _actors[a.Slot] = n;
            }
            if (n.Model == null && a.Model.Length > 0 && n.ModelName != a.Model
                && ContentResolver.Resolve(lib, "chr", a.Model) is { } content)
                AttachModel(n, a.Model, content);
            n.Root.GlobalTransform = f.Root;
            var hasModel = n.Model != null;
            n.Root.Visible = f.Shown;
            n.Marker.Visible = !hasModel;
            n.Bones.Visible = f.Shown && !hasModel;
            n.Label.Visible = f.Shown && !hasModel;
            if (hasModel && f.Shown)
            {
                models++;
                posed += Pose(n, a, view);
            }
            else
                Points(n, f);
            n.Label.Text = $"{a.Model}{(a.Player ? " (player)" : "")}";
        }
        foreach (var slot in new List<ushort>(_actors.Keys))
        {
            if (!seen.Contains(slot))
            {
                Free(_actors[slot]);
                _actors.Remove(slot);
            }
        }
        Posed = (models, posed);
    }

    private static void Points(ActorNode n, GameStateView.ActorFrame f)
    {
        var mm = n.Bones.Multimesh;
        if (mm.InstanceCount != f.Bones.Length)
            mm.InstanceCount = f.Bones.Length;
        var top = f.Root.Origin;
        for (int i = 0; i < f.Bones.Length; i++)
        {
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, f.Bones[i]));
            if (f.Bones[i].Y > top.Y)
                top = f.Bones[i];
        }
        n.Label.GlobalPosition = top + Vector3.Up * 4f;
    }

    private static void AttachModel(ActorNode n, string name, Content content)
    {
        n.ModelName = name;
        var model = content.Node;
        if (model == null)
            return;
        model.Name = "Model";
        model.Transform = new Transform3D(FlipXY, Vector3.Zero);
        n.Root.AddChild(model);
        n.Model = model;
        // a remade character may opt out of the game's posing (docs/remake.md)
        if (!Content.Posed(model))
            return;
        var skels = model.FindChildren("*", nameof(Skeleton3D), true, false);
        if (skels.Count == 0 || skels[0] is not Skeleton3D skel)
        {
            n.Rigid = RigidParts(model);
            return;
        }
        n.Skeleton = skel;
        // the skeleton's place in the model (the glTF nodes above it)
        Transform3D t = Transform3D.Identity;
        for (Node? p = skel; p != null && p != model; p = p.GetParent())
            if (p is Node3D p3)
                t = p3.Transform * t;
        n.SkeletonInModelInv = t.AffineInverse();
        n.BoneMap = BoneOrder(skel);
    }

    /// <summary>
    /// A skinless model's parts and the elements they follow: part <c>MeshNodeK</c> follows the
    /// K-th leaf <c>SkeletonNode_i</c> by element (f001: three parts on elements 2, 3, 4, the
    /// leaves; the game moves 2 and 3 up 6 and turns them, 4 is the base). Seen on f001, where
    /// the game places the parts per mesh (<c>graphicCharacterInstanceSetWorldMatrixByMdlID</c>);
    /// the pairing is inferred, not read from the game.
    /// </summary>
    private static (Node3D, int, Transform3D)[] RigidParts(Node3D model)
    {
        const string bone = "SkeletonNode_", part = "MeshNode";
        var leaves = new List<int>();
        var parts = new SortedDictionary<int, Node3D>();
        foreach (var x in model.FindChildren("*", nameof(Node3D), true, false))
        {
            if (x is not Node3D n3)
                continue;
            var name = n3.Name.ToString();
            if (name.StartsWith(bone, System.StringComparison.Ordinal) && int.TryParse(name[bone.Length..], out var e)
                && !n3.GetChildren().OfType<Node3D>().Any(c => c.Name.ToString().StartsWith(bone, System.StringComparison.Ordinal)))
                leaves.Add(e);
            else if (name.StartsWith(part, System.StringComparison.Ordinal) && int.TryParse(name[part.Length..], out var k))
                parts[k] = n3;
        }
        leaves.Sort();
        var list = new List<(Node3D, int, Transform3D)>();
        foreach (var (k, node) in parts)
        {
            if (k >= leaves.Count)
                break;
            Transform3D t = Transform3D.Identity;
            for (Node? p = node.GetParent(); p != null && p != model; p = p.GetParent())
                if (p is Node3D p3)
                    t = p3.Transform * t;
            list.Add((node, leaves[k], t.AffineInverse()));
        }
        return list.ToArray();
    }

    /// <summary>
    /// The bones named <c>SkeletonNode_i</c> with their element i, sorted by depth (then bone
    /// index): the game's element order does not put parents first (c001: 0, 1, 2, 74, 3...).
    /// </summary>
    private static (int Bone, int Element)[] BoneOrder(Skeleton3D skel)
    {
        const string prefix = "SkeletonNode_";
        var bones = new List<(int Depth, int Bone, int Element)>();
        for (int b = 0; b < skel.GetBoneCount(); b++)
        {
            var name = skel.GetBoneName(b);
            if (!name.StartsWith(prefix, System.StringComparison.Ordinal)
                || !int.TryParse(name[prefix.Length..], out var e) || e < 0)
                continue;
            int depth = 0;
            for (int p = skel.GetBoneParent(b); p >= 0; p = skel.GetBoneParent(p))
                depth++;
            bones.Add((depth, b, e));
        }
        bones.Sort();
        return bones.ConvertAll(x => (x.Bone, x.Element)).ToArray();
    }

    /// <summary>A row-major 4x4 of the game (row vectors) as a Godot transform in the same space.</summary>
    private static Transform3D RowMajor(float[] m) => new(
        new Basis(new Vector3(m[0], m[1], m[2]), new Vector3(m[4], m[5], m[6]), new Vector3(m[8], m[9], m[10])),
        new Vector3(m[12], m[13], m[14]));

    private static bool IsIdentity(float[] m) =>
        m[0] == 1 && m[5] == 1 && m[10] == 1 && m[15] == 1 && m[1] == 0 && m[2] == 0 && m[4] == 0
        && m[6] == 0 && m[8] == 0 && m[9] == 0 && m[12] == 0 && m[13] == 0 && m[14] == 0;

    /// <summary>Poses the model's skeleton from the game's bones (interpolated); the bones posed.</summary>
    private static int Pose(ActorNode n, Actor a, GameStateView view)
    {
        var skel = n.Skeleton;
        Actor? prev = null;
        if (view.Previous != null)
            foreach (var p in view.Previous.Actors)
                if (p.Slot == a.Slot && p.Bones.Count == a.Bones.Count && view.Interpolates(p, a))
                    prev = p;
        if (skel == null)
        {
            int moved = 0;
            foreach (var (part, i, parentInv) in n.Rigid)
            {
                if (i >= a.Bones.Count)
                    continue;
                var t = RowMajor(a.Bones[i].Matrix);
                if (prev != null)
                    t = Lerp(RowMajor(prev.Bones[i].Matrix), t, view.Alpha);
                part.Transform = parentInv * Solid(t);
                moved++;
            }
            return moved;
        }
        int posed = 0;
        // in skeleton order, parents first: a parent's later set would move its children
        foreach (var (b, i) in n.BoneMap)
        {
            if (i >= a.Bones.Count)
                continue;
            var m = a.Bones[i].Matrix;
            // bones the game leaves at the identity keep the model's own pose
            if (IsIdentity(m))
                continue;
            var t = RowMajor(m);
            if (prev != null)
                t = Lerp(RowMajor(prev.Bones[i].Matrix), t, view.Alpha);
            SetGlobalPose(skel, b, n.SkeletonInModelInv * Solid(t));
            posed++;
        }
        return posed;
    }

    /// <summary>
    /// Between two bone matrices: <see cref="Transform3D.InterpolateWith"/> when both can be
    /// decomposed, else the newer one (the game hides parts with a zero scale, which has no
    /// rotation to interpolate).
    /// </summary>
    private static Transform3D Lerp(Transform3D from, Transform3D to, float alpha) =>
        Solid(from) == from && Solid(to) == to && Mathf.Abs(from.Basis.Determinant()) > 1e-6f && Mathf.Abs(to.Basis.Determinant()) > 1e-6f
            ? from.InterpolateWith(to, alpha)
            : to;

    /// <summary>
    /// <see cref="Skeleton3D.SetBoneGlobalPose"/> for the game's matrices, which can be sheared
    /// or degenerate: the local pose against the parent's, split into position, rotation (the
    /// orthonormalised part) and scale, which Godot's own setter refuses for such a basis.
    /// </summary>
    private static void SetGlobalPose(Skeleton3D skel, int bone, Transform3D global)
    {
        int parent = skel.GetBoneParent(bone);
        var local = parent < 0 ? global : skel.GetBoneGlobalPose(parent).AffineInverse() * global;
        var basis = local.Basis;
        var scale = basis.Scale;
        if (Mathf.Abs(basis.Determinant()) < 1e-12f || !scale.IsFinite())
            return;
        skel.SetBonePosePosition(bone, local.Origin);
        skel.SetBonePoseRotation(bone, basis.GetRotationQuaternion());
        skel.SetBonePoseScale(bone, scale);
    }

    /// <summary>A matrix Godot can invert: a zero (hidden) basis becomes a tiny scale at the same place.</summary>
    private static Transform3D Solid(Transform3D t) =>
        Mathf.Abs(t.Basis.Determinant()) < 1e-12f ? new Transform3D(Basis.FromScale(Vector3.One * 1e-4f), t.Origin) : t;

    private static void Free(ActorNode n)
    {
        n.Root.QueueFree();
        n.Bones.QueueFree();
        n.Label.QueueFree();
    }
}
