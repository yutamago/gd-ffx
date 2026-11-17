using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// A box of a map with lighting of its own (R3): where the camera is inside it, its set values
/// replace the map's, blended over <see cref="Blend"/> render units inside its faces. Each value
/// has a switch (<c>Set...</c>): a volume says only what it changes. Where volumes overlap, the
/// later one in the list wins.
/// </summary>
[GlobalClass]
[Tool]
public partial class LightingVolume : Resource
{
    /// <summary>Its name (unique on the map: its ID is <c>x:level/&lt;level&gt;#volume/&lt;name&gt;</c>).</summary>
    [Export] public string Name { get; set; } = "Volume";
    /// <summary>The box's centre and turn (render space; no scale: <see cref="Size"/> is the size).</summary>
    [Export] public Transform3D Transform { get; set; } = Transform3D.Identity;
    [Export] public Vector3 Size { get; set; } = new(100, 50, 100);
    /// <summary>The edge where it fades in (render units inside the faces; 0: a hard edge).</summary>
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

    /// <summary>How far inside it <paramref name="p"/> is: 0 outside, 1 deeper than <see cref="Blend"/> from every face.</summary>
    public float Weight(Vector3 p)
    {
        var local = Transform.AffineInverse() * p;
        var half = Size.Abs() * 0.5f;
        float inside = Mathf.Min(half.X - Mathf.Abs(local.X), Mathf.Min(half.Y - Mathf.Abs(local.Y), half.Z - Mathf.Abs(local.Z)));
        if (inside <= 0)
            return 0;
        return Blend <= 0.001f ? 1 : Mathf.SmoothStep(0, 1, Mathf.Min(1, inside / Blend));
    }
}
