#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// A map's remastered lighting in its edit scene (docs/remaster-editor.md, "Lighting"): the game's
/// lighting objects as nodes under <c>Lighting</c>, each tagged with its stable ID, built from the
/// project's <c>lighting.tres</c> (else the defaults) and the game's look as the game last showed it
/// (<see cref="LookCache"/>):
/// <list type="bullet">
/// <item><c>KeyLight</c> (<c>#light/key</c>): the game's key light as a directional light, turned,
/// coloured and dimmed with Godot's gizmo and inspector;</item>
/// <item><c>Ambient</c> (<c>#ambient</c>), <c>Fog</c> (<c>#fog</c>), <c>Environment</c>
/// (<c>#environment</c>): <see cref="LightingFacet"/>s, each a part of the map's
/// <see cref="MapLighting"/> in the inspector;</item>
/// <item><c>Volumes</c>: a <see cref="VolumeNode"/> per volume (<c>#volume/&lt;name&gt;</c>).</item>
/// </list>
/// While the 3D view draws the map remastered, the game's own rig (<see cref="RemasterLighting"/>)
/// shows the lighting (the key light node is its sun; the volumes by the editor camera). Saving
/// compares the key light with the game's (a turn is a custom direction, a colour a custom
/// colour) and writes <c>lighting.tres</c> beside the overrides: text, stable sub-resource IDs, a
/// save without an edit changes no byte; a lighting that says nothing but the defaults has no file.
/// </summary>
public sealed class LightingEdit
{
    public const string FileName = LightingLayers.FileName;

    public required MapEditRoot Root { get; init; }

    /// <summary>The lighting being edited (the facets edit it in place).</summary>
    public required MapLighting Res { get; init; }

    /// <summary>The game's look the key light is shown from (the cache's; without one, none: the defaults' direction).</summary>
    public required GdFfx.Api.Look Look { get; init; }

    /// <summary>Whether the look came from the game (the cache) or is the stand-in.</summary>
    public bool FromGame { get; init; }

    public Node3D Group { get; private set; } = null!;
    public DirectionalLight3D Key { get; private set; } = null!;
    public LightingFacet Ambient { get; private set; } = null!;
    public LightingFacet Fog { get; private set; } = null!;
    public LightingFacet Environment { get; private set; } = null!;
    public Node3D Volumes { get; private set; } = null!;

    private WorldEnvironment _envNode = null!;
    private RemasterLighting _rig = null!;

    /// <summary>The key light as shown at the open (or the last save): what a save compares the node with.</summary>
    private (Vector3 Dir, Color Colour, float Energy, bool On, float Level) _shown;

    public static string PathOf(string key) => $"{MapStore.Dir}/{key}/{FileName}";

    /// <summary>The rig of the preview (for the checks).</summary>
    public RemasterLighting Rig => _rig;

