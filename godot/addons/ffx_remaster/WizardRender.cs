#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The wizard's before/after pictures of a map (docs/remaster-editor.md, "The remaster wizard"),
/// rendered offscreen in the editor: the classic map loaded into a viewport of its own (its own
/// world, nothing of the edited scene), seen from the game's camera when the game captured the map
/// (R5's <see cref="MapCapture"/>), else from three viewpoints over its walk mesh; drawn classic,
/// then remastered with the staged materials (registered as a layer while it draws) under the staged
/// lighting (the game's own rig, <see cref="RemasterLighting"/>, its look from <see cref="LookCache"/>).
/// Needs a renderer (none headless: <see cref="Available"/>). Driven by <see cref="Tick"/>, a few
/// frames a picture.
/// </summary>
public sealed class WizardRender
{
    public const int Width = 640, Height = 360;

    /// <summary>The layer the staged textures are registered as while the remastered pictures draw.</summary>
    public const string Layer = "wizard-preview";

    public static bool Available => DisplayServer.GetName() != "headless";

    private readonly SubViewport _vp;
    private readonly Camera3D _cam;
    private readonly Node3D _map;
    private readonly WorldEnvironment _env;
    private readonly RemasterLighting _rig;
    private readonly string _level;
    private readonly List<Transform3D> _views;
    private readonly RemasterTextureSet _staged;
    private readonly string _stagedDir;
    private readonly GdFfx.Api.Look _look;
    private readonly bool _classicOnly;
    private int _view, _frames;
    private bool _remastered;

    public List<Image> Classic { get; } = new();
    public List<Image> Remastered { get; } = new();

    public bool Done { get; private set; }

    /// <summary>Where the views came from ("the game's camera", "3 viewpoints over the walk mesh").</summary>
    public string Views { get; }

