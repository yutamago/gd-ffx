using Godot;

namespace GdFfx.Characters;

/// <summary>Where a retargeted bone's position comes from (docs/remake.md, "Retargeting").</summary>
public enum BonePositions
{
    /// <summary>The remade skeleton's own: the bone keeps its rest offset from its parent (its proportions), turned as the game turns it.</summary>
    Rotation,
    /// <summary>The game's joint (scaled by the rig's alignment): the hips carry the walk this way; on every bone the remade joints sit on the game's.</summary>
    Game,
}

/// <summary>
/// One bone of a remade character's skeleton and the game's element that drives it
/// (<see cref="CharacterRig"/>): its global rotation follows the element's global rotation
/// relative to their rest poses (<see cref="Rest"/>), its position as <see cref="Positions"/>
/// says, then its own <see cref="Offset"/>.
/// </summary>
[GlobalClass]
[Tool]
public partial class RigBone : Resource
{
    /// <summary>The remade skeleton's bone name.</summary>
    [Export] public string Bone { get; set; } = "";

    /// <summary>The game's element (<c>Actor.bones[i]</c>, the classic model's <c>SkeletonNode_i</c>); -1 none (the bone follows its parent).</summary>
    [Export] public int Element { get; set; } = -1;

    /// <summary>The body part the automatic mapping found (<c>hips</c>, <c>upperarm.L</c>...; empty: set by hand or none).</summary>
    [Export] public string Label { get; set; } = "";

    /// <summary>Whether the mapping was set by hand (the automatic mapping leaves it alone).</summary>
    [Export] public bool ByHand { get; set; }

    [Export] public BonePositions Positions { get; set; } = BonePositions.Rotation;

    /// <summary>
    /// The rest offset <c>O</c>: the element's rest rotation (in character space) to the bone's
    /// aligned rest rotation (in character space), <c>R(bone) = R(element) O</c>. Computed by
    /// <see cref="RigBuilder.ComputeRest"/> from the classic skeleton's rest, the bone's rest
    /// turned so it points where the element's child points (the rest-pose alignment).
    /// </summary>
    [Export] public Quaternion Rest { get; set; } = Quaternion.Identity;

    /// <summary>The mapped bone its rest was aimed at (the rest-pose alignment; the error measure's direction), empty none.</summary>
    [Export] public string Aim { get; set; } = "";

    /// <summary>The user's offset on top, in the bone's own axes (turn and move).</summary>
    [Export] public Transform3D Offset { get; set; } = Transform3D.Identity;

    public RigBone Copy() => new()
    {
        Bone = Bone, Element = Element, Label = Label, ByHand = ByHand, Positions = Positions, Rest = Rest, Aim = Aim, Offset = Offset,
    };
}
