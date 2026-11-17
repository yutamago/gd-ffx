#if TOOLS
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// One of the game's lighting objects in a map's edit scene (docs/remaster-editor.md,
/// "Lighting"): the ambient, the fog or the environment, a node tagged with its stable ID
/// (<c>x:level/&lt;key&gt;#ambient</c> and the others) whose inspector shows that part of the map's
/// <see cref="MapLighting"/> (the properties whose names start with <see cref="Prefixes"/>) as its
/// own: Godot's inspector edits them, each change one step of the scene's undo history, the
/// revert arrow goes back to the default (the game's look). The edit scene's
/// <see cref="LightingEdit"/> holds the resource and writes it on save.
/// </summary>
[Tool]
public partial class LightingFacet : Node3D
{
    /// <summary>The lighting being edited (the edit scene's).</summary>
    public MapLighting? Target { get; set; }

    /// <summary>The property name prefixes shown (and the extra names in <see cref="Names"/>).</summary>
    public string[] Prefixes { get; set; } = [];

    /// <summary>Single properties shown besides the prefixed ones.</summary>
    public string[] Names { get; set; } = [];

    /// <summary>Called after an edit (the preview applies it).</summary>
    public System.Action? Edited { get; set; }

    private bool Shows(string name) =>
        Names.Contains(name) || Prefixes.Any(p => name.StartsWith(p, System.StringComparison.Ordinal));

    public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetPropertyList()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        if (Target == null)
            return list;
        list.Add(new Godot.Collections.Dictionary
        {
            ["name"] = "Game lighting",
            ["type"] = (int)Variant.Type.Nil,
            ["usage"] = (int)PropertyUsageFlags.Category,
        });
        foreach (var p in Target.GetPropertyList())
        {
            var name = p["name"].AsString();
            var usage = (PropertyUsageFlags)p["usage"].AsInt64();
            if (!usage.HasFlag(PropertyUsageFlags.ScriptVariable) || !Shows(name))
                continue;
            list.Add(new Godot.Collections.Dictionary
            {
                ["name"] = name,
                ["type"] = p["type"],
                ["hint"] = p["hint"],
                ["hint_string"] = p["hint_string"],
                // shown and edited, never saved with the node (the edit scene keeps nothing of the map)
                ["usage"] = (int)PropertyUsageFlags.Editor,
            });
        }
        return list;
    }

    public override Variant _Get(StringName property)
    {
        var name = property.ToString();
        return Target != null && Shows(name) ? Target.Get(name) : default;
    }

    public override bool _Set(StringName property, Variant value)
    {
        var name = property.ToString();
        if (Target == null || !Shows(name))
            return false;
        Target.Set(name, value);
        Edited?.Invoke();
        return true;
    }

    public override bool _PropertyCanRevert(StringName property) => Target != null && Shows(property.ToString());

    public override Variant _PropertyGetRevert(StringName property) => LightingLayers.Defaults.Get(property.ToString());
}
#endif
