#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The <b>In map</b> preview (docs/remaster-materials.md, "The preview"; phase 5): the map that uses
/// the texture (the open map when it draws it, else the first map the where-used index names,
/// loaded from the classic import on demand) in the big preview, the camera at the piece that
/// draws the texture, lit by that map's remastered lighting (R3's, "The map's light"), its surfaces
/// drawn by the game's lit shader; the surfaces that draw the texture with the live, unsaved
/// material (the passes' targets: a change shows at once), the compare's side with the classic
/// look (or the raw texture lit). Orbit, zoom, pan, the wipe and colour picks work as on the
/// objects.
/// </summary>
public partial class MaterialsTab
{
    /// <summary>The texture the In map copy is of (null: none asked).</summary>
    private string? _inMapFor;

    /// <summary>A level whose classic import is waited for (the copy made when it is in).</summary>
    private string? _inMapLevel;

    private int _shapeBefore = MaterialPreview.Sphere;

    /// <summary>The live lit materials (by classic material), filled again on each change.</summary>
    private readonly Dictionary<Material, ShaderMaterial> _inMapLit = new(), _inMapRaw = new();

    /// <summary>What the In map view shows or why not (the checks).</summary>
    public string InMapNote { get; private set; } = "";

    /// <summary>Whether the preview shows the In map view.</summary>
    public bool InMapShown => _preview.Shape == MaterialPreview.InMap;

    /// <summary>The In map view on (the map that uses the texture) or off (the object before).</summary>
    public string ToggleInMap()
    {
        if (_shape.Selected != MaterialPreview.InMap)
        {
            _shapeBefore = _shape.Selected;
            SetShapeForCheck(MaterialPreview.InMap);
        }
        else
            SetShapeForCheck(_shapeBefore);
        return InMapShown ? $"in map: {InMapNote}" : _shape.Selected == MaterialPreview.InMap ? InMapNote : "in map off";
    }

    /// <summary>The In map view asked for: the copy made (or its map loaded), the map's light on.</summary>
    private void EnterInMap()
    {
        if (Current == null)
            return;
        if (_inMapFor != Current || _preview.MapScene == null && _inMapLevel == null)
            FindInMap(Current);
        if (_light.Selected != (int)PreviewLight.Map)
        {
            _light.Selected = (int)PreviewLight.Map;
            UpdateLight();
        }
    }

    /// <summary>The copy of a map that uses <paramref name="id"/>: the open map's at once, else its first map's classic import asked for.</summary>
    private void FindInMap(string id)
    {
        _inMapFor = id;
        _inMapLevel = null;
        _inMapLit.Clear();
        _inMapRaw.Clear();
        if (_plugin.Current is { Built: true, Map: { } map } root && InMapScene.Build(map, id, root.Level) is { } scene)
        {
            UseInMap(scene, "the open map");
            return;
        }
        _preview.SetInMap(null);
        var use = _plugin.Uses.Uses(id).FirstOrDefault();
        _inMapLevel = use != null ? _plugin.Session.Catalogue?.Find(use.Level)?.Level : null;
        InMapNote = _inMapLevel != null ? $"loading {PartIds.LevelKey(_inMapLevel)}..." : "no map known for this texture (the where-used index has none)";
        Say(InMapNote);
    }

    /// <summary>Loads the map the In map view waits for, once the classic import has it.</summary>
    private void PollInMap()
    {
        if (_inMapLevel == null || _inMapFor != Current || _plugin.Session.Classic is not { } classic)
            return;
        if (classic.Path("map", _inMapLevel) is not { } path)
        {
            if (classic.Failed("map", _inMapLevel))
            {
                InMapNote = $"{PartIds.LevelKey(_inMapLevel)}: the classic import failed";
                _inMapLevel = null;
            }
            return;
        }
        var level = _inMapLevel;
        _inMapLevel = null;
        if (ClassicLibrary.Load(path, $"PS3Data{level}tex/") is not { } map)
            return;
        var scene = InMapScene.Build(map, Current!, level);
        // the copy holds the meshes and materials (resources): the loaded nodes go
        map.Free();
        if (scene != null)
            UseInMap(scene, PartIds.LevelKey(level));
        else
            InMapNote = $"{PartIds.LevelKey(level)} draws no surface with this texture";
    }

    private void UseInMap(InMapScene scene, string from)
    {
        _preview.SetInMap(scene);
        InMapNote = $"{PartIds.LevelKey(scene.Level)} ({from}): {scene.Live.Count} surface(s) with the texture, {scene.Meshes} mesh(es)";
        GD.Print($"ffx remaster: materials: in map: {InMapNote}");
        if (_shape.Selected == MaterialPreview.InMap)
            _preview.SetShape(MaterialPreview.InMap, null, null);
        UpdateLight();
        UpdateMaterials();
    }

    /// <summary>The lit material of <paramref name="classic"/> with <paramref name="pbr"/> (kept per classic material and filled again), or the classic one when it is not a map material.</summary>
    private static Material? LitFor(Material? classic, BaseMaterial3D pbr, Dictionary<Material, ShaderMaterial> made)
    {
        if (classic == null)
            return null;
        made.TryGetValue(classic, out var reuse);
        if (RemasterLook.LitWith(classic, pbr, reuse) is not ShaderMaterial m)
            return classic;
        made[classic] = m;
        return m;
    }

    /// <summary>The In map view's materials for the preview: the live one on the material's side; the classic look (or the raw texture lit) on the compare's.</summary>
    private void SetInMapMaterials(bool classicCompare)
    {
        _preview.InMapRight = c => LitFor(c, _mat, _inMapLit);
        _preview.InMapLeft = classicCompare ? c => c : c => LitFor(c, _raw, _inMapRaw);
    }
}
#endif
