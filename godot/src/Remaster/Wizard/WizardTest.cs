using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GdFfx.Api;
using Godot;

namespace GdFfx.Remaster.Wizard;

/// <summary>
/// R4's game check (<c>--selftest-wizard</c>, with <c>--devsave=7</c>): Besaid beach drawn with the
/// remaster wizard's output.
/// <list type="bullet">
/// <item>With <c>--wizard-expect=FILE</c> (the editor check's <c>wizard-expect.json</c>): the
/// files the editor's wizard applied (the texture index, the map's overrides and
/// <c>lighting.tres</c>) registered as layers: the map drawn remastered by its own render path,
/// every accepted texture drawn with its made material, the rejected one without, the remade one
/// with its new profile, the lighting file in force (its GI and exposure).</item>
/// <item>Without: the wizard's pass run in the game on the same code (<see cref="WizardPass"/>):
/// the map's textures classified, materials made by the chosen profiles on the CPU (at most
/// <c>--wizard-size</c> px, 512 by default), the lighting from the game's look with GI for the
/// map's size, the exposure matched once to the classic picture as the editor does (the GPU check
/// on Windows, where the editor's files are not at hand).</item>
/// </list>
/// Both: the classic picture first; remastered, R3's colour check against it (with a renderer;
/// skipped headless); F7 classic again. <c>--shot=FILE.png</c>: FILE.png (classic) and
/// FILE-remastered.png. Files of its own in <c>user://selftest-wizard</c>, removed at the end.
/// </summary>
public sealed class WizardTest : ITest
{
    public const string Level = "/map/bsil/bsil00/";

    private enum Phase { Setup, Boot, ShotClassic, Pass, Remaster, Check, Tune, Toggle, Done }

    private readonly double _timeout;
    private readonly string? _shot;
    private readonly string _dir = ProjectSettings.GlobalizePath("user://selftest-wizard");
    private readonly bool _pictures = DisplayServer.GetName() != "headless";
    private readonly JsonObject? _expect;
    private Phase _phase = Phase.Setup;
    private double _phaseTime, _total;
    private readonly List<string> _seen = new();
    private Image? _classic;
    private bool _acted;
    private readonly HashSet<string> _made = new();
    private MapLighting? _lighting;
    private float _firstDL;

    public string Note { get; private set; } = "";

