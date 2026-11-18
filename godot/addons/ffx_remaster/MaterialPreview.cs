#if TOOLS
using System.Collections.Generic;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>What the preview lights the material with.</summary>
public enum PreviewLight
{
    /// <summary>Key, fill and rim lights on a dark grey; the key turnable (Shift+drag).</summary>
    Studio,
    /// <summary>An HDRI's sky lighting and reflections (Alt+drag turns it).</summary>
    Hdri,
    /// <summary>The lighting of a map that uses the texture (R3's: its key light, ambient, background, tone map).</summary>
    Map,
}

/// <summary>The lighting of a map, resolved for the preview (<see cref="RemasterLighting.Resolve"/>).</summary>
public sealed record MapLight(string Level, ResolvedLighting Light, MapLighting Lighting, Color Background, bool FromGame);

/// <summary>
/// The Materials tab's 3D preview (docs/remaster-materials.md, "The preview"): the material on a
/// plane, cube, cylinder, sphere or the game piece that uses the texture; orbit (drag), zoom
/// (wheel), pan (middle drag), the light turned (Shift+drag), the environment turned (Alt+drag),
/// frame (F); and a compare wipe across it: two views of one world, the left one's camera seeing
/// the object with the compare material (layer 2), the right one's with the material (layer 1),
/// joined at the wipe. Tone mapping as the remastered path (the map's lighting, else R3's
/// defaults).
/// </summary>
[Tool]
public partial class MaterialPreview : Control
{
    public const int Plane = 0, Cube = 1, Cylinder = 2, Sphere = 3, Object = 4;
    public static readonly string[] Objects = ["Plane", "Cube", "Cylinder", "Sphere", "Object"];

    private const string WipeCode = @"shader_type canvas_item;
uniform sampler2D left_view : filter_linear;
uniform sampler2D right_view : filter_linear;
uniform float split = 0.5;
void fragment() {
	vec4 c = UV.x < split ? texture(left_view, UV) : texture(right_view, UV);
	if (abs(UV.x - split) < 0.0015) c.rgb = vec3(1.0, 0.85, 0.2);
	COLOR = vec4(c.rgb, 1.0);
}";

    private readonly SubViewport _left = new() { Name = "Left", OwnWorld3D = false, Msaa3D = Viewport.Msaa.Msaa4X };
    private readonly SubViewport _right = new() { Name = "Right", OwnWorld3D = false, Msaa3D = Viewport.Msaa.Msaa4X };
    private readonly World3D _world = new();
    private readonly Environment _env = new();
    private readonly Camera3D _camLeft = new() { CullMask = 2, Fov = 40 };
    private readonly Camera3D _camRight = new() { CullMask = 1, Fov = 40 };
    private readonly Node3D _root = new() { Name = "PreviewRoot" };
    private readonly MeshInstance3D _objRight = new() { Layers = 1 };
    private readonly MeshInstance3D _objLeft = new() { Layers = 2 };
    private readonly DirectionalLight3D _key = new() { ShadowEnabled = true, LightEnergy = 2f };
    private readonly DirectionalLight3D _fill = new() { LightEnergy = 0.5f, LightColor = new Color(0.85f, 0.9f, 1f) };
    private readonly DirectionalLight3D _rim = new() { LightEnergy = 1.2f };
    private readonly ShaderMaterial _wipe = new() { Shader = new Shader { Code = WipeCode } };
    private readonly TextureRect _view = new();
    private Material? _rightMat, _leftMat;
    private Material?[]? _objectClassic;
    private Mesh? _objectMesh;

    private float _yaw = 0.6f, _pitch = -0.45f, _distance = 4.2f;
    private Vector3 _target;
    private float _keyYaw = -0.7f, _keyPitch = -0.8f, _envYaw;
    private enum Drag { None, Orbit, Light, Environment, Pan, Wipe }
    private Drag _drag;

    public int Shape { get; private set; } = Sphere;

    /// <summary>The next left click picks the object's UV under the mouse (<see cref="Picked"/>) instead of orbiting.</summary>
    public bool Picking { get; set; }

    /// <summary>A click while <see cref="Picking"/>: the UV of the object under it (0-1, the tiling undone).</summary>
    public event System.Action<Vector2>? Picked;

