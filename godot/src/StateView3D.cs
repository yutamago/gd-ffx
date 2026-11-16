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
    private readonly Dictionary<ushort, ActorNode> _actors = new();
    private SphereMesh _boneMesh = null!;
    private Node3D? _map;
    private string _mapLevel = "";
    private string _wantedLevel = "";

    /// <summary>Character space (the field's, y down) to Godot's: half a turn about z.</summary>
    private static readonly Basis FlipXY = new(new Vector3(-1, 0, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1));

    /// <summary>The level whose map is shown ("" none yet).</summary>
    public string MapLevel => _mapLevel;

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
    }

    public override void _Ready()
    {
        _camera = new Camera3D { Current = true, Near = 0.5f, Far = 20000f };
        AddChild(_camera);
        var env = new WorldEnvironment
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
        AddChild(env);
        AddChild(new EffectView { Name = "EffectView" });
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
        UpdateMap(host.Classic, view.Current);
        UpdateActors(host.Classic, view);
    }

    // ---- the map ----------------------------------------------------------------

    private void UpdateMap(ClassicLibrary? lib, Snapshot? s)
    {
        var level = s?.World.Level ?? "";
        if (level.Length == 0 || !(level.StartsWith("/map/") || level.StartsWith("/btlmap/")))
            return;
        if (level == _mapLevel)
            return;
        _wantedLevel = level;
        if (ContentResolver.Resolve(lib, "map", level) is not { } content)
            return;
        var map = content.Node;
        _map?.QueueFree();
        _map = map;
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
            return;
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
        if (skel == null)
            return 0;
        Actor? prev = null;
        if (view.Previous != null)
            foreach (var p in view.Previous.Actors)
                if (p.Slot == a.Slot && p.Bones.Count == a.Bones.Count && view.Interpolates(p, a))
                    prev = p;
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
                t = RowMajor(prev.Bones[i].Matrix).InterpolateWith(t, view.Alpha);
            skel.SetBoneGlobalPose(b, n.SkeletonInModelInv * t);
            posed++;
        }
        return posed;
    }

    private static void Free(ActorNode n)
    {
        n.Root.QueueFree();
        n.Bones.QueueFree();
        n.Label.QueueFree();
    }
}
