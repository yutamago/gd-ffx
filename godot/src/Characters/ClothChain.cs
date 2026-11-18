using Godot;

namespace GdFfx.Characters;

/// <summary>What moves a remade character's cloth part (<see cref="ClothChain"/>).</summary>
public enum ClothMode
{
    /// <summary>The game's cloth simulation (its capture: docs/architecture.md, "Cloth"): each bone points where the game's simulated vertices go.</summary>
    Game,
    /// <summary>Godot's own: a <c>SpringBoneSimulator3D</c> on the chain.</summary>
    Godot,
    /// <summary>Neither: the chain follows its parents (skinned only).</summary>
    Off,
}

/// <summary>
/// A cloth part of a remade character: a chain of bones, root first (hair, a hood, a coat's
/// tail), moved by the game's cloth or by Godot's spring bones (<see cref="Mode"/>).
/// </summary>
[GlobalClass]
[Tool]
public partial class ClothChain : Resource
{
    [Export] public string Name { get; set; } = "";

    /// <summary>The chain's bones, root first, each the parent of the next.</summary>
    [Export] public string[] Bones { get; set; } = [];

    [Export] public ClothMode Mode { get; set; } = ClothMode.Game;

    /// <summary>The game's cloth segment the chain follows (<see cref="ClothMode.Game"/>): its PS2 model and piece (docs/importer.md, "Cloth").</summary>
    [Export] public int Model { get; set; } = -1;
    [Export] public int Piece { get; set; } = -1;

    /// <summary>Per bone of <see cref="Bones"/>, the segment's vertex the bone sits on (its number in the segment; -1 none).</summary>
    [Export] public int[] Points { get; set; } = [];

    /// <summary>
    /// The game's element the cloth hangs from (the chain's nearest mapped bone's) and its rest
    /// rotation (character space), and per pair of points their direction at the bind pose: the
    /// chain turns by how far the game's simulated cloth hangs from where the element's turn alone
    /// would put it.
    /// </summary>
    [Export] public int Anchor { get; set; } = -1;
    [Export] public Quaternion AnchorRest { get; set; } = Quaternion.Identity;
    [Export] public Vector3[] RestDirections { get; set; } = [];

    /// <summary>
    /// Godot's spring bones (<see cref="ClothMode.Godot"/>): stiffness, drag, gravity and collision
    /// radius, in the game's units (the remade model's scaled by the rig's alignment: the automatic
    /// rig makes them from the model's metres).
    /// </summary>
    [Export] public float Stiffness { get; set; } = 0.6f;
    [Export] public float Drag { get; set; } = 0.4f;
    [Export] public float Gravity { get; set; } = 30f;
    [Export] public float Radius { get; set; } = 0.2f;

    public ClothChain Copy() => new()
    {
        Name = Name, Bones = (string[])Bones.Clone(), Mode = Mode, Model = Model, Piece = Piece, Points = (int[])Points.Clone(),
        Anchor = Anchor, AnchorRest = AnchorRest, RestDirections = (Vector3[])RestDirections.Clone(),
        Stiffness = Stiffness, Drag = Drag, Gravity = Gravity, Radius = Radius,
    };
}
