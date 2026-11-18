using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// The root of a 2D room rebuilt in 3D (R7b; docs/remaster-plan.md, "R7b in detail"; docs/remake.md,
/// "Rebuilt 2D rooms"): a remade map scene (G6's path: <c>res://remake/map/&lt;key&gt;.tscn</c>, the
/// registry, packs) for a map the game draws as painted layers. The scene holds references only:
/// <see cref="RoomProxy"/> nodes for the layers (by 2D scene and layer) and the floor, and the
/// user's own geometry. When the room is built (<see cref="Build"/>: in the game from the map's 2D
/// layers the view loaded, in the editor through <see cref="Provider"/>) each layer proxy gets a
/// plane at the layer's depth, the floor the walk mesh, and every proxy (and any mesh with the
/// metadata <c>ffx_layer</c> = <c>"scene:layer"</c>) the layer's picture projected from its scene's
/// fixed camera (<see cref="ClassicMaterials.ProjectedLayerShader"/>), drawn as the layer pass
/// draws it: from the game's camera the rebuilt room looks like the classic one, a starting point
/// for modelling. The pictures come from the player's archive at run time; none of it is saved
/// (<see cref="Node.NotificationEditorPreSave"/>).
/// <para>
/// In the game the room is drawn with the game's camera and its 2D window (the squeeze and pan,
/// <see cref="GameProjection"/>), the characters on the walk mesh as on any map; the proxies follow
/// the 2D scene and the layers the game shows (<see cref="Tick"/>). The game's camera is fixed in
/// these rooms: the rebuilt scene keeps it (no free or other perspective).
/// </para>
/// </summary>
[Tool, GlobalClass]
public partial class RebuiltRoom : Node3D
{
    /// <summary>The game's level (<c>/map/ptkl/ptkl14/</c>).</summary>
    [Export] public string Level { get; set; } = "";

    /// <summary>Metadata on any mesh under the room: <c>"scene:layer"</c>, the layer projected onto it.</summary>
    public const string LayerMeta = "ffx_layer";

    private static readonly Regex LayerName = new(@"scene(\d+)layer(\d+)", RegexOptions.CultureInvariant);

    /// <summary>One classic layer: its scene and id, its pixel rectangle, depth, UVs at the corners, colour, picture and material.</summary>
    public sealed record LayerSource(int Scene, int Id, Rect2 Pixels, float Z, Vector4 Uv, Color VertexColour,
        Texture2D? Texture, ShaderMaterial Classic);

    /// <summary>What the editor builds a room from: the level's 2D-layer import and its game data (null: not ready yet).</summary>
    public static System.Func<string, (Node3D? Layers, MapGameData? Data)?>? Provider { get; set; }

    public bool Built { get; private set; }

    /// <summary>The layers the room was built from, by (scene, id).</summary>
    public readonly Dictionary<(int Scene, int Id), LayerSource> Layers = new();

    /// <summary>The proxies and tagged meshes with their layer, as built.</summary>
    public readonly List<(MeshInstance3D Mesh, LayerSource Layer)> Projected = new();

    /// <summary>The 2D scenes' fixed cameras (render space) by scene.</summary>
    public readonly Dictionary<int, Transform3D> Cameras = new();

    /// <summary>The camera's vertical field of view (degrees) by scene.</summary>
    public readonly Dictionary<int, float> Fovs = new();

    /// <summary>The game data the room was built with (null: none).</summary>
    public MapGameData? Data { get; private set; }

    /// <summary>The farthest view distance a plane is put at (the camera's far plane, a little inside).</summary>
    public float Far { get; private set; } = 19000f;

    /// <summary><c>1.5 map_pan</c>: the main camera's clip y above the layer camera's (0 on ptkl14 and slik04).</summary>
    public float MapPanY { get; private set; }

    /// <summary>The editor's guides (the camera, walk mesh, triggers, entrances): not saved, not in the game.</summary>
    public Node3D? Guides { get; private set; }

