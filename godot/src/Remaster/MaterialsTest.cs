using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GdFfx.Api;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R2's check (<c>--selftest-materials</c>; run with <c>--devsave=7</c>): textures and materials
/// in the running game, with test images made at run time (no game data written anywhere but a
/// temporary folder, removed at the end). Before the map loads, it asks the where-used index
/// for Besaid beach's two most used colour textures, makes a replacement image for the first
/// (a synthetic pattern) and a physically based material from it with the ported generators,
/// and registers both as a texture layer, with the render path forced classic. Then:
/// <list type="number">
/// <item>classic: every surface sampling the first texture draws the replacement through the
/// classic shader (unshaded); every other one its classic texture;</item>
/// <item>Besaid's render path set to remastered (a registered overrides layer): the first
/// texture's surfaces draw the lit material with its maps (albedo, normal, ORM, height), the
/// others a lit material from their colour texture, the sky dome stays classic; the rig is on
/// (the sun from the game's key light, the sky, ClassicPost off under Forward+);</item>
/// <item>F7 (the toggle key, as a player presses it): classic again, the rig off; F7: remastered;</item>
/// <item>a replacement for the second texture registered while the map is shown, bound on its
/// surfaces at once (<see cref="RemasterTextures.Refresh"/>), drawn classic again by F7.</item>
/// </list>
/// With <c>--shot=FILE.png</c> under a renderer: FILE.png (classic, the replacement drawn by the
/// classic shaders) and FILE-remastered.png.
/// </summary>
public sealed class MaterialsTest : ITest
{
    public const string Level = "/map/bsil/bsil00/";

    private enum Phase { Setup, Boot, Classic, Remaster, Remastered, ToggleOff, ToggleOn, Refresh, Materials, ShotClassic, Final, ShotRemastered, Done }

    private readonly double _timeout;
    private readonly string? _shot;
    private readonly string _dir = ProjectSettings.GlobalizePath("user://selftest-materials");
    private Phase _phase = Phase.Setup;
    private double _phaseTime, _total;
    private string _first = "", _second = "";
    /// <summary>The texture whose material is checked remastered (the first one, or the editor's).</summary>
    private string _pbr = "";
    /// <summary><c>--textures-file=</c>: the editor's saved index instead of the test's own textures (<c>--textures-expect=</c> names them).</summary>
    private readonly string? _file = GameSettings.Arg("textures-file");
    private readonly RemasterTextureSet _set = new();
    private readonly List<string> _seen = new();
    /// <summary>The phase's one action (a key press, a registration) is done.</summary>
    private bool _acted;

    public string Note { get; private set; } = "";

