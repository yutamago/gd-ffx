#if TOOLS
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The game's own data on an open map (docs/remaster-editor.md, "The game's data"; R5), as
/// read-only nodes under <c>GameData</c>: the walk meshes (each triangle coloured by its kind),
/// the event scripts' triggers (lines, boxes; the ones leading to another map named by it), the
/// entrances, the actors' start places, the cameras (the 2D scenes' fixed ones, the game's as it
/// last showed the map) and the characters where the game last had them, and the alignment
/// check's marks. Each group toggles with its eye; a node's info shows in the inspector's
/// "FFX game data" section (metadata <see cref="Info"/>). None of it is saved: the group is the
/// editor's (<see cref="MapEditRoot.EditorNode"/>), rebuilt each time the map opens, its nodes
/// locked against the gizmos.
/// </summary>
public sealed class GameDataEdit
{
    /// <summary>Metadata on a game-data node: what it is, in words (the inspector shows it).</summary>
    public const string Info = "ffx_game";

    /// <summary>Metadata on a trigger node that leads to another map: the target's map number (the inspector's Open).</summary>
    public const string TargetMap = "ffx_game_target";

    public required MapEditRoot Root { get; init; }
    public MapGameData? Data { get; private set; }
    public MapCapture? Capture { get; private set; }

    public Node3D Group { get; private set; } = null!;
    public Node3D WalkMeshes { get; private set; } = null!;
    public Node3D Triggers { get; private set; } = null!;
    public Node3D Entrances { get; private set; } = null!;
    public Node3D Actors { get; private set; } = null!;
    public Node3D Cameras { get; private set; } = null!;
    public Node3D Characters { get; private set; } = null!;
    public Node3D AlignmentMarks { get; private set; } = null!;

    /// <summary>The last alignment check (null: not run).</summary>
    public AlignmentReport? Report { get; private set; }

    /// <summary>The parts the capture placed where the game had them, and the morphs set from it.</summary>
    public int Placed { get; private init; }
    public int Morphed { get; private init; }

    public static readonly string[] GroupNames = ["WalkMesh", "Triggers", "Entrances", "Actors", "Cameras", "Characters", "Alignment"];

    /// <summary>Reads the map's data (the archive) and capture (what the game last showed) and builds the nodes.</summary>
    public static GameDataEdit Build(MapEditRoot root, FfxGame? game, MapCatalogue? catalogue, MapCapture? capture = null, int placed = 0, int morphed = 0)
    {
        var e = new GameDataEdit { Root = root, Placed = placed, Morphed = morphed };
        var t0 = Time.GetTicksMsec();
        e.Data = game != null ? MapGameData.Read(game, root.Level) : null;
        e.Capture = capture ?? MapCapture.Read(root.Level);
        e.Make(catalogue);
        GD.Print($"ffx remaster: {root.Key}: game data {e.Describe()} ({Time.GetTicksMsec() - t0} ms)");
        return e;
    }

    public string Describe()
    {
        if (Data == null)
            return "not read (no archive?)";
        var walk = Data.Main;
        return $"{Data.WalkMeshes.Count} walk mesh(es) (the first {walk?.Count ?? 0} triangles), {Triggers.GetChildCount()} trigger(s), "
            + $"{Entrances.GetChildCount()} entrance(s), {Actors.GetChildCount()} actor start(s), {Cameras.GetChildCount()} camera(s); "
            + (Capture == null ? "no capture (play the map once: script-moved parts at their file places)"
                : $"the capture of map 0x{Capture.Map:x} ({Placed} part(s) where the game had them, {Morphed} morph(s), {Characters.GetChildCount()} character(s))");
    }

    // ---- building --------------------------------------------------------------------------

    private Node3D NewGroup(Node parent, string name)
    {
        var g = new Node3D { Name = name };
        Lock(g);
        parent.AddChild(g);
        g.Owner = Root;
        return g;
    }

    private static void Lock(Node n)
    {
        n.SetMeta(MapEditRoot.EditorNode, true);
        n.SetMeta("_edit_lock_", true);
    }

    private T Add<T>(Node3D parent, T n, string info) where T : Node3D
    {
        Lock(n);
        n.SetMeta(Info, info);
        parent.AddChild(n);
        n.Owner = Root;
        return n;
    }

