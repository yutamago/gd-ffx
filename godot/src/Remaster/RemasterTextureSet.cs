using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// A layer's texture index (docs/remaster-editor.md, "Textures"): the project's
/// <c>res://remaster/textures/textures.tres</c>, a pack's
/// <c>res://mods/&lt;pack&gt;/remaster/textures/textures.tres</c>. One <see cref="RemasterTexture"/>
/// per texture ID; textures are shared across maps, so this is project-wide, not per map.
/// </summary>
[GlobalClass]
[Tool]
public partial class RemasterTextureSet : Resource
{
    [Export] public Godot.Collections.Array<RemasterTexture> Textures { get; set; } = new();
}
