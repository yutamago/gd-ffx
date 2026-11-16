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
    private Snapshot? _lookOf;
    private (bool On, float None, float Full, float Gain) _glow = (false, -1, -1, -1);
    private WorldEnvironment _env = null!;
    private readonly Dictionary<ushort, ActorNode> _actors = new();
    private readonly HashSet<ushort> _seen = new();

    /// <summary>Character models added to the scene in one frame at most.</summary>
    private const int AttachesPerFrame = 3;
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
        public bool LabelPlayer;
        /// <summary>The model asked for (it may still be loading).</summary>
        public string Asked = "";
        public string ModelName = "";
        public Node3D? Model;
        public Skeleton3D? Skeleton;
        /// <summary>Every bone of the skeleton, parents before children, with its parent and game element (-1 none).</summary>
        public (int Bone, int Parent, int Element)[] AllBones = [];
        /// <summary>Each bone's global pose (scratch for <see cref="Locals"/>).</summary>
        public Transform3D[] Globals = [];
        /// <summary>The local poses of the character's two newest snapshots (<see cref="Locals"/>).</summary>
        public (Actor? Of, BoneLocal[] Bones) LocalsA = (null, []), LocalsB = (null, []);
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
        var t0 = Perf.Start();
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
        // the look changes with the snapshot (30 a second), not every frame
        if (!ReferenceEquals(view.Current, _lookOf))
        {
            _lookOf = view.Current;
            ClassicMaterials.SetLook(view.Current?.Look);
            // the game clears to the map's background colour (Look.background), drawn as it is
            if (view.Current?.Look is { Valid: true } look)
            {
                var bg = new Color(look.Background.X, look.Background.Y, look.Background.Z);
                if (_env.Environment.BackgroundColor != bg)
                    _env.Environment.BackgroundColor = bg;
                ApplyGlow(look);
            }
        }
        UpdateMap(host.Classic, view.Current);
        _layers.Tick(host.Game, view.Current?.World.Map ?? -1, _camera.Far);
        Perf.End(Perf.Section.View3D, t0);
        t0 = Perf.Start();
        UpdateObjects(view);
        Perf.End(Perf.Section.Objects, t0);
        t0 = Perf.Start();
        UpdateActors(host.Classic, view);
        Perf.End(Perf.Section.Actors, t0);
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
        var glow = (on, look.GlowNone, look.GlowFull, look.GlowGain);
        if (glow == _glow)
            return;
        _glow = glow;
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

    private Snapshot? _objectsPrevOf;
    private readonly Dictionary<uint, GdFfx.Api.SceneObject> _objectsPrev = new();

    /// <summary>Objects placed by the game this frame (for tests).</summary>
    public int ObjectsPlaced { get; private set; }

    private static readonly System.Text.RegularExpressions.Regex Fbx = new("FBXASC(\\d{3})");
    private static readonly System.Text.RegularExpressions.Regex MeshName = new("^(\\d+)_(\\d+)Shape$");

    /// <summary>A node name with FBX's <c>FBXASCnnn</c> escapes decoded.</summary>
    public static string FbxName(string n) => Fbx.Replace(n, m => ((char)int.Parse(m.Groups[1].Value)).ToString());

    private void IndexObjects()
    {
        _objectNodes = _map == null || _map.Name != "ClassicMap" ? new() : ObjectIndex(_map);
    }

    /// <summary>A classic map's mesh nodes by object id and mesh index (on the loading worker thread: a search of every node).</summary>
    private static Dictionary<(uint Id, int K), Node3D> ObjectIndex(Node3D map)
    {
        var index = new Dictionary<(uint Id, int K), Node3D>();
        foreach (var n in map.FindChildren("*", nameof(Node3D), true, false))
            if (n is Node3D n3 && MeshName.Match(FbxName(n3.Name)) is { Success: true } m)
                index.TryAdd((uint.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)), n3);
        return index;
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
        if (!ReferenceEquals(_objectsPrevOf, s))
        {
            _objectsPrevOf = s;
            _objectsPrev.Clear();
            if (view.Previous is { } p && !ReferenceEquals(p, s) && p.World.Level == s.World.Level)
                foreach (var o in p.Objects)
                    _objectsPrev.TryAdd(o.Id, o);
        }
        var prev = _objectsPrev.Count > 0 ? _objectsPrev : null;
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
        // a map still loading in the background that the game left already
        if (_wantedLevel.Length > 0 && _wantedLevel != level)
            ContentResolver.Forget("map", _wantedLevel);
        _wantedLevel = level;
        if (ContentResolver.Resolve(lib, "map", level, prepare: ObjectIndex) is not { } content)
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
        if (map != null)
            ClassicMaterials.FixLayers(map);
        _map?.QueueFree();
        _map = map;
        _objectNodes = content.Remade ? null : content.Prepared as Dictionary<(uint Id, int K), Node3D>;
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
        var seen = _seen;
        seen.Clear();
        int models = 0, posed = 0, attached = 0;
        foreach (var f in view.Actors())
        {
            var a = f.Actor;
            seen.Add(a.Slot);
            if (!_actors.TryGetValue(a.Slot, out var n) || n.Id != a.Id)
            {
                if (n != null)
                    Free(n, a.Slot);
                n = Make(a.Slot, a.Kind);
                n.Id = a.Id;
                _actors[a.Slot] = n;
            }
            if (n.Model == null && a.Model.Length > 0 && n.ModelName != a.Model)
            {
                // each character its own instance (the slot is its ticket), loaded in the background
                if (n.Asked.Length > 0 && n.Asked != a.Model)
                    ContentResolver.Forget("chr", n.Asked, a.Slot);
                n.Asked = a.Model;
                // a few models enter the scene a frame (a scene's dozen at once was a 30 ms frame)
                if (attached < AttachesPerFrame && ContentResolver.Resolve(lib, "chr", a.Model, a.Slot) is { } content)
                {
                    AttachModel(n, a.Model, content);
                    attached++;
                }
            }
            n.Root.GlobalTransform = f.Root;
            var hasModel = n.Model != null;
            n.Root.Visible = f.Shown;
            n.Marker.Visible = !hasModel;
            n.Bones.Visible = f.Shown && !hasModel;
            n.Label.Visible = f.Shown && !hasModel;
            if (hasModel && f.Shown)
            {
                models++;
                posed += Pose(n, f, view);
            }
            else if (f.Shown && !hasModel)
            {
                Points(n, f);
                if (n.LabelPlayer != a.Player || n.Label.Text.Length == 0)
                {
                    n.LabelPlayer = a.Player;
                    n.Label.Text = $"{a.Model}{(a.Player ? " (player)" : "")}";
                }
            }
        }
        if (_actors.Count > seen.Count)
            foreach (var slot in new List<ushort>(_actors.Keys))
            {
                if (!seen.Contains(slot))
                {
                    Free(_actors[slot], slot);
                    _actors.Remove(slot);
                }
        }
        Posed = (models, posed);
    }

    private static void Points(ActorNode n, GameStateView.ActorFrame f)
    {
        var mm = n.Bones.Multimesh;
        var bones = f.Bones();
        if (mm.InstanceCount != bones.Length)
            mm.InstanceCount = bones.Length;
        var top = f.Root.Origin;
        for (int i = 0; i < bones.Length; i++)
        {
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, bones[i]));
            if (bones[i].Y > top.Y)
                top = bones[i];
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
        n.AllBones = AllBones(skel);
        n.Globals = new Transform3D[skel.GetBoneCount()];
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
    /// All bones by depth (then index), with their parent and the game element of a
    /// <c>SkeletonNode_i</c> (-1 for the others).
    /// </summary>
    private static (int, int, int)[] AllBones(Skeleton3D skel)
    {
        const string prefix = "SkeletonNode_";
        var bones = new List<(int Depth, int Bone, int Parent, int Element)>();
        for (int b = 0; b < skel.GetBoneCount(); b++)
        {
            var name = skel.GetBoneName(b);
            int e = name.StartsWith(prefix, System.StringComparison.Ordinal) && int.TryParse(name[prefix.Length..], out var x) && x >= 0 ? x : -1;
            int depth = 0;
            for (int p = skel.GetBoneParent(b); p >= 0; p = skel.GetBoneParent(p))
                depth++;
            bones.Add((depth, b, skel.GetBoneParent(b), e));
        }
        bones.Sort();
        return bones.ConvertAll(x => (x.Bone, x.Parent, x.Element)).ToArray();
    }

    /// <summary>A row-major 4x4 of the game (row vectors) as a Godot transform in the same space.</summary>
    private static Transform3D RowMajor(float[] m) => new(
        new Basis(new Vector3(m[0], m[1], m[2]), new Vector3(m[4], m[5], m[6]), new Vector3(m[8], m[9], m[10])),
        new Vector3(m[12], m[13], m[14]));

    private static bool IsIdentity(float[] m) =>
        m[0] == 1 && m[5] == 1 && m[10] == 1 && m[15] == 1 && m[1] == 0 && m[2] == 0 && m[4] == 0
        && m[6] == 0 && m[8] == 0 && m[9] == 0 && m[12] == 0 && m[13] == 0 && m[14] == 0;

    /// <summary>Poses the model's skeleton from the game's bones (interpolated); the bones posed.</summary>
    private static int Pose(ActorNode n, GameStateView.ActorFrame f, GameStateView view)
    {
        var a = f.Actor;
        var skel = n.Skeleton;
        var prev = f.Previous is { } fp && fp.Bones.Count == a.Bones.Count ? fp : null;
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
        // each snapshot's bones are split into local poses once (Locals); a frame only
        // interpolates position, rotation and scale between the two
        var cur = Locals(n, skel, a);
        var from = prev != null ? Locals(n, skel, prev) : null;
        int posed = 0;
        foreach (var (b, _, _) in n.AllBones)
        {
            var c = cur[b];
            if (!c.Set)
                continue;
            var (pos, rot, scale) = (c.Position, c.Rotation, c.Scale);
            if (from != null && from[b] is { Set: true, Solid: true } p && c.Solid)
            {
                pos = p.Position.Lerp(pos, view.Alpha);
                rot = p.Rotation.Slerp(rot, view.Alpha);
                scale = p.Scale.Lerp(scale, view.Alpha);
            }
            skel.SetBonePosePosition(b, pos);
            skel.SetBonePoseRotation(b, rot);
            skel.SetBonePoseScale(b, scale);
            posed++;
        }
        return posed;
    }

    /// <summary>A bone's local pose from one snapshot: set by the game or not, and whether its matrix had a size (not hidden).</summary>
    private readonly record struct BoneLocal(bool Set, bool Solid, Vector3 Position, Quaternion Rotation, Vector3 Scale);

    /// <summary>
    /// <paramref name="a"/>'s bones as local poses of the skeleton (kept for the two newest
    /// snapshots of the character): in skeleton order, parents first, each bone's matrix (in the
    /// skeleton's space) under its parent's global pose, split into position, rotation (the
    /// orthonormalised part) and scale, as Godot holds a pose: the game's matrices can be sheared
    /// or degenerate, which Godot's own global setter refuses. Bones the game leaves at the
    /// identity (and those it does not drive) keep the model's own pose.
    /// </summary>
    private static BoneLocal[] Locals(ActorNode n, Skeleton3D skel, Actor a)
    {
        if (ReferenceEquals(n.LocalsA.Of, a))
            return n.LocalsA.Bones;
        if (ReferenceEquals(n.LocalsB.Of, a))
            return n.LocalsB.Bones;
        // the older of the two is replaced (its array reused)
        var bones = n.LocalsB.Bones.Length == n.Globals.Length ? n.LocalsB.Bones : new BoneLocal[n.Globals.Length];
        n.LocalsB = n.LocalsA;
        n.LocalsA = (a, bones);
        var globals = n.Globals;
        foreach (var (b, parent, i) in n.AllBones)
        {
            var parentGlobal = parent >= 0 ? globals[parent] : Transform3D.Identity;
            if (i >= 0 && i < a.Bones.Count && !IsIdentity(a.Bones[i].Matrix))
            {
                var m = RowMajor(a.Bones[i].Matrix);
                var solid = Mathf.Abs(m.Basis.Determinant()) > 1e-6f;
                var local = parentGlobal.AffineInverse() * (n.SkeletonInModelInv * Solid(m));
                var basis = local.Basis;
                var scale = basis.Scale;
                if (Mathf.Abs(basis.Determinant()) >= 1e-12f && scale.IsFinite())
                {
                    var rotation = basis.GetRotationQuaternion();
                    bones[b] = new BoneLocal(true, solid, local.Origin, rotation, scale);
                    // Godot's pose: rotation times scale, at the position
                    globals[b] = parentGlobal * new Transform3D(new Basis(rotation) * Basis.FromScale(scale), local.Origin);
                    continue;
                }
            }
            bones[b] = default;
            globals[b] = parentGlobal * skel.GetBonePose(b);
        }
        return bones;
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

    /// <summary>A matrix Godot can invert: a zero (hidden) basis becomes a tiny scale at the same place.</summary>
    private static Transform3D Solid(Transform3D t) =>
        Mathf.Abs(t.Basis.Determinant()) < 1e-12f ? new Transform3D(Basis.FromScale(Vector3.One * 1e-4f), t.Origin) : t;

    private static void Free(ActorNode n, ushort slot)
    {
        if (n.Model == null && n.Asked.Length > 0)
            ContentResolver.Forget("chr", n.Asked, slot);
        n.Root.QueueFree();
        n.Bones.QueueFree();
        n.Label.QueueFree();
    }
}