    public WizardTest(double timeout, string? shot)
    {
        (_timeout, _shot) = (timeout, shot);
        if (GameSettings.Arg("wizard-expect") is { Length: > 0 } f && System.IO.File.Exists(f))
            _expect = JsonNode.Parse(System.IO.File.ReadAllText(f))?.AsObject();
    }

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > _timeout)
            return Done(false, $"timed out in phase {_phase} ({string.Join("; ", _seen)})");
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        switch (_phase)
        {
            case Phase.Setup when GameHost.Instance?.Classic is { Open: true }:
                DirAccess.MakeDirRecursiveAbsolute(_dir);
                RenderPaths.Set("classic");
                UserSettings.Current.Lighting = "high";
                if (GameSettings.Arg("wizard-expect") is { Length: > 0 } && _expect == null)
                    return Done(false, "--wizard-expect: no such file");
                if (_expect != null)
                {
                    // the editor's files as layers (its overrides say remastered; F7's choice keeps classic for the first picture)
                    RemasterTextures.Register(Str("textures"), "wizard");
                    RemasterOverrides.Register(Level, Str("overrides"), "wizard");
                    LightingLayers.Register(Level, Str("lighting"), "wizard");
                }
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
                Next(_expect != null ? Phase.Remaster : Phase.Pass);
                break;
            case Phase.Pass:
                if (RunPass(view!) is { } p)
                    return Done(false, p);
                Next(Phase.Remaster);
                break;
            case Phase.Remaster:
                RenderPaths.Set(null);
                UserSettings.Current.RenderPath = "map";
                Next(Phase.Check);
                break;
            case Phase.Check when _phaseTime > 2.0:
            {
                if (Check(view!) is { } c)
                    return Done(false, c);
                var r = Colour("remastered");
                // the in-game pass tunes the exposure once, as the editor does
                if (_expect == null && r != null && _lighting != null
                    && WizardPass.TuneExposure(_lighting.ToneExposure, r) is var e && !Mathf.IsEqualApprox(e, _lighting.ToneExposure))
                {
                    _firstDL = r.DL;
                    _seen.Add($"exposure {_lighting.ToneExposure:0.##} -> {e:0.##}");
                    _lighting.ToneExposure = e;
                    SaveLighting();
                    Next(Phase.Tune);
                    break;
                }
                if (Judge(r) is { } j)
                    return Done(false, j);
                Next(Phase.Toggle);
                break;
            }
            case Phase.Tune when _phaseTime > 1.5:
            {
                var r = Colour("remastered");
                if (r != null && Mathf.Abs(r.DL) > Mathf.Abs(_firstDL))
                    _seen.Add("the tuned exposure did not bring the lightness nearer");
                if (Judge(r) is { } j)
                    return Done(false, j);
                Next(Phase.Toggle);
                break;
            }
            case Phase.Toggle when !_acted:
                _acted = true;
                Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = GameHost.RemasterKey, Keycode = GameHost.RemasterKey, Pressed = true });
                Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = GameHost.RemasterKey, Keycode = GameHost.RemasterKey, Pressed = false });
                break;
            case Phase.Toggle when _phaseTime > 1.0:
                if (view!.Remastered)
                    return Done(false, "F7 did not switch the map to classic");
                _seen.Add("F7: classic again");
                Next(Phase.Done);
                break;
            case Phase.Done:
                Cleanup();
                return Done(true, string.Join("; ", _seen));
        }
        return null;
    }

    private string Str(string key) => _expect?[key]?.GetValue<string>() ?? "";

    /// <summary>The wizard's pass in the game: classify, make the materials by their profiles (CPU), the lighting; registered as layers.</summary>
    private string? RunPass(StateView3D view)
    {
        var t0 = Time.GetTicksMsec();
        var (textures, box) = WizardPass.Analyze(view.MapNode!, Level, RemasterTextures.ClassicImage);
        int side = (int)GameSettings.Number("wizard-size", 512);
        var set = new RemasterTextureSet();
        foreach (var t in textures.Where(t => t.Class.Kind != TextureKind.Keep))
        {
            if (RemasterTextures.ClassicImage(t.Id) is not { } img || WizardPass.Recipe(t.Class.Profile, side) is not { } recipe)
                continue;
            var maps = Materialize.MaterialBaker.CpuImages(t.Id, PbrBuilder.From(img), recipe);
            var (a, n, o, h, e) = PbrBuilder.InMemory(maps);
            var file = $"{RemasterTextures.ShortName(t.Id)}.material.tres";
            if (ResourceSaver.Save(PbrBuilder.Material(a, n, o, h, e, recipe), $"{_dir}/{file}") != Error.Ok)
                return $"cannot write {file}";
            set.Textures.Add(new RemasterTexture { Id = t.Id, Material = file, Recipe = recipe, Profile = t.Class.Profile, Source = "the wizard's pass in the game (WizardTest)" });
            _made.Add(t.Id);
        }
        RemasterTextures.Register(set, "wizard", _dir);
        _lighting = WizardPass.Lighting(Level, box);
        SaveLighting();
        var ov = new MapOverrides { Level = PartIds.LevelKey(Level), Status = MapStatus.InProgress, RenderPath = RenderPath.Remastered };
        ResourceSaver.Save(ov, $"{_dir}/overrides.tres");
        RemasterOverrides.Register(Level, $"{_dir}/overrides.tres", "wizard");
        _seen.Add($"the pass in the game: {textures.Count} textures, {_made.Count} materials ({string.Join(", ", textures.Where(t => _made.Contains(t.Id)).GroupBy(t => t.Class.Profile).Select(g => $"{g.Key} {g.Count()}"))}) "
            + $"at most {side} px in {(Time.GetTicksMsec() - t0) / 1000.0:0.0} s, GI {_lighting.GiMode}");
        return _made.Count > 0 ? null : "no materials made";
    }

    private void SaveLighting()
    {
        // a new name each time (Godot's cache keeps a reloaded file's values)
        var path = $"{_dir}/lighting-{_lighting!.ToneExposure:0.00}.tres";
        ResourceSaver.Save(_lighting, path);
        LightingLayers.Register(Level, path, "wizard");
    }

    /// <summary>Remastered by the map's render path, the materials drawn, the lighting file in force.</summary>
    private string? Check(StateView3D view)
    {
        if (!view.Remastered || !view.Rig.Active)
            return "the map does not draw remastered (its render path says remastered)";
        if (view.Rig.Lighting.Layer != "wizard")
            return $"the wizard's lighting is not in force (layer '{view.Rig.Lighting.Layer}')";
        var want = _expect != null ? _expect["materials"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet() : _made;
        var drawn = new HashSet<string>();
        var plain = new HashSet<string>();
        foreach (var (_, _, m, id) in MaterialsTest.Surfaces(view.MapNode!))
            if (id != null && m is ShaderMaterial sm && !sm.HasMeta(Variants.Name(ClassicMaterials.MapShaderMeta)))
                (sm.Shader.Code.Contains("#define HAS_ORM") ? drawn : plain).Add(id);
        var missing = want.Where(id => !drawn.Contains(id) && plain.Contains(id)).ToList();
        if (missing.Count > 0)
            return $"{missing.Count} of the wizard's materials not drawn: {string.Join(", ", missing.Take(5).Select(RemasterTextures.ShortName))}";
        int shown = want.Count(drawn.Contains);
        if (shown < want.Count / 2)
            return $"only {shown} of {want.Count} materials drawn lit";
        var gi = view.Rig.Lighting.Lighting.GiMode;
        if (_expect != null)
        {
            var rejected = Str("rejected");
            if (drawn.Contains(rejected))
                return $"the rejected {RemasterTextures.ShortName(rejected)} is drawn with a material";
            var remade = Str("remade");
            if (RemasterTextures.MaterialFor(remade) == null)
                return $"the remade {RemasterTextures.ShortName(remade)} has no material";
            if (gi.ToString() != Str("gi"))
                return $"GI {gi}, the editor's file says {Str("gi")}";
            float exposure = _expect["exposure"]!.GetValue<float>() * (UserSettings.ForwardPlus() ? 1f : RemasterLighting.CompatExposure);
            if (Mathf.Abs(view.Rig.Base.Exposure - exposure) > 0.01f)
                return $"the exposure {view.Rig.Base.Exposure:0.##}, the file's {exposure:0.##}";
            _seen.Add($"the editor's output: {shown} of {want.Count} materials drawn with their maps, the rejected one without, the remade one's entry, "
                + $"GI {gi} (drawn {view.Rig.Now.Gi}), exposure {view.Rig.Base.Exposure:0.##}");
        }
        else
            _seen.Add($"remastered with the pass: {shown} of {want.Count} materials drawn with their maps, GI {gi} (drawn {view.Rig.Now.Gi}); {view.Rig.Base}");
        return null;
    }

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

    private ColourBalance.Result? Colour(string suffix)
    {
        var img = Picture("-" + suffix);
        if (img == null || _classic == null)
            return null;
        var r = ColourBalance.Compare(_classic, img);
        GD.Print($"ffx selftest: colour {suffix}: {r}");
        return r;
    }

    private string? Judge(ColourBalance.Result? r)
    {
        if (r == null)
        {
            _seen.Add("colour check: skipped (no renderer)");
            return null;
        }
        _seen.Add($"colour ({(UserSettings.ForwardPlus() ? "Forward+" : "Compatibility")}): {r}");
        return r.Ok ? null : $"the remastered colours leave the classic balance: {r}";
    }

    private void Cleanup()
    {
        RemasterTextures.Unregister("wizard");
        LightingLayers.Unregister(Level, "wizard");
        var bake = ProjectSettings.GlobalizePath(RemasterLighting.BakePath(Level));
        if (FileAccess.FileExists(bake))
            DirAccess.RemoveAbsolute(bake);
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
