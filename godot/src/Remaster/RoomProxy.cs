using Godot;

namespace GdFfx.Remaster;

/// <summary>What a <see cref="RoomProxy"/> stands for.</summary>
public enum RoomProxyKind
{
    /// <summary>A 2D layer: a plane at the layer's depth (or the proxy's own mesh) with the layer's picture projected from the scene camera.</summary>
    Layer,
    /// <summary>The floor: the walk mesh the map starts with, the farthest layer of its scene projected on it.</summary>
    Floor,
}

/// <summary>
/// A stand-in in a rebuilt 2D room (<see cref="RebuiltRoom"/>; docs/remaster-plan.md, "R7b in
/// detail"): a layer of the room's painted background, or its floor, by reference only. The
/// scene file holds the node, its kind, its 2D scene and layer, its transform and (when the user
/// gave it one) its own mesh; the layer's picture, the plane at the layer's depth and the walk
/// mesh are made from the player's archive when the room is built, and kept out of the file when
/// the editor saves it (<see cref="RebuiltRoom"/>'s pre-save). Its material projects the layer's
/// picture from the 2D scene's fixed camera, so whatever shape the proxy has, from the game's
/// camera it shows the classic layer where it covers it.
/// </summary>
[Tool, GlobalClass]
public partial class RoomProxy : MeshInstance3D
{
    [Export] public RoomProxyKind Kind { get; set; } = RoomProxyKind.Layer;

    /// <summary>The 2D scene (<c>scene&lt;s&gt;</c>; a map starts on 0, scripts change it).</summary>
    [Export] public int Scene { get; set; }

    /// <summary>The layer (<c>layer&lt;l&gt;</c>); for the floor -1: the scene's farthest layer.</summary>
    [Export] public int Layer { get; set; }

    /// <summary>Drawn in the game (off: a guide in the editor only).</summary>
    [Export] public bool Drawn { get; set; } = true;

    /// <summary>The mesh was made by the room (a plane, the walk mesh), not the user's: not saved.</summary>
    public bool GeneratedMesh { get; set; }

    /// <summary>The layer this proxy shows (null: none found when the room was built).</summary>
    public RebuiltRoom.LayerSource? Source { get; set; }
}
