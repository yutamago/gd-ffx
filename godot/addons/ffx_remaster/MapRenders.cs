#if TOOLS
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using GdFfx.Remake;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>One map's before/after renders (R8; docs/remaster-editor.md, "Before/after renders").</summary>
public sealed class RenderRecord
{
    public string Key { get; set; } = "";
    public string Level { get; set; } = "";
    /// <summary><c>3d</c> (classic against remastered), <c>2d</c> (the classic layers only), <c>2d-rebuilt</c> (the classic layers against the rebuilt room).</summary>
    public string Kind { get; set; } = "";
    /// <summary><c>done</c>, <c>failed</c>, <c>skipped</c>.</summary>
    public string State { get; set; } = "";
    public string Reason { get; set; } = "";
    public string When { get; set; } = "";
    public double Ms { get; set; }
    public string Views { get; set; } = "";
    /// <summary>The pictures, relative to the renders folder.</summary>
    public List<string> Before { get; set; } = new();
    public List<string> After { get; set; } = new();
    public string ThumbBefore { get; set; } = "";
    public string ThumbAfter { get; set; } = "";
    /// <summary>R3's colour check, the worst view (3D maps); "" for none.</summary>
    public string Colour { get; set; } = "";
    public bool ColourOk { get; set; } = true;
    public float DL { get; set; }
    public float Chroma { get; set; } = 1;
    public float Hue { get; set; }
    /// <summary>The mean absolute difference (0..255) of the after pictures against the before ones, the views' mean.</summary>
    public double Diff { get; set; } = -1;
    /// <summary>What the project changed on the map when it was drawn ("overrides 3, textures 51, lighting, rebuilt").</summary>
    public string Changes { get; set; } = "";
}

/// <summary>The renders folder's state: every map's record and the batch (resumable).</summary>
public sealed class RenderState
{
    public Dictionary<string, RenderRecord> Maps { get; set; } = new();
    public List<string> Batch { get; set; } = new();
    public int Next { get; set; }
    public string Started { get; set; } = "";
}

/// <summary>
/// Where the renders go and their state (<c>renders.json</c>): <c>--renders-dir=DIR</c> (an absolute
/// folder or <c>user://</c>), else <c>user://remaster/renders</c>. Pictures of the player's game:
/// never in git, never in the project.
/// </summary>
public static class RenderStore
{
    public const string FileName = "renders.json";

    public static string Dir => GameSettings.Arg("renders-dir") is { Length: > 0 } d ? d.TrimEnd('/') : "user://remaster/renders";

    public static string Abs(string rel = "") => GameSettings.Globalize(rel.Length == 0 ? Dir : $"{Dir}/{rel}");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static RenderState Load()
    {
        var path = Abs(FileName);
        try
        {
            if (System.IO.File.Exists(path) && JsonSerializer.Deserialize<RenderState>(System.IO.File.ReadAllText(path)) is { } s)
                return s;
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"ffx remaster: renders: {path}: {e.Message}");
        }
        return new RenderState();
    }

    public static void Save(RenderState s)
    {
        System.IO.Directory.CreateDirectory(Abs());
        var path = Abs(FileName);
        System.IO.File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(s, Options));
        System.IO.File.Move(path + ".tmp", path, true);
    }

    /// <summary>A record's picture as an image (null: missing).</summary>
    public static Image? Picture(string rel)
    {
        var path = Abs(rel);
        if (rel.Length == 0 || !System.IO.File.Exists(path))
            return null;
        var img = Image.LoadFromFile(path);
        return img != null && !img.IsEmpty() ? img : null;
    }
}

/// <summary>
/// A viewport of its own that takes pictures of a scene from a list of views: a few frames each
/// for the shaders and shadows to settle. Needs a renderer (none headless).
/// </summary>
public sealed class RenderShooter
{
    private readonly SubViewport _vp;
    private readonly WorldEnvironment _env;
    private Camera3D _cam;
    private List<(Transform3D T, float Fov)> _views = new();
    private int _view, _frames, _settle;

