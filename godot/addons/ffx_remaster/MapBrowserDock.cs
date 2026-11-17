#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remake;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>A map's state in the dock: the project's layer (hand-set status, render path, notes), and the derived marks.</summary>
public sealed record DockState(MapStatus Status, RenderPath RenderPath, string Notes, bool HasOverrides, bool HasAdditions, bool HasRemade)
{
    /// <summary>The status to show: the hand-set one, at least in progress with overrides or a remade scene (<see cref="MapState.Effective"/>).</summary>
    public MapStatus Effective => Status == MapStatus.Original && (HasOverrides || HasRemade) ? MapStatus.InProgress : Status;

    public bool Changed => HasOverrides || HasRemade || Status != MapStatus.Original;

    public string Marks => string.Join(", ", new[]
    {
        HasOverrides ? "overrides" : null, HasAdditions ? "additions" : null, HasRemade ? "remade" : null,
        RenderPath == RenderPath.Remastered ? "PBR" : null,
    }.Where(s => s != null));

    public static DockState Of(MapEntry e)
    {
        var res = MapStore.Find(e.Key);
        bool packs = RemasterOverrides.Exists(e.Level) && res == null;
        return new DockState(res?.Status ?? MapStatus.Original, res?.RenderPath ?? RenderPath.Classic, res?.Notes ?? "",
            res != null && (res.Parts.Count > 0 || MapStore.HasAdditions(e.Key)) || packs, MapStore.HasAdditions(e.Key),
            RemakeRegistry.Find("map", e.Level) != null);
    }
}

/// <summary>
/// The remaster dock (docs/remaster-editor.md): the game folder, the map catalogue grouped by
/// area (search by name, level or event; names in a chosen language; each map's status and
/// marks; only the maps the project changes), the selected map's status, render path and notes,
/// Open / Save / Play, and the open map's parts with their edits (changed only, select, revert).
/// </summary>
[Tool]
public partial class MapBrowserDock : EditorDock
{
    private readonly RemasterPlugin _plugin;
    private Label _archive = null!;
    private OptionButton _language = null!;
    private LineEdit _search = null!;
    private CheckBox _changedOnly = null!;
    private Tree _maps = null!;
    private Label _mapInfo = null!;
    private OptionButton _status = null!;
    private OptionButton _renderPath = null!;
    private TextEdit _notes = null!;
    private OptionButton _devSave = null!;
    private Label _playNote = null!;
    private Label _message = null!;
    private Tree _parts = null!;
    private CheckBox _partsChangedOnly = null!;
    private OptionButton _addKind = null!;
    private EditorFileDialog? _dirDialog;

    private MapEntry? _selected;
    private string _partsSignature = "";
    private double _sinceRefresh;
    private bool _filling;

    public static readonly string[] LanguageNames = ["jp", "us", "fr", "sp", "de", "it", "kr", "ch"];

    /// <summary>The maps listed now (for the checks).</summary>
    public List<MapEntry> Listed { get; } = new();

    public string Language => _language.ItemCount > 0 ? _language.GetItemText(_language.Selected) : "us";

    public MapEntry? Selected => _selected;

    public MapBrowserDock(RemasterPlugin plugin)
    {
        _plugin = plugin;
        Name = "FfxRemaster";
        Title = "Remaster";
        DefaultSlot = DockSlot.RightUl;
        Build();
    }

    // required by Godot's C# glue for a [Tool] node made by the engine (never used: the plugin makes the dock)
    public MapBrowserDock() : this(null!) { }

