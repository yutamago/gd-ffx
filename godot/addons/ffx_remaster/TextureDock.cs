#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The <b>Textures</b> dock (docs/remaster-editor.md, "Textures"): the texture browser (the open
/// map's textures, a search over the where-used index, or the project's edited ones), the
/// selected texture's before/after compare (a split slider over the classic texture and its
/// replacement or its material's maps), its uses (a map and its parts: double click opens the
/// map and selects the part), and the actions: replace, upscale, generate the material, edit it
/// in the inspector, revert; the batch with its review step. Every edit is one undo step
/// (<see cref="RemasterPlugin.SetTexture"/>).
/// </summary>
[Tool]
public partial class TextureDock : EditorDock
{
    private readonly RemasterPlugin _plugin;
    private OptionButton _mode = null!;
    private LineEdit _search = null!;
    private Label _index = null!;
    private Tree _list = null!;
    private Label _id = null!;
    private TextureRect _preview = null!;
    private ShaderMaterial _compare = null!;
    private HSlider _split = null!;
    private OptionButton _after = null!;
    private ItemList _uses = null!;
    private Label _message = null!;
    private EditorFileDialog? _fileDialog;
    private BatchPanel _batch = null!;
    private string? _selected;
    private bool _dirty = true;

    public const int ModeMap = 0, ModeSearch = 1, ModeProject = 2;

    /// <summary>The texture IDs listed now (for the checks).</summary>
    public List<string> Listed { get; } = new();

    public string? Selected => _selected;

    public BatchPanel Batch => _batch;

    public TextureDock(RemasterPlugin plugin)
    {
        _plugin = plugin;
        Name = "FfxRemasterTextures";
        Title = "Textures";
        DefaultSlot = DockSlot.RightUl;
        Build();
    }

    // required by Godot's C# glue for a [Tool] node made by the engine (never used: the plugin makes the dock)
    public TextureDock() : this(null!) { }

    // the compare: the classic texture left of the split, the other right of it (stretched to the classic's shape)
    private const string CompareShader = @"shader_type canvas_item;
uniform sampler2D before : filter_linear;
uniform sampler2D after : filter_linear;
uniform float split = 0.5;
uniform bool has_after = false;
void fragment() {
	vec4 a = texture(before, UV);
	vec4 b = has_after ? texture(after, UV) : a;
	COLOR = UV.x < split ? a : b;
	COLOR.rgb = mix(COLOR.rgb, vec3(1.0, 0.85, 0.2), step(abs(UV.x - split), 0.004) * (has_after ? 1.0 : 0.0));
	COLOR.a = 1.0;
}";

