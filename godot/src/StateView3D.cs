using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// G1's 3D view of the game-state API: the game's camera (interpolated), and
/// each character as a marker at its root with its skeleton as points and
/// its model name above. A stand-in until the classic importer (G2) brings
/// the game's own models and maps.
/// </summary>
public partial class StateView3D : Node3D
{
    private Camera3D _camera = null!;
    private readonly Dictionary<ushort, ActorNode> _actors = new();
    private SphereMesh _boneMesh = null!;

    private sealed class ActorNode
    {
        public required Node3D Root;
        public required MultiMeshInstance3D Bones;
        public required Label3D Label;
        public ushort Id;
    }

    public override void _Ready()
    {
        _camera = new Camera3D { Current = true, Near = 0.05f, Far = 5000f };
        AddChild(_camera);
        var env = new WorldEnvironment
        {
            Environment = new Environment
            {
                BackgroundMode = Environment.BGMode.Color,
                BackgroundColor = new Color(0.05f, 0.06f, 0.09f),
                AmbientLightSource = Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.6f, 0.65f),
            },
        };
        AddChild(env);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, 30, 0) });
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
        var marker = new MeshInstance3D
        {
            Mesh = new PrismMesh { Size = new Vector3(3f, 6f, 3f) },
            MaterialOverride = mat,
        };
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
        return new ActorNode { Root = root, Bones = bones, Label = label };
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
        }
        var seen = new HashSet<ushort>();
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
            n.Root.Visible = f.Shown;
            n.Bones.Visible = f.Shown;
            n.Label.Visible = f.Shown;
            n.Root.GlobalTransform = f.Root;
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
    }

    private static void Free(ActorNode n)
    {
        n.Root.QueueFree();
        n.Bones.QueueFree();
        n.Label.QueueFree();
    }
}