    private void Build()
    {
        // in a scroll container: the wrapping labels would otherwise ask the editor for their height at no width
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        AddChild(scroll);
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        scroll.AddChild(col);
        // the game folder
        var row = new HBoxContainer();
        col.AddChild(row);
        _archive = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "No archive open." };
        row.AddChild(_archive);
        var open = new Button { Text = "Open", TooltipText = "Open the player's game folder (the editor setting, FFX_GAME_DIR or the launcher's)" };
        open.Pressed += () => _plugin?.OpenArchive();
        row.AddChild(open);
        var choose = new Button { Text = "Folder...", TooltipText = $"Choose the game folder (saved as the editor setting {EditorClassic.GameDirSetting})" };
        choose.Pressed += ChooseFolder;
        row.AddChild(choose);
        // search
        row = new HBoxContainer();
        col.AddChild(row);
        _search = new LineEdit { PlaceholderText = "search: name, level, event", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _search.TextChanged += _ => FillMaps();
        row.AddChild(_search);
        _language = new OptionButton { TooltipText = "The language of the place names" };
        foreach (var l in LanguageNames)
            _language.AddItem(l);
        _language.Selected = 1;
        _language.ItemSelected += _ => FillMaps();
        row.AddChild(_language);
        _changedOnly = new CheckBox { Text = "Changed", TooltipText = "Only the maps the project changes (overrides, a remade scene or a status)" };
        _changedOnly.Toggled += _ => FillMaps();
        row.AddChild(_changedOnly);
        // the catalogue
        _maps = new Tree { Columns = 3, HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 220) };
        _maps.SetColumnTitlesVisible(true);
        _maps.SetColumnTitle(0, "Map");
        _maps.SetColumnTitle(1, "Level");
        _maps.SetColumnTitle(2, "Status");
        _maps.SetColumnExpand(1, false);
        _maps.SetColumnCustomMinimumWidth(1, 70);
        _maps.SetColumnExpand(2, false);
        _maps.SetColumnCustomMinimumWidth(2, 120);
        _maps.ItemSelected += OnMapSelected;
        _maps.ItemActivated += () => OpenSelected();
        col.AddChild(_maps);
        // the selected map
        _mapInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "" };
        col.AddChild(_mapInfo);
        row = new HBoxContainer();
        col.AddChild(row);
        row.AddChild(new Label { Text = "Status" });
        _status = new OptionButton();
        foreach (var s in System.Enum.GetNames<MapStatus>())
            _status.AddItem(s);
        _status.ItemSelected += _ => StateEdited();
        row.AddChild(_status);
        row.AddChild(new Label { Text = "Render" });
        _renderPath = new OptionButton { TooltipText = "Classic: the game's look; Remastered: Godot's PBR pipeline (used from R2)" };
        foreach (var s in System.Enum.GetNames<RenderPath>())
            _renderPath.AddItem(s);
        _renderPath.ItemSelected += _ => StateEdited();
        row.AddChild(_renderPath);
        _notes = new TextEdit { PlaceholderText = "notes", CustomMinimumSize = new Vector2(0, 48), WrapMode = TextEdit.LineWrappingMode.Boundary };
        _notes.FocusExited += StateEdited;
        col.AddChild(_notes);
        row = new HBoxContainer();
        col.AddChild(row);
        var openMap = new Button { Text = "Open map", TooltipText = "Open the map as an editable scene of its classic parts" };
        openMap.Pressed += () => OpenSelected();
        row.AddChild(openMap);
        var save = new Button { Text = "Save edits", TooltipText = "Write the open map's overrides.tres and additions.tscn (Ctrl+S on its scene does too)" };
        save.Pressed += () => SaveCurrent();
        row.AddChild(save);
        var play = new Button { Text = "Play", TooltipText = "Save, then run the game at a developer save on this map" };
        play.Pressed += PlaySelected;
        row.AddChild(play);
        _devSave = new OptionButton { TooltipText = "The developer save to start at" };
        row.AddChild(_devSave);
        _playNote = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_playNote);
        // the open map's parts
        col.AddChild(new HSeparator());
        row = new HBoxContainer();
        col.AddChild(row);
        row.AddChild(new Label { Text = "Parts", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _partsChangedOnly = new CheckBox { Text = "Changed only", ButtonPressed = true };
        _partsChangedOnly.Toggled += _ => FillParts(force: true);
        row.AddChild(_partsChangedOnly);
        var revert = new Button { Text = "Revert", TooltipText = "Put the selected part back as the classic import has it" };
        revert.Pressed += RevertSelectedPart;
        row.AddChild(revert);
        row = new HBoxContainer();
        col.AddChild(row);
        _addKind = new OptionButton { TooltipText = "What to add" };
        foreach (var k in RemasterPlugin.Primitives)
            _addKind.AddItem(k);
        row.AddChild(_addKind);
        var add = new Button { Text = "Add at selection", TooltipText = "Add it at the selected part (anchored: it follows the part in the game), else at the map's root" };
        add.Pressed += AddAtSelection;
        row.AddChild(add);
        _parts = new Tree { Columns = 2, HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 160) };
        _parts.SetColumnExpand(1, false);
        _parts.SetColumnCustomMinimumWidth(1, 150);
        _parts.ItemSelected += OnPartSelected;
        col.AddChild(_parts);
        _message = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_message);
    }

    public override void _Process(double delta)
    {
        _sinceRefresh += delta;
        if (_sinceRefresh < 0.5 || !IsVisibleInTree())
            return;
        _sinceRefresh = 0;
        FillParts();
    }

    /// <summary>Shows a message at the dock's foot (and in the log).</summary>
    public void Say(string text)
    {
        _message.Text = text;
        GD.Print($"ffx remaster: {text}");
    }

    // ---- the archive and the catalogue --------------------------------------------------------

    public void ArchiveChanged()
    {
        var s = _plugin.Session;
        _archive.Text = s.IsOpen
            ? $"{s.GameDir} ({s.Source}): {s.Catalogue?.Levels.Count ?? 0} maps, {s.DevSaves.Length} developer saves"
            : s.Error.Length > 0 ? s.Error : "No archive open.";
        FillMaps();
    }

    private void ChooseFolder()
    {
        _dirDialog ??= new EditorFileDialog
        {
            FileMode = EditorFileDialog.FileModeEnum.OpenDir,
            Access = EditorFileDialog.AccessEnum.Filesystem,
            Title = "The game folder (with FFX.exe and data/)",
        };
        if (_dirDialog.GetParent() == null)
        {
            AddChild(_dirDialog);
            _dirDialog.DirSelected += dir =>
            {
                EditorInterface.Singleton.GetEditorSettings().SetSetting(EditorClassic.GameDirSetting, dir);
                _plugin.OpenArchive();
            };
        }
        _dirDialog.PopupCentered(new Vector2I(800, 500));
    }

    /// <summary>Whether <paramref name="e"/> matches the search text (its names in every language, level, key, events).</summary>
    public static bool Matches(MapEntry e, string q)
    {
        if (q.Length == 0)
            return true;
        bool Has(string? s) => s != null && s.Contains(q, System.StringComparison.OrdinalIgnoreCase);
        return Has(e.Key) || Has(e.Level) || e.Place.Values.Any(Has) || e.Events.Any(v => Has(v.Name) || Has($"{v.Map:x}"));
    }

    /// <summary>Sets the search text, the language and the changed filter (null: as they are) and lists again.</summary>
    public void SetFilter(string? search, string? language, bool? changedOnly)
    {
        if (search != null)
            _search.Text = search;
        if (language != null && System.Array.IndexOf(LanguageNames, language) is >= 0 and var i)
            _language.Selected = i;
        if (changedOnly != null)
            _changedOnly.SetPressedNoSignal(changedOnly.Value);
        FillMaps();
    }

    /// <summary>Lists the catalogue under its areas with the filters.</summary>
    public void FillMaps()
    {
        _maps.Clear();
        Listed.Clear();
        var cat = _plugin.Session.Catalogue;
        if (cat == null)
            return;
        var root = _maps.CreateItem();
        var lang = Language;
        var q = _search.Text.Trim();
        bool changedOnly = _changedOnly.ButtonPressed;
        var byArea = cat.Levels.GroupBy(l => l.Area).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var area in cat.Areas.Select(a => a.Code).Concat(byArea.Keys.Where(k => cat.Areas.All(a => a.Code != k))).Distinct())
        {
            if (!byArea.TryGetValue(area, out var levels))
                continue;
            var shown = new List<(MapEntry E, DockState S)>();
            foreach (var e in levels)
            {
                if (!Matches(e, q))
                    continue;
                var st = DockState.Of(e);
                if (changedOnly && !st.Changed)
                    continue;
                shown.Add((e, st));
            }
            if (shown.Count == 0)
                continue;
            var a = cat.Areas.FirstOrDefault(x => x.Code == area);
            var title = a != null && a.Title.TryGetValue(lang, out var t) ? t : a?.Title.GetValueOrDefault("us") ?? area;
            var item = _maps.CreateItem(root);
            item.SetText(0, $"{title} ({shown.Count})");
            item.SetText(1, area);
            item.SetSelectable(0, false);
            item.SetSelectable(1, false);
            item.SetSelectable(2, false);
            item.Collapsed = q.Length == 0 && !changedOnly;
            foreach (var (e, st) in shown)
            {
                var m = _maps.CreateItem(item);
                m.SetText(0, e.Title(lang) + (e.Model3D == null && e.Kind == "field" ? " (2D)" : ""));
                m.SetTooltipText(0, $"{e.Level}\n{string.Join(", ", e.Events.Select(v => $"{v.Name} (map 0x{v.Map:x})"))}");
                m.SetText(1, e.Key);
                m.SetText(2, st.Effective.ToString() + (st.Marks.Length > 0 ? $" · {st.Marks}" : ""));
                m.SetCustomColor(2, StatusColour(st.Effective));
                m.SetMetadata(0, e.Key);
                Listed.Add(e);
                if (_selected?.Key == e.Key)
                    m.Select(0);
            }
        }
    }

    private static Color StatusColour(MapStatus s) => s switch
    {
        MapStatus.InProgress => new Color(0.95f, 0.75f, 0.3f),
        MapStatus.Remastered => new Color(0.45f, 0.75f, 1f),
        MapStatus.Approved => new Color(0.45f, 0.9f, 0.5f),
        _ => new Color(0.7f, 0.7f, 0.7f),
    };

    private void OnMapSelected()
    {
        if (_maps.GetSelected() is { } item && item.GetMetadata(0).AsString() is { Length: > 0 } key
            && _plugin.Session.Catalogue?.Find(key) is { } e)
            Select(e);
    }

    /// <summary>Shows <paramref name="e"/> in the map panel.</summary>
    public void Select(MapEntry e)
    {
        _selected = e;
        var st = DockState.Of(e);
        _filling = true;
        _status.Selected = (int)st.Status;
        _renderPath.Selected = (int)st.RenderPath;
        _notes.Text = st.Notes;
        _filling = false;
        var lang = Language;
        _mapInfo.Text = $"{e.Key}: {e.Title(lang)} ({e.Level}, {e.Kind}{(e.Model3D == null ? ", 2D layers only" : "")})\n"
            + $"events: {string.Join(", ", e.Events.Select(v => $"{v.Name} (map 0x{v.Map:x})"))}\n"
            + $"effective status {st.Effective}{(st.Marks.Length > 0 ? $"; {st.Marks}" : "")}";
        var plan = _plugin.Session.PlanFor(e);
        _devSave.Clear();
        foreach (var n in plan.Saves)
            _devSave.AddItem($"save {n}", n);
        _playNote.Text = plan.Note;
    }

    private void StateEdited()
    {
        if (_filling || _selected == null)
            return;
        _plugin.SetMapState(_selected.Key, (MapStatus)_status.Selected, (RenderPath)_renderPath.Selected, _notes.Text);
    }

    /// <summary>The maps' files changed (a save, a status): the list and the panel again.</summary>
    public void MapsChanged()
    {
        FillMaps();
        if (_selected != null)
            Select(_selected);
    }

    public string? OpenSelected()
    {
        if (_selected == null)
            return "no map selected";
        var err = _plugin.OpenMap(_selected);
        Say(err ?? $"opening {_selected.Key} ({_selected.Title(Language)})");
        return err;
    }

    private void SaveCurrent()
    {
        if (_plugin.SaveMap() is { } r)
            Say(r.Ok ? $"saved: {r.Parts} part entr(ies) ({r.Moved} moved, {r.Hidden} hidden, {r.Removed} removed, {r.Replaced} replaced), {r.Added} added"
                : "the save failed (see the log)");
        else
            Say("no map open (open one first)");
    }

    private void PlaySelected()
    {
        if (_selected == null)
            return;
        int save = _devSave.ItemCount > 0 ? _devSave.GetItemId(_devSave.Selected) : 0;
        Say(save > 0 ? _plugin.Play(save) : _plugin.Session.PlanFor(_selected).Note);
    }

    // ---- the open map's parts -----------------------------------------------------------------

    public void CurrentChanged()
    {
        if (_plugin.Current is { } root && _plugin.Session.Catalogue?.Find(root.Key) is { } e && _selected?.Key != e.Key)
            Select(e);
        FillParts(force: true);
    }

    /// <summary>The open map's parts and added nodes with their marks (again only when something changed).</summary>
    public void FillParts(bool force = false)
    {
        var root = _plugin.Current;
        if (root == null || !root.Built)
        {
            if (_partsSignature != "")
                _parts.Clear();
            _partsSignature = "";
            if (root != null)
                _message.Text = root.Status;
            return;
        }
        bool changedOnly = _partsChangedOnly.ButtonPressed;
        var rows = new List<(string Id, string Text, string Marks, Node? Node)>();
        foreach (var p in root.Order)
        {
            var m = root.MarksOf(p);
            if (changedOnly && m == PartMarks.None)
                continue;
            if (root.IsGone(p) && !root.IsRemoved(p))
                continue;
            rows.Add((p.Id, p.Short(root.Model), m == PartMarks.None ? "" : m.ToString().ToLowerInvariant(), root.IsGone(p) ? null : p.Node));
        }
        foreach (var (n, anchor) in root.Added())
            rows.Add(($"+{n.Name}", $"+ {n.Name}", "added" + (anchor != null ? $" at {anchor.Short(root.Model)}" : ""), n));
        var sig = string.Join("|", rows.Select(r => r.Id + ":" + r.Marks)) + changedOnly;
        if (!force && sig == _partsSignature)
            return;
        _partsSignature = sig;
        _parts.Clear();
        var top = _parts.CreateItem();
        foreach (var (id, text, marks, node) in rows)
        {
            var it = _parts.CreateItem(top);
            it.SetText(0, text);
            it.SetTooltipText(0, id);
            it.SetText(1, marks);
            it.SetMetadata(0, id);
        }
        _message.Text = $"{root.Key}: {root.Order.Count} parts, {rows.Count} listed";
    }

    private (MapEditRoot Root, PartInfo? Part, Node? Node)? PickedRow()
    {
        if (_plugin.Current is not { Built: true } root || _parts.GetSelected() is not { } it)
            return null;
        var id = it.GetMetadata(0).AsString();
        if (id.StartsWith('+'))
            return (root, null, root.Added().FirstOrDefault(a => "+" + a.Node.Name == id).Node);
        return root.Parts.TryGetValue(id, out var p) ? (root, p, root.IsGone(p) ? null : p.Node) : null;
    }

    private void OnPartSelected()
    {
        if (PickedRow() is { Node: { } n })
            RemasterPlugin.Select(n);
    }

    private void RevertSelectedPart()
    {
        var root = _plugin.Current;
        PartInfo? part = PickedRow()?.Part;
        if (part == null && root != null && EditorInterface.Singleton.GetSelection().GetSelectedNodes() is { Count: > 0 } sel)
            part = root.PartOf(sel[0]);
        if (root == null || part == null)
        {
            Say("select a part (here or in the scene)");
            return;
        }
        _plugin.RevertPart(root, part);
        FillParts(force: true);
    }

    private void AddAtSelection()
    {
        if (_plugin.Current is not { Built: true } root)
        {
            Say("open a map first");
            return;
        }
        PartInfo? anchor = null;
        if (EditorInterface.Singleton.GetSelection().GetSelectedNodes() is { Count: > 0 } sel)
            anchor = root.PartOf(sel[0]);
        var node = RemasterPlugin.MakePrimitive(_addKind.GetItemText(_addKind.Selected));
        _plugin.AddNode(root, node, anchor, RemasterPlugin.PlaceAt(root, anchor));
        Say($"added {node.Name}" + (anchor != null ? $" at {anchor.Short(root.Model)}" : " at the map's root"));
    }
}
#endif