    /// <summary>What the build found, for the log and the checks.</summary>
    public string Summary { get; private set; } = "not built";

    public override void _Ready()
    {
        if (Engine.IsEditorHint())
            SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (!Engine.IsEditorHint() || Built || Provider == null || Level.Length == 0)
            return;
        if (Provider(Level) is { Layers: { } layers } src)
            Build(layers, src.Data, 19000f, 0f, atc: true, guides: true);
    }

    // ---- building ------------------------------------------------------------------------------

    /// <summary>The classic layers of a 2D-layer import (meshes <c>scene&lt;s&gt;layer&lt;l&gt;Shape</c>).</summary>
    public static Dictionary<(int Scene, int Id), LayerSource> ReadLayers(Node layers)
    {
        var found = new Dictionary<(int, int), LayerSource>();
        foreach (var n in layers.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (n is not MeshInstance3D { Mesh: { } mesh } mi || LayerName.Match(mi.Name) is not { Success: true } m || mesh.GetSurfaceCount() == 0)
                continue;
            var arrays = mesh.SurfaceGetArrays(0);
            var pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var cols = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
            if (pos.Length == 0 || uvs.Length != pos.Length)
                continue;
            // the quad's corners: its smallest and largest pixel, and the UVs there
            int lo = 0, hi = 0;
            for (int i = 1; i < pos.Length; i++)
            {
                if (pos[i].X + pos[i].Y < pos[lo].X + pos[lo].Y)
                    lo = i;
                if (pos[i].X + pos[i].Y > pos[hi].X + pos[hi].Y)
                    hi = i;
            }
            var rect = new Rect2(pos[lo].X, pos[lo].Y, pos[hi].X - pos[lo].X, pos[hi].Y - pos[lo].Y);
            if (rect.Size.X <= 0 || rect.Size.Y <= 0)
                continue;
            if ((mi.GetSurfaceOverrideMaterial(0) ?? mesh.SurfaceGetMaterial(0)) is not ShaderMaterial sm || !ClassicMaterials.IsLayer(sm))
                continue;
            using var texV = sm.GetShaderParameter(Variants.Name("tex"));
            var key = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            found[key] = new LayerSource(key.Item1, key.Item2, rect, pos[lo].Z,
                new Vector4(uvs[lo].X, uvs[lo].Y, uvs[hi].X, uvs[hi].Y), cols.Length > 0 ? cols[0] : Colors.White,
                texV.AsGodotObject() as Texture2D, sm);
        }
        return found;
    }

    /// <summary>A scene's fixed camera in render space (eye, looking at the target), and its vertical field of view.</summary>
    public static (Transform3D Camera, float FovY)? CameraOf(MapGameData? data, int scene)
    {
        if (data == null)
            return null;
        foreach (var c in data.Cameras)
            if (c.Scene == scene && c.Sets)
            {
                var up = c.Up.LengthSquared() > 0 ? c.Up.Normalized() : Vector3.Up;
                return (new Transform3D(Basis.LookingAt(c.Target - c.Eye, up), c.Eye), c.VerticalFovDegrees);
            }
        return null;
    }

    /// <summary>Where PS2 screen pixel <paramref name="px"/> at view distance <paramref name="d"/> of a scene's camera is (render space).</summary>
    public static Vector3 Unproject(Transform3D camera, float fovY, Vector2 px, float d, float mapPanY = 0)
    {
        float tv = Mathf.Tan(Mathf.DegToRad(fovY) / 2f), th = tv * 4f / 3f;
        float nx = (px.X - 256f) / 256f, ny = -(px.Y - 224f) / 224f + mapPanY;
        return camera * new Vector3(nx * d * th, ny * d * tv, -d);
    }

