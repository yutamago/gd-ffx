using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GdFfx.Api;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R3's check (<c>--selftest-lighting</c>; run with <c>--devsave=7</c>): the remastered lighting
/// on Besaid beach, files made at run time in a temporary folder (removed at the end).
/// <list type="number">
/// <item>classic: a picture (with a renderer);</item>
/// <item>remastered by the defaults (no lighting file): the rig on, the sun from the game's key
/// light, the ambient, fog and tone map as <see cref="MapLighting"/>'s defaults make them; the
/// colour check against the classic picture (<see cref="ColourBalance"/>), lit from the colour
/// textures, then again with PBR materials made from the map's six biggest textures;</item>
/// <item>a lighting file registered (a custom key light, exposure, volumetric fog, VoxelGI and a
/// volume around the camera): each in force (the volume blended, the bake made or read);</item>
/// <item>the lighting quality low, medium, high: GI, volumetric fog and SSAO follow;</item>
/// <item>F7: classic again, the classic environment back (and its picture as the first one).</item>
/// </list>
/// With <c>--shot=FILE.png</c> under a renderer: FILE.png (classic), FILE-remastered.png,
/// FILE-pbr.png, FILE-file.png. <c>--lighting-variants=FILE.json</c> (development): after the
/// defaults, each variant (<c>[{"name": "...", "props": {"KeyEnergy": 2.0, ...}}]</c>, a
/// <see cref="MapLighting"/> property each, colours as [r, g, b]) drawn, measured, and with
/// <c>--shot</c> pictured as FILE-v&lt;name&gt;.png.
/// </summary>
public sealed class LightingTest : ITest
{
    public const string Level = "/map/bsil/bsil00/";

    private enum Phase { Setup, Boot, ShotClassic, Remaster, Defaults, Variant, Materials, Pbr, File, FileCheck, Quality, Toggle, Done }

    private readonly double _timeout;
    private readonly string? _shot;
    private readonly string _dir = ProjectSettings.GlobalizePath("user://selftest-lighting");
    private Phase _phase = Phase.Setup;
    private double _phaseTime, _total;
    private readonly List<string> _seen = new();
    private bool _acted;
    private Image? _classic;
    private readonly List<(string Name, JsonElement Props)> _variants = new();
    private int _variant;
    private int _step;
    private readonly bool _pictures = DisplayServer.GetName() != "headless";
    private readonly RemasterTextureSet _set = new();
    private readonly List<string> _made = new();

    public string Note { get; private set; } = "";

