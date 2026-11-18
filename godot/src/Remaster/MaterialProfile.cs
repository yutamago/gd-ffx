using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// A material profile (docs/remaster-materials.md, "Profiles"; R2b phase 3): every step's
/// settings and the material's, under a name, to make many textures' materials the same way
/// (rock, sand, wood...). A <see cref="PbrRecipe"/> with a title and a note; the steps' imported
/// files and the preview's working size are not a profile's (a texture's own, or the view's).
/// Saved as <c>res://remaster/profiles/&lt;name&gt;.tres</c> (<see cref="MaterialProfiles"/>:
/// text, only the settings that differ from the defaults, in a stable order); original work, so
/// in git and in packs, the later pack winning per name.
/// </summary>
[GlobalClass]
[Tool]
public partial class MaterialProfile : PbrRecipe
{
    /// <summary>The name shown (the file's name is made from it: <see cref="MaterialProfiles.FileName"/>).</summary>
    [Export] public string Title { get; set; } = "";

    /// <summary>What the profile is for, and on which textures it was tuned.</summary>
    [Export(PropertyHint.MultilineText)] public string Note { get; set; } = "";
}