    /// <summary>The UV tiling on the primitives (the pick undoes it).</summary>
    public float Tiling { get; set; } = 1;

    /// <summary>The UV of the object under a point of the view (a ray against its triangles), or null.</summary>
    public Vector2? UvAt(Vector2 pos)
    {
        if (_objRight.Mesh is not { } mesh || Size.X < 1 || Size.Y < 1)
            return null;
        // the camera's ray, from the view's own projection (the SubViewport has the view's size)
        var from = _camRight.ProjectRayOrigin(pos);
        var dir = _camRight.ProjectRayNormal(pos);
        var inv = _objRight.GlobalTransform.AffineInverse();
        from = inv * from;
        dir = (inv.Basis * dir).Normalized();
        float best = float.MaxValue;
        Vector2? uv = null;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            if (arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil)
                continue;
            var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var t = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var idx = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            int n = idx?.Length ?? v.Length;
            for (int i = 0; i + 2 < n; i += 3)
            {
                int a = idx?[i] ?? i, b = idx?[i + 1] ?? i + 1, c = idx?[i + 2] ?? i + 2;
                if (Hit(from, dir, v[a], v[b], v[c]) is not { } hit || hit.T >= best)
                    continue;
                best = hit.T;
                uv = t[a] * (1 - hit.U - hit.V) + t[b] * hit.U + t[c] * hit.V;
            }
        }
        if (uv is not { } u)
            return null;
        var tiled = Shape == Object ? u : u * Tiling;
        return new Vector2(tiled.X - Mathf.Floor(tiled.X), tiled.Y - Mathf.Floor(tiled.Y));
    }

    /// <summary>Möller-Trumbore: the ray's distance and the barycentric weights of <paramref name="b"/> and <paramref name="c"/>.</summary>
    private static (float T, float U, float V)? Hit(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = d.Cross(e2);
        float det = e1.Dot(p);
        if (Mathf.Abs(det) < 1e-9f)
            return null;
        float f = 1 / det;
        var s = o - a;
        float u = f * s.Dot(p);
        if (u < 0 || u > 1)
            return null;
        var q = s.Cross(e1);
        float v = f * d.Dot(q);
        if (v < 0 || u + v > 1)
            return null;
        float t = f * e2.Dot(q);
        return t > 1e-5f ? (t, u, v) : null;
    }
    public PreviewLight Light { get; private set; } = PreviewLight.Studio;
    public string Hdri { get; private set; } = "";
    public MapLight? MapLighting { get; private set; }

    /// <summary>The compare wipe (0: all the right side, the material; 1: all the left side).</summary>
    public float Wipe
    {
        get => _wipe.GetShaderParameter("split").AsSingle();
        set => _wipe.SetShaderParameter("split", Mathf.Clamp(value, 0, 1));
    }

    public MaterialPreview()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(320, 240);
        FocusMode = FocusModeEnum.Click;
        MouseFilter = MouseFilterEnum.Stop;
        _world.Environment = _env;
        _left.World3D = _world;
        _right.World3D = _world;
        foreach (var vp in new[] { _left, _right })
        {
            vp.TransparentBg = false;
            vp.RenderTargetUpdateMode = SubViewport.UpdateMode.WhenParentVisible;
            AddChild(vp);
        }
        // the lights on both views' layers: a camera culls lights by its cull mask too (phase 1 lit
        // the compare side with the ambient light only, so the material looked far brighter than the raw texture)
        _key.Layers = _fill.Layers = _rim.Layers = 1 | 2;
        _right.AddChild(_root);
        _root.AddChild(_objRight);
        _root.AddChild(_objLeft);
        _root.AddChild(_key);
        _root.AddChild(_fill);
        _root.AddChild(_rim);
        _right.AddChild(_camRight);
        _left.AddChild(_camLeft);
        _view.Material = _wipe;
        _view.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        _view.StretchMode = TextureRect.StretchModeEnum.Scale;
        _view.MouseFilter = MouseFilterEnum.Ignore;
        _view.SetAnchorsPreset(LayoutPreset.FullRect);
        // the wipe shader samples both views; the rect's own texture is the right one
        _view.Texture = _right.GetTexture();
        _wipe.SetShaderParameter("left_view", _left.GetTexture());
        _wipe.SetShaderParameter("right_view", _right.GetTexture());
        AddChild(_view);
        Wipe = 0.5f;
        Resized += OnResized;
        SetShape(Sphere, null, null);
        SetLight(PreviewLight.Studio, "", null);
        UpdateCamera();
    }

    private void OnResized()
    {
        var s = new Vector2I(Mathf.Max(16, (int)Size.X), Mathf.Max(16, (int)Size.Y));
        _left.Size = s;
        _right.Size = s;
    }

    /// <summary>The materials: <paramref name="right"/> the material, <paramref name="left"/> the compare.</summary>
    public void SetMaterials(Material? right, Material? left)
    {
        (_rightMat, _leftMat) = (right, left);
        ApplyMaterials();
    }

    private void ApplyMaterials()
    {
        int n = _objRight.Mesh?.GetSurfaceCount() ?? 0;
        for (int i = 0; i < n; i++)
        {
            _objRight.SetSurfaceOverrideMaterial(i, _rightMat);
            // the game piece's own classic materials on the left, when the compare is classic and it has them
            var classic = Shape == Object && _useClassic && _objectClassic != null && i < _objectClassic.Length ? _objectClassic[i] : null;
            _objLeft.SetSurfaceOverrideMaterial(i, classic ?? _leftMat);
        }
    }

    private bool _useClassic;

    /// <summary>Whether the left side shows the game piece's own classic materials (in the Object view).</summary>
    public bool UseClassic
    {
        get => _useClassic;
        set
        {
            _useClassic = value;
            ApplyMaterials();
        }
    }

    /// <summary>The object (<see cref="Plane"/> ... <see cref="Object"/>; for Object: the game piece's mesh and its classic materials by surface).</summary>
    public void SetShape(int shape, Mesh? objectMesh, Material?[]? classic)
    {
        if (objectMesh != null)
            (_objectMesh, _objectClassic) = (objectMesh, classic);
        Shape = shape == Object && _objectMesh == null ? Sphere : shape;
        Mesh mesh = Shape switch
        {
            Plane => new PlaneMesh { Size = new Vector2(2.4f, 2.4f) },
            Cube => new BoxMesh { Size = new Vector3(1.5f, 1.5f, 1.5f) },
            Cylinder => new CylinderMesh { TopRadius = 0.8f, BottomRadius = 0.8f, Height = 1.8f, RadialSegments = 64 },
            Object => _objectMesh!,
            _ => new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 96, Rings = 48 },
        };
        _objRight.Mesh = mesh;
        _objLeft.Mesh = mesh;
        ApplyMaterials();
        Frame();
    }

    /// <summary>Forgets the game piece (another texture).</summary>
    public void ClearObject()
    {
        (_objectMesh, _objectClassic) = (null, null);
        if (Shape == Object)
            SetShape(Sphere, null, null);
    }

    public bool HasObject => _objectMesh != null;

    /// <summary>The lighting: studio, an HDRI (a file), or a map's (resolved).</summary>
    public void SetLight(PreviewLight light, string hdri, MapLight? map)
    {
        (Light, Hdri, MapLighting) = (light, hdri, map);
        var tone = map?.Lighting ?? LightingLayers.Defaults;
        _env.TonemapMode = tone.ToneMapper switch
        {
            ToneMap.Linear => Environment.ToneMapper.Linear,
            ToneMap.Reinhard => Environment.ToneMapper.Reinhardt,
            ToneMap.Aces => Environment.ToneMapper.Aces,
            ToneMap.Agx => Environment.ToneMapper.Agx,
            _ => Environment.ToneMapper.Filmic,
        };
        _env.TonemapExposure = tone.ToneExposure;
        _env.TonemapWhite = tone.ToneWhite;
        _env.AdjustmentEnabled = true;
        _env.AdjustmentSaturation = tone.ToneSaturation;
        _env.AdjustmentContrast = tone.ToneContrast;
        _env.ReflectedLightSource = Environment.ReflectionSource.Bg;
        _key.Visible = _fill.Visible = _rim.Visible = false;
        switch (light)
        {
            case PreviewLight.Hdri when LoadHdri(hdri) is { } pano:
                _env.BackgroundMode = Environment.BGMode.Sky;
                _env.Sky = new Sky { SkyMaterial = new PanoramaSkyMaterial { Panorama = pano }, RadianceSize = Sky.RadianceSizeEnum.Size256 };
                _env.AmbientLightSource = Environment.AmbientSource.Sky;
                _env.ReflectedLightSource = Environment.ReflectionSource.Sky;
                _env.AmbientLightSkyContribution = 1f;
                break;
            case PreviewLight.Map when map != null:
                _env.BackgroundMode = Environment.BGMode.Color;
                _env.BackgroundColor = map.Background;
                _env.AmbientLightSource = Environment.AmbientSource.Color;
                _env.AmbientLightColor = map.Light.AmbientColour;
                _env.AmbientLightEnergy = map.Light.AmbientEnergy;
                _env.TonemapExposure = map.Light.Exposure;
                _env.AdjustmentSaturation = map.Light.Saturation;
                _key.Visible = map.Light.KeyOn;
                _key.LightColor = map.Light.KeyColour;
                _key.LightEnergy = map.Light.KeyEnergy;
                break;
            default:
                Light = light == PreviewLight.Map ? PreviewLight.Map : PreviewLight.Studio;
                _env.BackgroundMode = Environment.BGMode.Color;
                _env.BackgroundColor = new Color(0.16f, 0.17f, 0.19f);
                _env.AmbientLightSource = Environment.AmbientSource.Color;
                _env.AmbientLightColor = new Color(0.55f, 0.6f, 0.7f);
                _env.AmbientLightEnergy = 0.35f;
                _key.Visible = _fill.Visible = _rim.Visible = true;
                _key.LightColor = new Color(1f, 0.97f, 0.92f);
                _key.LightEnergy = 2f;
                break;
        }
        UpdateLights();
    }

    private static readonly Dictionary<string, Texture2D?> HdriCache = new();

    /// <summary>An HDRI as a texture: one of the project's (imported) or a file of one's own (read as it is).</summary>
    private static Texture2D? LoadHdri(string path)
    {
        if (path.Length == 0)
            return null;
        if (HdriCache.TryGetValue(path, out var t))
            return t;
        if (path.StartsWith("res://", System.StringComparison.Ordinal) && ResourceLoader.Exists(path))
            t = ResourceLoader.Load<Texture2D>(path);
        if (t == null && Image.LoadFromFile(ProjectSettings.GlobalizePath(path)) is { } img && !img.IsEmpty())
            t = ImageTexture.CreateFromImage(img);
        return HdriCache[path] = t;
    }

    private void UpdateLights()
    {
        if (Light == PreviewLight.Map && MapLighting != null)
        {
            var d = MapLighting.Light.KeyDirection.Normalized();
            // the map's key light turns with Shift+drag as well (around the vertical)
            d = d.Rotated(Vector3.Up, _keyYaw + 0.7f);
            _key.GlobalTransform = new Transform3D(Basis.LookingAt(d, Mathf.Abs(d.Y) > 0.99f ? Vector3.Forward : Vector3.Up), Vector3.Zero);
        }
        else
            _key.Rotation = new Vector3(_keyPitch, _keyYaw, 0);
        _fill.Rotation = new Vector3(-0.3f, _keyYaw + 2.4f, 0);
        _rim.Rotation = new Vector3(-0.35f, _keyYaw + Mathf.Pi, 0);
        _env.SkyRotation = new Vector3(0, _envYaw, 0);
    }

    /// <summary>Frames the object.</summary>
    public void Frame()
    {
        var aabb = _objRight.Mesh?.GetAabb() ?? new Aabb(-Vector3.One, Vector3.One * 2);
        _target = aabb.GetCenter();
        float r = Mathf.Max(0.5f, aabb.Size.Length() * 0.5f);
        _distance = r / Mathf.Tan(Mathf.DegToRad(_camRight.Fov * 0.5f)) * 1.15f;
        if (Shape == Plane)
            _pitch = Mathf.Min(_pitch, -0.6f);
        UpdateCamera();
    }

    /// <summary>Orbits the camera (radians), for the checks and the keys.</summary>
    public void Orbit(float yaw, float pitch)
    {
        _yaw += yaw;
        _pitch = Mathf.Clamp(_pitch + pitch, -1.5f, 1.5f);
        UpdateCamera();
    }

    /// <summary>Turns the key light (and the HDRI) around the vertical.</summary>
    public void TurnLight(float yaw)
    {
        _keyYaw += yaw;
        UpdateLights();
    }

    private void UpdateCamera()
    {
        var dir = new Vector3(Mathf.Sin(_yaw) * Mathf.Cos(_pitch), -Mathf.Sin(_pitch), Mathf.Cos(_yaw) * Mathf.Cos(_pitch));
        var t = new Transform3D(Basis.Identity, _target + dir * _distance).LookingAt(_target, Vector3.Up);
        _camRight.GlobalTransform = t;
        _camLeft.GlobalTransform = t;
        _camRight.Near = _camLeft.Near = Mathf.Max(0.01f, _distance * 0.02f);
        _camRight.Far = _camLeft.Far = _distance * 20f;
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _distance = Mathf.Max(0.3f, _distance / 1.1f);
                UpdateCamera();
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _distance = Mathf.Min(100f, _distance * 1.1f);
                UpdateCamera();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } pick when Picking:
                Picking = false;
                if (UvAt(pick.Position) is { } puv)
                    Picked?.Invoke(puv);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                _drag = !b.Pressed ? Drag.None
                    : Mathf.Abs(b.Position.X - Wipe * Size.X) < 6 ? Drag.Wipe
                    : b.ShiftPressed ? Drag.Light
                    : b.AltPressed ? Drag.Environment
                    : Drag.Orbit;
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } b:
                _drag = b.Pressed ? Drag.Pan : Drag.None;
                AcceptEvent();
                break;
            case InputEventMouseMotion m when _drag != Drag.None:
                var d = m.Relative * 0.01f;
                switch (_drag)
                {
                    case Drag.Orbit:
                        Orbit(-d.X, d.Y);
                        break;
                    case Drag.Light:
                        _keyYaw -= d.X;
                        _keyPitch = Mathf.Clamp(_keyPitch - d.Y, -1.5f, 0.2f);
                        UpdateLights();
                        break;
                    case Drag.Environment:
                        _envYaw -= d.X;
                        UpdateLights();
                        break;
                    case Drag.Pan:
                        var basis = _camRight.GlobalBasis;
                        _target += (-basis.X * m.Relative.X + basis.Y * m.Relative.Y) * _distance * 0.0015f;
                        UpdateCamera();
                        break;
                    case Drag.Wipe:
                        Wipe = m.Position.X / Mathf.Max(1, Size.X);
                        break;
                }
                AcceptEvent();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.F }:
                Frame();
                AcceptEvent();
                break;
        }
    }

    /// <summary>The preview as shown (the wipe joined; null without a renderer).</summary>
    public Image? Capture()
    {
        if (DisplayServer.GetName() == "headless")
            return null;
        var l = _left.GetTexture().GetImage();
        var r = _right.GetTexture().GetImage();
        if (l == null || r == null || l.IsEmpty() || r.IsEmpty())
            return null;
        l.Convert(Image.Format.Rgba8);
        r.Convert(Image.Format.Rgba8);
        int w = r.GetWidth(), h = r.GetHeight(), x0 = (int)(Wipe * w);
        if (l.GetWidth() == w && l.GetHeight() == h && x0 > 0)
            r.BlitRect(l, new Rect2I(0, 0, Mathf.Min(x0, w), h), Vector2I.Zero);
        return r;
    }

    /// <summary>The left view alone (the compare: the raw texture lit, or the classic look), null without a renderer.</summary>
    public Image? CaptureCompare()
    {
        if (DisplayServer.GetName() == "headless")
            return null;
        var l = _left.GetTexture().GetImage();
        return l == null || l.IsEmpty() ? null : l;
    }

    /// <summary>The right view alone (the material), null without a renderer.</summary>
    public Image? CaptureMaterial()
    {
        if (DisplayServer.GetName() == "headless")
            return null;
        var r = _right.GetTexture().GetImage();
        return r == null || r.IsEmpty() ? null : r;
    }

    /// <summary>Renders the views while the preview is shown, not when hidden.</summary>
    public void Active(bool on)
    {
        var mode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        _left.RenderTargetUpdateMode = mode;
        _right.RenderTargetUpdateMode = mode;
    }
}
#endif
