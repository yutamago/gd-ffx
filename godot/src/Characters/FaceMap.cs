using Godot;

namespace GdFfx.Characters;

/// <summary>What a face mapping reads of the game's element (its pose relative to <see cref="FaceMap.Relative"/>).</summary>
public enum FaceSource
{
    /// <summary>The local rotation's angle about x, y or z in degrees (Euler order z y x: the game's T Rz Ry Rx).</summary>
    RotationX, RotationY, RotationZ,
    /// <summary>The local position's x, y or z (character units).</summary>
    PositionX, PositionY, PositionZ,
}

/// <summary>What a face mapping drives on the remade face.</summary>
public enum FaceTarget
{
    /// <summary>A blend shape's weight (every mesh of the model that has it).</summary>
    BlendShape,
    /// <summary>A bone turned about its own x, y or z by the output in degrees, on top of its retargeted pose.</summary>
    BoneRotationX, BoneRotationY, BoneRotationZ,
}

/// <summary>
/// One row of a remade character's face table (<see cref="CharacterRig.Face"/>): a value of the
/// game's face element per snapshot (the jaw the game opens as it talks: attachment point 13), from
/// <see cref="InMin"/>..<see cref="InMax"/> to <see cref="OutMin"/>..<see cref="OutMax"/> (clamped),
/// onto a blend shape or a bone.
/// </summary>
[GlobalClass]
[Tool]
public partial class FaceMap : Resource
{
    [Export] public string Name { get; set; } = "";

    /// <summary>The game's element read; its pose is taken relative to <see cref="Relative"/> (its parent element; -1: the character).</summary>
    [Export] public int Element { get; set; } = -1;
    [Export] public int Relative { get; set; } = -1;

    [Export] public FaceSource Source { get; set; } = FaceSource.RotationX;
    [Export] public float InMin { get; set; }
    [Export] public float InMax { get; set; } = 10f;

    [Export] public FaceTarget Target { get; set; } = FaceTarget.BlendShape;

    /// <summary>The blend shape (<see cref="FaceTarget.BlendShape"/>) or bone name.</summary>
    [Export] public string TargetName { get; set; } = "";
    [Export] public float OutMin { get; set; }
    [Export] public float OutMax { get; set; } = 1f;

    public FaceMap Copy() => new()
    {
        Name = Name, Element = Element, Relative = Relative, Source = Source, InMin = InMin, InMax = InMax,
        Target = Target, TargetName = TargetName, OutMin = OutMin, OutMax = OutMax,
    };

    /// <summary>The output for input <paramref name="v"/>.</summary>
    public float Map(float v)
    {
        float span = InMax - InMin;
        float t = Mathf.Abs(span) < 1e-9f ? 0f : Mathf.Clamp((v - InMin) / span, 0f, 1f);
        return Mathf.Lerp(OutMin, OutMax, t);
    }
}
