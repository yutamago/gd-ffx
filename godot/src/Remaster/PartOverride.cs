using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// One part's edits in a <see cref="MapOverrides"/> layer, keyed by its stable ID
/// (docs/importer.md, "Stable part IDs"; <c>#...</c> is relative to the map's classic 3D model;
/// an added node's <c>x:level/&lt;key&gt;/+&lt;layer&gt;/&lt;name&gt;</c>). Every field has an
/// <c>Unset</c>: layers merge per part and per field, a later layer's set field over an earlier
/// one's (docs/remake.md, "Overrides").
/// </summary>
[GlobalClass]
[Tool] // the remaster editor reads and writes it (docs/remaster-editor.md): a real instance there, not a placeholder
public partial class PartOverride : Resource
{
    /// <summary>The part, instance (<c>.../*</c>), node or added node.</summary>
    [Export] public string Id { get; set; } = "";

    [Export] public RemoveOp Remove { get; set; } = RemoveOp.Unset;

    [Export] public VisibilityOp Visibility { get; set; } = VisibilityOp.Unset;

    [Export] public TransformOp TransformMode { get; set; } = TransformOp.Unset;

    /// <summary>The offset (<see cref="TransformOp.Offset"/>) or world transform (<see cref="TransformOp.Absolute"/>).</summary>
    [Export] public Transform3D Transform { get; set; } = Transform3D.Identity;

    [Export] public ReplaceOp Replace { get; set; } = ReplaceOp.Unset;

    /// <summary>
    /// The replacement (<see cref="ReplaceOp.Scene"/>): placed at the part's origin and axes (it
    /// follows the game's placement of the part), in render units; the part itself is no longer drawn.
    /// </summary>
    [Export] public PackedScene? Scene { get; set; }

    /// <summary>Free text (why, what to do next).</summary>
    [Export(PropertyHint.MultilineText)] public string Note { get; set; } = "";

    /// <summary>A copy (the scene shared; the ID in its file kept, so a save keeps it).</summary>
    public PartOverride Copy() => new()
    {
        Id = Id, Remove = Remove, Visibility = Visibility, TransformMode = TransformMode, Transform = Transform,
        Replace = Replace, Scene = Scene, Note = Note, ResourceSceneUniqueId = ResourceSceneUniqueId,
    };
}
