#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// R8's status dashboard (docs/remaster-editor.md, "The status dashboard"), a bottom panel: every map
/// of the catalogue with its status (original, in progress, remastered, approved), what the project
/// changes on it (<see cref="RemasterStatus"/>), its last renders' thumbnail and colour check
/// (<see cref="RenderStore"/>), its budgets (tools/budget.sh); filters (search, status, changed,
/// rendered, over budget), sorting, and a progress summary; the selected map's before/after pictures
/// and budget lines. Render draws the selected map's (or the changed maps') before/after pictures
/// (<see cref="RenderBatch"/>; a renderer is needed), Report writes the HTML report.
/// </summary>
[Tool]
public partial class StatusDock : EditorDock
{
    private readonly RemasterPlugin _plugin;
    private Label _summary = null!, _detail = null!, _message = null!;
    private LineEdit _search = null!;
    private OptionButton _status = null!, _sort = null!;
    private CheckBox _changed = null!, _rendered = null!, _over = null!;
    private Tree _tree = null!;
    private TextureRect _before = null!, _after = null!;
    private List<MapStatusRow> _all = new();
    private readonly Dictionary<string, ImageTexture?> _thumbs = new();
    private string? _selected;

    /// <summary>The rows listed now, in order (for the checks).</summary>
    public List<MapStatusRow> Listed { get; } = new();

    public StatusSummary? Summary { get; private set; }

    public string SummaryText => _summary.Text;

    /// <summary>The selected map's before and after pictures as shown (null: none).</summary>
    public Texture2D? BeforeShown => _before.Texture;

    public Texture2D? AfterShown => _after.Texture;

    /// <summary>Thumbnails drawn in the list's rows (for the checks).</summary>
    public int ThumbsInList { get; private set; }

    public static readonly string[] Sorts = ["area", "status", "changes", "colour", "budget", "render time"];

    public StatusDock(RemasterPlugin plugin)
    {
        _plugin = plugin;
        Name = "FfxStatus";
        Title = "Remaster status";
        DefaultSlot = DockSlot.Bottom;
        Build();
    }

    public StatusDock() : this(null!) { }

