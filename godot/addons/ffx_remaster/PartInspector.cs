#if TOOLS
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The inspector's "FFX part" section on a part (or added node) of an open map: its ID, what it
/// is and where it comes from (the model, the textures), its edits as the override file will
/// say them (move offset, hidden, removed, replaced, note), and Revert to classic, Replace with
/// a scene, Remove. The transform and visibility above it are Godot's own: changing them is the
/// move and the hiding.
/// </summary>
[Tool]
public partial class PartInspector : EditorInspectorPlugin
{
    private readonly RemasterPlugin _plugin;

    public PartInspector(RemasterPlugin plugin) => _plugin = plugin;

    public PartInspector() : this(null!) { }

    public override bool _CanHandle(GodotObject @object) =>
        @object is Node3D n && _plugin?.Current is { Built: true } root
        && (root.PartOf(n) != null || root.Added().Any(a => a.Node == n));

    public override void _ParseBegin(GodotObject @object)
    {
        if (@object is Node3D n && _plugin.Current is { Built: true } root)
            AddCustomControl(new PartPanel(_plugin, root, n));
    }
}

/// <summary>The section's controls (refreshed a few times a second: a gizmo move shows at once).</summary>
[Tool]
public partial class PartPanel : VBoxContainer
{
    private readonly RemasterPlugin _plugin;
    private readonly MapEditRoot _root;
    private readonly Node3D _node;
    private readonly PartInfo? _part;
    private Label _state = null!;
    private LineEdit _note = null!;
    private EditorFileDialog? _pick;
    private double _since = 1;

    public PartPanel(RemasterPlugin plugin, MapEditRoot root, Node3D node)
    {
        _plugin = plugin;
        _root = root;
        _node = node;
        _part = root.PartOf(node);
        Build();
    }

    public PartPanel() : this(null!, null!, null!) { }