    public LightingTest(double timeout, string? shot)
    {
        _timeout = timeout;
        _shot = shot;
        if (GameSettings.Arg("lighting-variants") is { } file && FileAccess.FileExists(file))
        {
            var doc = JsonDocument.Parse(FileAccess.GetFileAsString(file));
            foreach (var v in doc.RootElement.EnumerateArray())
                _variants.Add((v.GetProperty("name").GetString()!, v.GetProperty("props").Clone()));
        }
    }

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > _timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)}; {string.Join("; ", _seen)})");
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        switch (_phase)
        {
            case Phase.Setup when GameHost.Instance?.Classic is { Open: true }:
                DirAccess.MakeDirRecursiveAbsolute(_dir);
                RenderPaths.Set("classic");
                UserSettings.Current.Lighting = "high";
                Next(Phase.Boot);
                break;
            case Phase.Boot when v.Current is { World.Map: BesaidTest.MapBeach, World.PlayerControl: true }
                && view != null && view.MapLevel == Level && view.MapNode != null && _phaseTime > 1.0:
                Next(Phase.ShotClassic);
                break;
            case Phase.ShotClassic when _phaseTime > 1.0:
                if (view!.Remastered)
                    return Done(false, "the map draws remastered before the check asked");
                _classic = Picture("");
                if (GameSettings.Flag("lighting-debug"))
                    DebugColours(view);
                Next(Phase.Remaster);
                break;
            case Phase.Remaster:
            {
                var ov = new MapOverrides { Level = PartIds.LevelKey(Level), Status = MapStatus.InProgress, RenderPath = RenderPath.Remastered };
                ResourceSaver.Save(ov, $"{_dir}/overrides.tres");
                RemasterOverrides.Register(Level, $"{_dir}/overrides.tres", "selftest-lighting");
                RenderPaths.Set(null);
                UserSettings.Current.RenderPath = "map";
                Next(Phase.Defaults);
                break;
            }
            case Phase.Defaults when _phaseTime > 1.5:
                if (CheckDefaults(view!, v.Current?.Look) is { } d)
                    return Done(false, d);
                if (Colour(view!, "remastered", "lit from the colour textures") is { } c)
                    return Done(false, c);
                Next(_variants.Count > 0 ? Phase.Variant : Phase.Materials);
                break;
            case Phase.Variant when !_acted:
            {
                _acted = true;
                var (name, props) = _variants[_variant];
                var m = (MapLighting)LightingLayers.Defaults.Duplicate(true);
                foreach (var p in props.EnumerateObject())
                    SetProperty(m, p.Name, p.Value);
                // a path of its own each (Godot's cache keeps a reloaded file's missing values)
                var path = $"{_dir}/variant-{_variant}.lighting.tres";
                ResourceSaver.Save(m, path);
                LightingLayers.Register(Level, path, "variant");
                break;
            }
            case Phase.Variant when _phaseTime > 1.2:
            {
                var name = _variants[_variant].Name;
                var img = Picture("-v" + name);
                if (img != null && _classic != null)
                    GD.Print($"ffx selftest: variant {name}: {ColourBalance.Compare(_classic, img)}; {view!.Rig.Now}");
                LightingLayers.Unregister(Level, "variant");
                RestoreScales();
                if (++_variant < _variants.Count)
                {
                    _phaseTime = 0;
                    _acted = false;
                }
                else
                    Next(Phase.Materials);
                break;
            }
            case Phase.Materials when !_acted:
                _acted = true;
                if (MakeMaterials(view!) is { } mm)
                    return Done(false, mm);
                break;
            case Phase.Materials when _phaseTime > 0.3:
                Next(Phase.Pbr);
                break;
            case Phase.Pbr when _phaseTime > 1.5:
                if (Colour(view!, "pbr", $"with PBR materials for {_made.Count} texture(s)") is { } p2)
                    return Done(false, p2);
                Next(Phase.File);
                break;
            case Phase.File:
                if (RegisterFile(view!) is { } f)
                    return Done(false, f);
                Next(Phase.FileCheck);
                break;
            case Phase.FileCheck when _phaseTime > 1.5:
                if (CheckFile(view!) is { } fc)
                    return Done(false, fc);
                Picture("-file");
                Next(Phase.Quality);
                break;
            case Phase.Quality when _phaseTime > 0.4:
                if (CheckQuality(view!) is { } q)
                    return Done(false, q);
                if (_step > 3)
                    Next(Phase.Toggle);
                else
                    _phaseTime = 0;
                break;
            case Phase.Toggle when !_acted:
                _acted = true;
                PressToggle();
                break;
            case Phase.Toggle when _phaseTime > 1.0:
                if (CheckClassicAgain(view!) is { } t)
                    return Done(false, t);
                Next(Phase.Done);
                break;
            case Phase.Done:
                Cleanup();
                return Done(true, string.Join("; ", _seen));
        }
        return null;
    }

    /// <summary>Development: each classic map surface's material colour and mean vertex colour.</summary>
    private static void DebugColours(StateView3D view)
    {
        foreach (var (mi, si, m, id) in MaterialsTest.Surfaces(view.MapNode!))
        {
            if (m is ShaderMaterial wm && !wm.HasMeta(Variants.Name(ClassicMaterials.MapShaderMeta)))
            {
                var code = wm.Shader.Code;
                GD.Print($"ffx debug: other {mi.Name}/{si} {RemasterTextures.ShortName(id ?? "")} {code.Split('\n').FirstOrDefault(l => l.StartsWith("render_mode"))} {string.Join(" ", code.Split('\n').Where(l => l.StartsWith("#define")))} prio {wm.RenderPriority}");
            }
            if (m is not ShaderMaterial sm || !sm.HasMeta(Variants.Name(ClassicMaterials.MapShaderMeta)))
                continue;
            using var mc = sm.GetShaderParameter(Variants.Name("material_colour"));
            var arrays = mi.Mesh.SurfaceGetArrays(si);
            var cols = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
            var sum = new Vector4();
            foreach (var c in cols)
                sum += new Vector4(c.R, c.G, c.B, c.A);
            if (cols.Length > 0)
                sum /= cols.Length;
            using var tv = sm.GetShaderParameter(Variants.Name("tex"));
            var tmean = new Vector4();
            if (tv.AsGodotObject() is Texture2D tt && tt.GetImage() is { } ti)
            {
                ti = (Image)ti.Duplicate();
                if (ti.IsCompressed()) ti.Decompress();
                ti.Resize(16, 16);
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        var p = ti.GetPixel(x, y);
                        tmean += new Vector4(p.R, p.G, p.B, p.A) / 256f;
                    }
            }
            GD.Print($"ffx debug: {mi.Name}/{si} {RemasterTextures.ShortName(id ?? "")} blend {sm.Shader.Code.Contains("#define BLEND")} mc {(mc.VariantType == Variant.Type.Nil ? "unset" : mc.AsColor().ToString())} vertex mean {sum} ({cols.Length}) texture mean {tmean} prio {sm.RenderPriority}");
        }
    }

    /// <summary>The viewport's picture now (null headless), written as FILE&lt;suffix&gt;.png with <c>--shot</c>.</summary>
    private Image? Picture(string suffix)
    {
        if (!_pictures)
            return null;
        var img = (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage();
        if (_shot != null && img != null)
        {
            var file = suffix.Length == 0 ? _shot : _shot[..^4] + suffix + ".png";
            img.SavePng(file);
            GD.Print($"ffx selftest: picture {file}");
        }
        return img;
    }

    /// <summary>The colour check of the picture now against the classic one (skipped headless).</summary>
    private string? Colour(StateView3D view, string suffix, string what)
    {
        var img = Picture("-" + suffix);
        if (img == null || _classic == null)
        {
            _seen.Add($"colour check {what}: skipped (no renderer)");
            return null;
        }
        var r = ColourBalance.Compare(_classic, img);
        GD.Print($"ffx selftest: colour {what}: {r}");
        _seen.Add($"colour {what} ({(UserSettings.ForwardPlus() ? "Forward+" : "Compatibility")}): {r}");
        // trying variants (development): measured, not failed
        return r.Ok || _variants.Count > 0 ? null : $"the remastered colours ({what}) leave the classic balance: {r}";
    }

    private static void PressToggle()
    {
        Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = GameHost.RemasterKey, Keycode = GameHost.RemasterKey, Pressed = true });
        Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = GameHost.RemasterKey, Keycode = GameHost.RemasterKey, Pressed = false });
    }

    /// <summary>The defaults: the rig on, its values from the game's look as the defaults convert them.</summary>
    private string? CheckDefaults(StateView3D view, Look? look)
    {
        var rig = view.Rig;
        if (!view.Remastered || !rig.Active)
            return "the map does not draw remastered (its render path says remastered)";
        if (rig.Lighting.Layer.Length != 0)
            return $"a lighting layer ({rig.Lighting.Layer}) is in force without a file";
        if (look is not { Valid: true } || look.Lights.Count == 0)
            return "no look with a key light";
        var b = rig.Base;
        var want = -(ClassicMaterials.LightDirection(look.Lights[0]) * ClassicMaterials.LightSign).Normalized();
        if (!b.KeyOn || !rig.Sun.Visible || b.KeyDirection.AngleTo(want) > 0.01f)
            return $"the sun does not follow the game's key light ({b.KeyDirection} against {want})";
        if ((-rig.Sun.GlobalBasis.Z).AngleTo(want) > 0.01f)
            return "the sun node does not point as resolved";
        // the colours stay near white (the game's are made for characters, blue at Besaid)
        float spread = Mathf.Max(b.KeyColour.R, Mathf.Max(b.KeyColour.G, b.KeyColour.B)) / Mathf.Max(0.01f, Mathf.Min(b.KeyColour.R, Mathf.Min(b.KeyColour.G, b.KeyColour.B)));
        if (spread > 1.6f)
            return $"the sun's colour is far from white ({b.KeyColour})";
        if (b.AmbientEnergy < 0.2f)
            return $"the ambient is nearly black ({b.AmbientEnergy})";
        var env = view.GetWorld3D()?.Environment;
        if (env == null || env.BackgroundMode != Godot.Environment.BGMode.Sky || env.TonemapMode == Godot.Environment.ToneMapper.Linear && LightingLayers.Defaults.ToneMapper != ToneMap.Linear)
            return "the environment is not the remastered one (sky, tone map)";
        if (b.FogOn != (look.FogNear < 60000f) || b.FogOn && !env.FogEnabled)
            return "the fog does not follow the game's";
        _seen.Add($"defaults: {b}");
        return null;
    }

    /// <summary>Materials from the classic pixels of the map's six biggest colour textures (as a remaster project makes them).</summary>
    private string? MakeMaterials(StateView3D view)
    {
        var area = new Dictionary<string, float>();
        var look = MapLook.Find(view.MapNode!);
        foreach (var (mi, _, m, id) in MaterialsTest.Surfaces(view.MapNode!))
        {
            if (id == null || look?.SkyMeshes.Contains(mi) == true)
                continue;
            var b = mi.GlobalTransform * mi.GetAabb();
            area[id] = area.GetValueOrDefault(id) + b.Size.X * b.Size.Z + b.Size.X * b.Size.Y + b.Size.Y * b.Size.Z;
        }
        var t0 = Time.GetTicksMsec();
        foreach (var id in area.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, System.StringComparer.Ordinal).Select(kv => kv.Key).Take(6))
        {
            if (RemasterTextures.ClassicImage(id) is not { } img)
                return $"{id}: no classic pixels";
            var recipe = new PbrRecipe { Upscale = 2, MaxSide = 1024 };
            var maps = PbrBuilder.Make(PbrBuilder.From(img), recipe);
            var (a, n, o, h, e) = PbrBuilder.InMemory(maps);
            var file = $"{RemasterTextures.ShortName(id)}.material.tres";
            if (ResourceSaver.Save(PbrBuilder.Material(a, n, o, h, e, recipe), $"{_dir}/{file}") != Error.Ok)
                return $"cannot write {file}";
            _set.Textures.Add(new RemasterTexture { Id = id, Material = file, Recipe = recipe, Source = "generated from the classic texture (LightingTest)" });
            _made.Add(id);
        }
        RemasterTextures.Register(_set, "selftest-lighting", _dir);
        _seen.Add($"materials from the classic pixels of {_made.Count} texture(s) in {Time.GetTicksMsec() - t0} ms");
        return _made.Count > 0 ? null : "no colour texture to make a material of";
    }

    private Vector3 _fileDirection;

    /// <summary>A lighting file of the check's: a custom key light, exposure, volumetric fog, VoxelGI, a volume around the camera.</summary>
    private string? RegisterFile(StateView3D view)
    {
        var cam = view.GetViewport().GetCamera3D();
        if (cam == null)
            return "no camera";
        _fileDirection = new Vector3(0.3f, -0.8f, -0.52f).Normalized();
        var m = new MapLighting
        {
            Level = PartIds.LevelKey(Level),
            KeyMode = LightSource.Custom,
            KeyDirection = _fileDirection,
            KeyColourMode = LightSource.Custom,
            KeyColour = new Color(1f, 0.9f, 0.8f),
            KeyEnergy = 2.0f,
            ToneExposure = 1.1f,
            VolumetricFog = true,
            VolumetricDensity = 0.002f,
            ScreenSsao = true,
            GiMode = GiMode.VoxelGi,
        };
        m.Volumes.Add(new LightingVolume
        {
            Name = "Around the camera",
            Transform = new Transform3D(Basis.Identity, cam.GlobalPosition),
            Size = new Vector3(400, 400, 400),
            Blend = 100f,
            SetExposure = true,
            Exposure = 0.7f,
        });
        // a fresh bake (the check's own, removed at the end)
        if (FileAccess.FileExists(RemasterLighting.BakePath(Level)))
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(RemasterLighting.BakePath(Level)));
        var path = $"{_dir}/lighting.tres";
        if (ResourceSaver.Save(m, path) != Error.Ok)
            return $"cannot write {path}";
        LightingLayers.Register(Level, path, "selftest-lighting");
        return null;
    }

    private string? CheckFile(StateView3D view)
    {
        var rig = view.Rig;
        if (rig.Lighting.Layer != "selftest-lighting")
            return $"the registered lighting file is not in force (layer '{rig.Lighting.Layer}')";
        var now = rig.Now;
        if (now.KeyDirection.AngleTo(_fileDirection) > 0.01f)
            return $"the key light does not take the file's direction ({now.KeyDirection})";
        if (now.Volumes.Length == 0 || !now.Volumes.StartsWith("Around the camera", System.StringComparison.Ordinal))
            return "the camera's volume is not blended in";
        // the camera is 200 deep in a box with a 100 blend: the volume's exposure in full
        float want = 0.7f * (UserSettings.ForwardPlus() ? 1f : RemasterLighting.CompatExposure);
        if (Mathf.Abs(now.Exposure - want) > 0.01f)
            return $"the volume's exposure is not in force ({now.Exposure}, base {rig.Base.Exposure}, want {want})";
        var env = view.GetWorld3D()!.Environment;
        if (!Mathf.IsEqualApprox(env.TonemapExposure, now.Exposure))
            return "the environment does not show the blended exposure";
        bool fwd = UserSettings.ForwardPlus();
        if (env.VolumetricFogEnabled != fwd)
            return $"volumetric fog {(env.VolumetricFogEnabled ? "on" : "off")} under {(fwd ? "Forward+" : "Compatibility")}";
        if (now.Gi != (fwd ? GiMode.VoxelGi : GiMode.None))
            return $"GI {now.Gi} under {(fwd ? "Forward+" : "Compatibility")}";
        if (fwd && _pictures && rig.VoxelState.Length == 0)
            return "no VoxelGI bake";
        _seen.Add($"lighting file: {now}; VoxelGI {(rig.VoxelState.Length > 0 ? rig.VoxelState : "not drawn")}");
        return null;
    }

    /// <summary>Step 0 sets the quality low; steps 1 to 3 check the one set (low, medium, high) and set the next.</summary>
    private string? CheckQuality(StateView3D view)
    {
        if (_step > 0)
        {
            var q = UserSettings.Current.Lighting;
            var env = view.GetWorld3D()!.Environment;
            bool fwd = UserSettings.ForwardPlus();
            var gi = view.Rig.Now.Gi;
            // the file asks for VoxelGI: kept at medium and high
            if ((gi != GiMode.None) != (fwd && q != "low") || env.VolumetricFogEnabled != (fwd && q != "low") || env.SsaoEnabled != (fwd && q != "low"))
                return $"lighting {q}: GI {gi}, volumetric fog {env.VolumetricFogEnabled}, SSAO {env.SsaoEnabled}";
            _seen.Add($"lighting {q}: GI {gi}, volumetric fog {(env.VolumetricFogEnabled ? "on" : "off")}, SSAO {(env.SsaoEnabled ? "on" : "off")}, "
                + $"shadows {view.Rig.Sun.DirectionalShadowMode} to {view.Rig.Sun.DirectionalShadowMaxDistance:0}");
        }
        if (_step < 3)
            UserSettings.Current.Lighting = UserSettings.LightingQualities[2 - _step];
        _step++;
        return null;
    }

    private string? CheckClassicAgain(StateView3D view)
    {
        if (view.Remastered || view.Rig.Active)
            return "F7 did not switch the map to classic";
        var env = view.GetWorld3D()!.Environment;
        if (env.TonemapMode != Godot.Environment.ToneMapper.Linear || env.BackgroundMode != Godot.Environment.BGMode.Color || env.VolumetricFogEnabled || env.SdfgiEnabled)
            return "the classic environment is not back";
        if (view.Rig.Sun.Visible)
            return "the sun still shines on the classic map";
        if (_classic != null && Picture("-classic-again") is { } img)
        {
            var r = ColourBalance.Compare(_classic, img);
            if (Mathf.Abs(r.DL) > 0.01f || r.ChromaRatio is < 0.97f or > 1.03f)
                return $"the classic picture after F7 is not the first one: {r}";
            _seen.Add($"classic again after F7 (dL {r.DL:+0.000;-0.000;0.000})");
        }
        else
            _seen.Add("classic again after F7");
        return null;
    }

    /// <summary>A variant's property: a <see cref="MapLighting"/> one by name, or <c>_CompatKeyScale</c> and the like (the rig's Compatibility factors).</summary>
    private static void SetProperty(MapLighting m, string name, JsonElement value)
    {
        if (name.StartsWith('_'))
        {
            float f = value.GetSingle();
            switch (name)
            {
                case "_CompatKeyScale": RemasterLighting.CompatKeyScale = f; break;
                case "_CompatAmbientScale": RemasterLighting.CompatAmbientScale = f; break;
                case "_CompatExposure": RemasterLighting.CompatExposure = f; break;
            }
            return;
        }
        using var had = m.Get(name);
        Variant v = value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array when had.VariantType == Variant.Type.Vector3 => new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle()),
            JsonValueKind.Array => new Color(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle()),
            JsonValueKind.Number when had.VariantType == Variant.Type.Int => value.GetInt64(),
            JsonValueKind.Number => value.GetDouble(),
            _ => value.GetString() ?? "",
        };
        m.Set(name, v);
    }

    private static readonly (float, float, float) Scales = (RemasterLighting.CompatKeyScale, RemasterLighting.CompatAmbientScale, RemasterLighting.CompatExposure);

    private static void RestoreScales() =>
        (RemasterLighting.CompatKeyScale, RemasterLighting.CompatAmbientScale, RemasterLighting.CompatExposure) = Scales;

    private void Cleanup()
    {
        var bake = ProjectSettings.GlobalizePath(RemasterLighting.BakePath(Level));
        if (FileAccess.FileExists(bake))
        {
            DirAccess.RemoveAbsolute(bake);
            // the folders too when the check's bake was all they held
            DirAccess.RemoveAbsolute(bake.GetBaseDir());
            DirAccess.RemoveAbsolute(bake.GetBaseDir().GetBaseDir());
        }
        LightingLayers.Unregister(Level, "selftest-lighting");
        LightingLayers.Unregister(Level, "variant");
        if (DirAccess.DirExistsAbsolute(_dir))
        {
            foreach (var f in DirAccess.GetFilesAt(_dir))
                DirAccess.RemoveAbsolute($"{_dir}/{f}");
            DirAccess.RemoveAbsolute(_dir);
        }
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx selftest: {_phase} -> {p} ({_total:0.0} s)");
        _phase = p;
        _phaseTime = 0;
        _acted = false;
    }

    private bool Done(bool pass, string note)
    {
        if (!pass)
            Cleanup();
        Note = note;
        return pass;
    }
}
