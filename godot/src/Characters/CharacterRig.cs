using Godot;

namespace GdFfx.Characters;

/// <summary>
/// How a remade character with a skeleton of its own is driven by the game (docs/remake.md,
/// "Retargeting"; docs/remaster-plan.md R6): its alignment to the game's character space, each
/// bone's game element (<see cref="RigBone"/>), its cloth parts (<see cref="ClothChain"/>), its
/// face table (<see cref="FaceMap"/>) and attachments (<see cref="AttachMap"/>). Kept beside the
/// remade scene (<c>res://remake/chr/&lt;id&gt;.rig.tres</c>), which names it on its root
/// (<see cref="RemadeCharacter"/>). Text, IDs and numbers only: the user's work, never the game's
/// content.
/// </summary>
[GlobalClass]
[Tool]
public partial class CharacterRig : Resource
{
    /// <summary>The character's model id (<c>c001</c>).</summary>
    [Export] public string Character { get; set; } = "";

    [Export] public Remaster.MapStatus Status { get; set; } = Remaster.MapStatus.InProgress;

    [Export(PropertyHint.MultilineText)] public string Notes { get; set; } = "";

    /// <summary>The remade model the scene shows (informational; the scene decides).</summary>
    [Export] public string Model { get; set; } = "";

    /// <summary>
    /// The remade model's space to the game's character space (y down, the head towards -y,
    /// facing -z): its scale and turn (the rest-pose alignment). It is the remade root's transform
    /// in the game and the editor.
    /// </summary>
    [Export] public Transform3D Align { get; set; } = Transform3D.Identity;

    [Export] public Godot.Collections.Array<RigBone> Bones { get; set; } = new();

    [Export] public Godot.Collections.Array<ClothChain> Cloth { get; set; } = new();

    [Export] public Godot.Collections.Array<FaceMap> Face { get; set; } = new();

    [Export] public Godot.Collections.Array<AttachMap> Attach { get; set; } = new();

    /// <summary>The mapping of <paramref name="bone"/> (null none).</summary>
    public RigBone? Find(string bone)
    {
        foreach (var b in Bones)
            if (b.Bone == bone)
                return b;
        return null;
    }

    /// <summary>A deep copy (the undo history keeps copies).</summary>
    public CharacterRig Copy()
    {
        var r = new CharacterRig { Character = Character, Status = Status, Notes = Notes, Model = Model, Align = Align };
        foreach (var b in Bones)
            r.Bones.Add(b.Copy());
        foreach (var c in Cloth)
            r.Cloth.Add(c.Copy());
        foreach (var f in Face)
            r.Face.Add(f.Copy());
        foreach (var a in Attach)
            r.Attach.Add(a.Copy());
        return r;
    }
}