    /// <summary>
    /// Builds the room: the proxies' planes, the floor, the projected materials (from
    /// <paramref name="layers"/>, the level's 2D-layer import, and <paramref name="data"/>, its
    /// game data); <paramref name="far"/> the camera's far plane, <paramref name="mapPan"/> the map's
    /// own term of the 2D window, <paramref name="atc"/> the map's layer pass (cut-outs at 0.5),
    /// <paramref name="guides"/> the editor's guides. Again on a later call: rebuilt.
    /// </summary>
    public void Build(Node layers, MapGameData? data, float far, float mapPan, bool atc, bool guides = false)
    {
        Clear();
        Data = data;
        Far = far * 0.99f;
        MapPanY = 1.5f * mapPan;
        foreach (var (k, v) in ReadLayers(layers))
            Layers[k] = v;
        foreach (var (scene, _) in Layers.Keys)
            if (!Cameras.ContainsKey(scene) && CameraOf(data, scene) is { } cam)
            {
                Cameras[scene] = cam.Camera;
                Fovs[scene] = cam.FovY;
            }
        int planes = 0, floors = 0, tagged = 0, missing = 0;
        foreach (var n in FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (n is not MeshInstance3D mi || Guides != null && Guides.IsAncestorOf(mi))
                continue;
            if (mi is RoomProxy p)
            {
                if (Source(p) is not { } src || !Cameras.ContainsKey(src.Scene))
                {
                    missing++;
                    continue;
                }
                p.Source = src;
                if (p.Mesh == null || p.GeneratedMesh)
                {
                    p.Mesh = p.Kind == RoomProxyKind.Floor ? FloorMesh() : Plane(src);
                    p.GeneratedMesh = p.Mesh != null;
                }
                if (p.Kind == RoomProxyKind.Floor)
                    floors++;
                else
                    planes++;
                Project(p, src, atc, p.Kind == RoomProxyKind.Floor ? false : null);
            }
            else if (mi.HasMeta(LayerMeta) && ParseLayer(mi.GetMeta(LayerMeta).AsString()) is { } key
                && Layers.TryGetValue(key, out var src) && Cameras.ContainsKey(src.Scene))
            {
                tagged++;
                Project(mi, src, atc, null);
            }
        }
        if (guides)
            MakeGuides();
        Built = true;
        Summary = $"{Layers.Count} layer(s) in {Cameras.Count} scene(s); {planes} layer plane(s), {floors} floor(s), {tagged} tagged mesh(es)"
            + (missing > 0 ? $", {missing} proxy(ies) without their layer or camera" : "");
        GD.Print($"ffx: rebuilt room {PartIds.LevelKey(Level)}: {Summary}");
    }

    private LayerSource? Source(RoomProxy p)
    {
        if (p.Kind == RoomProxyKind.Layer || p.Layer >= 0)
            return Layers.TryGetValue((p.Scene, p.Layer), out var s) ? s : null;
        // the floor: the scene's farthest layer
        LayerSource? far = null;
        foreach (var l in Layers.Values)
            if (l.Scene == p.Scene && (far == null || l.Z > far.Z))
                far = l;
        return far;
    }

