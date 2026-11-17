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
/// poses <c>SkeletonNode_i</c>). A character whose model is not loaded yet is
/// not drawn; without classic content (or with <c>--stand-ins</c>) it is a
/// stand-in, a marker with skeleton points and its model's name. A map
/// change is held (<see cref="Holding"/>, <see cref="TransitionView"/>) until
/// the new map and its characters are ready (docs/architecture.md, "Map
/// transitions").
/// </summary>
public partial class StateView3D : Node3D
{
    private Camera3D _camera = null!;
    private long _lookOf = -1;
    private string _lookCached = "";
    private (bool On, float None, float Full, float Gain, float Spread) _glow = (false, -1, -1, -1, -1);
    private WorldEnvironment _env = null!;
    /// <summary>Forward+'s post effect (the game's colours and glow; null under Compatibility).</summary>
    private ClassicPost? _post;
    /// <summary>The remastered render path's lighting (on while the shown map draws remastered).</summary>
    private Remaster.RemasterLighting _rig = null!;

    /// <summary>The remastered path's lighting (for tests).</summary>
    public Remaster.RemasterLighting Rig => _rig;
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

    /// <summary>The shaders' clock set this frame (the game's <c>GlobalTime</c>, interpolated; -1 none).</summary>
    public float GlobalTime { get; private set; } = -1f;

    /// <summary>The level whose map is shown ("" none yet).</summary>
    public string MapLevel => _mapLevel;

    /// <summary>The map's 2D layers.</summary>
    public MapLayers Layers => _layers;

    private readonly ClothView _cloth = new();

    /// <summary>The characters' simulated hair and cloth.</summary>
    public ClothView Cloth => _cloth;

    /// <summary>Whether the cloth texture is rebuilt this frame (<see cref="ClothView.Refresh"/>).</summary>
    private bool _clothRebuild;

    /// <summary>Characters drawn with their classic model, and how many of their bones are posed.</summary>
    public (int Models, int PosedBones) Posed { get; private set; }

    private sealed class ActorNode
    {
        public required Node3D Root;
        /// <summary>The stand-in (a marker, skeleton points, the model's name): made only when stand-ins are shown.</summary>
        public MeshInstance3D? Marker;
        public MultiMeshInstance3D? Bones;
        public Label3D? Label;
        public int Kind;
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
        /// <summary>The decode (<see cref="GameStateView.Decoded"/>) the locals were last made at: older ones may name reused actors.</summary>
        public long LocalsAt = -1;
        /// <summary>The skeleton's transform in the model's space (inverted).</summary>
        public Transform3D SkeletonInModelInv = Transform3D.Identity;
        /// <summary>
        /// A model without a skin (rigid parts, e.g. the save sphere f001): each mesh with its
        /// segments' model id (the import's <c>ffx_model_id</c>) and its parent's transform in the
        /// model (inverted).
        /// </summary>
        public (Node3D Part, int ModelId, Transform3D ParentInv)[] Rigid = [];
        /// <summary>The model's cloth meshes (null: none; <see cref="ClothView"/>).</summary>
        public ClothView.Model? Cloth;
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
        // Forward+: the buffer holds the game's values, the game's glow and the decode after the
        // transparent pass (ClassicPost); Compatibility writes them as they are
        if (ClassicPost.Needed() && !GameSettings.Flag("no-classic-post"))
        {
            _post = new ClassicPost();
            _env.Compositor = new Compositor { CompositorEffects = [_post] };
        }
        AddChild(_env);
        _layers = new MapLayers { Name = "MapLayers" };
        AddChild(_layers);
        AddChild(new EffectView { Name = "EffectView" });
        AddChild(new EffectDrawView { Name = "EffectDrawView" });
        var light = new DirectionalLight3D { RotationDegrees = new Vector3(-55, 30, 0), LightEnergy = 0.8f };
        AddChild(light);
        // the remastered render path's lighting (R3; off while the map draws classic)
        _rig = new Remaster.RemasterLighting(_env, _post, light);
        AddChild(_rig);
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
        AddChild(root);
        return new ActorNode { Root = root, Kind = kind };
    }