    public System.Action<Camera3D>? EachFrame { get; set; }

    public List<Image> Images { get; } = new();

    public bool Done { get; private set; } = true;

    public WorldEnvironment Environment => _env;

    public Camera3D Camera => _cam;

    public RenderShooter(Node parent, Vector2I size, Color background)
    {
        _vp = new SubViewport { Size = size, OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Msaa3D = Viewport.Msaa.Msaa2X };
        _env = new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = background } };
        _vp.AddChild(_env);
        _cam = new Camera3D { Fov = 50, Near = 1f, Far = 20000f, Current = true };
        _vp.AddChild(_cam);
        parent.AddChild(_vp);
    }

    public void Add(Node n)
    {
        if (n.GetParent() is { } old)
            old.RemoveChild(n);
        _vp.AddChild(n);
    }

    public void Remove(Node n)
    {
        if (n.GetParent() == _vp)
            _vp.RemoveChild(n);
    }

    /// <summary>Takes a picture from each of <paramref name="views"/> (Images cleared), <paramref name="settle"/> frames each.</summary>
    public void Begin(List<(Transform3D T, float Fov)> views, int settle)
    {
        _views = views;
        (_view, _frames, _settle, Done) = (0, 0, settle, views.Count == 0);
        Images.Clear();
        if (views.Count > 0)
            Aim(views[0]);
    }

    private void Aim((Transform3D T, float Fov) v)
    {
        _cam.GlobalTransform = v.T;
        _cam.Fov = v.Fov;
    }

    /// <summary>Once a frame; true when every view's picture is taken.</summary>
    public bool Tick()
    {
        if (Done)
            return true;
        // the environment on the camera: in the editor a WorldEnvironment node does not reach an offscreen viewport's own world
        if (_cam.Environment != _env.Environment)
            _cam.Environment = _env.Environment;
        EachFrame?.Invoke(_cam);
        if (++_frames < _settle)
            return false;
        Images.Add(_vp.GetTexture().GetImage());
        _frames = 0;
        if (++_view < _views.Count)
        {
            Aim(_views[_view]);
            return false;
        }
        Done = true;
        return true;
    }

    public void Free()
    {
        _vp.GetParent()?.RemoveChild(_vp);
        _vp.QueueFree();
    }
}

/// <summary>
/// R8's before/after renders of maps (docs/remaster-editor.md, "Before/after renders"), in the
/// editor, offscreen, one map after another, a few frames each: a 3D map's classic import drawn
/// classic (the project's textures left out: <see cref="RemasterTextures.Suspended"/>), then with
/// the project's overrides, textures and materials on the remastered path under its lighting
/// (<c>lighting.tres</c> or R3's defaults, the game's look), from the game's camera of R5's capture
/// (else three views over the walk mesh), with R3's colour check per map; a 2D-layer map's classic
/// layers from each 2D scene's camera (a rebuilt room made from them, which from that camera is the
/// classic room), and where the project has a rebuilt room (R7b) that room from the same cameras and
/// from above, with the mean difference. Into <see cref="RenderStore.Dir"/> (pictures and
/// thumbnails per map, <c>renders.json</c>, saved after each map: an interrupted batch goes on where
/// it stopped, a map already done is not drawn again unless asked). The time per map is kept.
/// </summary>
public sealed class RenderBatch
{
    public const int Width = 640, Height = 360, Width2D = 640, Height2D = 480, Settle = 4, SettleLit = 6;

    private enum Phase { Load, Before, PrepareAfter, After, Save }

    private sealed class Job
    {
        public required MapEntry Entry { get; init; }
        public required RenderRecord Rec { get; init; }
        public Phase Phase { get; set; } = Phase.Load;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public RenderShooter? Shooter { get; set; }
        public Node3D? Before { get; set; }
        public Node3D? After { get; set; }
        public List<(Transform3D, float)> Views { get; set; } = new();
        public List<(Transform3D, float)> AfterViews { get; set; } = new();
        public List<Image> BeforeImages { get; set; } = new();
        public RemasterLighting? Rig { get; set; }
        public double Waited { get; set; }
    }

