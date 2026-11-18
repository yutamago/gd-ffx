using Godot;
using Godot.Collections;

namespace GdFfx.Remaster;

/// <summary>A layer of replaced movies (<c>res://remaster/movies/movies.tres</c>; a pack's <c>res://mods/&lt;pack&gt;/remaster/movies/movies.tres</c>).</summary>
[Tool, GlobalClass]
public partial class RemasterMovieSet : Resource
{
    [Export] public Array<RemasterMovie> Movies { get; set; } = new();
}
