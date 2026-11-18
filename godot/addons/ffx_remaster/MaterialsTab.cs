#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using GdFfx.Remaster.Materialize;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The <b>Materials</b> tab (docs/remaster-materials.md; R2b phase 1): the material workbench in
/// Godot's main area, beside 2D, 3D, Script and Game. The queue of textures (left; arrow keys or
/// a click switch texture and keep the settings), the big 3D preview (centre:
/// <see cref="MaterialPreview"/>), the current step's map in 2D and only that step's settings
/// (right: <see cref="MapView2D"/>), the strip of every step's map (bottom) and Save.
/// <para>The maps are made on the GPU (<see cref="GpuPipeline"/>, R2b phase 2: the passes'
/// targets are the preview's textures; a change draws the passes it touches in the next frame,
/// the occlusion settles over a few frames) when there is a renderer, else on the CPU
/// (<see cref="MaterialPipeline"/> off the main thread: a change asks for every map at 256 px,
/// then at the working size); only the passes downstream of the change run again. Settings live in a <see cref="PbrRecipe"/> (<see cref="Recipe"/>); every change is
/// one step of the editor's global undo history (a slider drag: one step). Save writes the maps
/// and the material into R2's storage (<see cref="TextureStore"/>: content-named files) as one
/// undo step, the settings on the texture's entry.</para>
/// </summary>
[Tool]
public partial class MaterialsTab : VBoxContainer
{
    private readonly RemasterPlugin _plugin;

    // the queue
    private ItemList _queueList = null!;
    private Label _queueTitle = null!;
    private Label _status = null!;
    // the preview
    private MaterialPreview _preview = null!;
    private OptionButton _shape = null!, _compare = null!, _light = null!, _hdri = null!;
    private SpinBox _tile = null!;
    private HSlider _wipe = null!;
    // the step panel
    private Label _stepTitle = null!, _stepNote = null!, _checkLabel = null!;
    private MapView2D _map2d = null!;
    private CheckBox _tiled = null!, _split = null!;
    private OptionButton _mode = null!;
    private Button _importButton = null!;
    private VBoxContainer _settingsBox = null!;
    private readonly Dictionary<string, Control> _controls = new();
    private EditorFileDialog? _fileDialog;
    // the strip
    private readonly Dictionary<MaterialStep, (Button Button, TextureRect Thumb, Control Col)> _strip = new();
    private MenuButton _addMap = null!;
    private Button _removeMap = null!;
    private Button _save = null!;
    private Label _timing = null!;
    private TextureRect _lastMade = null!;
    private Label _lastMadeLabel = null!;
    /// <summary>Apply to queue's results by texture: a small albedo (the queue's icons).</summary>
    private readonly Dictionary<string, Texture2D> _thumbs = new();

    /// <summary>The textures being worked on (IDs).</summary>
    public List<string> Queue { get; } = new();

    /// <summary>The texture shown (null: none).</summary>
    public string? Current { get; private set; }

    public MaterialStep Step { get; private set; } = MaterialStep.Albedo;

    /// <summary>
    /// The settings the current texture is shown and made with: the queue's
    /// (<see cref="QueueSettings"/>, kept when the texture changes) with the texture's own
    /// exceptions over them (<see cref="ExceptionsOf"/>). Rebuilt by <see cref="Recompute"/>.
    /// </summary>
    public PbrRecipe Recipe { get; private set; } = new() { Version = 3 };

    /// <summary>The queue's settings (docs/remaster-materials.md, "Several textures"): what a change edits unless the setting is pinned to the texture.</summary>
    public PbrRecipe QueueSettings { get; private set; } = new() { Version = 3 };

    /// <summary>The profile the queue's settings came from (its name; empty: none).</summary>
    public string ProfileName { get; private set; } = "";

    /// <summary>Each texture's exceptions: the settings pinned to it (setting → value).</summary>
    private readonly Dictionary<string, Godot.Collections.Dictionary> _exceptions = new();

    /// <summary>The queue's value of each setting before its last change (Pin gives the queue that back).</summary>
    private readonly Dictionary<string, Variant> _prior = new();

    public MaterialPreview Preview => _preview;

    /// <summary>The source colour texture as it is (the raw side of the compare).</summary>
    public Texture2D SourceTexture => _sourceTex;