    private readonly RemasterPlugin _plugin;
    private readonly bool _again;
    private Job? _job;

    public RenderState State { get; private set; }

    public string Status { get; private set; } = "";

    public bool Finished => State.Next >= State.Batch.Count && _job == null;

    public int Done, Failed, Skipped, Kept;

    public static bool Available => DisplayServer.GetName() != "headless";

    public RenderBatch(RemasterPlugin plugin, IEnumerable<string>? keys, bool again)
    {
        _plugin = plugin;
        _again = again;
        State = RenderStore.Load();
        if (keys != null)
        {
            State.Batch = keys.Select(k => k.ToLowerInvariant()).Distinct().ToList();
            State.Next = 0;
            State.Started = Now();
            RenderStore.Save(State);
        }
        GD.Print($"ffx remaster: renders: {State.Batch.Count} map(s) into {RenderStore.Abs()}, from {State.Next + 1}");
    }

    public void Tick(double delta)
    {
        if (_job != null)
        {
            try
            {
                Step(_job, delta);
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"ffx remaster: renders: {_job.Entry.Key}: {e}");
                End(_job, "failed", e.Message);
            }
            return;
        }
        if (State.Next >= State.Batch.Count || !_plugin.Session.IsOpen)
            return;
        var key = State.Batch[State.Next];
        var entry = _plugin.Session.Catalogue?.Find(key);
        if (entry == null)
        {
            Record(new RenderRecord { Key = key, State = "failed", Reason = "not in the catalogue", When = Now() });
            Failed++;
            Advance();
            return;
        }
        if (!_again && State.Maps.TryGetValue(key, out var old) && old.State == "done" && old.Changes == Changes(entry))
        {
            Kept++;
            GD.Print($"ffx remaster: renders: {key}: kept (done {old.When}, {old.Ms / 1000:0.0} s)");
            Advance();
            return;
        }
        var rec = new RenderRecord { Key = key, Level = entry.Level, When = Now(), Changes = Changes(entry) };
        _job = new Job { Entry = entry, Rec = rec };
        Status = $"{key} ({State.Next + 1}/{State.Batch.Count})";
        if (entry.Model3D != null)
            _plugin.Session.Classic?.Path("map", entry.Level);
    }

    private void Advance()
    {
        State.Next++;
        RenderStore.Save(State);
    }

    private void Record(RenderRecord r)
    {
        State.Maps[r.Key] = r;
        RenderStore.Save(State);
    }

    // ---- one map -------------------------------------------------------------------------------

    private void Step(Job job, double delta)
    {
        var e = job.Entry;
        switch (job.Phase)
        {
            case Phase.Load:
                job.Waited += delta;
                if (job.Waited > 600)
                {
                    End(job, "failed", "the classic import did not come in 600 s");
                    return;
                }
                if (e.Model3D != null ? Load3D(job) : Load2D(job))
                    job.Phase = Phase.Before;
                return;
            case Phase.Before:
                if (!job.Shooter!.Tick())
                    return;
                job.BeforeImages = job.Shooter.Images.ToList();
                job.Phase = Phase.PrepareAfter;
                return;
            case Phase.PrepareAfter:
                if (e.Model3D != null)
                    PrepareAfter3D(job);
                else if (job.After != null)
                {
                    job.Shooter!.Remove(job.Before!);
                    job.Before!.QueueFree();
                    job.Before = null;
                    job.Shooter.Begin(job.AfterViews, Settle);
                }
                else
                {
                    job.Phase = Phase.Save;
                    return;
                }
                job.Phase = Phase.After;
                return;
            case Phase.After:
                if (!job.Shooter!.Tick())
                    return;
                job.Phase = Phase.Save;
                return;
            case Phase.Save:
                Save(job);
                return;
        }
    }

    private bool Load3D(Job job)
    {
        var e = job.Entry;
        var classic = _plugin.Session.Classic!;
        classic.Poll();
        if (classic.Path("map", e.Level) is not { } path)
        {
            if (classic.Failed("map", e.Level))
                End(job, "failed", "the classic map did not import");
            return false;
        }
        // the before picture: the classic map, nothing of the project's
        RemasterTextures.Suspended = true;
        RemasterTextures.Invalidate();
        Node3D? map;
        try
        {
            map = ClassicLibrary.Load(path, $"PS3Data{e.Level}tex/");
        }
        finally
        {
            RemasterTextures.Suspended = false;
            RemasterTextures.Invalidate();
        }
        if (map == null)
        {
            End(job, "failed", $"{path} did not load");
            return false;
        }
        ClassicMaterials.FixLayers(map);
        map.Scale = Vector3.One * ClassicLibrary.MapScale;
        var (_, box) = Remaster.Wizard.MapTextures.Collect(map);
        var look = LookCache.Read(e.Level);
        var bg = look is { Valid: true } && look.Background.LengthSquared() > 0 ? new Color(look.Background.X, look.Background.Y, look.Background.Z) : new Color(0.05f, 0.06f, 0.09f);
        job.Shooter = new RenderShooter(_plugin, new Vector2I(Width, Height), bg);
        job.Shooter.Add(map);
        job.Before = map;
        var capture = MapCapture.Read(e.Level);
        if (capture is { CameraValid: true })
        {
            job.Views.Add((new Transform3D(capture.Camera.Basis.Orthonormalized(), capture.Camera.Origin), Mathf.RadToDeg(capture.FovY)));
            job.Rec.Views = "the game's camera";
        }
        else
        {
            var data = _plugin.Session.Game is { } g ? MapGameData.Read(g, e.Level) : null;
            job.Views.AddRange(WizardRender.Viewpoints(data?.Main, box).Select(t => (t, 50f)));
            job.Rec.Views = data?.Main != null ? $"{job.Views.Count} viewpoints over the walk mesh" : $"{job.Views.Count} viewpoints over the map";
        }
        job.Rec.Kind = "3d";
        if (look == null)
            job.Rec.Views += "; no look of the game's cached for this map (the defaults' light: play the map once)";
        job.Shooter.Begin(job.Views, Settle);
        return true;
    }

    /// <summary>The after picture of a 3D map: the project's overrides, textures, materials and lighting on the remastered path.</summary>
    private void PrepareAfter3D(Job job)
    {
        var e = job.Entry;
        var map = job.Before!;
        var key = e.Key;
        // the editor's own folders (a check's) as the last layer, as the game registers them
        if (MapStore.Dir != RemasterOverrides.BaseDir && ResourceLoader.Exists(MapStore.PathOf(key)))
            RemasterOverrides.Register(e.Level, MapStore.PathOf(key), "editor");
        if (MapStore.Dir != RemasterOverrides.BaseDir && ResourceLoader.Exists(LightingEdit.PathOf(key)))
            LightingLayers.Register(e.Level, LightingEdit.PathOf(key), "editor");
        RemasterOverrides.Invalidate(e.Level);
        LightingLayers.Invalidate(e.Level);
        if (RemasterOverrides.For(e.Level) is { } merged)
            MapOverrideRuntime.Apply(map, merged, ClassicLibrary.MapScale);
        RemasterTextures.Refresh(map);
        var (lighting, _) = LightingLayers.For(e.Level);
        var remade = RemakeRegistry.Find("map", e.Level);
        Node3D shown = map;
        if (remade != null && ResourceLoader.Load<PackedScene>(remade)?.Instantiate() is Node3D scene && RebuiltRoom.In(scene) == null)
        {
            // a remade map scene (G6) in the classic map's place
            job.Shooter!.Remove(map);
            map.QueueFree();
            job.Before = null;
            job.Shooter.Add(scene);
            shown = scene;
            job.After = scene;
        }
        else
            MapLook.Of(map).Set(true, lighting.BlendedParts == BlendedLook.Lit);
        var look = LookCache.Read(e.Level) ?? new GdFfx.Api.Look { Valid = true };
        var rig = new RemasterLighting(job.Shooter!.Environment, null, null) { Fixed = lighting };
        job.Shooter.Add(rig);
        job.Rig = rig;
        job.Shooter.EachFrame = cam => rig.Set(true, look, 1, e.Level, shown, cam.GlobalPosition);
        job.Shooter.Begin(job.Views, SettleLit);
    }

    private bool Load2D(Job job)
    {
        var e = job.Entry;
        if (_plugin.RoomSources(e.Level) is not { } src)
            return false;
        if (src.Layers == null)
        {
            End(job, "skipped", "no 3D model and no 2D layers");
            return false;
        }
        var classic = RebuiltRoom.Create(e.Level, src.Layers);
        job.Shooter = new RenderShooter(_plugin, new Vector2I(Width2D, Height2D), Colors.Black);
        job.Shooter.Add(classic);
        classic.Build(src.Layers, src.Data, 19000f, 0f, atc: true, guides: false);
        job.Before = classic;
        foreach (var (sc, cam) in classic.Cameras.OrderBy(c => c.Key).Take(3))
            job.Views.Add((cam, classic.Fovs[sc]));
        if (job.Views.Count == 0)
        {
            End(job, "skipped", "2D layers without a scene camera");
            return false;
        }
        job.Rec.Views = $"{job.Views.Count} 2D scene camera(s)";
        job.Rec.Kind = "2d";
        var path = RemasterPlugin.RoomScenePath(e.Key);
        var remade = ResourceLoader.Exists(path) ? path : RemakeRegistry.Find("map", e.Level);
        if (remade != null && ResourceLoader.Load<PackedScene>(remade, "", ResourceLoader.CacheMode.Ignore)?.Instantiate() is Node3D scene
            && RebuiltRoom.In(scene) is { } room)
        {
            job.After = scene;
            job.Shooter.Add(scene);
            scene.Visible = false;
            room.Build(src.Layers, src.Data, 19000f, 0f, atc: true, guides: false);
            // the scene cameras, and a view from above and behind the first one (the room's model shows)
            job.AfterViews.AddRange(job.Views);
            var (t, fov) = job.Views[0];
            var eye = t.Origin + t.Basis.Y * 60f + t.Basis.Z * 120f;
            job.AfterViews.Add((Transform3D.Identity.Translated(eye).LookingAt(t.Origin - t.Basis.Z * 200f, Vector3.Up), fov));
            job.Rec.Kind = "2d-rebuilt";
            job.Rec.Views += ", the rebuilt room also from above";
        }
        job.Shooter.Begin(job.Views, Settle);
        if (job.After != null)
            job.Shooter.EachFrame = _ =>
            {
                // the rebuilt room hidden while the classic one draws, shown after
                if (job.Phase == Phase.After && job.After is { Visible: false } a)
                    a.Visible = true;
            };
        return true;
    }

    private void Save(Job job)
    {
        var r = job.Rec;
        var dir = r.Key;
        System.IO.Directory.CreateDirectory(RenderStore.Abs(dir));
        r.Before = SaveAll(job.BeforeImages, dir, "classic");
        var after = job.Phase == Phase.Save && job.Shooter != null && (job.Entry.Model3D != null || job.After != null) ? job.Shooter.Images.ToList() : new List<Image>();
        r.After = SaveAll(after, dir, job.Entry.Model3D != null ? "remastered" : "rebuilt");
        r.ThumbBefore = Thumb(job.BeforeImages.FirstOrDefault(), $"{dir}/thumb-classic.png");
        r.ThumbAfter = Thumb(after.FirstOrDefault(), $"{dir}/thumb-{(job.Entry.Model3D != null ? "remastered" : "rebuilt")}.png");
        int pairs = Mathf.Min(job.BeforeImages.Count, after.Count);
        if (pairs > 0)
        {
            r.Diff = System.Math.Round(Enumerable.Range(0, pairs).Average(i => MeanDiff(job.BeforeImages[i], after[i])), 2);
            if (job.Entry.Model3D != null)
            {
                ColourBalance.Result? worst = null;
                for (int i = 0; i < pairs; i++)
                {
                    var c = ColourBalance.Compare(job.BeforeImages[i], after[i]);
                    if (worst == null || !c.Ok && worst.Ok || c.Ok == worst.Ok && Mathf.Abs(c.DL) > Mathf.Abs(worst.DL))
                        worst = c;
                }
                if (worst != null)
                    (r.Colour, r.ColourOk, r.DL, r.Chroma, r.Hue) = (worst.ToString(), worst.Ok, Round(worst.DL), Round(worst.ChromaRatio), Round(worst.DHue));
            }
        }
        End(job, "done", "");
    }

    private void End(Job job, string state, string reason)
    {
        var r = job.Rec;
        r.State = state;
        r.Reason = reason;
        r.Ms = System.Math.Round(job.Clock.Elapsed.TotalMilliseconds);
        job.Shooter?.Free();
        foreach (var n in new[] { job.Before, job.After })
            if (n != null && GodotObject.IsInstanceValid(n) && n.GetParent() == null)
                n.QueueFree();
        if (job.Rig != null && GodotObject.IsInstanceValid(job.Rig))
            job.Rig.Set(false, null, 0);
        _job = null;
        if (state == "done")
            Done++;
        else if (state == "failed")
            Failed++;
        else
            Skipped++;
        Record(r);
        GD.Print($"ffx remaster: renders: {r.Key}: {state}{(reason.Length > 0 ? $" ({reason})" : "")} in {r.Ms / 1000:0.0} s: {r.Kind} {r.Views}; "
            + $"{r.Before.Count} before, {r.After.Count} after{(r.Diff >= 0 ? $", mean difference {r.Diff:0.00}/255" : "")}{(r.Colour.Length > 0 ? $"; colour {r.Colour}" : "")}"
            + (r.Changes.Length > 0 ? $"; the project: {r.Changes}" : ""));
        Advance();
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>What the project changes on <paramref name="e"/> (the dashboard's columns, short).</summary>
    public string Changes(MapEntry e) => RemasterStatus.Of(_plugin, e, State, new()).ChangesText;

    private static List<string> SaveAll(List<Image> images, string dir, string name)
    {
        var list = new List<string>();
        for (int i = 0; i < images.Count; i++)
        {
            var rel = $"{dir}/{name}-{i}.png";
            images[i].SavePng(RenderStore.Abs(rel));
            list.Add(rel);
        }
        return list;
    }

    private static string Thumb(Image? img, string rel)
    {
        if (img == null)
            return "";
        var t = (Image)img.Duplicate();
        t.Resize(192, Mathf.Max(1, 192 * t.GetHeight() / Mathf.Max(1, t.GetWidth())), Image.Interpolation.Bilinear);
        t.SavePng(RenderStore.Abs(rel));
        return rel;
    }

    /// <summary>The mean absolute difference per channel (0..255) of two pictures of the same size.</summary>
    public static double MeanDiff(Image a, Image b)
    {
        if (a.GetSize() != b.GetSize())
            return 255;
        a = (Image)a.Duplicate();
        b = (Image)b.Duplicate();
        a.Convert(Image.Format.Rgb8);
        b.Convert(Image.Format.Rgb8);
        var x = a.GetData();
        var y = b.GetData();
        long sum = 0;
        for (int i = 0; i < x.Length; i++)
            sum += System.Math.Abs(x[i] - y[i]);
        return (double)sum / System.Math.Max(1, x.Length);
    }

    private static float Round(float v) => Mathf.Snapped(v, 0.001f);

    private static string Now() => System.DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
#endif