    private void Build()
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(box);
        var top = new HBoxContainer();
        box.AddChild(top);
        _summary = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "No archive open." };
        top.AddChild(_summary);
        var refresh = new Button { Text = "Refresh", TooltipText = "Read the project's files, the renders and the budgets again" };
        refresh.Pressed += Fill;
        top.AddChild(refresh);
        var render = new Button { Text = "Render", TooltipText = "Before/after pictures of the selected map (none selected: the maps the project changes), offscreen; needs a renderer" };
        render.Pressed += () => Say(RenderSelected());
        top.AddChild(render);
        var report = new Button { Text = "Report", TooltipText = $"The HTML report (the pictures copied beside it) into {StatusReport.DefaultDir}" };
        report.Pressed += () => Say(StatusReport.Write(_plugin, StatusReport.DefaultDir) is var (path, err) && err == null ? $"report: {GameSettings.Globalize(path)}" : err);
        top.AddChild(report);
        var filters = new HBoxContainer();
        box.AddChild(filters);
        _search = new LineEdit { PlaceholderText = "search: name, level, area", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _search.TextChanged += _ => List();
        filters.AddChild(_search);
        _status = new OptionButton { TooltipText = "Only maps of this status" };
        _status.AddItem("Any status");
        foreach (var s in System.Enum.GetNames<MapStatus>())
            _status.AddItem(s);
        _status.ItemSelected += _ => List();
        filters.AddChild(_status);
        _changed = new CheckBox { Text = "Changed", TooltipText = "Only the maps the project changes" };
        _changed.Toggled += _ => List();
        filters.AddChild(_changed);
        _rendered = new CheckBox { Text = "Rendered", TooltipText = "Only the maps with before/after renders" };
        _rendered.Toggled += _ => List();
        filters.AddChild(_rendered);
        _over = new CheckBox { Text = "Off", TooltipText = "Only the maps whose colour check is off or whose budget is over" };
        _over.Toggled += _ => List();
        filters.AddChild(_over);
        filters.AddChild(new Label { Text = "Sort" });
        _sort = new OptionButton();
        foreach (var s in Sorts)
            _sort.AddItem(s);
        _sort.ItemSelected += _ => List();
        filters.AddChild(_sort);
        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 240) };
        box.AddChild(split);
        _tree = new Tree { Columns = 6, HideRoot = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, SelectMode = Tree.SelectModeEnum.Row };
        _tree.SetColumnTitlesVisible(true);
        string[] titles = ["Map", "Area", "Status", "The project changes", "Renders", "Budget"];
        for (int i = 0; i < titles.Length; i++)
            _tree.SetColumnTitle(i, titles[i]);
        _tree.SetColumnExpand(1, false);
        _tree.SetColumnCustomMinimumWidth(1, 60);
        _tree.SetColumnExpand(2, false);
        _tree.SetColumnCustomMinimumWidth(2, 100);
        _tree.ItemSelected += () => Select(_tree.GetSelected()?.GetMetadata(0).AsString());
        split.AddChild(_tree);
        var side = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        split.AddChild(side);
        _detail = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "Select a map." };
        side.AddChild(_detail);
        var pics = new HBoxContainer();
        side.AddChild(pics);
        _before = new TextureRect { CustomMinimumSize = new Vector2(200, 112), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TooltipText = "classic" };
        _after = new TextureRect { CustomMinimumSize = new Vector2(200, 112), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TooltipText = "remastered / rebuilt" };
        pics.AddChild(_before);
        pics.AddChild(_after);
        _message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        side.AddChild(_message);
    }

    public void Say(string? text) => _message.Text = text ?? "";

    /// <summary>Reads everything again (the archive opened, a render finished, Refresh).</summary>
    public void Fill()
    {
        _thumbs.Clear();
        _all = _plugin.Session.Catalogue != null ? RemasterStatus.All(_plugin) : new();
        Summary = _all.Count > 0 ? RemasterStatus.Summarise(_all) : null;
        _summary.Text = Summary?.ToString() ?? "No archive open.";
        if (!_plugin.Uses.Ready && _all.Count > 0)
            _summary.Text += " (textures per map: the where-used index is being built)";
        List();
    }

    /// <summary>Sets the filters (for the checks); null leaves one as it is.</summary>
    public void SetFilter(string? search = null, int? status = null, bool? changed = null, bool? rendered = null, bool? off = null, int? sort = null)
    {
        if (search != null) _search.Text = search;
        if (status != null) _status.Selected = status.Value;
        if (changed != null) _changed.SetPressedNoSignal(changed.Value);
        if (rendered != null) _rendered.SetPressedNoSignal(rendered.Value);
        if (off != null) _over.SetPressedNoSignal(off.Value);
        if (sort != null) _sort.Selected = sort.Value;
        List();
    }

    private void List()
    {
        _tree.Clear();
        Listed.Clear();
        ThumbsInList = 0;
        var root = _tree.CreateItem();
        var q = _search.Text.Trim();
        IEnumerable<MapStatusRow> rows = _all;
        if (q.Length > 0)
            rows = rows.Where(r => MapBrowserDock.Matches(r.Entry, q) || r.Entry.Area.Contains(q, System.StringComparison.OrdinalIgnoreCase));
        if (_status.Selected > 0)
            rows = rows.Where(r => (int)r.Status == _status.Selected - 1);
        if (_changed.ButtonPressed)
            rows = rows.Where(r => r.Changed);
        if (_rendered.ButtonPressed)
            rows = rows.Where(r => r.Render is { State: "done" });
        if (_over.ButtonPressed)
            rows = rows.Where(r => r.Render is { ColourOk: false } || r.BudgetVerdict == "over");
        rows = Sorts[System.Math.Max(0, _sort.Selected)] switch
        {
            "status" => rows.OrderByDescending(r => r.Status).ThenBy(r => r.Entry.Key),
            "changes" => rows.OrderByDescending(r => r.ChangesText.Length).ThenBy(r => r.Entry.Key),
            "colour" => rows.OrderBy(r => r.Render is { Kind: "3d" } x ? (x.ColourOk ? 1 : 0) : 2).ThenByDescending(r => System.Math.Abs(r.Render?.DL ?? 0)),
            "budget" => rows.OrderByDescending(r => r.Budgets.Select(b => b.FrameMs).DefaultIfEmpty(-1).Max()),
            "render time" => rows.OrderByDescending(r => r.Render?.Ms ?? -1),
            _ => rows,
        };
        var lang = _plugin.Dock?.Language ?? "us";
        foreach (var r in rows)
        {
            var it = _tree.CreateItem(root);
            it.SetText(0, $"{r.Entry.Title(lang)} ({r.Entry.Key}){(r.Is2D && r.Entry.Kind == "field" ? " 2D" : "")}");
            it.SetMetadata(0, r.Entry.Key);
            it.SetText(1, r.Entry.Area);
            it.SetText(2, r.Status.ToString());
            it.SetCustomColor(2, r.Status switch
            {
                MapStatus.Approved => new Color(0.4f, 0.85f, 0.45f),
                MapStatus.Remastered => new Color(0.45f, 0.7f, 1f),
                MapStatus.InProgress => new Color(1f, 0.75f, 0.35f),
                _ => new Color(0.65f, 0.65f, 0.65f),
            });
            it.SetText(3, r.ChangesText.Length > 0 ? r.ChangesText : r.Textures >= 0 ? $"({r.Textures} textures)" : "");
            it.SetText(4, r.RenderText);
            if (r.Render is { State: "done", ColourOk: false })
                it.SetCustomColor(4, new Color(1f, 0.5f, 0.35f));
            if (Thumb(r.Render?.ThumbAfter is { Length: > 0 } a ? a : r.Render?.ThumbBefore) is { } t)
            {
                it.SetIcon(4, t);
                it.SetIconMaxWidth(4, 64);
                ThumbsInList++;
            }
            it.SetText(5, r.Budgets.Count == 0 ? "" : $"{r.BudgetVerdict}: " + string.Join("; ", r.Budgets.Where(b => b.Gi == "None" || b.Path == "classic").Select(b => System.FormattableString.Invariant($"{b.Preset} {b.Path[0]} {b.FrameMs:0.0} ms"))));
            if (r.BudgetVerdict == "over")
                it.SetCustomColor(5, new Color(1f, 0.5f, 0.35f));
            Listed.Add(r);
            if (r.Entry.Key == _selected)
                it.Select(0);
        }
    }

    private ImageTexture? Thumb(string? rel)
    {
        if (rel is not { Length: > 0 })
            return null;
        if (_thumbs.TryGetValue(rel, out var t))
            return t;
        var img = RenderStore.Picture(rel);
        return _thumbs[rel] = img != null ? ImageTexture.CreateFromImage(img) : null;
    }

    /// <summary>Shows <paramref name="key"/>'s details: what changes, the renders (the first view's pictures), the budgets.</summary>
    public void Select(string? key)
    {
        _selected = key;
        var r = _all.FirstOrDefault(x => x.Entry.Key == key);
        if (r == null)
        {
            _detail.Text = "Select a map.";
            (_before.Texture, _after.Texture) = (null, null);
            return;
        }
        var lines = new List<string>
        {
            $"{r.Entry.Title(_plugin.Dock?.Language ?? "us")} — {r.Entry.Key} ({r.Entry.Level}), {r.Entry.Area}, {r.Status}"
                + (r.Dock.Notes.Length > 0 ? $"\nnotes: {r.Dock.Notes.Split('\n')[0]}" : ""),
            "the project: " + (r.ChangesText.Length > 0 ? r.ChangesText : "nothing") + (r.Textures >= 0 ? $" ({r.Textures} textures on the map)" : ""),
        };
        if (r.Render is { } rec)
            lines.Add($"renders ({rec.When}, {rec.Ms / 1000:0.0} s, {rec.Views}): {rec.State}{(rec.Reason.Length > 0 ? $" ({rec.Reason})" : "")}"
                + (rec.Colour.Length > 0 ? $"\ncolour: {rec.Colour}" : "") + (rec.Diff >= 0 ? System.FormattableString.Invariant($"\nmean difference {rec.Diff:0.00}/255") : "")
                + (rec.Changes != r.ChangesText ? "\n(the project changed since: render again)" : ""));
        else
            lines.Add("no renders yet (Render)");
        foreach (var b in r.Budgets)
            lines.Add($"budget {b.Short} ({b.Width}x{b.Height}, {b.Adapter})");
        _detail.Text = string.Join("\n", lines);
        _before.Texture = Picture(r.Render?.Before.FirstOrDefault());
        _after.Texture = Picture(r.Render?.After.FirstOrDefault());
    }

    private static ImageTexture? Picture(string? rel) => rel != null && RenderStore.Picture(rel) is { } img ? ImageTexture.CreateFromImage(img) : null;

    private string RenderSelected()
    {
        if (!RenderBatch.Available)
            return "no renderer (headless): run the editor with a window, or --remaster-renders under Xvfb";
        var keys = _selected != null ? [_selected] : _all.Where(r => r.Changed).Select(r => r.Entry.Key).ToList();
        if (keys.Count == 0)
            return "no maps to render (select one)";
        return _plugin.StartRenders(keys, again: true) ?? $"rendering {keys.Count} map(s): {string.Join(", ", keys.Take(5))}{(keys.Count > 5 ? ", ..." : "")}";
    }
}
#endif