    private RgbaImage? _original;
    private readonly ImageTexture _sourceTex = new();
    private readonly MaterialPipeline _pipe = new();
    /// <summary>The GPU's pipeline (null: no renderer, or the editor setting <see cref="GpuSetting"/> off).</summary>
    private GpuPipeline? _gpu;
    private int _run = -1;
    private readonly Dictionary<MaterialStep, Texture2D> _maps = new();
    private readonly Dictionary<MaterialStep, ImageTexture> _cpuTex = new();
    private readonly Dictionary<MaterialStep, RgbaImage> _pixels = new();
    private readonly Dictionary<MaterialStep, int> _shownSide = new();
    private readonly ImageTexture _orm = new();
    private readonly ImageTexture _maskCpu = new();
    /// <summary>The CPU's aux maps (the detail's albedo and normal).</summary>
    private readonly Dictionary<string, ImageTexture> _auxCpu = new();
    private RgbaImage? _ormPixels;
    private float[]? _heightFloat;
    /// <summary>The colour sample shown as a mask in the 2D view (the isolate view), or null.</summary>
    private (MaterialStep Step, int Sample)? _isolate;
    private bool _maskShown;
    /// <summary>The colour setting the next click in the 2D view picks.</summary>
    private string? _pickFor;
    private readonly Dictionary<string, RgbaImage> _imports = new();
    private readonly HashSet<string> _savedHere = new();
    private int _alpha; // 0 opaque, 1 cut-out, 2 blended
    private readonly OrmMaterial3D _mat = new();
    private readonly StandardMaterial3D _raw = new() { Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
    private readonly StandardMaterial3D _unshaded = new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
    private bool _runDone;
    private int _workingSide;

    /// <summary>The last run's latency (ms from the change): the focused step's map at 256 px, at the working size, and every map at the working size.</summary>
    public (double Small, double Refined, double All) Latency { get; private set; }

    /// <summary>Whether the maps asked for last are all shown.</summary>
    public bool Idle => _runDone && !_pipe.Busy && _pipe.Results.IsEmpty && _saveRun < 0 && _bake == null;

    /// <summary>The order the last run's maps arrived in (side, step): the checks see small before refined.</summary>
    public List<(int Side, MaterialStep? Step)> Arrivals { get; } = new();

    public MaterialsTab(RemasterPlugin plugin)
    {
        _plugin = plugin;
        Name = "FfxMaterials";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        Build();
        if (GpuBackend.Available && UseGpuSetting())
            _gpu = new GpuPipeline(this);
    }

    /// <summary>Whether the maps are made on the GPU (else the CPU).</summary>
    public bool UsingGpu => _gpu != null;

    /// <summary>The GPU's pipeline (the checks), null on the CPU.</summary>
    public GpuPipeline? Gpu => _gpu;

    /// <summary>Makes the maps on the CPU from now on (the checks compare), or on the GPU again.</summary>
    public void UseGpu(bool on)
    {
        if (on && _gpu == null && GpuBackend.Available)
            _gpu = new GpuPipeline(this);
        else if (!on && _gpu != null)
        {
            _gpu.Backend.Clear();
            _gpu.Backend.QueueFree();
            _gpu = null;
        }
        _maps.Clear();
        _pixels.Clear();
        _shownSide.Clear();
        Remake();
    }

    public MaterialsTab() : this(null!) { }

    // ---- layout ----------------------------------------------------------------------------------

    private void Build()
    {
        var main = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(main);
        main.AddChild(BuildQueue());
        var right = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        main.AddChild(right);
        right.AddChild(BuildPreview());
        right.AddChild(BuildStepPanel());
        AddChild(new HSeparator());
        AddChild(BuildStrip());
        RebuildSettings();
    }

    private Control BuildQueue()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(230, 0) };
        _queueTitle = new Label { Text = "Queue (0)", ThemeTypeVariation = "HeaderSmall" };
        col.AddChild(_queueTitle);
        _queueList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, SelectMode = ItemList.SelectModeEnum.Single };
        _queueList.ItemSelected += i => Open(Queue[(int)i]);
        col.AddChild(_queueList);
        var row = new HBoxContainer();
        col.AddChild(row);
        Add(row, "+ Open map", "Add every texture the open map draws", () => Say($"{AddOpenMap()} texture(s) added"));
        Add(row, "+ Selected", "Add the textures selected in the Textures dock", () => Say($"{Add(_plugin.Textures?.SelectedIds() ?? [])} texture(s) added"));
        row = new HBoxContainer();
        col.AddChild(row);
        Add(row, "Remove", "Take the selected texture out of the queue", RemoveCurrent);
        Add(row, "Its settings", "Load the selected texture's saved settings (as it was made, and its profile) as the queue's (one undo step)", () => LoadSaved());
        col.AddChild(BuildProfileBox());
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 60) };
        col.AddChild(_status);
        return col;
    }

    // the profile box (R2b phase 3)
    private OptionButton _profilePick = null!;
    private Label _profileState = null!, _exceptionsLabel = null!, _staleLabel = null!;
    private Button _makeAgain = null!, _applyButton = null!, _cancelButton = null!;
    private ProgressBar _progress = null!;
    private ConfirmationDialog? _nameDialog;

    private Control BuildProfileBox()
    {
        var col = new VBoxContainer();
        col.AddChild(new HSeparator());
        var row = new HBoxContainer();
        col.AddChild(row);
        row.AddChild(new Label { Text = "Profile" });
        _profilePick = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Load a profile's settings as the queue's (one undo step); the later pack's profile wins per name" };
        _profilePick.ItemSelected += i => LoadProfile(_profilePick.GetItemMetadata((int)i).AsString());
        row.AddChild(_profilePick);
        _profileState = new Label { Modulate = new Color(1, 1, 1, 0.7f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_profileState);
        row = new HBoxContainer();
        col.AddChild(row);
        Add(row, "Save", "Write the queue's settings over the profile (one undo step)", () => Say(SaveProfile() ?? $"profile {ProfileName} saved"));
        Add(row, "Save as...", "The queue's settings as a new profile", () => AskName("Save as profile", "", t => Say(SaveAsProfile(t) ?? $"profile {ProfileName} saved")));
        Add(row, "Revert", "The queue's settings back to the profile's (one undo step)", () => Say(RevertToProfile() ?? $"back to {ProfileName}"));
        row = new HBoxContainer();
        col.AddChild(row);
        Add(row, "Rename...", "Rename the profile (its file, and the textures made with it: one undo step)",
            () => AskName("Rename the profile", MaterialProfiles.Find(ProfileName)?.Title ?? ProfileName, t => Say(RenameProfile(t) ?? $"renamed to {ProfileName}")));
        Add(row, "Delete", "Delete the project's profile (one undo step; the textures made with it keep their settings)", () => Say(DeleteProfile() ?? "profile deleted"));
        row = new HBoxContainer();
        col.AddChild(row);
        _exceptionsLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Settings pinned to the current texture (● beside them): made with their own value, not the queue's" };
        row.AddChild(_exceptionsLabel);
        Add(row, "Clear", "Unpin every setting of the current texture: it takes the queue's settings again (one undo step)", () => Say(ClearExceptions() ?? "exceptions cleared"));
        row = new HBoxContainer();
        col.AddChild(row);
        _applyButton = Add(row, "Apply to queue", "Make every texture in the queue with the queue's settings and its own exceptions, at the full size, in the background (one undo step for all)",
            () => Say(ApplyToQueue() ?? "applying to the queue..."));
        _cancelButton = Add(row, "Cancel", "Stop the run: the textures made so far are kept (one undo step)", () => Say(CancelBake() ?? "cancelled"));
        _progress = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = true, CustomMinimumSize = new Vector2(0, 18) };
        col.AddChild(_progress);
        _staleLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1f, 0.85f, 0.5f) };
        col.AddChild(_staleLabel);
        _makeAgain = new Button { Text = "Make again", TooltipText = "Make every texture whose profile changed since it was made again, with the profile as it is now and its own exceptions (background, one undo step)" };
        _makeAgain.Pressed += () => Say(MakeAgainStale() ?? "making again...");
        col.AddChild(_makeAgain);
        return col;
    }

    private void AskName(string title, string start, System.Action<string> then)
    {
        if (_nameDialog == null)
        {
            _nameDialog = new ConfirmationDialog { MinSize = new Vector2I(360, 100) };
            _nameDialog.AddChild(new LineEdit { Name = "Name", PlaceholderText = "Rock, Stone and concrete..." });
            AddChild(_nameDialog);
        }
        var edit = _nameDialog.GetNode<LineEdit>("Name");
        edit.Text = start;
        _nameDialog.Title = title;
        foreach (var c in _nameDialog.GetSignalConnectionList(AcceptDialog.SignalName.Confirmed))
            _nameDialog.Disconnect(AcceptDialog.SignalName.Confirmed, c["callable"].AsCallable());
        _nameDialog.Confirmed += () => then(edit.Text);
        _nameDialog.PopupCentered();
        edit.GrabFocus();
    }

    private Control BuildPreview()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2.2f };
        _preview = new MaterialPreview();
        _preview.PickDrag += PickDragged;
        col.AddChild(_preview);
        var row = new HBoxContainer();
        col.AddChild(row);
        _shape = new OptionButton { TooltipText = "The object: Object is the game piece that draws the texture (from the open map, else the first map that uses it); In map is that map around it, lit by its remastered lighting, with the live material (M)" };
        foreach (var o in MaterialPreview.Objects)
            _shape.AddItem(o);
        _shape.Selected = MaterialPreview.Sphere;
        _shape.ItemSelected += i => SetShape((int)i);
        row.AddChild(_shape);
        row.AddChild(new Label { Text = "Compare" });
        _compare = new OptionButton { TooltipText = "The left side of the wipe: the raw texture lit as a plain colour, or the classic look (the game's own material on the Object)" };
        _compare.AddItem("Raw, lit");
        _compare.AddItem("Classic");
        _compare.ItemSelected += _ => UpdateMaterials();
        row.AddChild(_compare);
        _wipe = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.01, Value = 0.5, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "The wipe: left the compare, right the material (or drag the line in the view)" };
        _wipe.ValueChanged += v => _preview.Wipe = (float)v;
        row.AddChild(_wipe);
        row = new HBoxContainer();
        col.AddChild(row);
        _light = new OptionButton { TooltipText = "Studio: key, fill and rim (Shift+drag turns the key); HDRI; the light of a map that uses the texture (R3's)" };
        _light.AddItem("Studio");
        _light.AddItem("HDRI");
        _light.AddItem("The map's light");
        _light.ItemSelected += _ => UpdateLight();
        row.AddChild(_light);
        _hdri = new OptionButton { TooltipText = "The HDRI (the plugin's CC0 ones, then the editor setting ffx_remaster/hdri_dir's)" };
        foreach (var h in Hdris())
        {
            _hdri.AddItem(h.GetFile().GetBaseName());
            _hdri.SetItemMetadata(_hdri.ItemCount - 1, h);
        }
        _hdri.ItemSelected += _ =>
        {
            _light.Selected = (int)PreviewLight.Hdri;
            UpdateLight();
        };
        row.AddChild(_hdri);
        row.AddChild(new Label { Text = "Tile" });
        _tile = new SpinBox { MinValue = 1, MaxValue = 8, Step = 1, Value = 1, TooltipText = "UV tiling on the primitives" };
        _tile.ValueChanged += _ => UpdateMaterials();
        row.AddChild(_tile);
        Add(row, "Frame", "Frame the object (F in the view)", () => _preview.Frame());
        var hints = new HBoxContainer();
        col.AddChild(hints);
        hints.AddChild(new Label { Text = "Drag: orbit · wheel: zoom · middle: pan · Shift+drag: light · Alt+drag: environment · F: frame · Tab: compare · M: in map", Modulate = new Color(1, 1, 1, 0.6f), SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true });
        var help = new Button { Text = "?", Flat = true, TooltipText = "The tab's shortcuts (?)", FocusMode = FocusModeEnum.None };
        help.Pressed += () => ShowShortcuts();
        hints.AddChild(help);
        return col;
    }

    private Control BuildStepPanel()
    {
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(360, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(col);
        _stepTitle = new Label { ThemeTypeVariation = "HeaderSmall" };
        col.AddChild(_stepTitle);
        _stepNote = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.7f) };
        col.AddChild(_stepNote);
        _map2d = new MapView2D();
        _map2d.PickDrag += PickDragged;
        _map2d.ArrowMoved += a => LiveSet("NormalLightAngle", a);
        _map2d.ArrowReleased += _ => EndDrag("NormalLightAngle");
        col.AddChild(_map2d);
        _checkLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(0.8f, 0.95f, 1f), Visible = false };
        col.AddChild(_checkLabel);
        var row = new HBoxContainer();
        col.AddChild(row);
        _tiled = new CheckBox { Text = "2×2", TooltipText = "Tile the map 2×2: seams show" };
        _tiled.Toggled += on => _map2d.Tiled = on;
        row.AddChild(_tiled);
        _split = new CheckBox { Text = "Before | after", TooltipText = "The source texture left of the split, the map right of it (Shift+move in the view moves the split)" };
        _split.Toggled += on => _map2d.Split = on ? 0.5f : -1f;
        row.AddChild(_split);
        Add(row, "Whole", "Back to the whole map (or double click)", () => _map2d.Reset());
        col.AddChild(BuildPickRadius());
        row = new HBoxContainer();
        col.AddChild(row);
        _mode = new OptionButton { TooltipText = "Generate the map, take a file of your own (Import), or a constant (Skip)" };
        _mode.AddItem("Generate");
        _mode.AddItem("Import");
        _mode.AddItem("Skip");
        _mode.ItemSelected += i =>
        {
            if ((StepMode)(int)i == StepMode.Import)
                ChooseImport(Step);
            else
                SetMode(Step, (StepMode)(int)i);
        };
        row.AddChild(_mode);
        _importButton = Add(row, "Import...", "A map of your own (PNG, WebP, JPEG), resized to the working size", () => ChooseImport(Step));
        Add(row, "Reset step", "Every setting of this step back to its default, the mode too (one undo step)", () => ResetStep(Step));
        _removeMap = Add(row, "Remove map", "Take this map out of the material (its settings are kept; one undo step)", () => Say(RemoveMap(Step) ?? ""));
        col.AddChild(new HSeparator());
        _settingsBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddChild(_settingsBox);
        return scroll;
    }

    private Control BuildStrip()
    {
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 118), VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var row = new HBoxContainer();
        scroll.AddChild(row);
        var group = new ButtonGroup();
        foreach (var s in MaterialPipeline.Steps)
        {
            if (s == MaterialPipeline.Extras[0])
            {
                // the maps added per profile ("+ map": the strip stays short)
                _addMap = new MenuButton { Text = "+ map", Flat = false, TooltipText = "Add a map to the material: bent normal, rim, clearcoat, anisotropy, subsurface, transmittance, backlight, refraction, detail (kept in the profile)" };
                _addMap.GetPopup().IdPressed += id => Say(AddMap((MaterialStep)(int)id) ?? "");
                row.AddChild(_addMap);
            }
            var col = new VBoxContainer();
            var thumb = new TextureRect
            {
                CustomMinimumSize = new Vector2(84, 72), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore,
            };
            col.AddChild(thumb);
            var b = new Button { ToggleMode = true, ButtonGroup = group, Text = $"{(int)s} {s}", TooltipText = $"Go to the {s} step" };
            var step = s;
            b.Pressed += () => SelectStep(step);
            col.AddChild(b);
            row.AddChild(col);
            _strip[s] = (b, thumb, col);
        }
        // "+ map" after the added maps
        row.MoveChild(_addMap, row.GetChildCount() - 1);
        row.AddChild(new VSeparator());
        var end = new VBoxContainer();
        row.AddChild(end);
        _save = new Button { Text = "Save", TooltipText = "Make the maps at the full size and write them and the material into the project's texture layer (one undo step)", CustomMinimumSize = new Vector2(110, 40) };
        _save.Pressed += () => Say(Save() ?? "saving...");
        end.AddChild(_save);
        _timing = new Label { Modulate = new Color(1, 1, 1, 0.7f) };
        end.AddChild(_timing);
        // Apply to queue's results as they come: the last one made, and how far the run is
        var made = new VBoxContainer();
        row.AddChild(made);
        _lastMade = new TextureRect
        {
            CustomMinimumSize = new Vector2(84, 72), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore,
        };
        made.AddChild(_lastMade);
        _lastMadeLabel = new Label { Modulate = new Color(1, 1, 1, 0.7f) };
        made.AddChild(_lastMadeLabel);
        return scroll;
    }

    private static Button Add(HBoxContainer row, string text, string tip, System.Action act)
    {
        var b = new Button { Text = text, TooltipText = tip };
        b.Pressed += act;
        row.AddChild(b);
        return b;
    }

    public void Say(string text)
    {
        if (text.Length > 0)
            GD.Print($"ffx remaster: materials: {text}");
        _status.Text = text;
    }

    // ---- HDRIs -----------------------------------------------------------------------------------

    public const string HdriDir = "res://addons/ffx_remaster/hdri";
    public const string HdriSetting = "ffx_remaster/hdri_dir";

    /// <summary>The editor setting: make the maps on the GPU (default on; off: the CPU, as headless).</summary>
    public const string GpuSetting = "ffx_remaster/materials_gpu";

    private static bool UseGpuSetting()
    {
        var es = EditorInterface.Singleton.GetEditorSettings();
        return !es.HasSetting(GpuSetting) || es.GetSetting(GpuSetting).AsBool();
    }

    /// <summary>The editor setting for a folder of one's own HDRIs (registered once).</summary>
    public static void RegisterSettings()
    {
        var es = EditorInterface.Singleton.GetEditorSettings();
        if (!es.HasSetting(HdriSetting))
            es.SetSetting(HdriSetting, "");
        es.AddPropertyInfo(new Godot.Collections.Dictionary
        {
            ["name"] = HdriSetting,
            ["type"] = (int)Variant.Type.String,
            ["hint"] = (int)PropertyHint.GlobalDir,
        });
        if (!es.HasSetting(GpuSetting))
            es.SetSetting(GpuSetting, true);
        es.AddPropertyInfo(new Godot.Collections.Dictionary { ["name"] = GpuSetting, ["type"] = (int)Variant.Type.Bool });
    }

    /// <summary>The HDRIs: the plugin's (CC0, from Poly Haven), then the editor setting's folder's.</summary>
    public static List<string> Hdris()
    {
        var r = new List<string>();
        static IEnumerable<string> In(string dir) => DirAccess.DirExistsAbsolute(dir)
            ? DirAccess.GetFilesAt(dir).Where(f => f.EndsWith(".hdr", System.StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exr", System.StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, System.StringComparer.Ordinal).Select(f => dir.TrimEnd('/') + "/" + f)
            : [];
        r.AddRange(In(HdriDir));
        var es = EditorInterface.Singleton.GetEditorSettings();
        if (es.HasSetting(HdriSetting) && es.GetSetting(HdriSetting).AsString() is { Length: > 0 } own)
            r.AddRange(In(own));
        return r;
    }

    // ---- the queue -------------------------------------------------------------------------------

    /// <summary>Adds textures to the queue (those not in it); opens the first when nothing is open. Returns how many were added.</summary>
    public int Add(IEnumerable<string> ids)
    {
        int n = 0;
        string? first = null;
        foreach (var raw in ids)
        {
            var id = raw.ToLowerInvariant();
            if (id.Length == 0 || Queue.Contains(id))
                continue;
            Queue.Add(id);
            first ??= id;
            n++;
        }
        FillQueue();
        if (Current == null && first != null)
            Open(first);
        return n;
    }

    /// <summary>Adds every texture the open map draws.</summary>
    public int AddOpenMap() => Add(TextureActions.OnMap(_plugin.Current).Keys);

    /// <summary>Takes <paramref name="id"/> out of the queue (the current one: the next shown).</summary>
    public void RemoveFromQueue(string id)
    {
        id = id.ToLowerInvariant();
        if (id == Current)
            RemoveCurrent();
        else if (Queue.Remove(id))
            FillQueue();
    }

    private void RemoveCurrent()
    {
        if (Current == null)
            return;
        int i = Queue.IndexOf(Current);
        Queue.Remove(Current);
        Current = null;
        FillQueue();
        if (Queue.Count > 0)
            Open(Queue[Mathf.Clamp(i, 0, Queue.Count - 1)]);
    }

    private void FillQueue()
    {
        _queueList.Clear();
        foreach (var id in Queue)
        {
            var marks = new List<string>();
            var own = TextureStore.Find(id);
            var state = _bakeRun?.StateOf(id);
            if (state is { } st && st != QueueBake.JobState.Done) marks.Add(st == QueueBake.JobState.Making ? "making…" : st.ToString().ToLowerInvariant());
            else if (state == QueueBake.JobState.Done) marks.Add("made");
            else if (_savedHere.Contains(id)) marks.Add("saved");
            else if (RemasterTextures.For(id)?.HasMaterial == true) marks.Add("PBR");
            if (MaterialProfiles.Stale(own)) marks.Add($"⟳ {MaterialProfiles.Find(own!.Profile)?.Title ?? own.Profile} changed");
            else if (own is { Profile.Length: > 0, Material.Length: > 0 }) marks.Add(own.Profile);
            if (MaterialProfiles.Missing(own)) marks.Add($"{own!.Profile}: no such profile");
            if (RemasterTextures.For(id)?.Replaced == true) marks.Add("replaced");
            int pinned = _exceptions.TryGetValue(id, out var ex) ? ex.Count : own is { Profile.Length: > 0 } ? own.Exceptions.Count : 0;
            if (pinned > 0) marks.Add($"● {pinned}");
            int i = _queueList.AddItem((id == Current ? "▶ " : "   ") + RemasterTextures.ShortName(id) + (marks.Count > 0 ? $"  ({string.Join(", ", marks)})" : ""),
                _thumbs.GetValueOrDefault(id));
            _queueList.SetItemTooltip(i, id + (pinned > 0 ? $"\n● {pinned} setting(s) pinned to it (exceptions)" : ""));
            if (id == Current)
                _queueList.Select(i);
        }
        _queueTitle.Text = $"Queue ({Queue.Count})";
    }

    /// <summary>The next (or previous) texture of the queue, the settings kept.</summary>
    public void Next(int by)
    {
        if (Queue.Count == 0)
            return;
        int i = Current == null ? 0 : Queue.IndexOf(Current) + by;
        Open(Queue[((i % Queue.Count) + Queue.Count) % Queue.Count]);
    }

    /// <summary>Shows <paramref name="id"/> (the settings kept): its pixels read here (the extension answers on the main thread), the maps made again.</summary>
    public string? Open(string id)
    {
        id = id.ToLowerInvariant();
        if (!Queue.Contains(id))
            Queue.Add(id);
        if (TextureActions.Source(id) is not { } img)
        {
            Say($"{RemasterTextures.ShortName(id)}: no pixels (the archive is not open?)");
            return "no pixels";
        }
        Current = id;
        Recompute();
        _original = PbrBuilder.From(img);
        _alpha = !_original.HasAlpha ? 0 : _original.HasOnlyBinaryAlpha ? 1 : 2;
        _sourceTex.SetImage(PbrBuilder.ToImage(_original));
        foreach (var t in _cpuTex.Values)
            t.SetImage(Image.CreateEmpty(1, 1, false, Image.Format.Rgba8));
        _pixels.Clear();
        _shownSide.Clear();
        FillQueue();
        RebuildSettings();
        RefreshProfileBox();
        FindObject(id);
        // the In map view follows the texture (its map found again)
        if (_shape.Selected == MaterialPreview.InMap)
            EnterInMap();
        UpdateLight();
        Remake();
        UpdateMaterials();
        Say($"{RemasterTextures.ShortName(id)}: {_original.Width}×{_original.Height}{(_alpha == 1 ? ", cut-out" : _alpha == 2 ? ", blended alpha" : "")}");
        return null;
    }

    /// <summary>
    /// The current texture's saved settings as the queue's (one undo step): its entry's recipe as
    /// baked (an R2-era one migrated: <see cref="MaterialPipeline.Migrate"/>), and its profile.
    /// </summary>
    public string? LoadSaved()
    {
        if (Current == null || TextureStore.Find(Current) is not { Recipe: { } r } entry)
        {
            Say("no saved settings for this texture");
            return "no saved settings";
        }
        var copy = (PbrRecipe)r.Duplicate();
        MaterialPipeline.Migrate(copy);
        var d = new Godot.Collections.Dictionary();
        foreach (var name in RecipeProps())
            d[name] = copy.Get(name);
        d[ProfileKey] = entry.Profile;
        Commit($"Load the saved settings of {RemasterTextures.ShortName(Current)}", d, queueOnly: true);
        Say($"{RemasterTextures.ShortName(Current)}: its saved settings{(entry.Profile.Length > 0 ? $" (profile {entry.Profile})" : "")}"
            + (r.Version < 3 ? $", migrated from version {r.Version}" : ""));
        return null;
    }

    /// <summary>Every setting the tab edits from <paramref name="r"/> (one undo step).</summary>
    public void SetRecipe(PbrRecipe r, string action)
    {
        var d = new Godot.Collections.Dictionary();
        foreach (var name in RecipeProps())
            d[name] = r.Get(name);
        Commit(action, d);
    }

    // ---- the game piece and the map's light ------------------------------------------------------

    private string? _objectLevel;
    private string? _objectFor;

    private void FindObject(string id)
    {
        _preview.ClearObject();
        (_objectLevel, _objectFor) = (null, id);
        if (_plugin.Current is { Built: true, Map: { } map } && PreviewObject.Extract(map, id) is { } o)
        {
            _preview.SetShape(_shape.Selected, o.Mesh, o.Classic);
            UpdateMaterials();
            return;
        }
        var use = _plugin.Uses.Uses(id).FirstOrDefault();
        _objectLevel = use != null ? _plugin.Session.Catalogue?.Find(use.Level)?.Level : null;
        if (_shape.Selected == MaterialPreview.Object && _objectLevel == null)
            Say("no game piece known for this texture (the where-used index has none)");
    }

    /// <summary>Loads the game piece from the first map that uses the texture, once the classic import has it.</summary>
    private void PollObject()
    {
        if (_objectLevel == null || _objectFor != Current || _plugin.Session.Classic is not { } classic)
            return;
        if (classic.Path("map", _objectLevel) is not { } path)
        {
            if (classic.Failed("map", _objectLevel))
                _objectLevel = null;
            return;
        }
        var level = _objectLevel;
        _objectLevel = null;
        if (ClassicLibrary.Load(path, $"PS3Data{level}tex/") is not { } map)
            return;
        var o = PreviewObject.Extract(map, Current!);
        map.Free();
        if (o is { } got)
        {
            _preview.SetShape(_shape.Selected, got.Mesh, got.Classic);
            UpdateMaterials();
            GD.Print($"ffx remaster: materials: the game piece of {RemasterTextures.ShortName(Current!)} from {level} ({got.Surfaces} surface(s))");
        }
    }

    private void SetShape(int shape)
    {
        if (shape == MaterialPreview.InMap)
            EnterInMap();
        if (shape == MaterialPreview.Object && !_preview.HasObject)
            Say(_objectLevel != null ? "loading the game piece..." : "no game piece known for this texture: a sphere");
        _preview.SetShape(shape, null, null);
        UpdateMaterials();
    }

    /// <summary>The map whose light the preview takes: the open map when it draws the texture, else the first that uses it.</summary>
    public MapLight? MapLightFor(string id)
    {
        string? level = null;
        MapLighting? lighting = null;
        if (_plugin.Current is { Built: true } root && _plugin.Uses.Uses(id).Any(u => u.Level == root.Key))
            (level, lighting) = (root.Level, root.Lighting?.Res);
        level ??= _plugin.Uses.Uses(id).FirstOrDefault() is { } u ? _plugin.Session.Catalogue?.Find(u.Level)?.Level ?? u.Level : null;
        if (level == null && _plugin.Current is { Built: true } open)
            (level, lighting) = (open.Level, open.Lighting?.Res);
        if (level == null)
            return null;
        lighting ??= LightingLayers.For(level).Lighting;
        var look = LookCache.Read(level);
        bool fromGame = look is { Valid: true };
        look ??= new GdFfx.Api.Look { Valid = true, CharacterAmbient = new Vector3(0.34f, 0.34f, 0.45f), Background = new Vector3(0.3f, 0.35f, 0.5f) };
        var resolved = RemasterLighting.Resolve(lighting, look);
        var bg = new Color(Mathf.Clamp(look.Background.X, 0, 1), Mathf.Clamp(look.Background.Y, 0, 1), Mathf.Clamp(look.Background.Z, 0, 1)).SrgbToLinear();
        return new MapLight(level, resolved, lighting, bg, fromGame);
    }

    private void UpdateLight()
    {
        var light = (PreviewLight)_light.Selected;
        string hdri = _hdri.ItemCount > 0 && _hdri.Selected >= 0 ? _hdri.GetItemMetadata(_hdri.Selected).AsString() : "";
        MapLight? map = light == PreviewLight.Map && Current != null ? MapLightFor(Current) : null;
        _preview.SetLight(light, hdri, map);
        if (light == PreviewLight.Map)
            Say(map == null ? "no map known for this texture: the studio light"
                : $"the light of {PartIds.LevelKey(map.Level)}" + (map.FromGame ? "" : " (the game has not shown it here yet: the defaults' direction; play it once)"));
    }

    // ---- steps -----------------------------------------------------------------------------------

    /// <summary>Goes to step <paramref name="s"/>: its map shown (made with the current settings when it is not yet), its settings in the panel.</summary>
    public void SelectStep(MaterialStep s)
    {
        Step = s;
        _strip[s].Button.SetPressedNoSignal(true);
        RebuildSettings();
        ShowStep();
        if (!_pixels.ContainsKey(s) || _shownSide.GetValueOrDefault(s) < _workingSide)
            Remake();
    }

    private static readonly Dictionary<MaterialStep, string> Notes = new()
    {
        [MaterialStep.Source] = "The colour texture the maps start from (the replacement, else the classic one), upscaled; or an image of your own (Import). The seam tool makes a texture that does not tile tile (every map follows; check with 2×2).",
        [MaterialStep.Albedo] = "R2's de-lighting: the painted light out of the colour, hot and dark spots evened, the colour checked against the classic texture. Skip: the source as it is.",
        [MaterialStep.Height] = "From the albedo (or the source): seven bands of its brightness, fine to coarse, each a weight and a contrast; colour samples set their colour's height. Skip: flat (no parallax).",
        [MaterialStep.Normal] = "The height's slope in seven bands (crisp to smooth), shape from the painted shading, angularity; OpenGL convention (green up, as Godot reads it). Skip: flat.",
        [MaterialStep.Roughness] = "From the albedo: darker and busier is rougher; colour samples set their colour's roughness, metal its own, worn edges smoother. Skip: the base value everywhere.",
        [MaterialStep.Metallic] = "Most FFX textures skip it (the value everywhere). Generate: a colour mask (pick the metal) or a mask from brightness.",
        [MaterialStep.Occlusion] = "Horizon occlusion from the height and the normals over many directions (the preview adds four a frame). Skip: none (white).",
        [MaterialStep.Emission] = "Usually skipped. Generate: the albedo where its brightest channel passes the threshold.",
        [MaterialStep.BentNormal] = "The mean open direction (cosine-weighted), from the occlusion's directions (its radius, depth and directions; progressive as the occlusion): better occlusion of the sky's light. Skip: flat.",
        [MaterialStep.Rim] = "Soft light at grazing angles (cloth, velvet, moss): a mask (everywhere, by brightness, a picked colour, height or tilt) times the material's rim and tint. Skip: everywhere.",
        [MaterialStep.Clearcoat] = "A lacquer layer (polished wood, wet stone, paint): a mask times the material's coat and its roughness. Skip: everywhere.",
        [MaterialStep.Anisotropy] = "Highlights stretched along a direction (brushed metal, hair, grain): the texture's own grain (its brightness gradient, smoothed) or a fixed angle; the strength from how clear the grain is. Skip: the fixed angle.",
        [MaterialStep.Subsurface] = "Light scattered under the surface (skin, wax, leaves): a mask times the material's strength (Forward+ only: the WSL editor's Compatibility renderer does not show it). Skip: everywhere.",
        [MaterialStep.Transmittance] = "Light through the surface: its colour (the albedo's hue or a colour) and how much (thin: more; the thickness from the height or brightness). Skip: one thickness.",
        [MaterialStep.Backlight] = "Light through thin surfaces from behind (leaves, paper, cloth): a mask times a tint (the albedo's own colour, or a colour). Skip: everywhere.",
        [MaterialStep.Refraction] = "What is behind bent by the normal map (glass, water): a mask times the material's scale. The material becomes transparent; the game's map path leaves it out. Skip: everywhere.",
        [MaterialStep.Detail] = "A second, finer texture laid over at its own scale: the mask (where), and the detail's albedo and normal from two files or another texture's made maps.",
    };

    /// <summary>The band equalisers' presets (fine to coarse).</summary>
    private static readonly Dictionary<string, (string Name, float[] Values)[]> Presets = new()
    {
        ["bands:weights"] = [("Default", [0.15f, 0.19f, 0.3f, 0.5f, 0.7f, 0.9f, 1f]), ("Details", [0.7f, 0.4f, 0.3f, 0.5f, 0.8f, 0.9f, 0.7f]),
            ("Shapes", [0.02f, 0.03f, 0.1f, 0.35f, 0.7f, 0.9f, 1f])],
        ["bands:contrast"] = [("Flat", [1f, 1f, 1f, 1f, 1f, 1f, 1f]), ("Cracks", [1f, 1f, 1f, 0.5f, -0.5f, -1f, -1f]), ("Fine only", [1.5f, 1.2f, 1f, 0.5f, 0f, 0f, 0f])],
        ["bands:normal"] = [("Crisp", [1f, 0.7f, 0.5f, 0.35f, 0.25f, 0.15f, 0.1f]), ("Mids", [0.2f, 0.5f, 1f, 0.8f, 0.5f, 0.3f, 0.15f]),
            ("Smooth", [0.05f, 0.1f, 0.3f, 0.6f, 1f, 0.8f, 0.5f])],
    };

    private void RebuildSettings()
    {
        _stepTitle.Text = $"{Mathf.Max(0, StripSteps().IndexOf(Step))} · {Label(Step)}";
        _stepNote.Text = Notes[Step];
        foreach (var c in _settingsBox.GetChildren())
            c.QueueFree();
        _controls.Clear();
        var list = MaterialPipeline.Settings[Step];
        for (int i = 0; i < list.Length; i++)
        {
            var st = list[i];
            if (st.Group is { } g && g.StartsWith("bands:", System.StringComparison.Ordinal))
            {
                var band = list.Skip(i).TakeWhile(x => x.Group == g).ToArray();
                BuildEqualiser(g, band);
                i += band.Length - 1;
                continue;
            }
            var type = MaterialPipeline.Prop(st.Property).PropertyType;
            if (type == typeof(Color))
            {
                if (st.Group == "sample")
                    BuildSample(st);
                else
                    BuildColour(st);
                continue;
            }
            var row = new HBoxContainer();
            _settingsBox.AddChild(row);
            var prop = st.Property;
            row.AddChild(new Label { Text = (Pinned(prop) ? "● " : "") + st.Label, CustomMinimumSize = new Vector2(130, 0), TooltipText = st.Tip, MouseFilter = MouseFilterEnum.Pass });
            Control ctl;
            if (type == typeof(bool))
            {
                var cb = new CheckBox { TooltipText = st.Tip, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                cb.Toggled += on => SetSetting(prop, on);
                ctl = cb;
            }
            else if (type.IsEnum)
            {
                var ob = new OptionButton { TooltipText = st.Tip, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                foreach (var n in System.Enum.GetNames(type))
                    ob.AddItem(n);
                ob.ItemSelected += i2 => SetSetting(prop, (int)i2);
                ctl = ob;
            }
            else
            {
                var sl = new EditorSpinSlider
                {
                    MinValue = st.Min, MaxValue = st.Max, Step = st.Step, TooltipText = st.Tip, SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    EditingInteger = type == typeof(int), AllowGreater = false,
                };
                sl.Grabbed += () => BeginDrag(prop);
                sl.Ungrabbed += () => EndDrag(prop);
                sl.ValueChanged += v => SliderChanged(prop, v);
                ctl = sl;
            }
            row.AddChild(ctl);
            _controls[prop] = ctl;
            AddReset(row, prop, st.Label);
            AddPin(row, [prop], st.Label);
        }
        if (Step == MaterialStep.Detail)
            BuildDetailSources();
        if (Step == MaterialStep.Source)
            BuildComfyRow();
        _removeMap.Visible = !MaterialPipeline.IsCore(Step);
        _map2d.Arrow = Step == MaterialStep.Normal ? Recipe.NormalLightAngle : null;
        _checkLabel.Visible = Step == MaterialStep.Albedo && _checkLabel.Text.Length > 0;
        RefreshControls();
    }

    /// <summary>A colour setting (a tint): a picker, Pick (a click in the 2D map or on the object), reset, pin.</summary>
    private void BuildColour(StepSetting st)
    {
        var row = new HBoxContainer();
        _settingsBox.AddChild(row);
        var prop = st.Property;
        row.AddChild(new Label { Text = (Pinned(prop) ? "● " : "") + st.Label, CustomMinimumSize = new Vector2(130, 0), TooltipText = st.Tip, MouseFilter = MouseFilterEnum.Pass });
        var cp = new ColorPickerButton { EditAlpha = false, CustomMinimumSize = new Vector2(44, 0), TooltipText = st.Tip, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cp.ColorChanged += c => LiveSet(prop, new Color(c, 1));
        cp.PopupClosed += () => EndDrag(prop);
        row.AddChild(cp);
        _controls[prop] = cp;
        var pick = new Button { Text = "Pick", TooltipText = "Click or drag in the 2D map or on the object in the preview (the mean over the pick radius)", ToggleMode = true };
        pick.Toggled += on =>
        {
            if (on)
                StartPick(prop);
            else
                EndPick();
        };
        row.AddChild(pick);
        AddReset(row, prop, st.Label);
        AddPin(row, [prop], st.Label);
    }

    /// <summary>The detail's albedo and normal: another texture's made maps (a texture of the project with a material), or two files.</summary>
    private void BuildDetailSources()
    {
        if (Recipe.DetailFrom == DetailFrom.Texture)
        {
            var row = new HBoxContainer();
            _settingsBox.AddChild(row);
            row.AddChild(new Label { Text = (Pinned("DetailTexture") ? "● " : "") + "Texture", CustomMinimumSize = new Vector2(130, 0), TooltipText = "The texture whose made albedo and normal are the detail" });
            var ob = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "A texture of the project with a material: its albedo and normal map, at the detail's scale" };
            ob.AddItem("(none)");
            ob.SetItemMetadata(0, "");
            foreach (var t in DetailCandidates())
            {
                ob.AddItem(RemasterTextures.ShortName(t));
                ob.SetItemMetadata(ob.ItemCount - 1, t);
                if (t == Recipe.DetailTexture)
                    ob.Selected = ob.ItemCount - 1;
            }
            ob.ItemSelected += i => SetSetting("DetailTexture", ob.GetItemMetadata((int)i).AsString());
            row.AddChild(ob);
            AddPin(row, ["DetailTexture"], "Detail texture");
            return;
        }
        foreach (var (prop, label) in new[] { ("DetailAlbedoFile", "Detail albedo"), ("DetailNormalFile", "Detail normal") })
        {
            var row = new HBoxContainer();
            _settingsBox.AddChild(row);
            row.AddChild(new Label { Text = (Pinned(prop) ? "● " : "") + label, CustomMinimumSize = new Vector2(130, 0) });
            var file = (string)MaterialPipeline.Prop(prop).GetValue(Recipe)!;
            var p = prop;
            var b = new Button { Text = file.Length > 0 ? file.GetFile() : "Choose...", SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = $"{label}: a file of your own (PNG, WebP, JPEG)" };
            b.Pressed += () => ChooseFile($"{label} (a file of your own)", f => SetSetting(p, f));
            row.AddChild(b);
            var clear = new Button { Text = "×", Flat = true, TooltipText = "No file" };
            clear.Pressed += () => SetSetting(p, "");
            row.AddChild(clear);
            AddPin(row, [prop], label);
        }
    }

    /// <summary>The project's textures with a material (other than the current one): the detail's choices.</summary>
    public static IEnumerable<string> DetailCandidates() =>
        TextureStore.Set.Textures.Where(t => t != null && t.Material.Length > 0).Select(t => t!.Id).OrderBy(id => RemasterTextures.ShortName(id), System.StringComparer.Ordinal);

    /// <summary>The pin of a setting (or a band equaliser's seven): ● pinned to the current texture (click: unpin), ○ the queue's (click: pin this value to the texture).</summary>
    private void AddPin(HBoxContainer row, string[] props, string label)
    {
        bool pinned = props.All(Pinned);
        var pin = new Button
        {
            Text = pinned ? "●" : "○", Flat = true, Disabled = Current == null,
            TooltipText = pinned ? $"{label}: pinned to this texture (an exception); click to take the queue's value again"
                : $"{label}: the queue's; click to pin this value to this texture only (an exception: the queue's goes back to what it was before)",
        };
        pin.Pressed += () => Say((pinned ? ClearException(props) : Pin(props)) ?? "");
        row.AddChild(pin);
    }

    private void AddReset(HBoxContainer row, string prop, string label)
    {
        var reset = new Button { Text = "↺", TooltipText = $"{label} back to its default", Flat = true };
        reset.Pressed += () => ResetSetting(prop);
        row.AddChild(reset);
    }

    /// <summary>A band equaliser: seven small vertical sliders, fine to coarse, and its presets (one undo step each).</summary>
    private void BuildEqualiser(string group, StepSetting[] bands)
    {
        var head = new HBoxContainer();
        _settingsBox.AddChild(head);
        string title = bands[0].Label[..bands[0].Label.LastIndexOf(' ')];
        var bandProps = bands.Select(x => x.Property).ToArray();
        head.AddChild(new Label { Text = (bandProps.All(Pinned) ? "● " : "") + title, TooltipText = bands[0].Tip, MouseFilter = MouseFilterEnum.Pass, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        foreach (var (name, values) in Presets.GetValueOrDefault(group) ?? [])
        {
            var b = new Button { Text = name, TooltipText = $"{title}: {string.Join(" ", values)} (fine to coarse)", Flat = false };
            var props = bands.Select(x => x.Property).ToArray();
            b.Pressed += () => ApplyBands(title + ": " + name, props, values);
            head.AddChild(b);
        }
        AddPin(head, bandProps, title);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _settingsBox.AddChild(row);
        for (int k = 0; k < bands.Length; k++)
        {
            var st = bands[k];
            var col = new VBoxContainer();
            var sl = new VSlider
            {
                MinValue = st.Min, MaxValue = st.Max, Step = st.Step, CustomMinimumSize = new Vector2(30, 84),
                TooltipText = $"{st.Label}: {(k == 0 ? "the finest" : k == bands.Length - 1 ? "the coarsest" : "")} band", SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            };
            var prop = st.Property;
            sl.DragStarted += () => BeginDrag(prop);
            sl.DragEnded += _ => EndDrag(prop);
            sl.ValueChanged += v => SliderChanged(prop, v);
            col.AddChild(sl);
            col.AddChild(new Label { Text = k == 0 ? "fine" : k == bands.Length - 1 ? "wide" : k.ToString(), HorizontalAlignment = HorizontalAlignment.Center });
            row.AddChild(col);
            _controls[prop] = sl;
        }
        _settingsBox.AddChild(new HSeparator());
    }

    /// <summary>A band equaliser's preset (one undo step).</summary>
    public void ApplyBands(string action, string[] props, float[] values)
    {
        var d = new Godot.Collections.Dictionary();
        for (int i = 0; i < props.Length && i < values.Length; i++)
            d[props[i]] = values[i];
        Commit($"Materials: {action}", d);
    }

    /// <summary>A colour sample: its colour (a picker, or Pick: a click in the 2D map), the isolate view of its mask, clear.</summary>
    private void BuildSample(StepSetting st)
    {
        var row = new HBoxContainer();
        _settingsBox.AddChild(row);
        var prop = st.Property;
        row.AddChild(new Label { Text = (Pinned(prop) ? "● " : "") + st.Label, CustomMinimumSize = new Vector2(80, 0), TooltipText = st.Tip, MouseFilter = MouseFilterEnum.Pass });
        var cp = new ColorPickerButton { EditAlpha = false, CustomMinimumSize = new Vector2(44, 0), TooltipText = "The colour (none: off)" };
        cp.ColorChanged += c => LiveSet(prop, new Color(c, 1));
        cp.PopupClosed += () => EndDrag(prop);
        row.AddChild(cp);
        _controls[prop] = cp;
        var pick = new Button { Text = "Pick", TooltipText = "Click or drag in the 2D map or on the object in the preview (the mean over the pick radius; its mask shown while picking)", ToggleMode = true };
        pick.Toggled += on =>
        {
            if (on)
                StartPick(prop);
            else
                EndPick();
        };
        row.AddChild(pick);
        int index = prop[^1] is >= '1' and <= '3' ? prop[^1] - '0' : 1;
        var iso = new Button { Text = "Isolate", TooltipText = "Show this colour's mask in the 2D map", ToggleMode = true };
        iso.SetPressedNoSignal(_isolate is { } cur && cur.Step == Step && cur.Sample == index);
        iso.Toggled += on => SetIsolate(on ? (Step, index) : null);
        row.AddChild(iso);
        var clear = new Button { Text = "×", TooltipText = "No colour: the sample off", Flat = true };
        clear.Pressed += () => SetSetting(prop, new Color(0, 0, 0, 0));
        row.AddChild(clear);
        AddPin(row, [prop], st.Label);
    }

    /// <summary>Shows a colour sample's mask in the 2D view (null: the map).</summary>
    public void SetIsolate((MaterialStep Step, int Sample)? isolate)
    {
        _isolate = isolate;
        Remake();
        ShowStep();
    }

    /// <summary>A setting changed live (a colour being picked, the arrow dragged): one undo step when it ends (<see cref="EndDrag"/>).</summary>
    private void LiveSet(string prop, Variant value)
    {
        if (!_dragFrom.ContainsKey(prop))
            BeginDrag(prop);
        SetLive(prop, value);
        Changed([prop]);
    }

    private void RefreshControls()
    {
        foreach (var (prop, ctl) in _controls)
        {
            var v = Recipe.Get(prop);
            switch (ctl)
            {
                case CheckBox cb:
                    cb.SetPressedNoSignal(v.AsBool());
                    break;
                case OptionButton ob:
                    ob.Selected = v.AsInt32();
                    break;
                case ColorPickerButton cp:
                    cp.Color = v.AsColor();
                    cp.Modulate = v.AsColor().A > 0 ? Colors.White : new Color(1, 1, 1, 0.35f);
                    break;
                case Range r:
                    r.SetValueNoSignal(v.AsDouble());
                    break;
            }
        }
        if (Step == MaterialStep.Normal)
            _map2d.Arrow = Recipe.NormalLightAngle;
        var mode = MaterialPipeline.ModeOf(Recipe, Step);
        _mode.Selected = (int)mode;
        _mode.SetItemDisabled((int)StepMode.Skip, Step == MaterialStep.Source);
        var file = MaterialPipeline.FileOf(Recipe, Step);
        _importButton.Text = mode == StepMode.Import && file.Length > 0 ? $"Import... ({file.GetFile()})" : "Import...";
        RefreshStrip();
    }

    private void RefreshStrip()
    {
        var popup = _addMap.GetPopup();
        popup.Clear();
        foreach (var s in MaterialPipeline.Extras)
            if (!MaterialPipeline.On(Recipe, s))
                popup.AddItem(Label(s), (int)s);
        _addMap.Disabled = popup.ItemCount == 0;
        // numbered by their place in the strip: the keys 0 to 9 go there
        var shown = StripSteps();
        foreach (var s in MaterialPipeline.Steps)
        {
            _strip[s].Col.Visible = MaterialPipeline.On(Recipe, s);
            var mode = MaterialPipeline.ModeOf(Recipe, s);
            string mark = mode == StepMode.Skip ? " –" : mode == StepMode.Import ? " ⇩" : _shownSide.GetValueOrDefault(s) >= _workingSide && _workingSide > 0 ? " ✓" : " …";
            int at = shown.IndexOf(s);
            _strip[s].Button.Text = $"{(at >= 0 ? at : (int)s)} {Label(s)}{mark}";
            _strip[s].Button.TooltipText = $"Go to the {Label(s)} step" + (at is >= 0 and <= 9 ? $" (key {at})" : "");
            _strip[s].Thumb.Texture = s == MaterialStep.Source ? (_maps.TryGetValue(s, out var src) ? src : _sourceTex) : MaterialPipeline.On(Recipe, s) ? _maps.GetValueOrDefault(s) : null;
            if (s == Step)
                _strip[s].Button.SetPressedNoSignal(true);
        }
    }

    private void ShowStep()
    {
        _maskShown = _isolate is { } iso && iso.Step == Step && MaskTexture() is not null;
        var map = _maskShown ? MaskTexture() : Step == MaterialStep.Source ? _maps.GetValueOrDefault(Step) ?? _sourceTex : _maps.GetValueOrDefault(Step);
        _map2d.SetMaps(map, _sourceTex);
        _checkLabel.Visible = Step == MaterialStep.Albedo && _checkLabel.Text.Length > 0;
        RefreshStrip();
    }

    private Texture2D? MaskTexture() => _gpu != null ? _gpu.Mask : _maskCpu.GetWidth() > 1 ? _maskCpu : null;

    /// <summary>Whether the 2D view shows a colour sample's mask (the isolate view).</summary>
    public bool MaskShown => _maskShown;

    // ---- settings and undo ------------------------------------------------------------------------

    private EditorUndoRedoManager Undo => _plugin.GetUndoRedo();

    /// <summary>Every recipe property the tab edits (the steps' settings, modes and files).</summary>
    public static IEnumerable<string> RecipeProps()
    {
        foreach (var s in MaterialPipeline.Steps)
        {
            var (m, f) = MaterialPipeline.ModeProps(s);
            if (!MaterialPipeline.IsCore(s))
                yield return MaterialPipeline.OnProp(s);
            yield return m;
            yield return f;
            foreach (var st in MaterialPipeline.Settings[s])
                yield return st.Property;
        }
        yield return nameof(PbrRecipe.DetailTexture);
        yield return nameof(PbrRecipe.DetailAlbedoFile);
        yield return nameof(PbrRecipe.DetailNormalFile);
    }

    /// <summary>A step's name in the strip and the title.</summary>
    public static string Label(MaterialStep s) => s switch
    {
        MaterialStep.BentNormal => "Bent normal",
        _ => s.ToString(),
    };

    /// <summary>Adds "+ map" step <paramref name="s"/> to the material (one undo step) and goes to it. An error or null.</summary>
    public string? AddMap(MaterialStep s)
    {
        if (MaterialPipeline.IsCore(s) || MaterialPipeline.On(Recipe, s))
            return $"{Label(s)} is in the strip already";
        Commit($"Materials: + {Label(s)}", new Godot.Collections.Dictionary { [MaterialPipeline.OnProp(s)] = true });
        SelectStep(s);
        return null;
    }

    /// <summary>Takes "+ map" step <paramref name="s"/> out of the material (one undo step; its settings kept). An error or null.</summary>
    public string? RemoveMap(MaterialStep s)
    {
        if (MaterialPipeline.IsCore(s) || !MaterialPipeline.On(Recipe, s))
            return $"{Label(s)} is not an added map";
        if (Step == s)
            SelectStep(MaterialStep.Emission);
        Commit($"Materials: remove {Label(s)}", new Godot.Collections.Dictionary { [MaterialPipeline.OnProp(s)] = false });
        return null;
    }

    /// <summary>The key of an undo step's dictionary that sets the queue's profile (not a setting).</summary>
    private const string ProfileKey = "@profile";

    /// <summary>Sets the queue's settings (the undo history calls it; <see cref="ProfileKey"/>: the profile's name), then makes again what they change.</summary>
    public void ApplyRecipe(Godot.Collections.Dictionary values)
    {
        var keys = new List<string>();
        foreach (var (k, v) in values)
        {
            var name = k.AsString();
            if (name == ProfileKey)
            {
                ProfileName = v.AsString();
                continue;
            }
            QueueSettings.Set(name, v);
            keys.Add(name);
        }
        Recompute();
        Changed(keys);
        RefreshProfileBox();
    }

    /// <summary>Sets texture <paramref name="id"/>'s exceptions (the undo history calls it), then makes again what they change.</summary>
    public void ApplyExceptions(string id, Godot.Collections.Dictionary exceptions)
    {
        var was = ExceptionsOf(id);
        _exceptions[id] = TextureStore.Sorted(exceptions);
        if (id != Current)
        {
            FillQueue();
            return;
        }
        Recompute();
        Changed(was.Keys.Concat(exceptions.Keys).Select(k => k.AsString()).Distinct().ToList());
        RebuildSettings();
        RefreshProfileBox();
    }

    /// <summary><see cref="Recipe"/> made again: the queue's settings with the current texture's exceptions over them.</summary>
    private void Recompute() => Recipe = Compose(QueueSettings, Current != null ? ExceptionsOf(Current) : null);

    /// <summary><paramref name="queue"/>'s settings (all of them, its files too) with <paramref name="exceptions"/> over them.</summary>
    public static PbrRecipe Compose(PbrRecipe queue, Godot.Collections.Dictionary? exceptions)
    {
        var r = (PbrRecipe)queue.Duplicate();
        r.Version = 3;
        if (exceptions != null)
            foreach (var (k, v) in exceptions)
                r.Set(k.AsString(), v);
        return r;
    }

    /// <summary>
    /// Texture <paramref name="id"/>'s exceptions: those set in this session, else its entry's
    /// (the settings that differed from its profile when it was made; none without a profile).
    /// </summary>
    public Godot.Collections.Dictionary ExceptionsOf(string id)
    {
        if (!_exceptions.TryGetValue(id, out var d))
            _exceptions[id] = d = TextureStore.Find(id) is { Profile.Length: > 0 } e ? TextureStore.Sorted(e.Exceptions) : new Godot.Collections.Dictionary();
        return d;
    }

    /// <summary>Whether <paramref name="prop"/> is pinned to the current texture.</summary>
    public bool Pinned(string prop) => Current != null && ExceptionsOf(Current).ContainsKey(prop);

    /// <summary>
    /// One undo step that sets <paramref name="after"/> (the old values from the settings):
    /// settings pinned to the current texture change its exceptions, the others the queue's.
    /// <paramref name="execute"/>: false when they are set already (a drag's end).
    /// </summary>
    private void Commit(string action, Godot.Collections.Dictionary after, Godot.Collections.Dictionary? before = null, bool execute = true, bool queueOnly = false)
    {
        before ??= new Godot.Collections.Dictionary();
        var qAfter = new Godot.Collections.Dictionary();
        var qBefore = new Godot.Collections.Dictionary();
        Godot.Collections.Dictionary? exAfter = null, exBefore = null;
        foreach (var (k, v) in after)
        {
            var name = k.AsString();
            var old = before.TryGetValue(k, out var b) ? b : name == ProfileKey ? ProfileName : queueOnly ? QueueSettings.Get(name) : Recipe.Get(name);
            if (name != ProfileKey && !queueOnly && Pinned(name))
            {
                exAfter ??= TextureStore.Sorted(ExceptionsOf(Current!));
                exBefore ??= TextureStore.Sorted(ExceptionsOf(Current!));
                exAfter[name] = v;
                exBefore[name] = old;
                continue;
            }
            qAfter[name] = v;
            qBefore[name] = old;
            if (name != ProfileKey)
                _prior[name] = old;
        }
        Undo.CreateAction(action, UndoRedo.MergeMode.Disable, _plugin);
        if (qAfter.Count > 0)
        {
            Undo.AddDoMethod(this, MethodName.ApplyRecipe, qAfter);
            Undo.AddUndoMethod(this, MethodName.ApplyRecipe, qBefore);
        }
        if (exAfter != null)
        {
            Undo.AddDoMethod(this, MethodName.ApplyExceptions, Current!, exAfter);
            Undo.AddUndoMethod(this, MethodName.ApplyExceptions, Current!, exBefore!);
        }
        Undo.CommitAction(execute);
    }

    /// <summary>A setting changed live (a drag, a pick): the queue's or, pinned, the texture's exception; the undo step comes at its end.</summary>
    private void SetLive(string prop, Variant value)
    {
        if (Pinned(prop))
            ExceptionsOf(Current!)[prop] = value;
        else
            QueueSettings.Set(prop, value);
        Recipe.Set(prop, value);
    }

    private static string LabelOf(string prop) =>
        MaterialPipeline.Settings.Values.SelectMany(s => s).FirstOrDefault(s => s.Property == prop)?.Label ?? prop;

    /// <summary>Sets one setting (one undo step).</summary>
    public void SetSetting(string prop, Variant value)
    {
        if (Same(Recipe.Get(prop), value))
            return;
        var after = new Godot.Collections.Dictionary { [prop] = value };
        // the seam tool makes a tiling texture: the filters wrap from then on
        if (prop == nameof(PbrRecipe.SeamMode) && value.AsInt32() != (int)SeamMode.Off && !Recipe.Wrap)
            after[nameof(PbrRecipe.Wrap)] = true;
        Commit($"Materials: {LabelOf(prop)}", after);
    }

    private static bool Same(Variant a, Variant b) => a.VariantType switch
    {
        Variant.Type.Bool => a.AsBool() == b.AsBool(),
        Variant.Type.Int => a.AsInt64() == b.AsInt64(),
        Variant.Type.Float => Mathf.IsEqualApprox(a.AsDouble(), b.AsDouble()),
        _ => a.AsString() == b.AsString(),
    };

    private readonly Dictionary<string, Variant> _dragFrom = new();

    private void BeginDrag(string prop) => _dragFrom[prop] = Recipe.Get(prop);

    private void SliderChanged(string prop, double v)
    {
        var value = MaterialPipeline.Prop(prop).PropertyType == typeof(int) ? Variant.From((int)System.Math.Round(v)) : Variant.From((float)v);
        if (_dragFrom.ContainsKey(prop))
        {
            // live while the slider moves; one undo step at its end
            SetLive(prop, value);
            Changed([prop]);
        }
        else
            SetSetting(prop, value);
    }

    private void EndDrag(string prop)
    {
        if (!_dragFrom.Remove(prop, out var from))
            return;
        var now = Recipe.Get(prop);
        if (Same(now, from))
            return;
        Commit($"Materials: {LabelOf(prop)}", new Godot.Collections.Dictionary { [prop] = now },
            new Godot.Collections.Dictionary { [prop] = from }, execute: false);
    }

    /// <summary>A slider dragged through <paramref name="values"/> as the editor's slider does it (the checks): live changes, one undo step.</summary>
    public void Drag(string prop, params double[] values)
    {
        BeginDrag(prop);
        foreach (var v in values)
            SliderChanged(prop, v);
        EndDrag(prop);
        RefreshControls();
    }

    private void ResetSetting(string prop) => SetSetting(prop, new PbrRecipe().Get(prop));

    /// <summary>Every setting of <paramref name="s"/>, its mode and file back to the defaults (one undo step).</summary>
    public void ResetStep(MaterialStep s)
    {
        var defaults = new PbrRecipe();
        var after = new Godot.Collections.Dictionary();
        var (m, f) = MaterialPipeline.ModeProps(s);
        foreach (var p in MaterialPipeline.Settings[s].Select(x => x.Property).Append(m).Append(f))
            after[p] = defaults.Get(p);
        Commit($"Materials: reset {s}", after);
    }

    /// <summary>Sets <paramref name="s"/>'s mode (one undo step; Import: see <see cref="Import"/>).</summary>
    public void SetMode(MaterialStep s, StepMode mode)
    {
        if (MaterialPipeline.ModeOf(Recipe, s) == mode)
            return;
        var after = new Godot.Collections.Dictionary { [MaterialPipeline.ModeProps(s).Mode] = (int)mode };
        if (s == MaterialStep.Emission && mode == StepMode.Generate && Recipe.EmissionThreshold > 1f)
            after["EmissionThreshold"] = 0.85f;
        if (s == MaterialStep.Metallic && mode == StepMode.Generate && Recipe.MetallicThreshold > 1f && Recipe.Metallic <= 0f)
            (after["MetallicThreshold"], after["Metallic"]) = (0.8f, 1f);
        Commit($"Materials: {s} {mode}", after);
    }

    private void ChooseImport(MaterialStep s) => ChooseFile($"A map of your own for {s}", file => Say(Import(s, file) ?? $"{s}: {file.GetFile()}"));

    private void ChooseFile(string title, System.Action<string> then)
    {
        _fileDialog ??= new EditorFileDialog
        {
            FileMode = EditorFileDialog.FileModeEnum.OpenFile, Access = EditorFileDialog.AccessEnum.Filesystem,
            Filters = ["*.png ; PNG", "*.webp ; WebP", "*.jpg, *.jpeg ; JPEG"],
        };
        _fileDialog.Title = title;
        if (_fileDialog.GetParent() == null)
            AddChild(_fileDialog);
        foreach (var c in _fileDialog.GetSignalConnectionList(FileDialog.SignalName.FileSelected))
            _fileDialog.Disconnect(FileDialog.SignalName.FileSelected, c["callable"].AsCallable());
        _fileDialog.FileSelected += file => then(file);
        _fileDialog.PopupFileDialog();
        RefreshControls();
    }

    /// <summary>Takes <paramref name="file"/> as <paramref name="s"/>'s map (one undo step); an error or null.</summary>
    public string? Import(MaterialStep s, string file)
    {
        if (LoadImport(file) == null)
            return $"cannot read {file.GetFile()}";
        var (m, f) = MaterialPipeline.ModeProps(s);
        Commit($"Materials: import {s}", new Godot.Collections.Dictionary { [m] = (int)StepMode.Import, [f] = file });
        return null;
    }

    /// <summary>An imported file's pixels (read on the main thread; a store-relative path once saved).</summary>
    private RgbaImage? LoadImport(string file)
    {
        if (file.Length == 0)
            return null;
        if (file.StartsWith("texture:", System.StringComparison.Ordinal))
            return LoadMadeMap(file);
        if (_imports.TryGetValue(file, out var have))
            return have;
        var path = file.StartsWith("res://", System.StringComparison.Ordinal) || file.StartsWith("user://", System.StringComparison.Ordinal)
            || System.IO.Path.IsPathRooted(file) ? file : TextureStore.Absolute(file);
        var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(path));
        if (img == null || img.IsEmpty())
            return null;
        return _imports[file] = PbrBuilder.From(img);
    }

    /// <summary>The material each <c>texture:</c> key was last read from (read again when it changed).</summary>
    private readonly Dictionary<string, string> _madeFrom = new();

    /// <summary>
    /// Another texture's made map, <c>texture:&lt;id&gt;:albedo</c> or <c>:normal</c> (the detail's):
    /// the file its material names (read from the material's text: no import needed), cached per
    /// material file.
    /// </summary>
    private RgbaImage? LoadMadeMap(string key)
    {
        var parts = key.Split(':');
        if (parts.Length < 3)
            return null;
        var role = parts[^1];
        var id = string.Join(":", parts[1..^1]);
        if (TextureStore.Find(id) is not { Material.Length: > 0 } e)
            return null;
        var mat = TextureStore.Absolute(e.Material);
        if (_madeFrom.TryGetValue(key, out var from) && from == mat && _imports.TryGetValue(key, out var have))
            return have;
        string? path = null;
        if (FileAccess.FileExists(mat))
            foreach (var line in FileAccess.GetFileAsString(mat).Split('\n'))
                if (line.StartsWith("[ext_resource", System.StringComparison.Ordinal) && line.Contains($"id=\"{role}\"") && line.Split("path=\"") is { Length: > 1 } sp)
                    path = sp[1].Split('"')[0];
        if (path == null && ResourceLoader.Load(mat, "", ResourceLoader.CacheMode.Ignore) is BaseMaterial3D bm)
            path = (role == "normal" ? bm.NormalTexture : bm.AlbedoTexture)?.ResourcePath;
        if (path is not { Length: > 0 } || Image.LoadFromFile(ProjectSettings.GlobalizePath(path)) is not { } img || img.IsEmpty())
            return null;
        _madeFrom[key] = mat;
        return _imports[key] = PbrBuilder.From(img);
    }

    /// <summary>After settings changed: the controls, then the maps (or only the material for its own settings).</summary>
    private void Changed(IReadOnlyCollection<string> props)
    {
        // an added or removed map, or the detail's source: the panel's rows change
        if (props.Any(p => p.EndsWith("On", System.StringComparison.Ordinal) || p is nameof(PbrRecipe.DetailFrom) or nameof(PbrRecipe.DetailTexture)
            or nameof(PbrRecipe.DetailAlbedoFile) or nameof(PbrRecipe.DetailNormalFile) or nameof(PbrRecipe.Upscaler)))
            RebuildSettings();
        RefreshControls();
        if (props.All(MaterialPipeline.MaterialOnly.Contains))
            UpdateMaterials();
        else
            Remake();
    }

    // ---- making the maps --------------------------------------------------------------------------

    private ulong _requestUsec;
    private bool _gotSmall, _gotRefined;
    private readonly Dictionary<MaterialStep, RgbaImage> _gpuPixels = new();
    private ColourCheck? _check;

    /// <summary>
    /// Asks for every map with the current settings: on the GPU, built now and drawn this frame
    /// (only the passes whose settings or inputs changed); on the CPU, 256 px first, then the
    /// working size (the run before cancelled).
    /// </summary>
    public void Remake()
    {
        if (Current == null || _original == null)
            return;
        var snap = (PbrRecipe)Recipe.Duplicate();
        foreach (var f in MaterialPipeline.Files(snap))
            LoadImport(f);
        // ComfyUI's upscale as the source when it is cached (else asked for; Lanczos meanwhile)
        ComfyPrepare(snap);
        // the maps taken out of the material: off it at once
        bool removed = false;
        foreach (var s in MaterialPipeline.Extras)
            if (!MaterialPipeline.On(snap, s))
            {
                removed |= _maps.Remove(s);
                _pixels.Remove(s);
                _shownSide.Remove(s);
            }
        if (removed)
            UpdateMaterials();
        var inputs = new MaterialPipeline.Inputs(Current, _original, snap, new Dictionary<string, RgbaImage>(_imports));
        var (w, h, _) = MaterialPipeline.Size(inputs, Mathf.Max(MaterialPipeline.SmallSide, snap.WorkingSide));
        _workingSide = Mathf.Max(w, h);
        _requestUsec = Time.GetTicksUsec();
        (_gotSmall, _gotRefined, _runDone) = (false, false, false);
        Arrivals.Clear();
        OcclusionStages.Clear();
        _check = null;
        if (_gpu != null)
        {
            _gpu.Isolate = _isolate;
            _gpu.Update(inputs, snap.WorkingSide);
            _gpuPixels.Clear();
            foreach (var s in MaterialPipeline.Steps)
                if (_gpu.Texture(s) is { } t)
                {
                    _maps[s] = t;
                    _shownSide[s] = _workingSide;
                }
            _sides = [snap.WorkingSide];
            UpdateMaterials();
            ShowStep();
            return;
        }
        var (sw, sh, _) = MaterialPipeline.Size(inputs, MaterialPipeline.SmallSide);
        int small = Mathf.Max(sw, sh);
        int[] sides = small < _workingSide ? [MaterialPipeline.SmallSide, snap.WorkingSide] : [snap.WorkingSide];
        _pipe.Isolate = _isolate;
        _run = _pipe.Request(inputs, Step, sides);
        _sides = sides;
    }

    private int[] _sides = [];

    public void Tick(double delta)
    {
        if (_gpu != null)
            TickGpu();
        TickComfy();
        if (!Saving && _bakeRun == null)
            BakeBackends.Trim();
        int shown = 0;
        while (shown < 6 && _pipe.Results.TryDequeue(out var r))
        {
            if (r.Run == _saveRun)
            {
                Collect(r);
                continue;
            }
            if (r.Run != _run || _gpu != null)
                continue;
            shown++;
            Show(r);
        }
        if (_bake is { Done: true })
            FinishBake();
        TickBake();
        PollObject();
        PollInMap();
    }

    private void TickGpu()
    {
        var gpu = _gpu!;
        bool changed = gpu.Tick();
        if (changed)
            _gpuPixels.Clear();
        if (!_gotRefined && gpu.DrawnMs >= 0)
        {
            _gotRefined = true;
            if (RecordStages && Pixels(MaterialStep.Occlusion) is { } firstAo)
                OcclusionStages.Add(firstAo);
            Latency = (0, gpu.DrawnMs, Latency.All);
            foreach (var s in MaterialPipeline.Steps)
                Arrivals.Add((_workingSide, s));
            Arrivals.Add((_workingSide, null));
            UpdateMaterials();
            ShowStep();
        }
        if (!_runDone && gpu.Settled && gpu.SettledMs >= 0)
        {
            _runDone = true;
            Latency = (0, Latency.Refined, gpu.SettledMs);
            _timing.Text = $"GPU: {Latency.Refined:0} ms drawn, occlusion settled {Latency.All:0} ms ({gpu.Backend.LastDraws} of {gpu.Backend.Count} passes last)";
            _check = gpu.Check();
            ShowCheck();
        }
    }

    private void ShowCheck()
    {
        _checkLabel.Text = _check is { } c ? $"Colour check (OKLab means), classic → albedo: {c}" : "";
        _checkLabel.Visible = Step == MaterialStep.Albedo && _checkLabel.Text.Length > 0;
    }

    /// <summary>The albedo's colour check against the classic texture (OKLab means).</summary>
    public ColourCheck? Check => _check;

    private void Show(MaterialPipeline.Result r)
    {
        bool small = _sides.Length > 1 && r.Side == _sides[0];
        if (r.Kind == ResultKind.Mask)
        {
            _maskCpu.SetImage(PbrBuilder.ToImage(r.Image));
            ShowStep();
        }
        else if (r.Kind == ResultKind.Aux && r.Aux is { } aux)
        {
            if (!_auxCpu.TryGetValue(aux, out var at))
                _auxCpu[aux] = at = new ImageTexture();
            var img = PbrBuilder.ToImage(r.Image);
            img.GenerateMipmaps();
            at.SetImage(img);
        }
        else if (r.Step is { } s)
        {
            Arrivals.Add((r.Side, r.Step));
            if (!_cpuTex.TryGetValue(s, out var tex))
                _cpuTex[s] = tex = new ImageTexture();
            var img = PbrBuilder.ToImage(r.Image);
            if (!small)
                img.GenerateMipmaps();
            tex.SetImage(img);
            _maps[s] = tex;
            _pixels[s] = r.Image;
            if (s == MaterialStep.Occlusion && !small)
                OcclusionStages.Add(r.Image);
            if (r.Float != null)
                _heightFloat = r.Float;
            if (r.Check != null && !small)
            {
                _check = r.Check;
                ShowCheck();
            }
            _shownSide[s] = Mathf.Max(r.Image.Width, r.Image.Height);
            if (s == Step)
            {
                if (small && !_gotSmall)
                    (_gotSmall, Latency) = (true, (r.Ms, Latency.Refined, Latency.All));
                if (!small && !_gotRefined)
                    (_gotRefined, Latency) = (true, (Latency.Small, r.Ms, Latency.All));
                ShowStep();
            }
        }
        else
        {
            Arrivals.Add((r.Side, r.Step));
            var img = PbrBuilder.ToImage(r.Image);
            if (!small)
                img.GenerateMipmaps();
            _orm.SetImage(img);
            _ormPixels = r.Image;
        }
        if (r.Last)
        {
            _runDone = true;
            Latency = (_gotSmall ? Latency.Small : 0, Latency.Refined, r.Ms);
            _timing.Text = $"CPU: {(_gotSmall ? $"{Latency.Small:0} ms small, " : "")}{Latency.Refined:0} ms refined, all {Latency.All:0} ms";
            if (_pipe.Error is { } err)
                Say($"the pipeline failed: {err}");
        }
        UpdateMaterials();
        RefreshStrip();
    }

    /// <summary>The pixels of <paramref name="s"/>'s map as shown (the checks; on the GPU a read back).</summary>
    public RgbaImage? Pixels(MaterialStep s)
    {
        if (_gpu == null)
            return _pixels.GetValueOrDefault(s);
        if (!_gpuPixels.TryGetValue(s, out var img) && _gpu.Pixels(s) is { } got)
            _gpuPixels[s] = img = got;
        return img;
    }

    /// <summary>Whether the GPU's first occlusion (its first directions) is kept in <see cref="OcclusionStages"/> (a read back each change: the checks).</summary>
    public bool RecordStages { get; set; }

    /// <summary>The occlusion maps posted at the working size since the last change (its first directions, then all; on the GPU the first only, with <see cref="RecordStages"/>).</summary>
    public List<RgbaImage> OcclusionStages { get; } = new();

    /// <summary>The isolated colour mask as shown (null: none).</summary>
    public RgbaImage? MaskPixels()
    {
        if (!_maskShown)
            return null;
        if (_gpu != null)
            return _gpu.Outputs?.Mask is { } m && _gpu.Backend.Drawn ? _gpu.Backend.Read(m) : null;
        return _maskCpu.GetImage() is { } img && !img.IsEmpty() ? PbrBuilder.From(img) : null;
    }

    /// <summary>The ORM pack as shown.</summary>
    public RgbaImage? OrmPixels => _gpu != null ? _gpu.OrmPixels : _ormPixels;

    /// <summary>The 2D view (the checks: its maps).</summary>
    public MapView2D Map2D => _map2d;

    // ---- the preview's materials -------------------------------------------------------------------

    private void UpdateMaterials()
    {
        var r = Recipe;
        // the In map view draws the game's UVs as they are
        bool inMap = _shape.Selected == MaterialPreview.InMap;
        float t = inMap ? 1f : (float)_tile.Value;
        _preview.Tiling = t;
        var uv = new Vector3(t, t, 1);
        foreach (var m in new BaseMaterial3D[] { _mat, _raw, _unshaded })
        {
            m.Uv1Scale = uv;
            m.TextureRepeat = r.Wrap;
            m.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
            m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            m.Transparency = _alpha switch
            {
                1 => BaseMaterial3D.TransparencyEnum.AlphaScissor,
                2 => BaseMaterial3D.TransparencyEnum.Alpha,
                _ => BaseMaterial3D.TransparencyEnum.Disabled,
            };
        }
        _raw.AlbedoTexture = _sourceTex;
        _unshaded.AlbedoTexture = _sourceTex;
        _mat.AlbedoTexture = _maps.GetValueOrDefault(MaterialStep.Albedo) ?? (Texture2D)_sourceTex;
        _mat.NormalEnabled = _maps.ContainsKey(MaterialStep.Normal);
        _mat.NormalTexture = _maps.GetValueOrDefault(MaterialStep.Normal);
        var orm = _gpu != null ? _gpu.Orm : _ormPixels != null ? _orm : null;
        _mat.OrmTexture = orm;
        (_mat.Roughness, _mat.Metallic, _mat.MetallicSpecular) = orm != null ? (1f, 1f, 0.5f) : (0.85f, 0f, 0.5f);
        bool height = r.HeightScale > 0 && MaterialPipeline.ModeOf(r, MaterialStep.Height) != StepMode.Skip && _maps.ContainsKey(MaterialStep.Height);
        _mat.HeightmapEnabled = height;
        _mat.HeightmapTexture = height ? _maps[MaterialStep.Height] : null;
        _mat.HeightmapScale = r.HeightScale * 100f;
        bool emission = MaterialPipeline.ModeOf(r, MaterialStep.Emission) != StepMode.Skip && _maps.ContainsKey(MaterialStep.Emission);
        _mat.EmissionEnabled = emission;
        _mat.EmissionTexture = emission ? _maps[MaterialStep.Emission] : null;
        _mat.Emission = Colors.White;
        _mat.EmissionEnergyMultiplier = r.EmissionEnergy;
        // the maps added with "+ map", shown live (the passes' targets on the GPU)
        PbrBuilder.ApplyExtras(_mat, role => ExtraTexture(role, r), r);
        _mat.UV2Scale = new Vector3(r.DetailScale * t, r.DetailScale * t, 1);
        bool classic = _compare.Selected == 1;
        SetInMapMaterials(classic);
        _preview.UseClassic = classic;
        _preview.SetMaterials(_mat, classic ? _unshaded : _raw);
    }

    /// <summary>An added map's texture as shown, by its file role (null: not made, or off).</summary>
    private Texture2D? ExtraTexture(string role, PbrRecipe r)
    {
        if (role is MaterialPipeline.DetailAlbedo or MaterialPipeline.DetailNormal)
            return !r.DetailOn ? null : _gpu != null ? _gpu.Aux(role) : _auxCpu.GetValueOrDefault(role);
        foreach (var s in MaterialPipeline.Extras)
            if (MaterialPipeline.Role(s) == role)
                return MaterialPipeline.On(r, s) ? _maps.GetValueOrDefault(s) : null;
        return null;
    }

    /// <summary>The lighting as the light box sets it (the checks).</summary>
    public void SetLightForCheck(PreviewLight light, int hdri)
    {
        (_light.Selected, _hdri.Selected) = ((int)light, Mathf.Min(hdri, _hdri.ItemCount - 1));
        UpdateLight();
    }

    /// <summary>The object as the shape box sets it (the checks).</summary>
    public void SetShapeForCheck(int shape)
    {
        _shape.Selected = shape;
        SetShape(shape);
    }

    /// <summary>The compare as its box sets it (the checks).</summary>
    public void SetCompareForCheck(int compare)
    {
        _compare.Selected = compare;
        UpdateMaterials();
    }

    /// <summary>A step's map as the preview's material samples it (the checks), or null.</summary>
    public Texture2D? MapTexture(MaterialStep s) => s == MaterialStep.Source ? _maps.GetValueOrDefault(s) ?? _sourceTex : _maps.GetValueOrDefault(s);

    /// <summary>The preview's material (the checks).</summary>
    public OrmMaterial3D Material3D => _mat;

    // ---- profiles and exceptions (R2b phase 3) -----------------------------------------------------

    /// <summary>
    /// Loads profile <paramref name="name"/>'s settings as the queue's (one undo step; empty: no
    /// profile, the settings kept). The current texture's exceptions stay over them. An error or null.
    /// </summary>
    public string? LoadProfile(string name)
    {
        var d = new Godot.Collections.Dictionary { [ProfileKey] = name };
        if (name.Length > 0)
        {
            if (MaterialProfiles.Load(name) is not { } p)
                return $"no profile {name}";
            foreach (var prop in MaterialProfiles.Props)
                d[prop] = p.Get(prop);
        }
        Commit(name.Length > 0 ? $"Materials: the profile {MaterialProfiles.Find(name)?.Title ?? name}" : "Materials: no profile", d, queueOnly: true);
        return null;
    }

    /// <summary>The queue's settings back to the profile's (one undo step).</summary>
    public string? RevertToProfile() => ProfileName.Length == 0 ? "no profile chosen" : LoadProfile(ProfileName);

    /// <summary>The queue's settings written over the profile (one undo step: the file's text). An error, a note, or null.</summary>
    public string? SaveProfile()
    {
        if (ProfileName.Length == 0)
            return "no profile chosen: Save as...";
        var title = MaterialProfiles.Find(ProfileName)?.Title ?? ProfileName;
        var note = MaterialProfiles.Load(ProfileName)?.Note ?? "";
        _plugin.SaveProfile(ProfileName, MaterialProfiles.From(QueueSettings, title, note), $"Save the profile {title}");
        RefreshProfileBox();
        var winner = MaterialProfiles.Find(ProfileName);
        return winner != null && winner.Path != ProfileStore.PathOf(ProfileName) ? $"saved, but the pack {winner.Layer}'s {ProfileName} is the one used" : null;
    }

    /// <summary>The queue's settings as a new profile titled <paramref name="title"/> (one undo step), chosen. An error or null.</summary>
    public string? SaveAsProfile(string title)
    {
        var name = MaterialProfiles.FileName(title);
        if (name.Length == 0)
            return "a profile needs a name";
        if (FileAccess.FileExists(ProfileStore.PathOf(name)))
            return $"there is a profile {name} already (choose it and Save)";
        _plugin.SaveProfile(name, MaterialProfiles.From(QueueSettings, title.Trim()), $"Save as profile {title.Trim()}");
        ProfileName = name;
        RefreshProfileBox();
        return null;
    }

    /// <summary>Renames the chosen profile (its file, and the textures made with it; one undo step). An error or null.</summary>
    public string? RenameProfile(string title)
    {
        if (ProfileName.Length == 0)
            return "no profile chosen";
        if (_plugin.RenameProfile(ProfileName, title) is { } err)
            return err;
        ProfileName = MaterialProfiles.FileName(title);
        RefreshProfileBox();
        return null;
    }

    /// <summary>Deletes the chosen profile from the project (one undo step); the queue keeps its settings. An error or null.</summary>
    public string? DeleteProfile()
    {
        if (ProfileName.Length == 0)
            return "no profile chosen";
        if (!FileAccess.FileExists(ProfileStore.PathOf(ProfileName)))
            return $"{ProfileName} is not in the project's profiles ({MaterialProfiles.Find(ProfileName)?.Layer ?? "none"}): nothing to delete";
        _plugin.DeleteProfile(ProfileName);
        ProfileName = "";
        RefreshProfileBox();
        return null;
    }

    /// <summary>
    /// Pins <paramref name="props"/> to the current texture with their values now (an exception:
    /// one undo step); the queue's settings go back to what they were before their last change.
    /// </summary>
    public string? Pin(params string[] props)
    {
        if (Current == null)
            return "no texture";
        var exBefore = TextureStore.Sorted(ExceptionsOf(Current));
        var exAfter = TextureStore.Sorted(ExceptionsOf(Current));
        var qAfter = new Godot.Collections.Dictionary();
        var qBefore = new Godot.Collections.Dictionary();
        foreach (var p in props)
        {
            if (Pinned(p))
                continue;
            exAfter[p] = Recipe.Get(p);
            if (_prior.TryGetValue(p, out var was) && !Same(was, QueueSettings.Get(p)))
            {
                qAfter[p] = was;
                qBefore[p] = QueueSettings.Get(p);
            }
        }
        if (exAfter.Count == exBefore.Count)
            return "pinned already";
        Undo.CreateAction($"Materials: pin {LabelOf(props[0])}{(props.Length > 1 ? " ..." : "")} to {RemasterTextures.ShortName(Current)}", UndoRedo.MergeMode.Disable, _plugin);
        Undo.AddDoMethod(this, MethodName.ApplyExceptions, Current, exAfter);
        Undo.AddUndoMethod(this, MethodName.ApplyExceptions, Current, exBefore);
        if (qAfter.Count > 0)
        {
            Undo.AddDoMethod(this, MethodName.ApplyRecipe, qAfter);
            Undo.AddUndoMethod(this, MethodName.ApplyRecipe, qBefore);
        }
        Undo.CommitAction();
        return null;
    }

    /// <summary>Unpins <paramref name="props"/> from the current texture: it takes the queue's values again (one undo step).</summary>
    public string? ClearException(params string[] props)
    {
        if (Current == null)
            return "no texture";
        var before = TextureStore.Sorted(ExceptionsOf(Current));
        var after = TextureStore.Sorted(before);
        foreach (var p in props)
            after.Remove(p);
        if (after.Count == before.Count)
            return "not pinned";
        Undo.CreateAction($"Materials: unpin {LabelOf(props[0])}{(props.Length > 1 ? " ..." : "")} from {RemasterTextures.ShortName(Current)}", UndoRedo.MergeMode.Disable, _plugin);
        Undo.AddDoMethod(this, MethodName.ApplyExceptions, Current, after);
        Undo.AddUndoMethod(this, MethodName.ApplyExceptions, Current, before);
        Undo.CommitAction();
        return null;
    }

    /// <summary>Unpins every setting of the current texture (one undo step).</summary>
    public string? ClearExceptions()
    {
        if (Current == null)
            return "no texture";
        var keys = ExceptionsOf(Current).Keys.Select(k => k.AsString()).ToArray();
        return keys.Length == 0 ? "no exceptions" : ClearException(keys);
    }

    /// <summary>The profile box as things are: the profiles (every layer's, the winner per name), the queue against its profile, the exceptions, the run, the changed profiles.</summary>
    private void RefreshProfileBox()
    {
        if (_profilePick == null)
            return;
        _profilePick.Clear();
        _profilePick.AddItem("(none: the queue's own)");
        _profilePick.SetItemMetadata(0, "");
        int sel = 0;
        foreach (var (name, r) in MaterialProfiles.All)
        {
            _profilePick.AddItem(r.Title + (r.Layer is "base" or "project" ? "" : $" ({r.Layer})"));
            int i = _profilePick.ItemCount - 1;
            _profilePick.SetItemMetadata(i, name);
            _profilePick.SetItemTooltip(i, $"{r.Path} (layer {r.Layer})");
            if (name == ProfileName)
                sel = i;
        }
        _profilePick.Selected = sel;
        if (ProfileName.Length == 0)
            _profileState.Text = "No profile: the queue's own settings.";
        else if (MaterialProfiles.Load(ProfileName) is not { } p)
            _profileState.Text = $"{ProfileName}: no such profile any more.";
        else
        {
            int diff = MaterialProfiles.Props.Count(x => !MaterialProfiles.Same(QueueSettings, p, x));
            var layer = MaterialProfiles.Find(ProfileName)!.Layer;
            _profileState.Text = (diff == 0 ? $"The queue's settings are {p.Title}'s." : $"● {diff} setting(s) differ from {p.Title}: Save writes them, Revert drops them.")
                + (layer is "base" or "project" ? "" : $" From the pack {layer}.");
        }
        int pinned = Current != null ? ExceptionsOf(Current).Count : 0;
        _exceptionsLabel.Text = Current == null ? "No texture" : $"● {pinned} exception(s) on {RemasterTextures.ShortName(Current)}";
        var stale = ProfileStore.Stale();
        _staleLabel.Text = ProfileStore.StaleText(stale);
        _makeAgain.Visible = stale.Count > 0;
        bool running = _bakeRun != null;
        _applyButton.Disabled = running || Queue.Count == 0;
        _makeAgain.Disabled = running;
        _cancelButton.Disabled = !running;
        _progress.Value = _bakeRun?.Progress ?? 0;
    }

    /// <summary>The project's textures changed (an edit, undo): the queue's marks and the changed profiles again.</summary>
    public void TexturesChanged()
    {
        FillQueue();
        RefreshProfileBox();
    }

    /// <summary>A profile was written, renamed or deleted (or undone): the list, the queue against it, the marks.</summary>
    public void ProfilesChanged()
    {
        if (ProfileName.Length > 0 && MaterialProfiles.Find(ProfileName) == null)
        {
            // renamed (undone or redone): follow it when one profile has the queue's settings
            var match = MaterialProfiles.All.Keys.FirstOrDefault(n => MaterialProfiles.Load(n) is { } p && MaterialProfiles.Hash(p) == MaterialProfiles.Hash(QueueSettings));
            if (match != null)
                ProfileName = match;
        }
        FillQueue();
        RefreshProfileBox();
    }

    // ---- Apply to queue, Make again (R2b phase 3) --------------------------------------------------

    private QueueBake? _bakeRun;

    /// <summary>The run going (null: none).</summary>
    public QueueBake? BakeRun => _bakeRun;

    /// <summary>The last run's outcome (null: none yet), and its texture times.</summary>
    public string? BakeResult { get; private set; }

    public List<double> LastBakeTimes { get; private set; } = new();

    /// <summary>
    /// Makes every texture of the queue with the queue's settings and its own exceptions at the
    /// full size in the background (GPU when there is one); the entries set as one undo step when
    /// the run ends. An error or null.
    /// </summary>
    public string? ApplyToQueue()
    {
        if (_bakeRun != null)
            return "a run is going";
        if (Queue.Count == 0)
            return "the queue is empty";
        var jobs = Queue.Select(id => new QueueBake.Job(id, Compose(QueueSettings, ExceptionsOf(id)), ProfileName)).ToList();
        var title = ProfileName.Length > 0 ? MaterialProfiles.Find(ProfileName)?.Title ?? ProfileName : "";
        Start(jobs, $"Apply to queue: {jobs.Count} material(s){(title.Length > 0 ? $" ({title})" : "")}");
        return null;
    }

    /// <summary>
    /// Makes again every texture of the project whose profile changed since it was made (only
    /// <paramref name="profile"/>'s when given): the profile as it is now with the texture's own
    /// exceptions, in the background, one undo step. An error or null.
    /// </summary>
    public string? MakeAgainStale(string? profile = null)
    {
        if (_bakeRun != null)
            return "a run is going";
        var stale = ProfileStore.Stale();
        var jobs = new List<QueueBake.Job>();
        foreach (var (name, ids) in stale)
        {
            if (profile != null && name != profile || MaterialProfiles.Load(name) is not { } p)
                continue;
            foreach (var id in ids)
                jobs.Add(new QueueBake.Job(id, MaterialProfiles.Effective(p, TextureStore.Find(id)?.Exceptions), name));
        }
        if (jobs.Count == 0)
            return "no texture's profile changed";
        Start(jobs, $"Make again: {ProfileStore.StaleText(stale)}");
        return null;
    }

    /// <summary>
    /// Makes <paramref name="ids"/> with profile <paramref name="profile"/> as it is (each with its
    /// own exceptions), in the background, one undo step; the queue is not touched (R4's wizard
    /// and the checks choose a profile per texture). An error or null.
    /// </summary>
    public string? MakeWith(IEnumerable<string> ids, string profile)
    {
        if (_bakeRun != null)
            return "a run is going";
        if (MaterialProfiles.Load(profile) is not { } p)
            return $"no profile {profile}";
        var jobs = ids.Select(id => id.ToLowerInvariant()).Distinct()
            .Select(id => new QueueBake.Job(id, MaterialProfiles.Effective(p, TextureStore.Find(id) is { Profile.Length: > 0 } e && e.Profile == profile ? e.Exceptions : null), profile)).ToList();
        if (jobs.Count == 0)
            return "no textures";
        Start(jobs, $"Make with {p.Title}: {jobs.Count} material(s)");
        return null;
    }

    /// <summary>Stops the run going: what it made is kept (one undo step). An error or null.</summary>
    public string? CancelBake()
    {
        if (_bakeRun == null)
            return "no run going";
        _bakeRun.Cancel();
        return null;
    }

    private void Start(List<QueueBake.Job> jobs, string action)
    {
        BakeResult = null;
        _bakeRun = new QueueBake(this, jobs, _gpu != null, LoadImport, FinishJob, action) { Upscaled = UpscaledFor };
        Say($"{action}: making {jobs.Count} on the {(_gpu != null ? "GPU" : "CPU")}...");
        FillQueue();
        RefreshProfileBox();
    }

    private void TickBake()
    {
        if (_bakeRun is not { } run || !run.Tick())
            return;
        _progress.Value = run.Progress;
        if (run.LastAlbedo is { } last && run.LastId is { } lastId)
        {
            _lastMade.Texture = _thumbs.GetValueOrDefault(lastId);
            _lastMadeLabel.Text = $"{run.Made}/{run.Total}: {RemasterTextures.ShortName(lastId)}";
        }
        FillQueue();
        if (!run.Finished)
            return;
        _bakeRun = null;
        LastBakeTimes = run.Times;
        int failed = run.Jobs.Count(j => run.StateOf(j.Id) == QueueBake.JobState.Failed);
        int cancelled = run.Jobs.Count(j => run.StateOf(j.Id) == QueueBake.JobState.Cancelled);
        // the run's entries as one undo step (its files are written: content-named)
        if (run.Results.Count > 0)
            _plugin.SetTextures(run.Results, run.Action + (run.Cancelling ? $" (cancelled after {run.Results.Count})" : ""));
        foreach (var (id, _) in run.Results)
            _savedHere.Add(id);
        BakeResult = $"{run.Action}: {run.Results.Count} made in {run.Clock.Elapsed.TotalSeconds:0.0} s"
            + (cancelled > 0 ? $", {cancelled} cancelled" : "") + (failed > 0 ? $", {failed} failed" : "");
        Say(BakeResult);
        FillQueue();
        RefreshProfileBox();
    }

    /// <summary>A run's texture made: its imported files kept beside it, its maps and material written, its entry (the profile and the exceptions to it).</summary>
    private RemasterTexture FinishJob(QueueBake.Job job, BakedMaps baked)
    {
        var r = (PbrRecipe)job.Recipe.Duplicate();
        var entry = MakeEntry(job.Id, baked, r, job.Profile);
        SetMaterialSourceNote(entry, UpscaleNote(r, _bakeRun?.ComfyUsed.Contains(job.Id) == true));
        var thumb = PbrBuilder.ToImage(baked.Maps[MaterialStep.Albedo]);
        thumb.Resize(48, Mathf.Max(1, 48 * thumb.GetHeight() / Mathf.Max(1, thumb.GetWidth())), Image.Interpolation.Bilinear);
        _thumbs[job.Id] = ImageTexture.CreateFromImage(thumb);
        return entry;
    }

    /// <summary>
    /// Writes <paramref name="id"/>'s material made with <paramref name="r"/> (its imported files
    /// copied beside it first) and returns its entry, linked to <paramref name="profile"/> (its
    /// hash now, the settings that differ from it as exceptions; none: unlinked).
    /// </summary>
    private RemasterTexture MakeEntry(string id, BakedMaps baked, PbrRecipe r, string profile)
    {
        StoreImports(id, r);
        var maps = MaterialBaker.Images(baked, r);
        string hash = "";
        Godot.Collections.Dictionary? ex = null;
        if (profile.Length > 0 && MaterialProfiles.Load(profile) is { } p)
            (hash, ex) = (MaterialProfiles.Hash(p), MaterialProfiles.Differences(r, p));
        else
            profile = "";
        return TextureActions.WithMaterial(id, maps, r, profile, hash, ex);
    }

    /// <summary>The steps' imported files of <paramref name="r"/> kept beside <paramref name="id"/>'s maps (content-named), <paramref name="r"/> naming them relative to the store.</summary>
    private void StoreImports(string id, PbrRecipe r)
    {
        // the detail's own files, kept as the imports are
        if (r.DetailOn && r.DetailFrom == DetailFrom.Files)
            foreach (var (prop, role, preset) in new[] { ("DetailAlbedoFile", "import-detail-albedo", TexturePreset.Colour), ("DetailNormalFile", "import-detail-normal", TexturePreset.Normal) })
            {
                var file = (string)MaterialPipeline.Prop(prop).GetValue(r)!;
                if (file.Length == 0 || LoadImport(file) is not { } dpx)
                    continue;
                var rel = TextureStore.WriteImage(id, role, PbrBuilder.ToImage(dpx), preset);
                _imports[rel] = dpx;
                r.Set(prop, rel);
            }
        foreach (var s in MaterialPipeline.Steps)
        {
            var (_, fp) = MaterialPipeline.ModeProps(s);
            var file = MaterialPipeline.FileOf(r, s);
            if (!MaterialPipeline.On(r, s) || MaterialPipeline.ModeOf(r, s) != StepMode.Import || file.Length == 0 || LoadImport(file) is not { } px)
                continue;
            var rel = TextureStore.WriteImage(id, $"import-{s.ToString().ToLowerInvariant()}", PbrBuilder.ToImage(px),
                s is MaterialStep.Source or MaterialStep.Albedo or MaterialStep.Emission ? TexturePreset.Colour : s is MaterialStep.Normal or MaterialStep.BentNormal ? TexturePreset.Normal : TexturePreset.Mask);
            _imports[rel] = px;
            r.Set(fp, rel);
        }
    }

    // ---- save --------------------------------------------------------------------------------------

    private int _saveRun = -1;
    private string? _saveId;
    private PbrRecipe? _saveRecipe;
    private readonly Dictionary<MaterialStep, RgbaImage> _saveMaps = new();
    private readonly Dictionary<string, RgbaImage> _saveAux = new();
    private RgbaImage? _saveOrm;
    private Image? _saveHeight;
    private GpuBake? _bake;
    /// <summary>Whether the save's source is ComfyUI's upscale (its Source note).</summary>
    private bool _saveComfy;

    /// <summary>Whether a save is going; the last save's outcome (null: none yet).</summary>
    public bool Saving => _saveRun >= 0 || _bake != null;
    public string? SaveResult { get; private set; }

    /// <summary>The last save's time (ms: every map at the full size, read back; the files not included).</summary>
    public double SaveMakeMs { get; private set; }

    private System.Diagnostics.Stopwatch _saveClock = new();

    /// <summary>
    /// Makes every map at the full size (on the GPU in one frame, else on the CPU in the
    /// background; not cancelled by changes), then writes them and the material (one undo step).
    /// An error or null.
    /// </summary>
    public string? Save()
    {
        if (Current == null || _original == null)
            return "nothing to save: add a texture to the queue";
        if (Saving)
            return "a save is going";
        _saveRecipe = (PbrRecipe)Recipe.Duplicate();
        _saveRecipe.Version = 3;
        _saveId = Current;
        _saveComfy = ComfySourceInUse;
        _saveMaps.Clear();
        _saveAux.Clear();
        (_saveOrm, _saveHeight) = (null, null);
        SaveResult = null;
        _saveClock = System.Diagnostics.Stopwatch.StartNew();
        var inputs = new MaterialPipeline.Inputs(Current, _original, _saveRecipe, new Dictionary<string, RgbaImage>(_imports));
        _save.Disabled = true;
        if (_gpu != null)
        {
            var (w, h, scale) = MaterialPipeline.Size(inputs, MaterialPipeline.FullSide);
            // the last bake's passes when kept (no targets allocated again at the same size)
            _bake = new GpuBake(this, inputs, w, h, scale, BakeBackends.Take(this));
            return null;
        }
        _saveRun = _pipe.Request(inputs, Step, [MaterialPipeline.FullSide], keep: true);
        // the preview's run was cancelled by the save's turn: asked for again after it
        _run = -1;
        return null;
    }

    private void FinishBake()
    {
        var (baked, _) = _bake!.Finish(recipe: _saveRecipe);
        BakeBackends.Return(_bake.Backend);
        _bake = null;
        foreach (var (s, img) in baked.Maps)
            _saveMaps[s] = img;
        foreach (var (name, img) in baked.Aux ?? new())
            _saveAux[name] = img;
        _saveOrm = baked.Orm;
        var height = baked.Height;
        height.Convert(Image.Format.Rh);
        _saveHeight = height;
        SaveMakeMs = _saveClock.Elapsed.TotalMilliseconds;
        _save.Disabled = false;
        SaveResult = Write();
        Say(SaveResult);
    }

    private void Collect(MaterialPipeline.Result r)
    {
        if (r.Step is { } s)
        {
            _saveMaps[s] = r.Image;
            if (r.Float is { } f)
            {
                var bytes = new byte[f.Length * 4];
                System.Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
                _saveHeight = Image.CreateFromData(r.Image.Width, r.Image.Height, false, Image.Format.Rf, bytes);
                _saveHeight.Convert(Image.Format.Rh);
            }
        }
        else if (r.Side != 0 && r.Kind == ResultKind.Orm)
            _saveOrm = r.Image;
        else if (r.Kind == ResultKind.Aux && r.Aux is { } aux)
            _saveAux[aux] = r.Image;
        if (!r.Last)
            return;
        _saveRun = -1;
        _save.Disabled = false;
        SaveMakeMs = _saveClock.Elapsed.TotalMilliseconds;
        SaveResult = _saveOrm != null && _saveMaps.Count == MaterialPipeline.Active(_saveRecipe!).Count ? Write() : $"the save failed: {_pipe.Error}";
        Say(SaveResult);
        Remake();
    }

    private string Write()
    {
        var id = _saveId!;
        var r = _saveRecipe!;
        // the height in 16 bits (half floats: an EXR), so parallax shows no terraces
        var baked = new BakedMaps(new Dictionary<MaterialStep, RgbaImage>(_saveMaps), _saveOrm!, _saveHeight ?? PbrBuilder.ToImage(_saveMaps[MaterialStep.Height]),
            new Dictionary<string, RgbaImage>(_saveAux));
        var entry = MakeEntry(id, baked, r, ProfileName);
        SetMaterialSourceNote(entry, UpscaleNote(r, _saveComfy));
        _plugin.SetTexture(id, entry, $"Save the material of {RemasterTextures.ShortName(id)}");
        _savedHere.Add(id);
        FillQueue();
        var albedo = TextureStore.Absolute(entry.Material);
        return $"{RemasterTextures.ShortName(id)}: saved ({_saveMaps[MaterialStep.Albedo].Width}×{_saveMaps[MaterialStep.Albedo].Height} maps, {albedo.GetFile()}"
            + (entry.Profile.Length > 0 ? $", profile {entry.Profile}{(entry.Exceptions.Count > 0 ? $" with {entry.Exceptions.Count} exception(s)" : "")})" : ")");
    }
}
#endif
