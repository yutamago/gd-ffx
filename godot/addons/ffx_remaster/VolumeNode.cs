#if TOOLS
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// A lighting volume in a map's edit scene (docs/remaster-editor.md, "Volumes"): a box (its
/// centre and turn the node's, its size <see cref="Size"/> times the node's scale) with lighting
/// of its own, tagged <c>x:level/&lt;key&gt;#volume/&lt;name&gt;</c>, drawn as a wire box; moved, turned
/// and scaled with Godot's gizmos, its values in the inspector. Saved as a
/// <see cref="LightingVolume"/> of the map's <c>lighting.tres</c>.
/// </summary>
[Tool]
public partial class VolumeNode : Node3D
{
    [Export] public Vector3 Size { get; set; } = new(100, 50, 100);
    [Export] public float Blend { get; set; } = 20f;
    [Export] public bool SetExposure { get; set; }
    [Export(PropertyHint.Range, "0.05,16,0.01")] public float Exposure { get; set; } = 1f;
    [Export] public bool SetAmbient { get; set; }
    [Export] public Color AmbientColour { get; set; } = new(0.8f, 0.85f, 0.95f);
    [Export(PropertyHint.Range, "0,16,0.01")] public float AmbientEnergy { get; set; } = 0.75f;
    [Export] public bool SetFog { get; set; }
    [Export] public Color FogColour { get; set; } = new(0.6f, 0.7f, 0.85f);
    [Export(PropertyHint.Range, "0,1,0.01")] public float FogDensity { get; set; } = 0.15f;
    [Export] public bool SetKeyEnergy { get; set; }
    [Export(PropertyHint.Range, "0,16,0.01")] public float KeyEnergy { get; set; } = 1f;
    [Export] public bool SetSaturation { get; set; }
    [Export(PropertyHint.Range, "0,2,0.01")] public float Saturation { get; set; } = 1f;
    [Export] public bool SetVolumetricDensity { get; set; }
    [Export(PropertyHint.Range, "0,0.2,0.0005")] public float VolumetricDensity { get; set; } = 0.01f;

    private MeshInstance3D? _box;
    private Vector3 _drawn;

    public override void _Ready()
    {
        if (!Engine.IsEditorHint())
            return;
        _box = new MeshInstance3D
        {
            Name = "Box",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1f, 0.75f, 0.2f, 0.12f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                NoDepthTest = false,
            },
        };
        _box.SetMeta(MapEditRoot.EditorNode, true);
        AddChild(_box);
        Redraw();
    }

    public override void _Process(double delta)
    {
        if (_box != null && _drawn != Size)
            Redraw();
    }

    private void Redraw()
    {
        _drawn = Size;
        if (_box != null)
            _box.Mesh = new BoxMesh { Size = Size.Abs() };
    }

    /// <summary>The volume as the file keeps it: the node's place (scale taken into the size), its values.</summary>
    public LightingVolume ToVolume()
    {
        var g = GlobalTransform;
        var scale = g.Basis.Scale;
        return new LightingVolume
        {
            Name = Name,
            Transform = new Transform3D(g.Basis.Orthonormalized(), g.Origin),
            Size = Size * scale,
            Blend = Blend,
            SetExposure = SetExposure, Exposure = Exposure,
            SetAmbient = SetAmbient, AmbientColour = AmbientColour, AmbientEnergy = AmbientEnergy,
            SetFog = SetFog, FogColour = FogColour, FogDensity = FogDensity,
            SetKeyEnergy = SetKeyEnergy, KeyEnergy = KeyEnergy,
            SetSaturation = SetSaturation, Saturation = Saturation,
            SetVolumetricDensity = SetVolumetricDensity, VolumetricDensity = VolumetricDensity,
        };
    }

    /// <summary>A node for <paramref name="v"/> (placed in render space by the caller).</summary>
    public static VolumeNode From(LightingVolume v) => new()
    {
        Name = v.Name.Length > 0 ? v.Name : "Volume",
        Size = v.Size,
        Blend = v.Blend,
        SetExposure = v.SetExposure, Exposure = v.Exposure,
        SetAmbient = v.SetAmbient, AmbientColour = v.AmbientColour, AmbientEnergy = v.AmbientEnergy,
        SetFog = v.SetFog, FogColour = v.FogColour, FogDensity = v.FogDensity,
        SetKeyEnergy = v.SetKeyEnergy, KeyEnergy = v.KeyEnergy,
        SetSaturation = v.SetSaturation, Saturation = v.Saturation,
        SetVolumetricDensity = v.SetVolumetricDensity, VolumetricDensity = v.VolumetricDensity,
    };
}
#endif
