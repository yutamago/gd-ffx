using Godot;

namespace GdFfx.Characters;

/// <summary>
/// The root of a remade character scene whose skeleton is its own (any names, any proportions):
/// <see cref="Rig"/> says how the game's skeleton drives it (docs/remake.md, "Retargeting").
/// The game (<c>StateView3D</c>) and the editor's character scene drive it through a
/// <see cref="Retargeter"/>; the root's transform is the rig's alignment. A remade scene
/// without this root keeps G6's path (bones named <c>SkeletonNode_i</c>).
/// </summary>
[GlobalClass]
[Tool]
public partial class RemadeCharacter : Node3D
{
    [Export] public CharacterRig? Rig { get; set; }

    /// <summary>The driver made for this instance (kept with it: a model the game pools and shows again keeps its spring bones).</summary>
    public Retargeter? Driver { get; set; }

    /// <summary>The driver, made on first use (null without a rig or a skeleton).</summary>
    public Retargeter? Drive(bool springs = true)
    {
        if (Driver != null || Rig == null || Skeleton is not { } skel)
            return Driver;
        Driver = new Retargeter(this, Rig, skel, springs);
        return Driver;
    }

    /// <summary>The scene's skeleton: the first <see cref="Skeleton3D"/> under the root (null none).</summary>
    public Skeleton3D? Skeleton => FindSkeleton(this);

    public static Skeleton3D? FindSkeleton(Node root)
    {
        if (root is Skeleton3D s)
            return s;
        for (int i = 0, n = root.GetChildCount(); i < n; i++)
            if (FindSkeleton(root.GetChild(i)) is { } found)
                return found;
        return null;
    }

    /// <summary>The remade character under <paramref name="model"/> (the node itself, or a child: the content wrapper's), null none.</summary>
    public static RemadeCharacter? Of(Node model)
    {
        if (model is RemadeCharacter rc)
            return rc;
        for (int i = 0, n = model.GetChildCount(); i < n; i++)
            if (model.GetChild(i) is RemadeCharacter c)
                return c;
        return null;
    }
}