    private void Make(MapCatalogue? catalogue)
    {
        Group = new Node3D { Name = "GameData" };
        Lock(Group);
        Group.SetMeta(Info, "The game's own data on this map (read-only, not saved): walk meshes, triggers, entrances, actors, cameras.");
        Root.AddChild(Group);
        Group.Owner = Root;
        WalkMeshes = NewGroup(Group, "WalkMesh");
        Triggers = NewGroup(Group, "Triggers");
        Entrances = NewGroup(Group, "Entrances");
        Actors = NewGroup(Group, "Actors");
        Cameras = NewGroup(Group, "Cameras");
        Characters = NewGroup(Group, "Characters");
        AlignmentMarks = NewGroup(Group, "Alignment");
        if (Data != null)
        {
            MakeWalk();
            MakeTriggers(catalogue);
            MakeEntrances();
            MakeActors();
            MakeSceneCameras();
        }
        if (Capture != null)
            MakeCapture();
    }

    private static StandardMaterial3D Unshaded(Color c, bool vertexColours = false) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = c.A < 1 || vertexColours ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        AlbedoColor = c,
        VertexColorUseAsAlbedo = vertexColours,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = false,
        RenderPriority = 1,
    };

    /// <summary>A walk-mesh triangle's colour by its kind (translucent).</summary>
    public static Color KindColour(int kind) => kind switch
    {
        0 => new Color(0.2f, 0.85f, 0.35f, 0.35f),
        1 => new Color(0.95f, 0.15f, 0.15f, 0.5f),
        14 => new Color(1f, 0.55f, 0.1f, 0.5f),
        2 or 13 => new Color(0.95f, 0.9f, 0.2f, 0.5f),
        >= 0x30 and <= 0x3f => new Color(0.3f, 0.45f, 1f, 0.45f),
        _ => new Color(0.6f, 0.6f, 0.6f, 0.4f),
    };

    private const float Lift = 0.15f;

    private void MakeWalk()
    {
        foreach (var w in Data!.WalkMeshes)
        {
            var kinds = new SortedDictionary<int, int>();
            var faces = new SurfaceTool();
            faces.Begin(Mesh.PrimitiveType.Triangles);
            var edges = new SurfaceTool();
            edges.Begin(Mesh.PrimitiveType.Lines);
            var up = Vector3.Up * Lift;
            for (int t = 0; t < w.Count; t++)
            {
                var k = w.Kind(t);
                kinds[k] = kinds.GetValueOrDefault(k) + 1;
                var (a, b, c) = w.Corners(t);
                faces.SetColor(KindColour(k));
                faces.AddVertex(a + up);
                faces.AddVertex(b + up);
                faces.AddVertex(c + up);
                edges.SetColor(new Color(0.05f, 0.25f, 0.1f, 0.9f));
                foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                {
                    edges.AddVertex(p + up * 1.5f);
                    edges.AddVertex(q + up * 1.5f);
                }
            }
            var info = new StringBuilder($"Walk mesh page {w.Page} of {w.File}: {w.Count} triangles over {w.Vertices.Length} vertices"
                + (w.Page == 0 ? " (the map starts on this page)" : " (a page the scripts switch to)") + "\nKinds:");
            foreach (var (k, n) in kinds)
                info.Append($"\n  {n} {WalkMesh.KindName(k)}");
            var mesh = Add(WalkMeshes, new MeshInstance3D
            {
                Name = $"Page{w.Page}", Mesh = faces.Commit(), MaterialOverride = Unshaded(Colors.White, true),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = w == Data.Main,
            }, info.ToString());
            mesh.SetMeta("ffx_walk_page", w.Page);
            Add(mesh, new MeshInstance3D
            {
                Name = "Edges", Mesh = edges.Commit(), MaterialOverride = Unshaded(Colors.White, true),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            }, info.ToString());
        }
    }

    private static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string P(Vector3 v) => $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";

    /// <summary>Where a map number leads, by the catalogue: "Besaid - Crossroads (bsil02, map 0x14)".</summary>
    public static string Target(MapCatalogue? catalogue, int map)
    {
        var e = catalogue?.FindMap(map);
        return e == null ? $"map 0x{map:x}" : $"{e.Title()} ({e.Key}, map 0x{map:x})";
    }

    private static Color TriggerColour(GameWorker w) => (w.Type, w.Exits.Count > 0) switch
    {
        (_, true) => new Color(1f, 0.3f, 0.1f, 0.55f),
        (2, _) => new Color(1f, 0.85f, 0.1f, 0.45f),
        (3, _) => new Color(0.9f, 0.2f, 0.9f, 0.35f),
        (5, _) => new Color(0.2f, 0.85f, 0.95f, 0.3f),
        _ => new Color(0.75f, 0.5f, 0.95f, 0.3f),
    };

    /// <summary>A line trigger's height as drawn (the game's lines are crossed in x and z: any height).</summary>
    public const float LineHeight = 16f;

    /// <summary>A box of any height drawn this high.</summary>
    public const float BoxHeight = 30f;

    private void MakeTriggers(MapCatalogue? catalogue)
    {
        foreach (var w in Data!.Workers())
        {
            var exits = string.Join("\n", w.Exits.Select(x => $"  to {Target(catalogue, x.Map)}, entrance {x.Entrance} (entry {x.Entry})"));
            var head = $"Worker {w.Event}/{w.Index}: {GameWorker.TypeName(w.Type)}";
            var name = $"{w.Event}_w{w.Index}";
            if (w.ExtendedLine() is { } line)
            {
                var (a, b) = line;
                var st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                var h = Vector3.Up * LineHeight;
                foreach (var v in new[] { a, b, b + h, a, b + h, a + h })
                    st.AddVertex(v);
                var info = $"{head}\nLine {P(w.Line!.Value.A)} to {P(w.Line!.Value.B)}"
                    + (w.LineExtend is { } e ? $", extended {F(e.X)} back and {F(e.Y)} front" : "")
                    + (w.Exits.Count > 0 ? $"\nLeads (crossing it runs the entry that changes the map):\n{exits}" : "")
                    + (w.Triggers.Count > 0 ? $"\nTriggers enabled: {string.Join(", ", w.Triggers)}" : "");
                var n = Add(Triggers, new MeshInstance3D
                {
                    Name = name, Mesh = st.Commit(), MaterialOverride = Unshaded(TriggerColour(w)),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                }, info);
                Tag(n, w, catalogue);
            }
            else if (w.BoxTransform(BoxHeight) is { } box)
            {
                var info = $"{head}\nBox at {P(w.Point!.Value)}, half extents {F(w.Box!.Value.X)} x {F(w.Box.Value.Y)}"
                    + (w.Box.Value.Z > 0 ? $", half height {F(w.Box.Value.Z)}" : ", any height") + $", turned {F(Mathf.RadToDeg(w.BoxYaw))}°"
                    + (w.Exits.Count > 0 ? $"\nLeads:\n{exits}" : "");
                var n = Add(Triggers, new MeshInstance3D
                {
                    Name = name, Mesh = new BoxMesh { Size = Vector3.One }, MaterialOverride = Unshaded(TriggerColour(w)),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                }, info);
                n.Transform = box with { Origin = box.Origin + Vector3.Up * (w.Box.Value.Z > 0 ? 0 : BoxHeight / 2) };
                Tag(n, w, catalogue);
            }
        }
    }

    private void Tag(Node3D n, GameWorker w, MapCatalogue? catalogue)
    {
        n.SetMeta("ffx_game_worker", $"{w.Event}/{w.Index}");
        if (w.Exits.Count == 0)
            return;
        n.SetMeta(TargetMap, w.Exits[0].Map);
        var mid = n is MeshInstance3D { Mesh: { } m } ? m.GetAabb().GetCenter() : Vector3.Zero;
        Add(n, new Label3D
        {
            Name = "Label", Text = $"to {Target(catalogue, w.Exits[0].Map)}" + (w.Exits.Count > 1 ? $" (+{w.Exits.Count - 1})" : ""),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true, PixelSize = 0.0015f, FontSize = 28,
            Modulate = new Color(1f, 0.6f, 0.3f), NoDepthTest = true, Position = mid + Vector3.Up * LineHeight * 0.75f,
        }, n.GetMeta(Info).AsString());
    }

    private static Mesh Arrow()
    {
        // a cone along -z (Godot's forward) on a short shaft, 10 units long
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int sides = 8;
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides, a1 = Mathf.Tau * (i + 1) / sides;
            var p0 = new Vector3(Mathf.Cos(a0) * 2.5f, Mathf.Sin(a0) * 2.5f + 3f, 0);
            var p1 = new Vector3(Mathf.Cos(a1) * 2.5f, Mathf.Sin(a1) * 2.5f + 3f, 0);
            var tip = new Vector3(0, 3f, -10f);
            st.AddVertex(p0);
            st.AddVertex(p1);
            st.AddVertex(tip);
            st.AddVertex(p1);
            st.AddVertex(p0);
            st.AddVertex(new Vector3(0, 3f, 0));
        }
        return st.Commit();
    }

    /// <summary>A transform at <paramref name="at"/> whose forward (-z) is the game's facing <paramref name="yaw"/>.</summary>
    public static Transform3D Facing(Vector3 at, float yaw)
    {
        var f = MapGameData.Facing(yaw);
        return new Transform3D(Basis.LookingAt(f, Vector3.Up), at);
    }

    private void MakeEntrances()
    {
        var arrow = Arrow();
        foreach (var ev in Data!.Events)
            foreach (var en in ev.Entrances)
            {
                var info = $"Entrance {en.Index} of {ev.Name} (map 0x{ev.Map:x}): where the player arrives by it\n"
                    + $"At {P(en.Position)}, facing {F(Mathf.RadToDeg(en.Yaw))}°, camera entry {en.Camera}, map index {en.MapIndex}";
                var n = Add(Entrances, new MeshInstance3D
                {
                    Name = $"{ev.Name}_e{en.Index}", Mesh = arrow, MaterialOverride = Unshaded(new Color(0.2f, 0.6f, 1f)),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                }, info);
                n.Transform = Facing(en.Position, en.Yaw);
                Add(n, new Label3D
                {
                    Name = "Label", Text = $"{ev.Name} entrance {en.Index}", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    FixedSize = true, PixelSize = 0.0015f, FontSize = 24, Modulate = new Color(0.5f, 0.8f, 1f), Position = Vector3.Up * 14,
                }, info);
            }
    }

    private void MakeActors()
    {
        var capsule = new CapsuleMesh { Radius = 3f, Height = 17f };
        var arrow = Arrow();
        foreach (var w in Data!.Workers())
        {
            if (w.Type != 1 || w.Position is not { } p)
                continue;
            var info = $"Worker {w.Event}/{w.Index}: an actor{(w.Model != null ? $", model {w.Model}" : "")}, placed by its script at {P(p)}"
                + (w.Yaw is { } y ? $", facing {F(Mathf.RadToDeg(y))}°" : "")
                + (w.TalkRange is { } tr ? $", talk range {F(tr)}" : "")
                + (w.Exits.Count > 0 ? $"\nChanges the map: {string.Join(", ", w.Exits.Select(x => $"0x{x.Map:x}/{x.Entrance}"))}" : "")
                + "\n(the start the script's init sets; events move actors later)";
            var n = Add(Actors, new MeshInstance3D
            {
                Name = $"{w.Event}_w{w.Index}{(w.Model != null ? "_" + w.Model : "")}", Mesh = capsule,
                MaterialOverride = Unshaded(new Color(0.95f, 0.9f, 0.3f, 0.45f)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = p + Vector3.Up * 8.5f,
            }, info);
            n.SetMeta("ffx_game_worker", $"{w.Event}/{w.Index}");
            if (w.Yaw is { } yaw)
            {
                var a = Add(n, new MeshInstance3D { Name = "Facing", Mesh = arrow, MaterialOverride = Unshaded(new Color(0.95f, 0.8f, 0.2f)) }, info);
                a.GlobalTransform = Facing(p, yaw);
            }
            Add(n, new Label3D
            {
                Name = "Label", Text = w.Model ?? $"w{w.Index}", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true,
                PixelSize = 0.0015f, FontSize = 22, Position = Vector3.Up * 11,
            }, info);
        }
    }

    private void MakeSceneCameras()
    {
        foreach (var c in Data!.Cameras)
        {
            if (!c.Sets)
                continue;
            var cam = Add(Cameras, new Camera3D
            {
                Name = $"Scene{c.Scene}", Fov = c.VerticalFovDegrees, Far = 20000f, Near = 1f,
            }, $"The fixed camera of 2D scene {c.Scene} ({c.Layers} layer(s); the YNSC block of {c.File}): eye {P(c.Eye)}, "
                + $"looking at {P(c.Target)}, field of view {F(c.FovDegrees)}° ({F(c.VerticalFovDegrees)}° vertical), y offset {c.YOffset}\n"
                + "Look through it: the 3D view's Preview with the camera selected.");
            cam.LookAtFromPosition(c.Eye, c.Target, c.Up.LengthSquared() > 0 ? c.Up.Normalized() : Vector3.Up);
        }
    }

    /// <summary>The capture: the game's camera, the characters, and the parts the game moves where it had them (with their morphs).</summary>
    private void MakeCapture()
    {
        var c = Capture!;
        if (c.CameraValid)
        {
            var cam = Add(Cameras, new Camera3D { Name = "Game", Fov = Mathf.RadToDeg(c.FovY), Far = c.Far > 0 ? c.Far : 20000f, Near = 1f },
                $"The game's camera as it last showed the map here (map 0x{c.Map:x}, entrance {c.Entrance}, event {c.Event}, story {c.Story}): "
                + $"at {P(c.Camera.Origin)}, vertical field of view {F(Mathf.RadToDeg(c.FovY))}°, aspect {F(c.Aspect)}\n"
                + "Look through it: the 3D view's Preview with the camera selected.");
            cam.Transform = new Transform3D(c.Camera.Basis.Orthonormalized(), c.Camera.Origin);
        }
        var capsule = new CapsuleMesh { Radius = 3f, Height = 17f };
        foreach (var a in c.Characters)
        {
            if (a.Hidden != 0 && !a.Player)
                continue;
            var info = $"{(a.Player ? "The player" : "A character")} ({a.Model}, slot {a.Slot}) where the game had it at the capture: {P(a.Position)}, "
                + $"facing {F(Mathf.RadToDeg(a.Yaw))}°";
            var n = Add(Characters, new MeshInstance3D
            {
                Name = $"{(a.Player ? "Player" : "Slot" + a.Slot)}_{a.Model}", Mesh = capsule,
                MaterialOverride = Unshaded(a.Player ? new Color(0.3f, 1f, 0.6f, 0.6f) : new Color(0.8f, 0.8f, 0.8f, 0.4f)),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = a.Position + Vector3.Up * 8.5f,
            }, info);
            Add(n, new Label3D
            {
                Name = "Label", Text = a.Model, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true,
                PixelSize = 0.0015f, FontSize = 22, Position = Vector3.Up * 11,
            }, info);
        }
    }

    /// <summary>
    /// The script-moved parts where the game had them (the capture): each placed part's node at
    /// its captured world, its classic base moved there too, so edits stay offsets on the game's
    /// placement (as the game applies them); the display objects' morph values on their meshes.
    /// Before the project's overrides are applied.
    /// </summary>
    public static (int Placed, int Morphed) PlaceFromCapture(MapEditRoot root, MapCapture capture)
    {
        int placed = 0, morphed = 0;
        foreach (var p in capture.Parts)
        {
            if (!root.Parts.TryGetValue(p.Id, out var info) || info.Node.GetParent() is not Node3D parent)
                continue;
            if (p.World.Basis.Determinant() == 0)
                continue;
            var world = root.GlobalTransform * p.World;
            info.BaseLocal = parent.GlobalTransform.AffineInverse() * world;
            info.Node.Transform = info.BaseLocal;
            info.GamePlaced = true;
            placed++;
        }
        if (root.Map != null && capture.Morphs.Count > 0)
            foreach (var n in root.Map.FindChildren("*", nameof(MeshInstance3D), true, false))
                if (n is MeshInstance3D { Mesh: { } mesh } mi && StateView3D.MorphOf(mi, mesh) is { } m
                    && capture.Morphs.TryGetValue(m.ObjId, out var v) && v != Vector2.Zero)
                {
                    StateView3D.ApplyMorph(m, v);
                    morphed++;
                }
        return (placed, morphed);
    }

    // ---- the alignment check ---------------------------------------------------------------

    /// <summary>
    /// The remade geometry of the open map: every added node's meshes and every replacement's
    /// (the parts the project replaces), plus <paramref name="extra"/> (a remade map's scene
    /// placed in the map: <see cref="CheckScene"/>). Nodes with the metadata
    /// <c>ffx_alignment_skip</c> are left out (a sky dome).
    /// </summary>
    public List<Surface> RemadeSurfaces(Node? extra = null)
    {
        var list = new List<Surface>();
        static bool Skip(Node n) => n.HasMeta("ffx_alignment_skip") || n is Light3D or ReflectionProbe or Camera3D or FogVolume;
        foreach (var (node, _) in Root.Added())
            Alignment.Collect(node, Root.GlobalTransform, list, $"added {node.Name}", Skip);
        foreach (var p in Root.Order)
            if (p.Preview is { } h && GodotObject.IsInstanceValid(h) && h.IsInsideTree())
                Alignment.Collect(h, Root.GlobalTransform, list, $"replaces {p.Short(Root.Model)}", Skip);
        if (extra != null)
            Alignment.Collect(extra, Root.GlobalTransform, list, null, Skip);
        return list;
    }

    /// <summary>Runs the check over the walk mesh the map starts with and marks each flagged triangle.</summary>
    public AlignmentReport? Check(Node? extra = null, float tolerance = Alignment.DefaultTolerance,
        float clearance = Alignment.DefaultClearance)
    {
        if (Data?.Main is not { } walk)
            return null;
        var r = Alignment.Check(walk, RemadeSurfaces(extra), tolerance, clearance);
        Report = r;
        foreach (var c in AlignmentMarks.GetChildren())
        {
            AlignmentMarks.RemoveChild(c);
            c.QueueFree();
        }
        if (r.Issues.Count > 0)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            var up = Vector3.Up * Lift * 3;
            foreach (var i in r.Issues)
            {
                var (a, b, c) = walk.Corners(i.Triangle);
                st.SetColor(i.Kind == AlignmentKind.Blocked ? new Color(1f, 0.1f, 0.4f, 0.7f) : new Color(1f, 0.8f, 0f, 0.7f));
                st.AddVertex(a + up);
                st.AddVertex(b + up);
                st.AddVertex(c + up);
            }
            Add(AlignmentMarks, new MeshInstance3D
            {
                Name = "Flagged", Mesh = st.Commit(), MaterialOverride = Unshaded(Colors.White, true),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            }, $"The alignment check: {r.Summary()}\nMagenta: blocked (a remade surface within {r.Clearance} units over the walk mesh); "
                + $"yellow: floating (the remade ground more than {r.Tolerance} under it).");
        }
        GD.Print($"ffx remaster: {Root.Key}: alignment: {r.Summary()}");
        return r;
    }

    /// <summary>The check with a remade map's scene placed in the map (render space, at the map's root), not kept.</summary>
    public AlignmentReport? CheckScene(string path)
    {
        if (ResourceLoader.Load(path) is not PackedScene ps || ps.Instantiate() is not Node3D scene)
            return null;
        Root.AddChild(scene);
        try
        {
            return Check(scene);
        }
        finally
        {
            Root.RemoveChild(scene);
            scene.Free();
        }
    }

    /// <summary>The last check's issue <paramref name="index"/> (the dock's list).</summary>
    public AlignmentIssue? Issue(int index) => Report != null && index >= 0 && index < Report.Issues.Count ? Report.Issues[index] : null;

    public Node3D? GroupByName(string name) => name switch
    {
        "WalkMesh" => WalkMeshes,
        "Triggers" => Triggers,
        "Entrances" => Entrances,
        "Actors" => Actors,
        "Cameras" => Cameras,
        "Characters" => Characters,
        "Alignment" => AlignmentMarks,
        _ => null,
    };
}
#endif
