using Godot;

namespace GdFfx.Characters;

/// <summary>
/// An attachment of a remade character (<see cref="CharacterRig.Attach"/>): what the game binds to
/// its element <see cref="Element"/> (a weapon: <c>Actor.parent</c>, <c>Actor.parent_bone</c>)
/// follows the remade bone <see cref="Bone"/> instead, so it stays in the remade hand.
/// </summary>
[GlobalClass]
[Tool]
public partial class AttachMap : Resource
{
    /// <summary>The game's element (attachment points 4 and 5 are the hands' weapon points on the party).</summary>
    [Export] public int Element { get; set; } = -1;

    /// <summary>The game's attachment point id it came from (-1: set by hand).</summary>
    [Export] public int Point { get; set; } = -1;

    [Export] public string Bone { get; set; } = "";

    /// <summary>The nearest mapped element at or above <see cref="Element"/> (the hand: informational; the automatic rig attaches to its bone without a socket).</summary>
    [Export] public int Via { get; set; } = -1;

    /// <summary>
    /// The element's rest frame in the bone's (unscaled) aligned rest frame
    /// (<see cref="RigBuilder.ComputeRest"/>): the bone's frame times this is where the element's
    /// frame goes on the remade character. On a mapped bone (the hand) the whole frame: what the game
    /// binds keeps its place relative to the hand; on a socket bone the turn only: it sits at the
    /// socket.
    /// </summary>
    [Export] public Transform3D Rest { get; set; } = Transform3D.Identity;

    /// <summary>The user's offset on top (in the element's frame).</summary>
    [Export] public Transform3D Offset { get; set; } = Transform3D.Identity;

    public AttachMap Copy() => new() { Element = Element, Point = Point, Bone = Bone, Via = Via, Rest = Rest, Offset = Offset };
}
