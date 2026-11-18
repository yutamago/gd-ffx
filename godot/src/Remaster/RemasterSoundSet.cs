using Godot;
using Godot.Collections;

namespace GdFfx.Remaster;

/// <summary>A layer of replaced sounds (<c>res://remaster/audio/audio.tres</c>; a pack's <c>res://mods/&lt;pack&gt;/remaster/audio/audio.tres</c>).</summary>
[Tool, GlobalClass]
public partial class RemasterSoundSet : Resource
{
    [Export] public Array<RemasterSound> Sounds { get; set; } = new();
}
