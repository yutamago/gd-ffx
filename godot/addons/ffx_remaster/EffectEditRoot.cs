#if TOOLS
using System.Collections.Generic;
using GdFfx.Remake;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// An effect opened in the editor (docs/remaster-editor.md, "Effects"; R7): the edit scene
/// <c>res://remaster/edit/effect-&lt;id&gt;.tscn</c> (the root and the id only, not in git) builds, when
/// opened, the classic effect as the game showed it (its recording, <see cref="EffectRecorder"/>:
/// the draw list of each update, drawn as the game draws it: <see cref="EffectDrawView.MakeMaterial"/>)
/// under <c>Classic</c>, and the remade scene registered for the id under <c>Remake</c>, side by side
/// (<see cref="SideBySide"/>) or at the same place. The dock plays, pauses and scrubs the classic one
/// (<see cref="Frame"/>); the remake runs on its own (particles preview in the editor). Neither is saved:
/// both are rebuilt at each opening.
/// </summary>
[Tool]
public partial class EffectEditRoot : Node3D
{
    public const string EditDir = "res://remaster/edit";

    public static string EditScenePath(string id) => $"{EditDir}/effect-{id}.tscn";

    /// <summary>The effect id (<c>bsil00-fp15</c>).</summary>
    [Export] public string Effect { get; set; } = "";

    /// <summary>The recording read (null: none for this id).</summary>
    public EffectRecorder.Recording? Recording { get; private set; }

    /// <summary>The classic replay's node and the remake's (null: none).</summary>
    public Node3D? Classic { get; private set; }
    public Node3D? Remake { get; private set; }

    /// <summary>The remake beside the classic effect (else at the same place, both drawn).</summary>
    public bool SideBySide { get; set; } = true;

    public bool Playing { get; set; } = true;

    /// <summary>The classic frame shown (0 .. frames - 1).</summary>
    public int Frame { get; private set; }

    /// <summary>Draws in the frame shown (for the dock and tests).</summary>
    public int FrameDraws { get; private set; }

    public string Status { get; private set; } = "";

    private MeshInstance3D? _mesh;
    private double _clock;
    private readonly Dictionary<string, Texture2D?> _textures = new();
    private readonly Dictionary<(string, EffectDrawView.Look), ShaderMaterial> _materials = new();

    public override void _Ready()
    {
        if (!Engine.IsEditorHint() || Effect.Length == 0)
            return;
        Build();
    }

    /// <summary>Builds (again) the classic replay and the remake.</summary>
    public void Build()
    {
        foreach (var c in GetChildren())
        {
            RemoveChild(c);
            c.QueueFree();
        }
        Classic = Remake = null;
        _mesh = null;
        Recording = EffectRecorder.Read(EffectRecorder.PathOf(Effect));
        Classic = new Node3D { Name = "Classic" };
        AddChild(Classic);
        if (Recording != null)
        {
            _mesh = new MeshInstance3D { Name = "Draws", Mesh = new ArrayMesh(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            Classic.AddChild(_mesh);
            ShowFrame(0);
        }
        BuildRemake();
        Status = Recording == null
            ? $"{Effect}: no recording yet (play the game where it shows: it records each effect once a run)"
            : $"{Effect}: {Recording.Frames.Count} frames recorded on {Recording.Level}{(Remake != null ? ", remade" : "")}";
        RemasterPlugin.Instance?.EffectBuilt(this);
    }

    /// <summary>The remade scene registered for the id (the convention's file or a registry entry), instanced beside the classic effect.</summary>
    public void BuildRemake()
    {
        if (Remake != null)
        {
            RemoveChild(Remake);
            Remake.QueueFree();
            Remake = null;
        }
        var path = MediaStore.EffectScene(Effect);
        if (path == null || ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace) is not PackedScene ps || ps.Instantiate() is not Node3D node)
            return;
        Remake = new Node3D { Name = "Remake" };
        Remake.AddChild(node);
        Remake.Position = SideBySide ? new Vector3(Width() * 1.2f + 4f, 0, 0) : Vector3.Zero;
        AddChild(Remake);
    }

    /// <summary>The classic effect's width over its recording (x extent, render units; at least 6).</summary>
    public float Width()
    {
        if (Recording == null)
            return 6f;
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var f in Recording.Frames)
            foreach (var d in f)
                foreach (var p in d.Positions)
                    if (float.IsFinite(p.X))
                    {
                        lo = Mathf.Min(lo, p.X);
                        hi = Mathf.Max(hi, p.X);
                    }
        return hi > lo ? Mathf.Max(6f, hi - lo) : 6f;
    }

    /// <summary>Shows classic frame <paramref name="k"/> (clamped).</summary>
    public void ShowFrame(int k)
    {
        if (Recording == null || _mesh?.Mesh is not ArrayMesh mesh || Recording.Frames.Count == 0)
            return;
        Frame = Mathf.Clamp(k, 0, Recording.Frames.Count - 1);
        mesh.ClearSurfaces();
        int n = 0;
        foreach (var d in Recording.Frames[Frame])
        {
            if (d.Indices.Length == 0)
                continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = d.Positions;
            arrays[(int)Mesh.ArrayType.Color] = d.Colours;
            arrays[(int)Mesh.ArrayType.TexUV] = d.Uvs;
            arrays[(int)Mesh.ArrayType.Index] = d.Indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, Material(d));
            n++;
        }
        FrameDraws = n;
    }

    private ShaderMaterial Material(EffectRecorder.Draw d)
    {
        var key = (d.Texture, d.Look);
        if (_materials.TryGetValue(key, out var m))
            return m;
        Texture2D? t = null;
        if (d.Look.Textured && d.Texture.Length > 0 && d.Texture != EffectDrawView.FrameBufferName)
        {
            if (!_textures.TryGetValue(d.Texture, out t))
            {
                var px = TextureFetcher.Fetch("runtime", d.Texture);
                if (px != null)
                {
                    var img = px.ToImage();
                    img.GenerateMipmaps();
                    t = ImageTexture.CreateFromImage(img);
                }
                _textures[d.Texture] = t;
            }
        }
        m = EffectDrawView.MakeMaterial(d.Look, t, d.Texture == EffectDrawView.FrameBufferName);
        _materials[key] = m;
        return m;
    }

    /// <summary>The textures found for the replay (for tests).</summary>
    public int TexturesFound
    {
        get
        {
            int n = 0;
            foreach (var t in _textures.Values)
                if (t != null)
                    n++;
            return n;
        }
    }

    public override void _Process(double delta)
    {
        if (!Engine.IsEditorHint() || !Playing || Recording == null || Recording.Frames.Count == 0)
            return;
        _clock += delta;
        if (_clock >= 1.0 / 30)
        {
            _clock = 0;
            ShowFrame((Frame + 1) % Recording.Frames.Count);
        }
    }
}
#endif