    /// <summary>Builds the lighting nodes of <paramref name="root"/>'s map.</summary>
    public static LightingEdit Build(MapEditRoot root)
    {
        var path = PathOf(root.Key);
        var res = ResourceLoader.Exists(path) && ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) is MapLighting l
            ? l
            : new MapLighting { Level = root.Key };
        var cached = LookCache.Read(root.Level);
        var edit = new LightingEdit { Root = root, Res = res, Look = cached ?? new GdFfx.Api.Look { Valid = true }, FromGame = cached != null };
        edit.Make();
        return edit;
    }

    private void Make()
    {
        var key = Root.Key;
        Group = new Node3D { Name = "Lighting" };
        Group.SetMeta(MapEditRoot.EditorNode, true);
        Root.AddChild(Group);
        Group.Owner = Root;
        Key = new DirectionalLight3D { Name = "KeyLight", ShadowEnabled = true };
        PartIds.SetString(Key, PartIds.Part, MapLighting.KeyId(key));
        Add(Key);
        Ambient = Facet("Ambient", MapLighting.AmbientId(key), ["Ambient"], ["VertexLight", "BlendedParts"]);
        Fog = Facet("Fog", MapLighting.FogId(key), ["Fog", "Volumetric"], []);
        Environment = Facet("Environment", MapLighting.EnvironmentId(key), ["Sky", "Tone", "Glow", "Screen", "Gi"], []);
        Volumes = new Node3D { Name = "Volumes" };
        Volumes.SetMeta(MapEditRoot.EditorNode, true);
        Add(Volumes);
        foreach (var v in Res.Volumes)
            if (v != null)
            {
                var n = VolumeNode.From(v);
                PartIds.SetString(n, PartIds.Part, MapLighting.VolumeId(key, n.Name));
                Volumes.AddChild(n);
                n.GlobalTransform = v.Transform;
                n.Owner = Root;
            }
        ShowKey();
        // the preview: the game's rig, its sun the key light node (added while remastered)
        _envNode = new WorldEnvironment { Name = "LightingPreview", Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color } };
        _envNode.SetMeta(MapEditRoot.EditorNode, true);
        _rig = new RemasterLighting(_envNode, null, null) { Fixed = Res, ExternalSun = Key };
        _rig.SetMeta(MapEditRoot.EditorNode, true);
        Root.AddChild(_rig);
        GD.Print($"ffx remaster: {key}: lighting {(ResourceLoader.Exists(PathOf(key)) ? "from " + PathOf(key) : "the defaults")}, "
            + $"the game's look {(FromGame ? "from " + LookCache.PathOf(Root.Level) : "not cached (play the map once): the defaults' key light")}, {Res.Volumes.Count} volume(s)");
    }

    private void Add(Node3D n)
    {
        Group.AddChild(n);
        n.Owner = Root;
    }

    private LightingFacet Facet(string name, string id, string[] prefixes, string[] names)
    {
        var f = new LightingFacet { Name = name, Target = Res, Prefixes = prefixes, Names = names, Edited = Changed };
        PartIds.SetString(f, PartIds.Part, id);
        Add(f);
        return f;
    }

    /// <summary>An edit of the lighting: the preview applies it again.</summary>
    public void Changed() => _rig.Refresh();

    /// <summary>The key light node as the lighting and the game's look resolve it; noted as shown.</summary>
    private void ShowKey()
    {
        if (!GodotObject.IsInstanceValid(Key))
            return;
        var r = RemasterLighting.Resolve(Res, Look);
        var up = Mathf.Abs(r.KeyDirection.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        Key.GlobalBasis = Basis.LookingAt(r.KeyDirection, up);
        // the node sits a little above the map's middle (where its gizmo is found)
        Key.GlobalPosition = new Vector3(0, 120, 0);
        Key.LightColor = r.KeyColour.LinearToSrgb();
        Key.LightEnergy = r.KeyEnergy;
        Key.ShadowEnabled = Res.KeyShadows;
        Key.LightAngularDistance = Res.KeyAngularSize;
        Key.Visible = r.KeyOn;
        _shown = (r.KeyDirection, Key.LightColor, Key.LightEnergy, r.KeyOn, Res.KeyEnergy > 1e-6f ? r.KeyEnergy / Res.KeyEnergy : 1f);
    }

    private double _sync;

    /// <summary>Every editor frame: the preview on while the view draws the map remastered, the volumes as the nodes have them.</summary>
    public void Tick(bool remastered, double delta)
    {
        _sync -= delta;
        if (remastered && _sync <= 0)
        {
            _sync = 0.25;
            SyncVolumes();
        }
        if (remastered && _envNode.GetParent() == null)
            Root.AddChild(_envNode);
        var cam = EditorInterface.Singleton.GetEditorViewport3D(0)?.GetCamera3D();
        _rig.Set(remastered, Look, 0, Root.Level, Root.Map, cam?.GlobalPosition);
        if (!remastered && _envNode.GetParent() != null)
            Root.RemoveChild(_envNode);
    }

    /// <summary>The volume nodes into the lighting (in their order).</summary>
    private void SyncVolumes()
    {
        var list = new Godot.Collections.Array<LightingVolume>();
        foreach (var n in Volumes.GetChildren())
            if (n is VolumeNode v)
                list.Add(v.ToVolume());
        if (list.Count != Res.Volumes.Count || list.Zip(Res.Volumes).Any(p => Say(p.First) != Say(p.Second)))
        {
            Res.Volumes = list;
            _rig.Refresh();
        }
    }

    private static string Say(LightingVolume v) => GD.VarToStr(v.Transform) + v.Name + GD.VarToStr(v.Size) + v.Blend + v.SetExposure + v.Exposure
        + v.SetAmbient + v.AmbientColour + v.AmbientEnergy + v.SetFog + v.FogColour + v.FogDensity + v.SetKeyEnergy + v.KeyEnergy
        + v.SetSaturation + v.Saturation + v.SetVolumetricDensity + v.VolumetricDensity;

    /// <summary>The key light node against what was shown: the lighting's key fields.</summary>
    private void TakeKey()
    {
        // deleted in the scene dock: no key light
        if (!GodotObject.IsInstanceValid(Key) || !Key.IsInsideTree())
        {
            Res.KeyMode = LightSource.Off;
            return;
        }
        var dir = (-Key.GlobalBasis.Z).Normalized();
        if (!Key.Visible)
            Res.KeyMode = LightSource.Off;
        else if (dir.AngleTo(_shown.Dir) > 1e-3f)
        {
            Res.KeyMode = LightSource.Custom;
            Res.KeyDirection = Snap(dir);
        }
        else if (Res.KeyMode == LightSource.Off)
            Res.KeyMode = LightSource.Game;
        if (!Key.LightColor.IsEqualApprox(_shown.Colour))
        {
            Res.KeyColourMode = LightSource.Custom;
            Res.KeyColour = Key.LightColor;
        }
        if (!Mathf.IsEqualApprox(Key.LightEnergy, _shown.Energy))
            Res.KeyEnergy = Mathf.Snapped(Key.LightEnergy / Mathf.Max(_shown.Level, 1e-6f), 1e-4f);
        Res.KeyShadows = Key.ShadowEnabled;
        Res.KeyAngularSize = Key.LightAngularDistance;
    }

    private static Vector3 Snap(Vector3 v) => new(Mathf.Snapped(v.X, 1e-6f), Mathf.Snapped(v.Y, 1e-6f), Mathf.Snapped(v.Z, 1e-6f));

    /// <summary>Whether <paramref name="m"/> says only the defaults (no file is needed).</summary>
    public static bool IsDefault(MapLighting m)
    {
        if (m.Volumes.Count > 0)
            return false;
        var d = LightingLayers.Defaults;
        foreach (var p in m.GetPropertyList())
        {
            var name = p["name"].AsString();
            if (name is "Level" or "Volumes" || !((PropertyUsageFlags)p["usage"].AsInt64()).HasFlag(PropertyUsageFlags.ScriptVariable))
                continue;
            if (GD.VarToStr(m.Get(name)) != GD.VarToStr(d.Get(name)))
                return false;
        }
        return true;
    }

    /// <summary>Writes the lighting (removes its file when it says only the defaults); false on failure.</summary>
    public bool Save()
    {
        TakeKey();
        SyncVolumes();
        var key = Root.Key;
        var path = PathOf(key);
        Res.Level = key;
        if (IsDefault(Res))
        {
            if (FileAccess.FileExists(path))
                DirAccess.RemoveAbsolute(GameSettings.Globalize(path));
            Changed(key, path);
            GD.Print($"ffx remaster: {key}: lighting as the defaults; no {path}");
            ShowKey();
            return true;
        }
        DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(path.GetBaseDir()));
        // stable IDs: the scripts 1 and 2, each volume v_<hash of its name> (unique)
        Res.GetScript().As<Script>().SetIdForPath(path, "1");
        var seen = new HashSet<string>();
        foreach (var v in Res.Volumes)
        {
            v.GetScript().As<Script>().SetIdForPath(path, "2");
            var id = $"v_{MapStore.Hash(v.Name)}";
            while (!seen.Add(id))
                id += "x";
            v.ResourceSceneUniqueId = id;
        }
        Res.TakeOverPath(path);
        if (ResourceSaver.Save(Res, path) != Error.Ok)
        {
            GD.PrintErr($"ffx remaster: cannot write {path}");
            return false;
        }
        Changed(key, path);
        GD.Print($"ffx remaster: {key}: wrote {path} (key {Res.KeyMode}, {Res.Volumes.Count} volume(s))");
        ShowKey();
        return true;
    }

    private static void Changed(string key, string path)
    {
        LightingLayers.Invalidate(key);
        if (path.StartsWith("res://", System.StringComparison.Ordinal))
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);
    }

    /// <summary>A new volume node (not yet in the scene) named uniquely among the volumes.</summary>
    public VolumeNode NewVolume(string name = "Volume")
    {
        var names = Volumes.GetChildren().Select(n => n.Name.ToString()).ToHashSet();
        var unique = name;
        for (int i = 2; names.Contains(unique); i++)
            unique = $"{name}{i}";
        var v = new VolumeNode { Name = unique };
        PartIds.SetString(v, PartIds.Part, MapLighting.VolumeId(Root.Key, unique));
        return v;
    }
}
#endif