    private static (int, int)? ParseLayer(string s)
    {
        var parts = s.Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) ? (a, b) : null;
    }

    /// <summary>The view distance a layer is drawn at: 10 z, inside the far plane (the layer shader's).</summary>
    public float DepthOf(LayerSource l) => Mathf.Min(10f * l.Z, Far);

    /// <summary>A plane over the layer's pixel rectangle at its depth, facing the scene camera (render space, the room's frame).</summary>
    private ArrayMesh? Plane(LayerSource l)
    {
        var cam = Cameras[l.Scene];
        float fov = Fovs[l.Scene], d = DepthOf(l);
        var r = l.Pixels;
        var corners = new[] { r.Position, new Vector2(r.End.X, r.Position.Y), r.End, new Vector2(r.Position.X, r.End.Y) };
        var inv = GlobalTransformOrIdentity().AffineInverse();
        var v = new Vector3[4];
        for (int i = 0; i < 4; i++)
            v[i] = inv * Unproject(cam, fov, corners[i], d, MapPanY);
        var normal = (inv.Basis * cam.Basis.Z).Normalized();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v;
        arrays[(int)Mesh.ArrayType.Normal] = new[] { normal, normal, normal, normal };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>The walk mesh the map starts with, its walkable triangles (render space, the room's frame).</summary>
    private ArrayMesh? FloorMesh()
    {
        if (Data?.Main is not { } walk)
            return null;
        var inv = GlobalTransformOrIdentity().AffineInverse();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int n = 0;
        for (int t = 0; t < walk.Count; t++)
        {
            if (!WalkMesh.Walkable(walk.Kind(t)))
                continue;
            var (a, b, c) = walk.Corners(t);
            foreach (var p in new[] { a, b, c })
                st.AddVertex(inv * p);
            n++;
        }
        if (n == 0)
            return null;
        st.GenerateNormals();
        return st.Commit();
    }

    private Transform3D GlobalTransformOrIdentity() => IsInsideTree() ? GlobalTransform : Transform;

    /// <summary>Gives <paramref name="mi"/> the projected picture of <paramref name="l"/> (its material override, not saved).</summary>
    private void Project(MeshInstance3D mi, LayerSource l, bool atc, bool? depthWrite)
    {
        var classic = l.Classic.Shader;
        if (ClassicMaterials.ProjectedLayerShader(classic, depthWrite) is not { } shader)
            return;
        var m = new ShaderMaterial { Shader = shader };
        Variants.SetParameter(m, "tex", l.Texture);
        using (var mc = l.Classic.GetShaderParameter(Variants.Name("material_colour")))
            if (mc.VariantType != Variant.Type.Nil)
                m.SetShaderParameter(Variants.Name("material_colour"), mc);
        m.SetShaderParameter(Variants.Name("vertex_colour"), l.VertexColour);
        bool writes = depthWrite ?? ClassicMaterials.LayerWritesDepth(classic);
        m.SetShaderParameter(Variants.Name("cut_alpha"), atc && writes ? 0.5f : 0f);
        var cam = Cameras[l.Scene];
        float tv = Mathf.Tan(Mathf.DegToRad(Fovs[l.Scene]) / 2f);
        m.SetShaderParameter(Variants.Name("scene_view"), new Projection(cam.AffineInverse()));
        m.SetShaderParameter(Variants.Name("scene_tan"), new Vector2(tv * 4f / 3f, tv));
        m.SetShaderParameter(Variants.Name("map_pan_y"), MapPanY);
        m.SetShaderParameter(Variants.Name("layer_rect"), new Vector4(l.Pixels.Position.X, l.Pixels.Position.Y, l.Pixels.End.X, l.Pixels.End.Y));
        m.SetShaderParameter(Variants.Name("layer_uv"), l.Uv);
        // farther first, as the layer pass draws them (MapLayers.SetBlend); the floor before all
        m.RenderPriority = depthWrite == false && mi is RoomProxy { Kind: RoomProxyKind.Floor } ? -128 : Priority(l);
        mi.MaterialOverride = m;
        mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        // the layers' planes are backdrops: the alignment check judges the floor and the user's geometry
        if (mi is RoomProxy { Kind: RoomProxyKind.Layer })
            mi.SetMeta("ffx_alignment_skip", true);
        Projected.Add((mi, l));
    }

    private int Priority(LayerSource l)
    {
        // the layers' order by depth, farther first, as render priorities from -127
        int farther = 0;
        foreach (var o in Layers.Values)
            if (o.Scene == l.Scene && (o.Z > l.Z || o.Z == l.Z && o.Id < l.Id))
                farther++;
        return Mathf.Clamp(farther - 127, -127, 127);
    }

    /// <summary>Undoes a build: the generated meshes and materials, the guides.</summary>
    public void Clear()
    {
        foreach (var (mi, _) in Projected)
            if (IsInstanceValid(mi))
            {
                mi.MaterialOverride = null;
                if (mi is RoomProxy { GeneratedMesh: true } p)
                {
                    p.Mesh = null;
                    p.GeneratedMesh = false;
                }
            }
        Projected.Clear();
        Layers.Clear();
        Cameras.Clear();
        Fovs.Clear();
        if (Guides != null && IsInstanceValid(Guides))
        {
            RemoveChild(Guides);
            Guides.QueueFree();
        }
        Guides = null;
        Built = false;
    }

    // ---- in the game ---------------------------------------------------------------------------

    /// <summary>
    /// Shows what the game shows this frame: the proxies of the 2D scene <paramref name="scene"/>
    /// whose layer <paramref name="shown"/> says is drawn (the scripts' <c>PhyreScene_show2DLayer</c>);
    /// returns how many are shown.
    /// </summary>
    public int Tick(int scene, System.Func<int, int, bool> shown)
    {
        int n = 0;
        foreach (var (mi, l) in Projected)
        {
            bool on = l.Scene == scene && shown(l.Scene, l.Id) && (mi is not RoomProxy p || p.Drawn);
            if (mi.Visible != on)
                mi.Visible = on;
            if (on)
                n++;
        }
        return n;
    }

    // ---- the editor's guides -------------------------------------------------------------------

    private static StandardMaterial3D Guide(Color c) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = c,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        RenderPriority = 1,
    };

    /// <summary>The scene cameras, the walk mesh, the triggers and entrances as unsaved guides (the editor).</summary>
    private void MakeGuides()
    {
        Guides = new Node3D { Name = "Guides" };
        Guides.SetMeta("ffx_alignment_skip", true);
        AddChild(Guides);
        var inv = GlobalTransformOrIdentity().AffineInverse();
        foreach (var (scene, cam) in Cameras)
            Guides.AddChild(new Camera3D { Name = $"Scene{scene}Camera", Transform = inv * cam, Fov = Fovs[scene], Near = 1f, Far = 20000f });
        if (Data?.Main is { } walk)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Lines);
            for (int t = 0; t < walk.Count; t++)
            {
                var (a, b, c) = walk.Corners(t);
                foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                {
                    st.AddVertex(inv * (p + Vector3.Up * 0.3f));
                    st.AddVertex(inv * (q + Vector3.Up * 0.3f));
                }
            }
            Guides.AddChild(new MeshInstance3D { Name = "WalkMesh", Mesh = st.Commit(), MaterialOverride = Guide(new Color(0.2f, 1f, 0.4f, 0.8f)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        if (Data != null)
        {
            var triggers = new Node3D { Name = "Triggers" };
            Guides.AddChild(triggers);
            foreach (var w in Data.Workers())
            {
                if (w.ExtendedLine() is { } line)
                {
                    var st = new SurfaceTool();
                    st.Begin(Mesh.PrimitiveType.Triangles);
                    var h = Vector3.Up * 16f;
                    var (a, b) = line;
                    foreach (var v in new[] { a, b, b + h, a, b + h, a + h })
                        st.AddVertex(inv * v);
                    triggers.AddChild(new MeshInstance3D
                    {
                        Name = $"{w.Event}_w{w.Index}", Mesh = st.Commit(),
                        MaterialOverride = Guide(w.Exits.Count > 0 ? new Color(1f, 0.3f, 0.1f, 0.5f) : new Color(1f, 0.85f, 0.1f, 0.4f)),
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    });
                }
                else if (w.BoxTransform(30f) is { } box)
                    triggers.AddChild(new MeshInstance3D
                    {
                        Name = $"{w.Event}_w{w.Index}", Mesh = new BoxMesh { Size = Vector3.One }, Transform = inv * box,
                        MaterialOverride = Guide(new Color(0.9f, 0.2f, 0.9f, 0.3f)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    });
            }
            var entrances = new Node3D { Name = "Entrances" };
            Guides.AddChild(entrances);
            foreach (var e in Data.Events)
                foreach (var en in e.Entrances)
                {
                    var dir = MapGameData.Facing(en.Yaw);
                    var arrow = new MeshInstance3D
                    {
                        Name = $"{e.Name}_e{en.Index}", Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 2f, Height = 6f },
                        MaterialOverride = Guide(new Color(0.3f, 0.7f, 1f, 0.8f)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    };
                    var basis = Basis.Identity;
                    if (dir.LengthSquared() > 0)
                        basis = new Basis(new Quaternion(Vector3.Up, dir.Normalized()));
                    arrow.Transform = inv * new Transform3D(basis, en.Position + Vector3.Up * 3f);
                    entrances.AddChild(arrow);
                }
        }
    }

    // ---- saving --------------------------------------------------------------------------------

    private readonly List<(MeshInstance3D Mesh, Mesh? Generated, Material? Material)> _stripped = new();

    public override void _Notification(int what)
    {
        if (what == NotificationEditorPreSave)
        {
            // only references in the file: the planes, the walk mesh, the pictures are the player's
            _stripped.Clear();
            foreach (var (mi, _) in Projected)
                if (IsInstanceValid(mi))
                {
                    var gen = mi is RoomProxy { GeneratedMesh: true } ? mi.Mesh : null;
                    _stripped.Add((mi, gen, mi.MaterialOverride));
                    mi.MaterialOverride = null;
                    if (gen != null)
                        mi.Mesh = null;
                }
        }
        else if (what == NotificationEditorPostSave)
        {
            foreach (var (mi, gen, mat) in _stripped)
                if (IsInstanceValid(mi))
                {
                    if (gen != null)
                        mi.Mesh = gen;
                    mi.MaterialOverride = mat;
                }
            _stripped.Clear();
        }
    }

    /// <summary>
    /// A new rebuilt room for <paramref name="level"/> (not yet built): a layer proxy for every
    /// layer of <paramref name="layers"/> under <c>Layers</c>, the floor, an empty <c>Model</c>
    /// group for the user's geometry. The caller packs and saves it.
    /// </summary>
    public static RebuiltRoom Create(string level, Node layers)
    {
        var key = PartIds.LevelKey(level);
        var root = new RebuiltRoom { Name = key, Level = level };
        var group = new Node3D { Name = "Layers" };
        root.AddChild(group);
        group.Owner = root;
        var found = new List<LayerSource>(ReadLayers(layers).Values);
        found.Sort((a, b) => a.Scene != b.Scene ? a.Scene.CompareTo(b.Scene) : a.Id.CompareTo(b.Id));
        var scenes = new SortedSet<int>();
        foreach (var l in found)
        {
            scenes.Add(l.Scene);
            var p = new RoomProxy { Name = $"Scene{l.Scene}Layer{l.Id}", Kind = RoomProxyKind.Layer, Scene = l.Scene, Layer = l.Id };
            group.AddChild(p);
            p.Owner = root;
        }
        foreach (var s in scenes)
        {
            var floor = new RoomProxy { Name = scenes.Count == 1 ? "Floor" : $"Scene{s}Floor", Kind = RoomProxyKind.Floor, Scene = s, Layer = -1 };
            root.AddChild(floor);
            floor.Owner = root;
        }
        var model = new Node3D { Name = "Model" };
        model.SetMeta("ffx_note", "Your geometry for the room (render space, the game's units). A mesh with the metadata ffx_layer = \"scene:layer\" shows that layer projected from the scene's camera.");
        root.AddChild(model);
        model.Owner = root;
        return root;
    }

    /// <summary>The rebuilt room under a remade map's node (its wrapper's child), or null.</summary>
    public static RebuiltRoom? In(Node? map)
    {
        if (map is RebuiltRoom r)
            return r;
        return map != null && map.GetChildCount() > 0 ? map.GetChild(0) as RebuiltRoom : null;
    }
}