    /// <summary>
    /// Pictures of <paramref name="map"/> (a loaded classic map, render space; it is moved into the
    /// viewport and freed with it) for <paramref name="level"/>: classic, then (unless
    /// <paramref name="classicOnly"/>) remastered with <paramref name="staged"/> (its paths relative to
    /// <paramref name="stagedDir"/>) under <paramref name="lighting"/>.
    /// </summary>
    public WizardRender(Node parent, Node3D map, string level, Aabb box, MapLighting lighting, RemasterTextureSet staged, string stagedDir,
        MapGameData? data, bool classicOnly = false)
    {
        (_map, _level, _staged, _stagedDir, _classicOnly) = (map, level, staged, stagedDir, classicOnly);
        _look = LookCache.Read(level) ?? new GdFfx.Api.Look { Valid = true };
        _vp = new SubViewport
        {
            Size = new Vector2I(Width, Height),
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = Viewport.Msaa.Msaa2X,
        };
        var bg = _look.Valid && _look.Background.LengthSquared() > 0 ? new Color(_look.Background.X, _look.Background.Y, _look.Background.Z) : new Color(0.05f, 0.06f, 0.09f);
        _env = new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = bg } };
        _vp.AddChild(_env);
        if (map.GetParent() is { } old)
            old.RemoveChild(map);
        _vp.AddChild(map);
        var capture = MapCapture.Read(level);
        float fov = 50;
        _views = new List<Transform3D>();
        if (capture is { CameraValid: true })
        {
            _views.Add(new Transform3D(capture.Camera.Basis.Orthonormalized(), capture.Camera.Origin));
            fov = Mathf.RadToDeg(capture.FovY);
            Views = "the game's camera";
        }
        else
        {
            _views.AddRange(Viewpoints(data?.Main, box));
            Views = data?.Main != null ? $"{_views.Count} viewpoints over the walk mesh" : $"{_views.Count} viewpoints over the map";
        }
        _cam = new Camera3D { Fov = fov, Near = 1f, Far = 20000f, Current = true };
        _vp.AddChild(_cam);
        _rig = new RemasterLighting(_env, null, null) { Fixed = lighting };
        _vp.AddChild(_rig);
        parent.AddChild(_vp);
        _cam.GlobalTransform = _views[0];
    }

    /// <summary>Three views over <paramref name="walk"/> (else the map's box): around its middle at a character's height and some, looking at it.</summary>
    public static List<Transform3D> Viewpoints(WalkMesh? walk, Aabb box)
    {
        Vector3 centre;
        float reach;
        if (walk != null && walk.Count > 0)
        {
            var b = new Aabb(walk.Vertices[0], Vector3.Zero);
            foreach (var v in walk.Vertices)
                b = b.Expand(v);
            centre = b.GetCenter();
            reach = Mathf.Clamp(Mathf.Max(b.Size.X, b.Size.Z) * 0.45f, 60f, 600f);
            centre.Y = b.Position.Y;
        }
        else
        {
            centre = box.GetCenter();
            reach = Mathf.Clamp(Mathf.Max(box.Size.X, box.Size.Z) * 0.4f, 60f, 800f);
            centre.Y = box.Position.Y + box.Size.Y * 0.2f;
        }
        var list = new List<Transform3D>();
        for (int i = 0; i < 3; i++)
        {
            float a = Mathf.Tau * i / 3f + 0.4f;
            var eye = centre + new Vector3(Mathf.Cos(a) * reach, reach * 0.35f + 20f, Mathf.Sin(a) * reach);
            var target = centre + new Vector3(0, 10f, 0);
            list.Add(Transform3D.Identity.Translated(eye).LookingAt(target, Vector3.Up));
        }
        return list;
    }

    /// <summary>Once a frame; true when every picture is taken (then <see cref="Free"/>).</summary>
    public bool Tick()
    {
        if (Done)
            return true;
        if (_remastered)
            _rig.Set(true, _look, 1, _level, _map, _cam.GlobalPosition);
        // the environment on the camera: in the editor a WorldEnvironment node does not reach an offscreen viewport's own world
        if (_cam.Environment != _env.Environment)
            _cam.Environment = _env.Environment;
        // a few frames for the shaders and the shadows to settle
        if (++_frames < (_remastered ? 6 : 4))
            return false;
        var img = _vp.GetTexture().GetImage();
        (_remastered ? Remastered : Classic).Add(img);
        _frames = 0;
        if (++_view < _views.Count)
        {
            _cam.GlobalTransform = _views[_view];
            return false;
        }
        if (_remastered || _classicOnly)
        {
            Done = true;
            return true;
        }
        // remastered: the staged materials as a layer, the map's lit materials, the rig on
        _remastered = true;
        _view = 0;
        _cam.GlobalTransform = _views[0];
        RemasterTextures.Register(_staged, Layer, _stagedDir);
        MapLook.Of(_map).Set(true);
        _rig.Set(true, _look, 1, _level, _map, _cam.GlobalPosition);
        return false;
    }

    /// <summary>Draws the remastered pictures again under <paramref name="lighting"/> (the exposure tuned): the classic ones are kept.</summary>
    public void Again(MapLighting lighting)
    {
        _rig.Fixed = lighting;
        _rig.Refresh();
        Remastered.Clear();
        (_view, _frames, Done) = (0, 0, false);
        _cam.GlobalTransform = _views[0];
    }

    /// <summary>Takes the viewport down (the map with it) and the staged layer out; the rig off (the global shader values back).</summary>
    public void Free()
    {
        if (_remastered)
        {
            _rig.Set(false, null, 0);
            RemasterTextures.Unregister(Layer);
        }
        _vp.GetParent()?.RemoveChild(_vp);
        _vp.QueueFree();
    }

    /// <summary>The colour check over the pictures: each view's classic against its remastered one; the worst view's result.</summary>
    public ColourBalance.Result? Colour()
    {
        ColourBalance.Result? worst = null;
        for (int i = 0; i < Mathf.Min(Classic.Count, Remastered.Count); i++)
        {
            var r = ColourBalance.Compare(Classic[i], Remastered[i]);
            if (worst == null || !r.Ok && worst.Ok || r.Ok == worst.Ok && Mathf.Abs(r.DL) > Mathf.Abs(worst.DL))
                worst = r;
        }
        return worst;
    }

    /// <summary>A copy of the remastered pictures cleared, to draw them again (the exposure tuned, a texture made again).</summary>
    public static IEnumerable<string> Save(IEnumerable<Image> images, string dir, string name)
    {
        DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(dir));
        int i = 0;
        foreach (var img in images.ToList())
        {
            var path = $"{dir}/{name}-{i++}.png";
            img.SavePng(GameSettings.Globalize(path));
            yield return path;
        }
    }
}
#endif
