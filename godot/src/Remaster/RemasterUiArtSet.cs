using Godot;
using Godot.Collections;

namespace GdFfx.Remaster;

/// <summary>A layer of replaced UI art and fonts (<c>res://remaster/ui/ui.tres</c>; a pack's <c>res://mods/&lt;pack&gt;/remaster/ui/ui.tres</c>).</summary>
[Tool, GlobalClass]
public partial class RemasterUiArtSet : Resource
{
    [Export] public Array<RemasterUiArt> Art { get; set; } = new();
    [Export] public Array<RemasterUiFont> Fonts { get; set; } = new();
}
