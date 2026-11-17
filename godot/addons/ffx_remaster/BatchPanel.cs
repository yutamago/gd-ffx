#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The batch (docs/remaster-editor.md, "The batch"): materials for many textures at once (the
/// open map's, the selected map's area, every map the project changes, every map), generated
/// in the background a texture a frame into a review list (no file written yet), then the
/// ticked ones accepted as one undo step, or all discarded. At most <see cref="Chunk"/> a run:
/// run again for the next.
/// </summary>
[Tool]
public partial class BatchPanel : VBoxContainer
{
    public const int Chunk = 48;

    private readonly RemasterPlugin _plugin;
    private readonly TextureDock _dock;
    private OptionButton _scope = null!;
    private OptionButton _upscale = null!;
    private HSlider _delight = null!;
    private CheckBox _skip = null!;
    private Tree _review = null!;
    private Label _status = null!;
    private readonly Queue<string> _todo = new();
    private readonly List<(string Id, PbrImages Maps, PbrRecipe Recipe)> _made = new();

    public const int ScopeMap = 0, ScopeArea = 1, ScopeChanged = 2, ScopeAll = 3;

    /// <summary>Generated and waiting for review.</summary>
    public IReadOnlyList<(string Id, PbrImages Maps, PbrRecipe Recipe)> Made => _made;

    public bool Running => _todo.Count > 0;

    public BatchPanel(RemasterPlugin plugin, TextureDock dock)
    {
        _plugin = plugin;
        _dock = dock;
        AddChild(new Label { Text = "Batch: materials for many textures, reviewed before they are kept" });
        var row = new HBoxContainer();
        AddChild(row);
        _scope = new OptionButton();
        foreach (var s in new[] { "Open map", "Selected map's area", "Maps the project changes", "Every map" })
            _scope.AddItem(s);
        row.AddChild(_scope);
        _upscale = new OptionButton { TooltipText = "Upscale first (Lanczos)" };
        foreach (var u in new[] { "x1", "x2", "x4" })
            _upscale.AddItem(u);
        row.AddChild(_upscale);
        row.AddChild(new Label { Text = "De-light" });
        _delight = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = 0.6, CustomMinimumSize = new Vector2(80, 0) };
        row.AddChild(_delight);
        _skip = new CheckBox { Text = "Skip done", ButtonPressed = true, TooltipText = "Leave out textures that have a material" };
        row.AddChild(_skip);
        row = new HBoxContainer();
        AddChild(row);
        var run = new Button { Text = "Generate", TooltipText = $"Make up to {Chunk} materials for review (nothing is written yet)" };
        run.Pressed += () => _dock.Say(Start(_scope.Selected) ?? "");
        row.AddChild(run);
        var accept = new Button { Text = "Accept ticked", TooltipText = "Write the ticked materials (one undo step)" };
        accept.Pressed += () => _dock.Say(Accept());
        row.AddChild(accept);
        var discard = new Button { Text = "Discard" };
        discard.Pressed += Discard;
        row.AddChild(discard);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_status);
        _review = new Tree { Columns = 2, HideRoot = true, CustomMinimumSize = new Vector2(0, 120) };
        _review.SetColumnExpand(1, false);
        _review.SetColumnCustomMinimumWidth(1, 72);
        AddChild(_review);
    }

    public BatchPanel() : this(null!, null!) { }

    /// <summary>The scope's textures (colour textures only, the way the classic shaders sample them).</summary>
    public List<string> Scope(int scope)
    {
        var ids = new SortedSet<string>(System.StringComparer.Ordinal);
        var uses = _plugin.Uses;
        IEnumerable<string> Levels() => scope switch
        {
            ScopeArea when _plugin.Dock?.Selected is { } sel && _plugin.Session.Catalogue is { } cat =>
                cat.Levels.Where(e => e.Area == sel.Area).Select(e => e.Key),
            ScopeChanged => MapStore.Levels(),
            ScopeAll => uses.ByLevel.Keys,
            _ => [],
        };
        if (scope == ScopeMap)
            foreach (var id in TextureActions.OnMap(_plugin.Current).Keys)
                ids.Add(id);
        else
            foreach (var level in Levels())
                if (uses.ByLevel.TryGetValue(level, out var list))
                    ids.UnionWith(list);
        return ids.Where(id => !_skip.ButtonPressed || RemasterTextures.For(id)?.HasMaterial != true).ToList();
    }

    /// <summary>Starts a run over <paramref name="scope"/> (an error, or null).</summary>
    public string? Start(int scope, int limit = Chunk)
    {
        Discard();
        var ids = Scope(scope);
        if (ids.Count == 0)
            return "the batch found no texture to do";
        foreach (var id in ids.Take(limit))
            _todo.Enqueue(id);
        _status.Text = $"generating {_todo.Count} of {ids.Count}...";
        return null;
    }

    public void Tick()
    {
        if (_todo.Count == 0)
            return;
        var id = _todo.Dequeue();
        var recipe = new PbrRecipe { Upscale = _upscale.Selected switch { 1 => 2, 2 => 4, _ => 1 }, Delight = (float)_delight.Value };
        if (TextureActions.Make(id, recipe) is { } maps)
        {
            _made.Add((id, maps, recipe));
            var it = _review.CreateItem(_review.GetRoot() ?? _review.CreateItem());
            it.SetCellMode(0, TreeItem.TreeCellMode.Check);
            it.SetChecked(0, true);
            it.SetEditable(0, true);
            it.SetText(0, RemasterTextures.ShortName(id));
            it.SetTooltipText(0, id);
            it.SetMetadata(0, id);
            var thumb = (Image)maps.Albedo.Duplicate();
            thumb.Resize(64, 64 * thumb.GetHeight() / System.Math.Max(1, thumb.GetWidth()), Image.Interpolation.Bilinear);
            it.SetIcon(1, ImageTexture.CreateFromImage(thumb));
        }
        _status.Text = _todo.Count > 0 ? $"generating: {_made.Count} done, {_todo.Count} to go" : $"{_made.Count} material(s) to review: untick the bad ones, then Accept";
    }

    /// <summary>Unticks <paramref name="id"/> in the review (the checks).</summary>
    public void Untick(string id)
    {
        for (var it = _review.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
            if (it.GetMetadata(0).AsString() == id)
                it.SetChecked(0, false);
    }

    /// <summary>Writes the ticked materials as one undo step; what it did.</summary>
    public string Accept()
    {
        var ticked = new HashSet<string>();
        for (var it = _review.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
            if (it.IsChecked(0))
                ticked.Add(it.GetMetadata(0).AsString());
        var edits = new List<(string, RemasterTexture)>();
        foreach (var (id, maps, recipe) in _made)
            if (ticked.Contains(id))
                edits.Add((id, TextureActions.WithMaterial(id, maps, recipe)));
        _plugin.SetTextures(edits, $"Batch: {edits.Count} material(s)");
        Discard();
        return $"batch: {edits.Count} material(s) kept";
    }

    public void Discard()
    {
        _todo.Clear();
        _made.Clear();
        _review.Clear();
        _status.Text = "";
    }
}
#endif