    private void Build()
    {
        if (_root == null)
            return;
        AddChild(new Label { Text = "FFX part", ThemeTypeVariation = "HeaderSmall" });
        var id = new LineEdit
        {
            Text = _part?.Id ?? (_root.Added().FirstOrDefault(a => a.Node == _node) is { Node: not null } a
                ? PartIds.AddedId(_root.Key, "base", a.Node.Name) : ""),
            Editable = false,
            TooltipText = "The stable part ID (docs/importer.md, \"Stable part IDs\")",
        };
        AddChild(id);
        AddChild(new Label { Text = Describe(), AutowrapMode = TextServer.AutowrapMode.WordSmart });
        // its textures, each to the Materials tab
        foreach (var t in TexturesOf(_node))
        {
            var trow = new HBoxContainer();
            AddChild(trow);
            trow.AddChild(new Label { Text = RemasterTextures.ShortName(t), SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = t, MouseFilter = MouseFilterEnum.Pass });
            var mat = new Button { Text = "Materialize", TooltipText = "Add this texture to the Materials tab's queue and open the tab" };
            var texId = t;
            mat.Pressed += () => _plugin.Materialize([texId]);
            trow.AddChild(mat);
        }
        _state = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_state);
        if (_part == null)
            return;
        var row = new HBoxContainer();
        AddChild(row);
        row.AddChild(new Label { Text = "Note" });
        _note = new LineEdit { Text = _part.Note, SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "why, what next" };
        _note.TextSubmitted += t => _plugin.SetNote(_root, _part, t);
        _note.FocusExited += () => _plugin.SetNote(_root, _part, _note.Text);
        row.AddChild(_note);
        row = new HBoxContainer();
        AddChild(row);
        var revert = new Button { Text = "Revert to classic", TooltipText = "Placed by the game, shown, not replaced, not removed" };
        revert.Pressed += () => _plugin.RevertPart(_root, _part);
        row.AddChild(revert);
        var replace = new Button { Text = "Replace...", TooltipText = "Draw a scene instead of this part (in its frame, render units)" };
        replace.Pressed += PickReplacement;
        row.AddChild(replace);
        var drop = new Button { Text = "Unreplace" };
        drop.Pressed += () => _plugin.ReplacePart(_root, _part, "");
        row.AddChild(drop);
        if (_part.Kind == PartKind.Segment && _part.Parent is { } instance)
        {
            var up = new Button { Text = "Instance", TooltipText = $"Select its instance ({instance.Short(_root.Model)}): edits there move all its segments" };
            up.Pressed += () => RemasterPlugin.Select(instance.Node);
            row.AddChild(up);
        }
        var remove = new Button { Text = "Remove", TooltipText = "Take the part out of the map (as Delete in the scene dock)" };
        remove.Pressed += () => _plugin.RemovePart(_root, _part);
        row.AddChild(remove);
    }

    private string Describe()
    {
        if (_part == null)
        {
            var anchor = _root.Added().FirstOrDefault(a => a.Node == _node).Anchor;
            return "Added node (saved in additions.tscn), anchored to " + (anchor != null ? anchor.Short(_root.Model) : "the map's root")
                + ": it follows that part in the game; move it under another part to anchor it there.";
        }
        var kind = _part.Kind switch
        {
            PartKind.Model => "the map's model (the import's root)",
            PartKind.Instance => $"a mesh instance ({_part.Surfaces} segment(s), each a child node)",
            PartKind.Segment => $"segment {_part.Id[(_part.Id.LastIndexOf('/') + 1)..]} of {_part.Parent?.Short(_root.Model)}",
            _ => "a node of the model (PNode)",
        };
        var textures = TexturesOf(_node);
        var cache = GameSettings.Arg("classic") ?? ProjectSettings.GetSetting("ffx/classic/cache_dir", "user://classic").AsString();
        return $"{kind}\nsource: {_root.Model} (the player's archive; imported into {cache})"
            + (textures.Length > 0 ? $"\ntextures: {string.Join(", ", textures.Select(t => t[(t.LastIndexOf('/') + 1)..]))}" : "");
    }

    private static string[] TexturesOf(Node n)
    {
        if (n is MeshInstance3D { Mesh: { } m })
            return Enumerable.Range(0, m.GetSurfaceCount())
                .SelectMany(s => m.SurfaceGetMaterial(s) is { } mat ? PartIds.GetStrings(mat, PartIds.TextureIds) ?? [] : [])
                .Distinct().ToArray();
        return [];
    }

    public override void _Process(double delta)
    {
        _since += delta;
        if (_since < 0.25 || _root == null || !IsInstanceValid(_root))
            return;
        _since = 0;
        if (_part == null)
        {
            if (IsInstanceValid(_node) && _node.IsInsideTree())
            {
                var anchor = _root.Added().FirstOrDefault(a => a.Node == _node).Anchor;
                var local = _root.Frame(anchor).AffineInverse() * _node.GlobalTransform;
                _state.Text = $"place relative to its anchor: {local.Origin.Snapped(Vector3.One * 0.01f)} (render units)";
            }
            return;
        }
        var marks = _root.MarksOf(_part);
        var lines = new System.Collections.Generic.List<string>();
        if (marks.HasFlag(PartMarks.Removed))
            lines.Add("removed");
        if (marks.HasFlag(PartMarks.Hidden))
            lines.Add("hidden");
        if (marks.HasFlag(PartMarks.Moved))
        {
            var (mode, x) = MapEditSave.Move(_root, _part);
            var e = x.Basis.GetEuler() * (180f / Mathf.Pi);
            lines.Add(mode == TransformOp.Absolute
                ? $"placed at {x.Origin.Snapped(Vector3.One * 0.01f)} (absolute, render space)"
                : $"moved by {x.Origin.Snapped(Vector3.One * 0.01f)}, turned {e.Snapped(Vector3.One * 0.1f)}°, scaled {x.Basis.Scale.Snapped(Vector3.One * 0.001f)} (offset on the game's placement)");
        }
        if (_part.Replacement.Length > 0)
            lines.Add($"replaced by {_part.Replacement}");
        _state.Text = lines.Count == 0 ? "classic: no edits" : "edits: " + string.Join("; ", lines);
    }

    private void PickReplacement()
    {
        _pick ??= new EditorFileDialog { FileMode = EditorFileDialog.FileModeEnum.OpenFile, Title = "The scene that replaces the part" };
        if (_pick.GetParent() == null)
        {
            _pick.AddFilter("*.tscn, *.scn, *.glb, *.gltf", "Scenes");
            AddChild(_pick);
            _pick.FileSelected += path => _plugin.ReplacePart(_root, _part!, path);
        }
        _pick.PopupCentered(new Vector2I(800, 500));
    }
}
#endif
