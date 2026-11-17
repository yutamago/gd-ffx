using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// One layer's edits of one map (docs/remake.md, "Overrides"), Godot-native: a resource saved as
/// <c>res://remaster/maps/&lt;level&gt;/overrides.tres</c> (a pack's
/// <c>res://mods/&lt;pack&gt;/remaster/maps/&lt;level&gt;/overrides.tres</c>), keyed by the level
/// (<c>bsil00</c>). Its <see cref="Parts"/> edit the classic import (and a remade map's tagged
/// nodes) by stable ID; <see cref="Additions"/> (or <c>additions.tscn</c> beside it) holds added
/// nodes, each under the root with an optional <c>ffx_anchor</c> metadata (a part ID: it follows
/// that part; none: the map's origin), placed in render units relative to the anchor. The map's
/// hand-set status, notes and render path live here too.
/// </summary>
[GlobalClass]
public partial class MapOverrides : Resource
{
    /// <summary>The level key (<c>bsil00</c>); informational, the file's folder decides.</summary>
    [Export] public string Level { get; set; } = "";

    [Export] public MapStatus Status { get; set; } = MapStatus.Original;

    [Export] public RenderPath RenderPath { get; set; } = RenderPath.Classic;

    [Export(PropertyHint.MultilineText)] public string Notes { get; set; } = "";

    [Export] public Godot.Collections.Array<PartOverride> Parts { get; set; } = new();

    /// <summary>Added nodes (null: <c>additions.tscn</c> beside this file when there is one).</summary>
    [Export] public PackedScene? Additions { get; set; }
}