    /// <summary>The character's stand-in nodes, made on first use.</summary>
    private void MakeStandIn(ActorNode n)
    {
        if (n.Marker != null)
            return;
        var mat = new StandardMaterial3D { AlbedoColor = KindColor(n.Kind) };
        n.Marker = new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(3f, 6f, 3f) }, MaterialOverride = mat };
        n.Root.AddChild(n.Marker);
        n.Bones = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _boneMesh },
            MaterialOverride = mat,
        };
        AddChild(n.Bones);
        n.Label = new Label3D { Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.05f, FontSize = 48 };
        AddChild(n.Label);
    }

    /// <summary>
    /// Whether stand-ins are drawn for what has no model yet (characters here, effects in
    /// <see cref="EffectView"/>): without classic content, or with <c>--stand-ins</c> (for
    /// development). With classic content a character is drawn once its model is loaded.
    /// </summary>
    public static bool StandIns =>
        GameSettings.Flag("stand-ins") || GameHost.Instance?.Classic is not { Open: true };

    public override void _Process(double delta)
    {
        var host = GameHost.Instance;
        if (host == null)
            return;
        var view = host.View;
        var t0 = Perf.Start();
        var cam = view.Camera();
        if (cam is { } c)
        {
            _camera.GlobalTransform = c.Transform;
            // a check's view (--camera-pitch=DEG): the game's camera turned up by DEG degrees
            // about its own x axis (the sky over a map, where the game's camera does not look)
            if (CameraPitch != 0)
                _camera.RotateObjectLocal(Vector3.Right, Mathf.DegToRad(CameraPitch));
            _camera.Far = Mathf.Clamp(c.Far, 100f, 20000f);
            GameProjection.Apply(_camera, c, _mapLevel.Length > 0);
            // the game's screen shake (Effects.ShakeX/Y, in the PS2 screen's pixels, 448 high):
            // as the same share of the view at a subject 100 units away
            var fx = view.Current?.Effects;
            float unit = 2f * Mathf.Tan(c.FovY / 2f) * 100f / 448f;
            _camera.HOffset = (fx?.ShakeX ?? 0f) * unit;
            _camera.VOffset = -(fx?.ShakeY ?? 0f) * unit;
        }
        // the look changes with the snapshot (30 a second), not every frame
        if (view.Decoded != _lookOf)
        {
            _lookOf = view.Decoded;
            ClassicMaterials.SetLook(view.Current?.Look);
            // the game clears to the map's background colour (Look.background), drawn as it is
            if (view.Current?.Look is { Valid: true } look)
            {
                var bg = new Color(look.Background.X, look.Background.Y, look.Background.Z);
                // Forward+ clears with the colour taken as sRGB (decoded to its linear buffer):
                // encoded here, so the buffer holds the game's value
                if (_post != null)
                    bg = bg.LinearToSrgb();
                if (_env.Environment.BackgroundColor != bg)
                    _env.Environment.BackgroundColor = bg;
                ApplyGlow(look);
                // the editor shows the game's lights and fog from what the game last showed (once a map a run)
                // (a check writes it only into --remaster-looks=DIR: never the player's)
                if (_lookCached != _mapLevel && _mapLevel.Length > 0 && _map != null
                    && (!UserSettings.IsCheck() || GameSettings.Arg("remaster-looks") is { Length: > 0 }))
                {
                    _lookCached = _mapLevel;
                    if (Remaster.LookCache.Write(_mapLevel, look))
                        GD.Print($"ffx: look of {Remaster.PartIds.LevelKey(_mapLevel)} kept for the editor ({Remaster.LookCache.PathOf(_mapLevel)})");
                }
            }
        }
        // the scrolling UVs' and the water's clock, every frame (between snapshots too); held
        // under the game's ESC menu, whose picture the original freezes (afterGraphicSync stops
        // swapping) while the game's GlobalTime runs on (docs/architecture.md, "The ESC menu")
        if (view.Current?.Menus.EscMenu != true || GlobalTime < 0)
            GlobalTime = ClassicMaterials.SetTime(view.Previous, view.Current, view.Alpha);
        UpdateMap(host.Classic, view.Current);
        UpdateRenderPath(view);
        _prefetch.Tick(host.Classic, host.Game, view.Current, _mapLevel, _parked?.Level ?? "", ObjectIndex);
        _layers.Tick(view.Current, view.Current?.World.Map ?? -1, _camera.Far, cam);
        Perf.End(Perf.Section.View3D, t0);
        t0 = Perf.Start();
        UpdateObjects(view);
        _overrides?.Update();
        LookAtObject();
        Perf.End(Perf.Section.Objects, t0);
        t0 = Perf.Start();
        _clothRebuild = _cloth.Refresh(host.Game, view);
        Perf.End(Perf.Section.Cloth, t0);
        t0 = Perf.Start();
        UpdateActors(host.Classic, view);
        LookAtActor();
        UpdateTransition(host.Classic, view.Current);
        Perf.End(Perf.Section.Actors, t0);
    }

    // ---- the map ----------------------------------------------------------------

    /// <summary>
    /// The game's glow pass (docs/formats/postfx.md in ffx-rust, "Glow"): a glow map of what is
    /// brighter than <c>NoGlowThreshold</c> (full at <c>FullGlowThreshold</c>), blurred at half
    /// the picture's size (level 0; the game's levels 1 and 2 reach nothing) and added with
    /// <c>GlowGain</c>, the down pass's two blurs each times <c>spread + 1</c>. Under Forward+
    /// <see cref="ClassicPost"/> draws it as the game does. Under Compatibility Godot's
    /// environment glow stands in: its level 1 (half size) only, its ramp
    /// <c>smoothstep(N, N + hdr_scale, max(c))</c> from N over <c>F - N</c>, intensity
    /// <c>GlowGain (spread + 1)²</c> (the blur's shape and the map's colour are Godot's own;
    /// Besaid 11.8 against the original where the three levels and a scale of <c>1 / (F - N)</c>
    /// gave 13.9, 13.3 without glow). <c>--no-glow</c> turns it off.
    /// </summary>
    private void ApplyGlow(GdFfx.Api.Look look)
    {
        var env = _env.Environment;
        bool on = look.GlowGain > 0 && !GameSettings.Flag("no-glow");
        var glow = (on, look.GlowNone, look.GlowFull, look.GlowGain, look.GlowSpread);
        if (glow == _glow || _rig.Active)
            return;
        _glow = glow;
        if (_post != null)
        {
            // Forward+: the game's own glow (ClassicPost)
            env.GlowEnabled = false;
            (_post.GlowOn, _post.GlowNone, _post.GlowFull, _post.GlowGain, _post.GlowSpread) = glow;
            return;
        }
        env.GlowEnabled = on;
        if (!on)
            return;
        env.GlowBlendMode = Environment.GlowBlendModeEnum.Additive;
        env.GlowBloom = 0f;
        env.GlowHdrThreshold = Mathf.Max(look.GlowNone, 0.35f);
        env.GlowHdrScale = Mathf.Max(look.GlowFull - look.GlowNone, 0.01f);
        float s = look.GlowSpread + 1f;
        env.GlowIntensity = look.GlowGain * s * s;
        for (int i = 0; i < 7; i++)
            env.SetGlowLevel(i, i == 0 ? 1f : 0f);
    }

    // ---- the render path ----------------------------------------------------------

    private string _pathLevel = "\0";
    private int _pathVersion = -1;
    private int _pathLighting = -1;
    private bool _pathRemastered, _pathLitBlended;

    /// <summary>Whether the shown map draws remastered now (for tests).</summary>
    public bool Remastered => _rig.Active;

    /// <summary>
    /// The shown map's look (docs/remaster-editor.md, "The render path"): remastered when its
    /// render path (or the options' choice, or the F7 toggle) says so, its parts with the lit
    /// materials (<see cref="Remaster.MapLook"/>) and the lighting rig on (<see cref="Remaster.RemasterLighting"/>:
    /// the map's lighting file or the defaults from the game's look); classic otherwise.
    /// A remade map keeps its own materials, the rig follows the choice.
    /// </summary>
    private void UpdateRenderPath(GameStateView view)
    {
        // the choice evaluated when the map or the choice changes, not every frame (it makes strings)
        if (_pathLevel != _mapLevel || _pathVersion != Remaster.RenderPaths.Version || _pathLighting != Remaster.LightingLayers.Version)
        {
            (_pathLevel, _pathVersion, _pathLighting) = (_mapLevel, Remaster.RenderPaths.Version, Remaster.LightingLayers.Version);
            _pathRemastered = Remaster.RenderPaths.Remastered(_mapLevel);
            _pathLitBlended = _pathRemastered && Remaster.LightingLayers.For(_mapLevel).Lighting.BlendedParts == Remaster.BlendedLook.Lit;
        }
        bool want = _map != null && _pathRemastered;
        if (_map is { } m && m.Name == "ClassicMap")
        {
            int n = Remaster.MapLook.Of(m).Set(want, _pathLitBlended);
            if (n > 0)
                GD.Print($"ffx: map {_mapLevel} drawn {(want ? "remastered" : "classic")} ({n} surface(s); lit materials: "
                    + $"{Remaster.RemasterLook.Count.Pbr} PBR, {Remaster.RemasterLook.Count.Plain} from the colour texture)");
        }
        bool was = _rig.Active;
        _rig.Set(want, view.Current?.Look, view.Decoded, _mapLevel, _map, _camera.GlobalPosition);
        if (was && !_rig.Active)
        {
            // the classic look again, from the next snapshot
            _lookOf = -1;
            _glow = (false, -1, -1, -1, -1);
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k && k.PhysicalKeycode == GameHost.RemasterKey
            && GameHost.Instance?.Captured != true && _mapLevel.Length > 0)
        {
            Remaster.RenderPaths.Toggle(_mapLevel);
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- the map's objects ------------------------------------------------------

    /// <summary>The classic map's mesh nodes by object id and mesh index (the import's object ids, else the names <c>N_kShape</c>).</summary>
    private Dictionary<(uint Id, int K), Node3D>? _objectNodes;

    /// <summary>The classic map's morphing meshes (<see cref="MapObjects"/>).</summary>
    private List<Morph> _morphs = [];

    /// <summary>The shown map's overrides (docs/remake.md, "Overrides"; null: none).</summary>
    private Remaster.MapOverrideRuntime? _overrides;

    /// <summary>The shown map's overrides (for checks and the editor; null: none).</summary>
    public Remaster.MapOverrideRuntime? Overrides => _overrides;

    /// <summary>The shown map's node (null: none, or 2D layers only).</summary>
    public Node3D? MapNode => _map;

    /// <summary>Whether the game places <paramref name="node"/> (a mesh of one of its map objects) each frame (for checks).</summary>
    public bool IsGamePlaced(Node3D node) => _objectNodes?.ContainsValue(node) == true;

    /// <summary>Whether the game morphs <paramref name="mesh"/> (for checks).</summary>
    public bool IsMorphed(MeshInstance3D mesh) => _morphs.Exists(m => m.Mesh == mesh);

    private long _objectsPrevOf = -1;
    private readonly Dictionary<uint, GdFfx.Api.SceneObject> _objectsPrev = new();
    private readonly Dictionary<uint, GdFfx.Api.SceneObject> _objectsCur = new();

    /// <summary>Objects placed by the game this frame (for tests).</summary>
    public int ObjectsPlaced { get; private set; }

    /// <summary>The snapshot's object meshes with no mesh node in the classic map this frame (for tests).</summary>
    public int ObjectsUnmatched { get; private set; }

    /// <summary>Morphing meshes the game drives, and how many have a weight other than 0 this frame (for tests).</summary>
    public (int Meshes, int Moved) Morphed { get; private set; }

    /// <summary>How a mesh morphs (docs/importer.md, "Morphing meshes").</summary>
    private enum MorphKind { Position, Weight, UvOffset }

    /// <summary>
    /// A morphing mesh: the instance whose weight the game sets by its <c>m_objID</c>
    /// (<c>MorphManager_setWeights</c>), its kind, the morph targets of a position morph (1:
    /// in1 - in0, weight w; 2: weights 1 - w, w) and the values last set.
    /// </summary>
    private sealed class Morph
    {
        public required MeshInstance3D Mesh;
        public required uint ObjId;
        public required MorphKind Kind;
        public int Targets;
        public Vector2 Last = new(float.NaN, float.NaN);
    }

    /// <summary>What <see cref="ObjectIndex"/> finds in a classic map (on the loading worker).</summary>
    private sealed record MapObjects(Dictionary<(uint Id, int K), Node3D> Nodes, List<Morph> Morphs);

    private static readonly System.Text.RegularExpressions.Regex Fbx = new("FBXASC(\\d{3})");
    private static readonly System.Text.RegularExpressions.Regex MeshName = new("^(\\d+)_(\\d+)Shape$");

    /// <summary>A node name with FBX's <c>FBXASCnnn</c> escapes decoded.</summary>
    public static string FbxName(string n) => Fbx.Replace(n, m => ((char)int.Parse(m.Groups[1].Value)).ToString());

    private void IndexObjects()
    {
        var x = _map == null || _map.Name != "ClassicMap" ? null : ObjectIndex(_map);
        _objectNodes = x?.Nodes ?? new();
        _morphs = x?.Morphs ?? [];
    }

    /// <summary>
    /// A classic map's mesh nodes by object id and mesh index, and its morphing meshes (on the
    /// loading worker thread: a search of every node). A mesh morphs as
    /// <c>MorphManager_registerClusterMorphs</c> (<c>0x006aca10</c>) registers its instance: with
    /// <c>m_animCt</c> above 0, by <c>m_mimeCt</c> 1 a position morph (its morph targets), 4 a
    /// vertex colour and 3 a UV morph (a material with the weight), else with
    /// <c>m_dObjFlag</c> <c>0x400</c> a UV offset (a material with the offset); the import's
    /// mesh extras <c>ffx_anim_ct</c>, <c>ffx_mime_ct</c>, <c>ffx_dobj_flag</c>, <c>ffx_obj_id</c>.
    /// </summary>
    private static MapObjects ObjectIndex(Node3D map)
    {
        var byName = new Dictionary<(uint Id, int K), Node3D>();
        // by the import's object id (extras ffx_obj_id, the instance's m_objID): each object's
        // meshes in instance order (ffx_instance), as the game lists them
        var byId = new Dictionary<uint, List<(long Instance, Node3D Node)>>();
        var morphs = new List<Morph>();
        foreach (var n in map.FindChildren("*", nameof(Node3D), true, false))
        {
            if (n is not Node3D n3)
                continue;
            if (MeshName.Match(FbxName(n3.Name)) is { Success: true } m)
                byName.TryAdd((uint.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)), n3);
            if (n3 is not MeshInstance3D { Mesh: { } mesh } mi)
                continue;
            if (ObjectOf(mesh) is { } o)
            {
                if (!byId.TryGetValue(o.Id, out var list))
                    byId[o.Id] = list = new();
                list.Add((o.Instance, n3));
            }
            if (MorphOf(mi, mesh) is { } morph)
                morphs.Add(morph);
        }
        // an import without the object ids (format 2 and older): by the meshes' names
        if (byId.Count == 0)
            return new MapObjects(byName, morphs);
        var index = new Dictionary<(uint Id, int K), Node3D>();
        foreach (var (id, list) in byId)
        {
            list.Sort((a, b) => a.Instance.CompareTo(b.Instance));
            for (int k = 0; k < list.Count; k++)
                index[(id, k)] = list[k].Node;
        }
        return new MapObjects(index, morphs);
    }

    /// <summary>
    /// A map mesh's object id and instance (the import's mesh extras <c>ffx_obj_id</c>,
    /// <c>ffx_instance</c>); null for an import without them. The game's scene lists an object's
    /// meshes by the instances' <c>m_objID</c> (<c>PhyreScene_rebuildMapObjects</c>), not by
    /// their names: at Besaid a mesh named for object 6 belongs to object 5.
    /// </summary>
    private static (uint Id, long Instance)? ObjectOf(Mesh mesh)
    {
        var x = MeshExtras.Of(mesh);
        if (x.ObjId is not { } id || x.Instance is not { } instance)
            return null;
        return ((uint)id, instance);
    }

    private static Morph? MorphOf(MeshInstance3D mi, Mesh mesh)
    {
        var x = MeshExtras.Of(mesh);
        if ((x.AnimCt ?? 0) <= 0 || x.ObjId is not { } objId)
            return null;
        var (mime, flag, id) = (x.MimeCt ?? 0, x.DobjFlag ?? 0, (uint)objId);
        int targets = mesh is ArrayMesh am ? am.GetBlendShapeCount() : 0;
        if (mime == 1 && targets > 0)
            return new Morph { Mesh = mi, ObjId = id, Kind = MorphKind.Position, Targets = targets };
        bool weight = false, offset = false;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var (w, o) = ClassicMaterials.Morphs(mi.GetActiveMaterial(s));
            weight |= w;
            offset |= o;
        }
        if ((mime == 4 || mime == 3) && weight)
            return new Morph { Mesh = mi, ObjId = id, Kind = MorphKind.Weight };
        if ((flag & 0x400) != 0 && offset)
            return new Morph { Mesh = mi, ObjId = id, Kind = MorphKind.UvOffset };
        return null;
    }

    /// <summary>
    /// The morphing meshes' weights from the snapshot (interpolated): a display object's morph
    /// value (<c>SceneObject.morph</c>, <c>morph_v</c>: <c>yiSetMIMeParamDObj</c> or a keyframe
    /// track's UV offset); an object that is not a display object keeps 0 (the morph manager's
    /// first value). Set only when it changes.
    /// </summary>
    // a string passed for a StringName makes a new one each call
    private static readonly StringName MorphParam = "ffx_morph", UvOffsetParam = "ffx_uv_offset";

    private void UpdateMorphs(GameStateView view)
    {
        int moved = 0;
        bool noMorph = GameSettings.Flag("no-morph");
        foreach (var m in _morphs)
        {
            var v = Vector2.Zero;
            // --no-morph: every mesh in its stored pose (a check's comparison)
            if (!noMorph && _objectsCur.TryGetValue(m.ObjId, out var o) && o.Dobj)
            {
                v = new Vector2(o.Morph, o.MorphV);
                // between the two snapshots, unless the value jumps (a scroll wrapping)
                if (_objectsPrev.TryGetValue(m.ObjId, out var p) && p.Dobj)
                {
                    var a = new Vector2(p.Morph, p.MorphV);
                    if ((v - a).Abs() is { X: < 0.5f, Y: < 0.5f })
                        v = a.Lerp(v, view.Alpha);
                }
            }
            if (v != Vector2.Zero)
                moved++;
            if (v == m.Last || !GodotObject.IsInstanceValid(m.Mesh))
                continue;
            m.Last = v;
            switch (m.Kind)
            {
                case MorphKind.Position when m.Targets == 1:
                    m.Mesh.SetBlendShapeValue(0, v.X);
                    break;
                case MorphKind.Position:
                    m.Mesh.SetBlendShapeValue(0, 1f - v.X);
                    m.Mesh.SetBlendShapeValue(1, v.X);
                    break;
                case MorphKind.Weight:
                    m.Mesh.SetInstanceShaderParameter(MorphParam, v.X);
                    break;
                case MorphKind.UvOffset:
                    m.Mesh.SetInstanceShaderParameter(UvOffsetParam, v);
                    break;
            }
        }
        Morphed = (_morphs.Count, moved);
    }

    /// <summary>
    /// The map's objects where the game has them (API 8's <c>Snapshot.objects</c>): mesh k of
    /// object N (the k-th mesh of object id N, <see cref="ObjectIndex"/>) at its live world matrix, interpolated between the
    /// last two snapshots, shown while the game draws it. Scripts move objects this way, e.g.
    /// Besaid's birds, which the file places at the origin.
    /// </summary>
    private void UpdateObjects(GameStateView view)
    {
        ObjectsPlaced = 0;
        ObjectsUnmatched = 0;
        var s = view.Current;
        if (s == null || _map == null)
            return;
        if (_objectNodes == null)
            IndexObjects();
        if (_objectNodes!.Count == 0 && _morphs.Count == 0)
            return;
        if (_objectsPrevOf != view.Decoded)
        {
            _objectsPrevOf = view.Decoded;
            _objectsPrev.Clear();
            _objectsCur.Clear();
            if (view.Previous is { } p && !ReferenceEquals(p, s) && p.World.Level == s.World.Level)
                foreach (var o in p.Objects)
                    _objectsPrev.TryAdd(o.Id, o);
            foreach (var o in s.Objects)
                _objectsCur.TryAdd(o.Id, o);
        }
        UpdateMorphs(view);
        var prev = _objectsPrev.Count > 0 ? _objectsPrev : null;
        foreach (var o in s.Objects)
            for (int k = 0; k < o.Meshes.Count; k++)
            {
                if (!_objectNodes.TryGetValue((o.Id, k), out var node))
                {
                    ObjectsUnmatched++;
                    continue;
                }
                var m = o.Meshes[k];
                var t = GameStateView.CameraTransform(m.World);
                if (prev != null && prev.TryGetValue(o.Id, out var po) && k < po.Meshes.Count)
                {
                    var a = GameStateView.CameraTransform(po.Meshes[k].World);
                    if (a.Origin.DistanceTo(t.Origin) < GameStateView.WarpDistance * 4)
                        t = a.InterpolateWith(t, view.Alpha);
                }
                // the map's overrides on top: an offset over the game's placement, a hidden part held hidden
                var drawn = m.Drawn;
                _overrides?.Place(node, ref t, ref drawn);
                if (t.Basis.Determinant() != 0)
                    node.GlobalTransform = t;
                node.Visible = drawn;
                ObjectsPlaced++;
            }
    }

    /// <summary>
    /// A check's view (<c>--look-at-object=N</c>): the camera 150 units from map object N's first
    /// mesh, looking at it (Besaid's birds fly far above the save 7 view).
    /// </summary>
    private void LookAtObject()
    {
        if (GameSettings.Arg("look-at-object") is not { } a || !uint.TryParse(a, out var id)
            || _objectNodes == null || !_objectNodes.TryGetValue((id, 0), out var n) || n is not MeshInstance3D mi)
            return;
        var c = mi.GlobalTransform * mi.GetAabb().GetCenter();
        _camera.GlobalTransform = Transform3D.Identity.LookingAt(Vector3.Forward, Vector3.Up).Translated(c + new Vector3(0, 40, 150));
        _camera.LookAt(c);
    }

    /// <summary>
    /// A check's view (<c>--look-at-actor=MODEL</c>, e.g. <c>w012</c>): the camera in front of the
    /// first shown character with that model, looking at its first cloth mesh (else its model),
    /// from three times the mesh's size (at least 12 units): the cloth's pictures close up.
    /// </summary>
    private void LookAtActor()
    {
        if (GameSettings.Arg("look-at-actor") is not { Length: > 0 } name)
            return;
        foreach (var n in _actors.Values)
        {
            if (n.ModelName != name || n.Model == null || !n.Root.Visible)
                continue;
            var mi = n.Cloth is { Meshes: [var first, ..] } ? first.Node : FirstMesh(n.Model);
            if (mi == null)
                continue;
            var box = mi.GetAabb();
            var c = mi.GlobalTransform * box.GetCenter();
            float d = Mathf.Max(12f, 3f * box.Size.Length());
            // from the game camera's side, level with the mesh
            var from = _camera.GlobalPosition - c;
            from.Y = 0;
            from = from.LengthSquared() > 1e-6f ? from.Normalized() : Vector3.Back;
            _camera.GlobalTransform = Transform3D.Identity.Translated(c + from * d + new Vector3(0, d * 0.2f, 0));
            _camera.LookAt(c);
            return;
        }
    }

    private static readonly float CameraPitch =
        float.TryParse(GameSettings.Arg("camera-pitch"), System.Globalization.CultureInfo.InvariantCulture, out var deg) ? deg : 0f;

    private static MeshInstance3D? FirstMesh(Node n)
    {
        if (n is MeshInstance3D mi)
            return mi;
        for (int i = 0, k = n.GetChildCount(); i < k; i++)
            if (FirstMesh(n.GetChild(i)) is { } found)
                return found;
        return null;
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
        // a map still loading in the background that the game left already (unless loaded ahead
        // for a likely next map: MapPrefetch holds or drops those)
        if (_wantedLevel.Length > 0 && _wantedLevel != level && !_prefetch.Holds(_wantedLevel))
            ContentResolver.Forget("map", _wantedLevel);
        _wantedLevel = level;
        // back to the map left last: it was kept
        if (_parked is { } parked && parked.Level == level)
        {
            _parked = null;
            Park(_mapLevel);
            _map = parked.Node;
            _objectNodes = parked.Nodes;
            _morphs = parked.Morphs;
            _overrides = parked.Overrides;
            _mapLevel = level;
            if (_map != null)
                AddChild(_map);
            _mapSource = "kept";
            GD.Print($"ffx: map {level} shown (kept from before)");
            return;
        }
        bool ahead = _prefetch.Holds(level);
        if (ContentResolver.Resolve(lib, "map", level, prepare: ObjectIndex) is not { } content)
        {
            // a map of 2D layers only has no 3D model: shown once its layers are
            if (lib?.Failed("map", level) == true && _layers.Level == level)
            {
                Park(_mapLevel);
                _objectNodes = null;
                _morphs = [];
                _overrides = null;
                _mapLevel = level;
                _mapSource = "2D layers only";
                GD.Print($"ffx: map {level} shown (2D layers only)");
            }
            return;
        }
        var map = content.Node;
        if (map != null)
            ClassicMaterials.FixLayers(map);
        Park(_mapLevel);
        _map = map;
        var objects = content.Remade ? null : content.Prepared as MapObjects;
        _objectNodes = objects?.Nodes;
        _morphs = objects?.Morphs ?? [];
        _overrides = content.Overrides;
        _mapLevel = level;
        _mapSource = content.Remade ? "remade" : ahead ? "loaded ahead" : "loaded then";
        if (map == null)
            return;
        map.Name = content.Remade ? "RemadeMap" : "ClassicMap";
        map.Scale = Vector3.One * content.MapScale;
        AddChild(map);
        GD.Print($"ffx: map {level} shown{(content.Remade ? $" (remade: {content.Path})" : "")}");
    }

    /// <summary>The map shown before the current one, out of the tree, with its object index (null none).</summary>
    private (string Level, Node3D? Node, Dictionary<(uint Id, int K), Node3D>? Nodes, List<Morph> Morphs,
        Remaster.MapOverrideRuntime? Overrides)? _parked;

    /// <summary>
    /// Takes the shown map out of the tree and keeps it as the one to come back to (the player
    /// often walks back through the exit they came by), dropping the one kept before.
    /// </summary>
    private void Park(string level)
    {
        if (_parked is { Node: { } old })
            old.QueueFree();
        _parked = null;
        if (_map != null && level.Length > 0 && !GameSettings.Flag("no-prefetch"))
        {
            RemoveChild(_map);
            _parked = (level, _map, _objectNodes, _morphs, _overrides);
        }
        else
            _map?.QueueFree();
        _map = null;
        _objectNodes = null;
        _morphs = [];
        _overrides = null;
    }

    private readonly MapPrefetch _prefetch = new();

    /// <summary>Where the map shown came from: loaded ahead, kept, loaded then (for the transitions' log).</summary>
    private string _mapSource = "";

    /// <summary>The next maps loaded ahead (for tests and the log).</summary>
    public MapPrefetch Prefetch => _prefetch;

    public override void _ExitTree()
    {
        if (_parked is { Node: { } n })
            n.QueueFree();
        _parked = null;
        foreach (var list in _pool.Values)
            foreach (var m in list)
                m.QueueFree();
        _pool.Clear();
    }

    // ---- map transitions ------------------------------------------------------------

    /// <summary>
    /// Whether the picture is held this frame: a map change is under way and the new map, its
    /// 2D layers or its characters are not ready (<see cref="UpdateTransition"/>).
    /// <see cref="TransitionView"/> then covers the 3D view with the last frame drawn before
    /// (black when the game's fade was), so the new map's characters never show over the old
    /// map, and the new map shows with its characters at once. <c>--no-hold</c> turns it off.
    /// </summary>
    public bool Holding { get; private set; }

    /// <summary>Whether a map change is under way (held unless <c>--no-hold</c>).</summary>
    public bool InTransition => _inTransition;

    /// <summary>Whether the game's fade holds the screen black this frame (a fade to or from black at 1).</summary>
    public bool ScreenBlack { get; private set; }

    /// <summary>Whether a map was shown since the start (before that the hold is black).</summary>
    public bool EverShown { get; private set; }

    /// <summary>Seconds a hold lasts at most (an import of a map not seen before takes a few).</summary>
    public const double HoldLimit = 10;

    /// <summary>
    /// Frames while not held with characters drawn on another map than theirs (the snapshot's
    /// level not the map shown) or with stand-ins drawn, and frames held (for the checks).
    /// </summary>
    public (int Leaked, int StandIns, int Held) Frames { get; private set; }

    /// <summary>
    /// One finished map change: the levels; the milliseconds from the game's map change request
    /// to the start of the change here (-1 none seen), and from that start to the snapshot's new
    /// level, to its map shown in the scene and to the picture shown (the hold's end); where the
    /// map came from.
    /// </summary>
    public readonly record struct Transition(string From, string To, double RequestMs, double LevelMs, double MapMs,
        double LayersMs, double LoadedMs, double ModelsMs, double ShownMs, string Source, bool TimedOut)
    {
        private string After(double ms) => ms >= 0 ? $"{ms - LevelMs:F0}" : "-";

        public override string ToString() =>
            $"{(From.Length > 0 ? From : "(none)")} -> {To}: shown {ShownMs - LevelMs:F0} ms after the game's new level "
            + $"(its map {After(MapMs)} ms, {Source}; 2D layers {After(LayersMs)}, the game's load done {After(LoadedMs)}, "
            + $"characters' models {After(ModelsMs)} ms)"
            + (RequestMs >= 0 ? $", {ShownMs + RequestMs:F0} ms after its map change request" : "")
            + $", held {ShownMs:F0} ms{(TimedOut ? " (gave up waiting)" : "")}";
    }

    /// <summary>The map changes so far (for the checks and the log).</summary>
    public List<Transition> Transitions { get; } = new();

    private bool _inTransition, _gaveUp, _changeWas;
    private long _tStart, _tChange = -1;
    private double _levelMs, _mapMs, _layersMs, _loadedMs, _modelsMs;
    private string _from = "", _source = "";

    private static double Ms(long from) => (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static bool IsMapLevel(string level) => level.StartsWith("/map/") || level.StartsWith("/btlmap/");

    /// <summary>Whether a screen filter holds the screen black (a fade to or from black at 1).</summary>
    private static bool Black(Snapshot s)
    {
        foreach (var f in s.Effects.Filters)
            if (f is { Active: true, Mode: 2 or 3 } && ScreenFilterView.Alpha(f.Mode, f.Frames, f.Level) >= 1f)
                return true;
        return false;
    }

    /// <summary>
    /// The map change's state, once a frame after the map and the characters: it starts when the
    /// snapshot's level is not the map shown (the game started a new map: its characters are
    /// in the snapshot already), while the game holds its update for a load
    /// (<c>World.scene_loading</c>), or once its map change request (<c>World.map_change</c>)
    /// has the screen black; it ends when the map (and on a field map its 2D layers) is shown,
    /// the game loads nothing, and every character the game draws has its model, or after
    /// <see cref="HoldLimit"/>. Meanwhile <see cref="Holding"/>.
    /// </summary>
    /// <summary>The map level of the last snapshot that had one (<see cref="UpdateTransition"/>).</summary>
    private string _seenLevel = "";

    private void UpdateTransition(ClassicLibrary? lib, Snapshot? s)
    {
        if (s == null)
            return;
        var w = s.World;
        var level = w.Level;
        bool classic = lib is { Open: true };
        bool mapPending = IsMapLevel(level) && level != _mapLevel
            && (classic || RemakeRegistry.Find("map", level) != null);
        bool layersPending = classic && level.StartsWith("/map/") && _layers.Level != level;
        if (w.MapChange && !_changeWas)
            _tChange = System.Diagnostics.Stopwatch.GetTimestamp();
        _changeWas = w.MapChange;
        ScreenBlack = Black(s);
        bool changing = w.MapChange && ScreenBlack;
        // a new level whose map was loaded ahead is shown in its first frame: still a change,
        // held for the characters' models
        var previousLevel = _seenLevel;
        bool levelChanged = IsMapLevel(level) && IsMapLevel(previousLevel) && level != previousLevel;
        if (IsMapLevel(level))
            _seenLevel = level;
        bool start = mapPending || layersPending || w.SceneLoading || changing || levelChanged;
        if (_mapLevel.Length > 0)
            EverShown = true;
        if (!start && ModelsPending == 0)
            _gaveUp = false;
        if (!_inTransition && start && !_gaveUp)
        {
            _inTransition = true;
            _tStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _from = levelChanged && _mapLevel == level ? previousLevel : _mapLevel;
            _levelMs = _mapMs = _layersMs = _loadedMs = _modelsMs = -1;
            _source = "";
        }
        if (_inTransition)
        {
            if (_levelMs < 0 && level != _from)
                _levelMs = Ms(_tStart);
            if (level != _from)
            {
                // the last of each step after the new level came (each can go back and forth)
                if (_mapLevel == level)
                {
                    if (_mapMs < 0)
                    {
                        _mapMs = Ms(_tStart);
                        _source = _mapSource;
                    }
                }
                else
                    _mapMs = -1;
                _layersMs = layersPending ? -1 : _layersMs < 0 ? Ms(_tStart) : _layersMs;
                _loadedMs = w.SceneLoading ? -1 : _loadedMs < 0 ? Ms(_tStart) : _loadedMs;
                _modelsMs = ModelsPending > 0 || mapPending ? -1 : _modelsMs < 0 ? Ms(_tStart) : _modelsMs;
            }
            bool ready = !start && ModelsPending == 0;
            bool timedOut = !ready && Ms(_tStart) > HoldLimit * 1000;
            if (ready || timedOut)
            {
                _inTransition = false;
                _gaveUp = timedOut;
                var now = Ms(_tStart);
                // the request came before the start (the fade out), at most a minute before
                double request = _tChange >= 0 ? (_tStart - _tChange) * 1000.0 / System.Diagnostics.Stopwatch.Frequency : -1;
                if (request > 60000)
                    request = -1;
                _tChange = -1;
                if (level != _from || timedOut)
                {
                    var t = new Transition(_from, level, request, Mathf.Max(_levelMs, 0), _mapMs, _layersMs, _loadedMs, _modelsMs,
                        now, _source.Length > 0 ? _source : "already shown", timedOut);
                    Transitions.Add(t);
                    GD.Print($"ffx: transition {t}");
                }
            }
        }
        Holding = _inTransition && !GameSettings.Flag("no-hold");
        // what a frame shows while not held: characters over another map than theirs, stand-ins
        var (leaked, standIns, held) = Frames;
        if (Holding)
            held++;
        else
        {
            if (IsMapLevel(level) && level != _mapLevel && classic && Posed.Models > 0)
                leaked++;
            if (StandInsDrawn > 0 && classic && !GameSettings.Flag("stand-ins"))
                standIns++;
        }
        Frames = (leaked, standIns, held);
    }

    // ---- the characters -------------------------------------------------------------

    /// <summary>Characters the game draws whose model is still coming (not drawn until it is), this frame.</summary>
    public int ModelsPending { get; private set; }

    /// <summary>Stand-ins (markers, skeleton points, names) drawn this frame.</summary>
    public int StandInsDrawn { get; private set; }

    private void UpdateActors(ClassicLibrary? lib, GameStateView view)
    {
        var seen = _seen;
        seen.Clear();
        int models = 0, posed = 0, attached = 0, pending = 0, standIns = 0;
        bool showStandIns = StandIns;
        bool cloth = false, rebuild = _clothRebuild;
        if (rebuild)
            _cloth.Begin();
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
                // a model a character of the last map left (the party, from map to map)
                if (TakePooled(a.Model) is { } pooled)
                    AttachModel(n, a.Model, pooled);
                // a few models enter the scene a frame (a scene's dozen at once was a 30 ms frame)
                else if (attached < AttachesPerFrame && ContentResolver.Resolve(lib, "chr", a.Model, a.Slot) is { } content)
                {
                    AttachModel(n, a.Model, content.Node);
                    attached++;
                }
                // its cloth placed in the texture from the next frame on
                if (n.Cloth != null)
                    _cloth.Touch();
            }
            n.Root.GlobalTransform = f.Root;
            var hasModel = n.Model != null;
            // a model still coming: classic content is there, the model has a name and has not
            // loaded or failed yet (AttachModel names it either way)
            bool coming = !hasModel && a.Model.Length > 0 && n.ModelName != a.Model && lib is { Open: true } && !lib.Failed("chr", a.Model);
            bool standIn = f.Shown && !hasModel && showStandIns;
            if (f.Shown && coming)
                pending++;
            n.Root.Visible = f.Shown && (hasModel || standIn);
            if (standIn || n.Marker != null)
            {
                MakeStandIn(n);
                n.Marker!.Visible = standIn;
                n.Bones!.Visible = standIn;
                n.Label!.Visible = standIn;
            }
            if (hasModel && f.Shown)
            {
                models++;
                posed += Pose(n, f, view);
                if (n.Cloth != null)
                {
                    cloth = true;
                    if (rebuild)
                    {
                        var tc = Perf.Start();
                        _cloth.Place(a.Slot, n.ModelName, n.Cloth, GameStateView.FieldTransform(a.World),
                            f.Previous is { } fp ? GameStateView.FieldTransform(fp.World) : null, a.Kind == 4);
                        Perf.End(Perf.Section.Cloth, tc);
                    }
                }
            }
            else if (standIn)
            {
                standIns++;
                Points(n, f);
                if (n.LabelPlayer != a.Player || n.Label!.Text.Length == 0)
                {
                    n.LabelPlayer = a.Player;
                    n.Label!.Text = $"{a.Model}{(a.Player ? " (player)" : "")}";
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
        if (rebuild)
        {
            var tc = Perf.Start();
            _cloth.Upload();
            Perf.End(Perf.Section.Cloth, tc);
        }
        _cloth.Wanted = cloth;
        Posed = (models, posed);
        ModelsPending = pending;
        StandInsDrawn = standIns;
    }

    private static void Points(ActorNode n, GameStateView.ActorFrame f)
    {
        var mm = n.Bones!.Multimesh;
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
        n.Label!.GlobalPosition = top + Vector3.Up * 4f;
    }

    private static void AttachModel(ActorNode n, string name, Node3D? model)
    {
        n.ModelName = name;
        if (model == null)
            return;
        model.Name = "Model";
        model.Transform = new Transform3D(FlipXY, Vector3.Zero);
        n.Root.AddChild(model);
        n.Model = model;
        // a remade character may opt out of the game's posing (docs/remake.md)
        if (!Content.Posed(model))
            return;
        // the game's cloth moves its hair and clothes (ClothView), on skinned models and on rigid
        // ones (a weapon's ribbon: w012's instance 1)
        n.Cloth = ClothView.Of(model);
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
    /// A skinless model's meshes and the game models they belong to: the model id of their
    /// segments (<c>PMeshSegment +0x18</c>, the import's mesh extras <c>ffx_model_id</c>), by which
    /// <c>graphicCharacterInstanceSetWorldMatrixByMdlID</c> places them; a mesh without one is
    /// mesh number id, as the game falls back to.
    /// </summary>
    private static (Node3D, int, Transform3D)[] RigidParts(Node3D model)
    {
        var list = new List<(Node3D, int, Transform3D)>();
        int k = 0;
        foreach (var x in model.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (x is not MeshInstance3D { Mesh: { } mesh } mi)
                continue;
            int id = MeshExtras.Of(mesh).ModelId is { } v ? (int)v : k;
            k++;
            Transform3D t = Transform3D.Identity;
            for (Node? p = mi.GetParent(); p != null && p != model; p = p.GetParent())
                if (p is Node3D p3)
                    t = p3.Transform * t;
            list.Add((mi, id, t.AffineInverse()));
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
            // Ch_DrawMain's walk 1: an unskinned model at the character's world times its base
            // element's matrix (here relative to the character: the element's)
            int moved = 0;
            foreach (var (part, id, parentInv) in n.Rigid)
            {
                if (id >= a.Models.Count || a.Models[id].Skinned)
                    continue;
                int i = a.Models[id].Base;
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
        // interpolates position, rotation and scale between the two. Locals of an older pair
        // than the last may be keyed by an actor object reused for a newer snapshot: dropped.
        if (n.LocalsAt != view.Decoded)
        {
            if (view.Decoded - n.LocalsAt > 1)
            {
                n.LocalsA = (null, n.LocalsA.Bones);
                n.LocalsB = (null, n.LocalsB.Bones);
            }
            n.LocalsAt = view.Decoded;
        }
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

    private void Free(ActorNode n, ushort slot)
    {
        if (n.Model == null && n.Asked.Length > 0)
            ContentResolver.Forget("chr", n.Asked, slot);
        // the model kept for the next character with it (a map change re-creates the party)
        if (n.Model != null)
        {
            n.Root.RemoveChild(n.Model);
            Pool(n.ModelName, n.Model);
        }
        n.Root.QueueFree();
        n.Bones?.QueueFree();
        n.Label?.QueueFree();
    }

    /// <summary>Character models out of the tree by name, for the next character with that model.</summary>
    private readonly Dictionary<string, List<Node3D>> _pool = new();

    /// <summary>The pool's models, oldest first (the first to go beyond <see cref="PoolSize"/>).</summary>
    private readonly List<(string Name, Node3D Model)> _poolOrder = new();

    /// <summary>Character models kept out of the tree at most.</summary>
    public const int PoolSize = 16;

    private void Pool(string name, Node3D model)
    {
        if (GameSettings.Flag("no-prefetch"))
        {
            model.QueueFree();
            return;
        }
        if (!_pool.TryGetValue(name, out var list))
            _pool[name] = list = new();
        list.Add(model);
        _poolOrder.Add((name, model));
        while (_poolOrder.Count > PoolSize)
        {
            var (oldName, old) = _poolOrder[0];
            _poolOrder.RemoveAt(0);
            _pool[oldName].Remove(old);
            old.QueueFree();
        }
    }

    private Node3D? TakePooled(string name)
    {
        if (!_pool.TryGetValue(name, out var list) || list.Count == 0)
            return null;
        var model = list[^1];
        list.RemoveAt(list.Count - 1);
        _poolOrder.Remove((name, model));
        // the pose of the character it was: back to the model's own
        foreach (var x in model.FindChildren("*", nameof(Skeleton3D), true, false))
            if (x is Skeleton3D sk)
                sk.ResetBonePoses();
        return model;
    }
}