    private void Build()
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        AddChild(scroll);
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        scroll.AddChild(col);
        var row = new HBoxContainer();
        col.AddChild(row);
        _mode = new OptionButton { TooltipText = "Which textures: the open map's, a search of every map's (the where-used index), or the project's edited ones" };
        _mode.AddItem("Open map");
        _mode.AddItem("Where used");
        _mode.AddItem("Project");
        _mode.ItemSelected += _ => Fill();
        row.AddChild(_mode);
        _search = new LineEdit { PlaceholderText = "search: texture name, map", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _search.TextChanged += _ => Fill();
        row.AddChild(_search);
        var rebuild = new Button { Text = "Index", TooltipText = "Build the where-used index again from the archive (cached in user://)" };
        rebuild.Pressed += () =>
        {
            _plugin.Uses.Start(_plugin.Session, rebuild: true);
            UsesChanged();
        };
        row.AddChild(rebuild);
        _index = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_index);
        _list = new Tree { Columns = 3, HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 200) };
        _list.SetColumnTitlesVisible(true);
        _list.SetColumnTitle(0, "Texture");
        _list.SetColumnTitle(1, "Used");
        _list.SetColumnTitle(2, "Edits");
        _list.SetColumnExpand(1, false);
        _list.SetColumnCustomMinimumWidth(1, 80);
        _list.SetColumnExpand(2, false);
        _list.SetColumnCustomMinimumWidth(2, 110);
        // several at once (Materialize takes them all); the last one clicked is the selected texture
        _list.SelectMode = Tree.SelectModeEnum.Multi;
        _list.MultiSelected += (item, _, on) =>
        {
            if (on)
                Select(item.GetMetadata(0).AsString());
        };
        col.AddChild(_list);
        _id = new Label { AutowrapMode = TextServer.AutowrapMode.Arbitrary };
        col.AddChild(_id);
        _compare = new ShaderMaterial { Shader = new Shader { Code = CompareShader } };
        _preview = new TextureRect
        {
            CustomMinimumSize = new Vector2(0, 220), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Material = _compare,
        };
        col.AddChild(_preview);
        row = new HBoxContainer();
        col.AddChild(row);
        row.AddChild(new Label { Text = "Classic" });
        _split = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.01, Value = 0.5, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Before | after" };
        _split.ValueChanged += v => _compare.SetShaderParameter("split", (float)v);
        row.AddChild(_split);
        _after = new OptionButton { TooltipText = "What the right side shows" };
        foreach (var a in new[] { "Replacement", "Albedo", "Normal", "ORM" })
            _after.AddItem(a);
        _after.ItemSelected += _ => ShowPreview();
        row.AddChild(_after);
        row = new HBoxContainer();
        col.AddChild(row);
        Add(row, "Replace...", "Replace the texture by an image file (PNG, WebP, JPEG), everywhere it is used", ChooseFile);
        Add(row, "Upscale x2", "Upscale it (ComfyUI when configured in the editor settings, else Lanczos)", () => Upscale(2));
        Add(row, "x4", "Upscale it four times", () => Upscale(4));
        Add(row, "Revert", "Back to the classic texture: no replacement, no material", () => Act(id => TextureActions.Revert(_plugin, id)));
        row = new HBoxContainer();
        col.AddChild(row);
        Add(row, "Generate material", "Make its PBR material from it (de-lighting, normal, roughness, occlusion, height; its recipe, else the defaults)",
            () => Act(id => Say(TextureActions.GenerateMaterial(_plugin, id, _pendingRecipe.GetValueOrDefault(id)?.Duplicate() as PbrRecipe)
                ?? $"{RemasterTextures.ShortName(id)}: material generated")));
        Add(row, "Edit material", "Open its material in the inspector", () => Act(id => Say(TextureActions.EditMaterial(id) ? "" : "no material yet")));
        Add(row, "Recipe", "Open its material's recipe in the inspector (Generate uses it)", EditRecipe);
        Add(row, "Materialize", "Add the selected textures (several: Ctrl/Shift+click) to the Materials tab's queue and open the tab",
            () => Say(SelectedIds() is { Count: > 0 } ids ? $"{_plugin.Materialize(ids)} texture(s) added to the Materials tab" : "select a texture first"));
        col.AddChild(new Label { Text = "Used by (double click: open the map, select the part)" });
        _uses = new ItemList { CustomMinimumSize = new Vector2(0, 110), SizeFlagsVertical = SizeFlags.ExpandFill };
        _uses.ItemActivated += i => OpenUse((int)i);
        col.AddChild(_uses);
        col.AddChild(new HSeparator());
        _batch = new BatchPanel(_plugin, this);
        col.AddChild(_batch);
        _message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_message);
    }

    private static void Add(HBoxContainer row, string text, string tip, System.Action act)
    {
        var b = new Button { Text = text, TooltipText = tip };
        b.Pressed += act;
        row.AddChild(b);
    }

    public void Say(string text)
    {
        if (text.Length > 0)
            GD.Print($"ffx remaster: {text}");
        _message.Text = text;
    }

    private void Act(System.Action<string> act)
    {
        if (_selected == null)
        {
            Say("select a texture first");
            return;
        }
        act(_selected);
    }

    // ---- state ---------------------------------------------------------------------------------

    public void Tick(double delta)
    {
        if (_plugin.Uses.Building)
            _index.Text = $"Building the where-used index: {_plugin.Uses.Progress.Done} / {_plugin.Uses.Progress.Total} maps...";
        _batch.Tick();
        if (_dirty && IsVisibleInTree())
        {
            _dirty = false;
            Fill();
        }
    }

    public void UsesChanged()
    {
        var u = _plugin.Uses;
        _index.Text = u.Ready ? $"Where-used: {u.ByTexture.Count} textures in {u.ByLevel.Count} maps{(u.BuildMs > 0 ? $" (built in {u.BuildMs / 1000.0:0.0} s)" : " (cached)")}"
            : u.Building ? "Building the where-used index..." : "No where-used index (open the archive).";
        _dirty = true;
    }

    public void MapChanged()
    {
        _dirty = true;
        if (_mode.Selected == ModeMap)
            Fill();
    }

    public void TexturesChanged()
    {
        Fill();
        ShowPreview();
    }

    /// <summary>Sets the browser's mode and search (the checks).</summary>
    public void SetFilter(int mode, string search)
    {
        _mode.Selected = mode;
        _search.Text = search;
        Fill();
    }

    // ---- the list ------------------------------------------------------------------------------

    private static string Marks(string id)
    {
        var e = RemasterTextures.For(id);
        var own = TextureStore.Find(id);
        var m = new List<string>();
        if (e?.Replaced == true) m.Add("replaced");
        if (e?.HasMaterial == true) m.Add("PBR");
        if (e != null && own == null) m.Add($"({e.Layer})");
        return string.Join(", ", m);
    }

    public void Fill()
    {
        _list.Clear();
        Listed.Clear();
        var root = _list.CreateItem();
        var q = _search.Text.Trim().ToLowerInvariant();
        IEnumerable<(string Id, string Used)> rows = _mode.Selected switch
        {
            ModeMap => TextureActions.OnMap(_plugin.Current).Select(kv => (kv.Key, $"{kv.Value} surf.")),
            ModeProject => TextureStore.Set.Textures.Where(t => t != null).Select(t => (t.Id, UsedText(t.Id))),
            _ => _plugin.Uses.ByTexture.Select(kv => (kv.Key, UsedText(kv.Key))),
        };
        foreach (var (id, used) in rows.OrderBy(r => RemasterTextures.ShortName(r.Id), System.StringComparer.Ordinal).ThenBy(r => r.Id, System.StringComparer.Ordinal))
        {
            if (q.Length > 0 && !id.Contains(q, System.StringComparison.Ordinal)
                && !_plugin.Uses.Uses(id).Any(u => u.Level.Contains(q, System.StringComparison.Ordinal)))
                continue;
            if (Listed.Count >= 2000)
                break;
            var it = _list.CreateItem(root);
            it.SetText(0, RemasterTextures.ShortName(id));
            it.SetTooltipText(0, id);
            it.SetMetadata(0, id);
            it.SetText(1, used);
            it.SetText(2, Marks(id));
            if (id == _selected)
                it.Select(0);
            Listed.Add(id);
        }
    }

    /// <summary>The textures selected in the list (several with Ctrl/Shift+click), else the selected one.</summary>
    public List<string> SelectedIds()
    {
        var r = new List<string>();
        for (var it = _list.GetNextSelected(null); it != null; it = _list.GetNextSelected(it))
            if (it.GetMetadata(0).AsString() is { Length: > 0 } id)
                r.Add(id);
        if (r.Count == 0 && _selected != null)
            r.Add(_selected);
        return r;
    }

    private string UsedText(string id)
    {
        var u = _plugin.Uses.Uses(id);
        return u.Count == 0 ? "" : $"{u.Count} map{(u.Count == 1 ? "" : "s")}";
    }

    // ---- the selected texture -----------------------------------------------------------------

    public void Select(string? id)
    {
        _selected = id;
        _id.Text = id == null ? "" : $"{id}\n{Marks(id)}";
        _uses.Clear();
        if (id != null)
            foreach (var u in _plugin.Uses.Uses(id))
            {
                var entry = _plugin.Session.Catalogue?.Find(u.Level);
                int i = _uses.AddItem($"{u.Level} ({entry?.Title(_plugin.Dock?.Language ?? "us") ?? "?"}): {u.Parts.Count} part(s)");
                _uses.SetItemMetadata(i, u.Level);
            }
        ShowPreview();
    }

    private Texture2D? _classicPreview;
    private string? _classicOf;

    private void ShowPreview()
    {
        if (_selected is not { } id)
        {
            _preview.Texture = null;
            return;
        }
        if (_classicOf != id)
        {
            _classicOf = id;
            _classicPreview = RemasterTextures.ClassicImage(id) is { } img ? ImageTexture.CreateFromImage(img) : null;
        }
        var before = _classicPreview;
        Texture2D? after = null;
        var e = RemasterTextures.For(id);
        if (_after.Selected == 0 && e is { Replaced: true })
            after = RemasterTextures.Texture(e.Replacement);
        else if (_after.Selected > 0 && RemasterTextures.MaterialFor(id) is BaseMaterial3D m)
            after = _after.Selected switch { 1 => m.AlbedoTexture, 2 => m.NormalTexture, _ => m.OrmTexture };
        _preview.Texture = before ?? after;
        _compare.SetShaderParameter("before", before ?? after ?? new PlaceholderTexture2D());
        _compare.SetShaderParameter("after", after ?? before ?? new PlaceholderTexture2D());
        _compare.SetShaderParameter("has_after", after != null && before != null);
    }

    private void ChooseFile() => Act(id =>
    {
        _fileDialog ??= new EditorFileDialog
        {
            FileMode = EditorFileDialog.FileModeEnum.OpenFile, Access = EditorFileDialog.AccessEnum.Filesystem,
            Filters = ["*.png ; PNG", "*.webp ; WebP", "*.jpg, *.jpeg ; JPEG"], Title = "Replace the texture by",
        };
        if (_fileDialog.GetParent() == null)
            AddChild(_fileDialog);
        foreach (var c in _fileDialog.GetSignalConnectionList(FileDialog.SignalName.FileSelected))
            _fileDialog.Disconnect(FileDialog.SignalName.FileSelected, c["callable"].AsCallable());
        _fileDialog.FileSelected += file => Say(TextureActions.ReplaceFromFile(_plugin, id, file) ?? $"{RemasterTextures.ShortName(id)} replaced by {file.GetFile()}");
        _fileDialog.PopupFileDialog();
    });

    private void Upscale(int factor) => Act(id =>
    {
        Say(ComfyUpscaler.Configured ? $"upscaling x{factor} with ComfyUI..." : "");
        TextureActions.Upscale(_plugin, id, factor, Say);
    });

    private void EditRecipe() => Act(id =>
    {
        // the recipe the next Generate uses: the entry's (a copy, edited in place), else a new one kept for it
        var entry = TextureStore.Find(id);
        if (entry?.Recipe is { } r)
            EditorInterface.Singleton.EditResource(r);
        else
        {
            _pendingRecipe[id] = _pendingRecipe.GetValueOrDefault(id) ?? new PbrRecipe();
            EditorInterface.Singleton.EditResource(_pendingRecipe[id]);
        }
    });

    private readonly Dictionary<string, PbrRecipe> _pendingRecipe = new();

    private void OpenUse(int index)
    {
        var level = _uses.GetItemMetadata(index).AsString();
        if (_selected is not { } id || _plugin.Session.Catalogue?.Find(level) is not { } entry)
            return;
        var parts = _plugin.Uses.Uses(id).FirstOrDefault(u => u.Level == level)?.Parts;
        if (_plugin.OpenMap(entry) is { } err)
        {
            Say(err);
            return;
        }
        _selectWhenBuilt = parts is { Count: > 0 } ? (level, parts[0]) : null;
    }

    private (string Level, string Part)? _selectWhenBuilt;

    /// <summary>After a map opened from a use: selects the used part.</summary>
    public void SelectPendingPart()
    {
        if (_selectWhenBuilt is not { } want || _plugin.Current is not { Built: true } root || root.Key != want.Level)
            return;
        _selectWhenBuilt = null;
        if (root.Parts.TryGetValue(PartIds.Full(want.Part, root.Model), out var p))
            RemasterPlugin.Select(p.Node);
    }
}
#endif