    public MaterialsTest(double timeout, string? shot)
    {
        _timeout = timeout;
        _shot = shot;
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
            {
                if (view?.MapLevel == Level)
                    return Done(false, "the beach was shown before the check could register its textures");
                if (Setup(game) is { } fail)
                    return Done(false, fail);
                Next(Phase.Boot);
                break;
            }
            case Phase.Boot when v.Current is { World.Map: BesaidTest.MapBeach, World.PlayerControl: true }
                && view != null && view.MapLevel == Level && view.MapNode != null && _phaseTime > 1.0:
                Next(Phase.Classic);
                break;
            case Phase.Classic:
                if (CheckClassic(view!, _first, "at load") is { } c)
                    return Done(false, c);
                Next(Phase.Remaster);
                break;
            case Phase.Remaster:
                Remaster();
                Next(Phase.Remastered);
                break;
            case Phase.Remastered when _phaseTime > 0.3:
                if (CheckRemastered(view!) is { } r)
                    return Done(false, r);
                Next(Phase.ToggleOff);
                break;
            case Phase.ToggleOff when !_acted:
                _acted = true;
                PressToggle();
                break;
            case Phase.ToggleOff when _phaseTime > 0.3:
                if (view!.Remastered || view.Rig.Active)
                    return Done(false, "F7 did not switch the map to classic");
                if (CheckClassic(view, _first, "after F7") is { } t)
                    return Done(false, t);
                _seen.Add("F7: classic");
                Next(Phase.ToggleOn);
                break;
            case Phase.ToggleOn when !_acted:
                _acted = true;
                PressToggle();
                break;
            case Phase.ToggleOn when _phaseTime > 0.3:
                if (CheckRemastered(view!) is { } t2)
                    return Done(false, "after F7 again: " + t2);
                _seen.Add("F7: remastered");
                Next(Phase.Refresh);
                break;
            case Phase.Refresh when !_acted:
            {
                _acted = true;
                // a second replacement while the map is shown: classic again, bound at once
                RenderPaths.Set("classic");
                AddReplacement(_second, "second", new Color(0.9f, 0.2f, 0.6f), new Color(0.1f, 0.1f, 0.1f));
                RemasterTextures.Register(_set, "selftest-materials", _dir);
                int n = RemasterTextures.Refresh(view!.MapNode!);
                _seen.Add($"refresh: {n} texture parameter(s) bound");
                break;
            }
            case Phase.Refresh when _phaseTime > 0.3:
                if (CheckClassic(view!, _second, "after the refresh") is { } f)
                    return Done(false, f);
                Next(Phase.Materials);
                break;
            case Phase.Materials when !_acted:
                _acted = true;
                if (MakeMaterials(view!) is { } m)
                    return Done(false, m);
                break;
            case Phase.Materials when _phaseTime > 0.2:
                Next(Wants() ? Phase.ShotClassic : Phase.Final);
                break;
            case Phase.ShotClassic when _phaseTime > 0.5:
                Shot(_shot!);
                Next(Phase.Final);
                break;
            case Phase.Final when !_acted:
                // back to the map's own render path (remastered)
                _acted = true;
                RenderPaths.Set(null);
                break;
            case Phase.Final when _phaseTime > 0.3:
                if (CheckMade(view!) is { } fm)
                    return Done(false, fm);
                Next(Wants() ? Phase.ShotRemastered : Phase.Done);
                break;
            case Phase.ShotRemastered when _phaseTime > 1.0:
                Shot(_shot![..^4] + "-remastered.png");
                Next(Phase.Done);
                break;
            case Phase.Done:
                Cleanup();
                return Done(true, string.Join("; ", _seen));
        }
        return null;
    }

    private bool Wants() => _shot != null && DisplayServer.GetName() != "headless";

    private static void Shot(string file)
    {
        (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"ffx selftest: picture {file}");
    }

    private static void PressToggle()
    {
        Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = GameHost.RemasterKey, Keycode = GameHost.RemasterKey, Pressed = true });
        Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = GameHost.RemasterKey, Keycode = GameHost.RemasterKey, Pressed = false });
    }

    /// <summary>The two textures, the test images, the material, registered; the render path classic.</summary>
    private string? Setup(FfxGame game)
    {
        var json = game.ClassicTextureUses(Level);
        if (json.Length == 0)
            return "no where-used answer for Besaid beach";
        var ranked = new List<(string Id, int Parts)>();
        using (var doc = JsonDocument.Parse(json))
            foreach (var t in doc.RootElement.GetProperty("textures").EnumerateArray())
                if (t.GetProperty("samplers").EnumerateArray().Any(x => x.GetString() == "TextureSampler"))
                    ranked.Add((t.GetProperty("id").GetString()!, t.GetProperty("parts").GetArrayLength()));
        ranked.Sort((a, b) => b.Parts != a.Parts ? b.Parts.CompareTo(a.Parts) : string.CompareOrdinal(a.Id, b.Id));
        if (ranked.Count < 2)
            return $"Besaid beach samples {ranked.Count} colour texture(s)";
        DirAccess.MakeDirRecursiveAbsolute(_dir);
        if (_file != null)
        {
            // the editor's saved textures: its replaced one and its material
            if (GameSettings.Arg("textures-expect") is not { } expectFile || !FileAccess.FileExists(expectFile))
                return "--textures-file needs --textures-expect=FILE (the editor check's textures-expect.json)";
            using var expect = JsonDocument.Parse(FileAccess.GetFileAsString(expectFile));
            _first = expect.RootElement.GetProperty("replaced").GetString()!;
            _pbr = expect.RootElement.GetProperty("material").GetString()!;
            _second = ranked.First(r => r.Id != _first && r.Id != _pbr).Id;
            RemasterTextures.Register(_file, "editor");
            RenderPaths.Set("classic");
            _seen.Add($"the editor's textures ({_file}): {RemasterTextures.ShortName(_first)} replaced, {RemasterTextures.ShortName(_pbr)} with a material");
            return null;
        }
        (_first, _second) = (ranked[0].Id, ranked[1].Id);
        _pbr = _first;
        _seen.Add($"where-used: {ranked.Count} colour textures, {RemasterTextures.ShortName(_first)} on {ranked[0].Parts} parts, "
            + $"{RemasterTextures.ShortName(_second)} on {ranked[1].Parts}");
        var albedo = AddReplacement(_first, "first", new Color(0.85f, 0.75f, 0.35f), new Color(0.25f, 0.45f, 0.75f));
        // the material from the replacement, by a starter profile (R2b phase 3) on the CPU graph
        if (ByProfile("stone-and-concrete", 2, 4096) is not { } recipe)
            return "no starter profile stone-and-concrete";
        var t0 = Time.GetTicksMsec();
        var maps = Materialize.MaterialBaker.CpuImages(_first, PbrBuilder.From(albedo), recipe);
        var (a, n, o, h, e) = PbrBuilder.InMemory(maps);
        var mat = PbrBuilder.Material(a, n, o, h, e, recipe);
        var matPath = $"{_dir}/material.tres";
        if (ResourceSaver.Save(mat, matPath) != Error.Ok)
            return $"cannot write {matPath}";
        _set.Textures[0].Material = "material.tres";
        _set.Textures[0].Recipe = recipe;
        (_set.Textures[0].Profile, _set.Textures[0].ProfileHash) = ("stone-and-concrete", MaterialProfiles.Hash(MaterialProfiles.Load("stone-and-concrete")!));
        _seen.Add($"material made by the profile stone-and-concrete in {Time.GetTicksMsec() - t0} ms ({maps.Albedo.GetWidth()}x{maps.Albedo.GetHeight()}, "
            + $"{PbrBuilder.Files(maps).Count()} maps)");
        RemasterTextures.Register(_set, "selftest-materials", _dir);
        RenderPaths.Set("classic");
        GD.Print($"ffx selftest: textures registered: {_first} replaced (and a material), render path classic");
        return null;
    }

    /// <summary>
    /// A starter profile's settings (docs/remaster-materials.md, "Profiles") with the check's
    /// upscale and size, as a texture made with it is (null: no such profile).
    /// </summary>
    public static PbrRecipe? ByProfile(string name, int upscale, int maxSide)
    {
        if (MaterialProfiles.Load(name) is not { } p)
            return null;
        var r = MaterialProfiles.Effective(p, null);
        (r.Upscale, r.MaxSide) = (upscale, maxSide);
        return r;
    }

    /// <summary>A synthetic replacement for <paramref name="id"/> (a checker with a frame), written into the temporary folder and added to the set.</summary>
    private Image AddReplacement(string id, string name, Color a, Color b)
    {
        const int size = 128;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                img.SetPixel(x, y, x < 4 || y < 4 || x >= size - 4 || y >= size - 4 ? Colors.Black : ((x / 16 + y / 16) % 2 == 0 ? a : b));
        var file = $"{name}.png";
        img.SavePng($"{_dir}/{file}");
        var entry = _set.Textures.FirstOrDefault(t => t.Id == id);
        if (entry == null)
            _set.Textures.Add(entry = new RemasterTexture { Id = id });
        entry.Replacement = file;
        entry.Source = "a synthetic test pattern (MaterialsTest)";
        return img;
    }

    /// <summary>The textures given materials from their classic pixels (<see cref="MakeMaterials"/>).</summary>
    private readonly List<string> _made = new();

    /// <summary>
    /// Physically based materials for the map's biggest colour textures (by the area of the
    /// meshes drawn with them), made from their classic pixels by the generators, as a remaster
    /// project makes them (written into the temporary folder only, removed at the end).
    /// </summary>
    private string? MakeMaterials(StateView3D view)
    {
        var area = new Dictionary<string, float>();
        foreach (var (mi, _, m, id) in Surfaces(view.MapNode!))
        {
            if (id == null || id == _first || id == _second || id == _pbr || !m.HasMeta(Variants.Name(ClassicMaterials.MapShaderMeta))
                || MapLook.Find(view.MapNode!)?.Sky.Contains(mi.Name) == true)
                continue;
            var b = mi.GlobalTransform * mi.GetAabb();
            area[id] = area.GetValueOrDefault(id) + b.Size.X * b.Size.Z + b.Size.X * b.Size.Y + b.Size.Y * b.Size.Z;
        }
        var t0 = Time.GetTicksMsec();
        foreach (var id in area.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, System.StringComparer.Ordinal).Select(kv => kv.Key).Take(6))
        {
            if (RemasterTextures.ClassicImage(id) is not { } img)
                return $"{id}: no classic pixels";
            if (ByProfile("rock", 1, 1024) is not { } recipe)
                return "no starter profile rock";
            var maps = Materialize.MaterialBaker.CpuImages(id, PbrBuilder.From(img), recipe);
            var (a, n, o, h, e) = PbrBuilder.InMemory(maps);
            var file = $"{RemasterTextures.ShortName(id)}.material.tres";
            if (ResourceSaver.Save(PbrBuilder.Material(a, n, o, h, e, recipe), $"{_dir}/{file}") != Error.Ok)
                return $"cannot write {file}";
            _set.Textures.Add(new RemasterTexture { Id = id, Material = file, Recipe = recipe, Profile = "rock", Source = "generated from the classic texture by the profile rock (MaterialsTest)" });
            _made.Add(id);
        }
        RemasterTextures.Register(_set, "selftest-materials", _dir);
        _seen.Add($"materials made by the profile rock from the classic pixels of {_made.Count} texture(s) in {Time.GetTicksMsec() - t0} ms");
        return _made.Count > 0 ? null : "no colour texture to make a material of";
    }

    /// <summary>Remastered again: the made materials drawn with their maps.</summary>
    private string? CheckMade(StateView3D view)
    {
        if (!view.Remastered)
            return "the map does not draw remastered again";
        var found = new HashSet<string>();
        foreach (var (mi, s, m, id) in Surfaces(view.MapNode!))
            if (id != null && _made.Contains(id) && m is ShaderMaterial sm && sm.Shader.Code.Contains("#define HAS_ORM"))
                found.Add(id);
        if (found.Count != _made.Count)
            return $"{_made.Count - found.Count} made material(s) not drawn: {string.Join(", ", _made.Except(found))}";
        _seen.Add($"remastered with them: {found.Count} texture(s) drawn with their made materials");
        return null;
    }

    private void Remaster()
    {
        var ov = new MapOverrides { Level = PartIds.LevelKey(Level), Status = MapStatus.InProgress, RenderPath = RenderPath.Remastered };
        var path = $"{_dir}/overrides.tres";
        ResourceSaver.Save(ov, path);
        RemasterOverrides.Register(Level, path, "selftest-materials");
        RenderPaths.Set(null);
        UserSettings.Current.RenderPath = "map";
    }

    /// <summary>The map's surfaces drawn with classic map materials, and the texture ID each samples.</summary>
    internal static List<(MeshInstance3D Mesh, int Surface, Material Material, string? Id)> Surfaces(Node map)
    {
        var r = new List<(MeshInstance3D, int, Material, string?)>();
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } mesh } mi && mi.IsVisibleInTree())
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                    if (mi.GetSurfaceOverrideMaterial(s) is { } m)
                        r.Add((mi, s, m, RemasterTextures.ColourId(m) ?? PartIds.GetString(m, RemasterLook.LitTextureMeta)));
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(map);
        return r;
    }

    private static Texture? Param(Material m, string name)
    {
        using var v = ((ShaderMaterial)m).GetShaderParameter(Variants.Name(name));
        return v.AsGodotObject() as Texture;
    }

    /// <summary>Classic: <paramref name="id"/>'s surfaces draw its replacement unshaded, the others their classic textures.</summary>
    private string? CheckClassic(StateView3D view, string id, string when)
    {
        if (view.Remastered)
            return $"{when}: the map draws remastered";
        var want = RemasterTextures.Texture(RemasterTextures.For(id)?.Replacement ?? "");
        if (want == null)
            return $"{when}: no replacement loaded for {id}";
        int replaced = 0, others = 0;
        foreach (var (mi, s, m, sid) in Surfaces(view.MapNode!))
        {
            if (m is not ShaderMaterial sm || !sm.HasMeta(Variants.Name(ClassicMaterials.MapShaderMeta)))
                continue;
            if (!sm.Shader.Code.Contains("unshaded"))
                return $"{when}: {mi.Name}/{s} is not drawn by the classic shader";
            var tex = Param(sm, "tex");
            if (sid == id)
            {
                if (tex != want)
                    return $"{when}: {mi.Name}/{s} samples {id} but draws {tex?.ResourceName ?? "nothing"}, not the replacement";
                replaced++;
            }
            else if (sid != null && RemasterTextures.For(sid) is not { Replaced: true } && tex != RemasterTextures.ClassicOf(sm, "tex"))
                return $"{when}: {mi.Name}/{s} ({sid}) does not draw its classic texture";
            else
                others++;
        }
        if (replaced == 0)
            return $"{when}: no surface draws {id}";
        _seen.Add($"classic {when}: {replaced} surface(s) draw the replacement of {RemasterTextures.ShortName(id)}, {others} other(s) as they were");
        return null;
    }

    /// <summary>Remastered: the first texture's surfaces draw its PBR material lit, the others lit from their colour texture, the rig on.</summary>
    private string? CheckRemastered(StateView3D view)
    {
        if (!view.Remastered || !view.Rig.Active)
            return "the map does not draw remastered (its render path says remastered)";
        if (!view.Rig.Sun.Visible)
            return "the rig's sun is off";
        var env = view.GetWorld3D()?.Environment;
        if (env == null || env.BackgroundMode != Godot.Environment.BGMode.Sky)
            return "the environment shows no sky";
        var pbr = RemasterTextures.MaterialFor(_pbr) as BaseMaterial3D;
        if (pbr == null)
            return $"{_pbr}: no material loaded";
        int lit = 0, plain = 0, classic = 0;
        foreach (var (mi, s, m, sid) in Surfaces(view.MapNode!))
        {
            if (m is not ShaderMaterial sm)
                continue;
            if (sm.HasMeta(Variants.Name(ClassicMaterials.MapShaderMeta)))
            {
                // only the sky and the blended layers (MapLighting.BlendedParts) keep the classic map shader
                if (MapLook.Find(view.MapNode!)?.Sky.Contains(mi.Name) != true && !RemasterLook.IsBlended(sm))
                    return $"{mi.Name}/{s} still draws classic";
                classic++;
                continue;
            }
            if (!sm.HasMeta(Variants.Name(RemasterLook.LitTextureMeta)))
                continue;
            var code = sm.Shader.Code;
            if (code.Contains("unshaded"))
                return $"{mi.Name}/{s}: the lit material is unshaded";
            if (sid == _pbr)
            {
                if (!code.Contains("#define HAS_ORM") || !code.Contains("#define HAS_NORMAL") || !code.Contains("#define HAS_HEIGHT"))
                    return $"{mi.Name}/{s}: {_pbr}'s lit material lacks its maps";
                if (Param(sm, "albedo_tex") != pbr.AlbedoTexture || Param(sm, "orm_tex") != pbr.OrmTexture)
                    return $"{mi.Name}/{s}: the lit material does not sample the PBR material's maps";
                lit++;
            }
            else
                plain++;
        }
        if (lit == 0 || plain == 0)
            return $"lit surfaces: {lit} with the PBR material, {plain} from a colour texture";
        bool fwd = UserSettings.ForwardPlus();
        if (fwd && RemasterLighting.LinearOut < 0.5f)
            return "Forward+: the classic shaders do not write linear colours while remastered";
        _seen.Add($"remastered: {lit} surface(s) with the PBR material, {plain} lit from their colour texture, {classic} sky and blended surface(s) classic; "
            + $"sun {view.Rig.Sun.LightEnergy:0.00} at {view.Rig.Sun.GlobalRotationDegrees.X:0}/{view.Rig.Sun.GlobalRotationDegrees.Y:0} deg");
        return null;
    }

    private void Cleanup()
    {
        foreach (var f in DirAccess.GetFilesAt(_dir))
            DirAccess.RemoveAbsolute($"{_dir}/{f}");
        DirAccess.RemoveAbsolute(_dir);
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
