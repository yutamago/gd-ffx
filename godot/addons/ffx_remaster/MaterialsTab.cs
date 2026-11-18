#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The <b>Materials</b> tab (docs/remaster-materials.md; R2b phase 1): the material workbench in
/// Godot's main area, beside 2D, 3D, Script and Game. The queue of textures (left; arrow keys or
/// a click switch texture and keep the settings), the big 3D preview (centre:
/// <see cref="MaterialPreview"/>), the current step's map in 2D and only that step's settings
/// (right: <see cref="MapView2D"/>), the strip of every step's map (bottom) and Save.
/// <para>The maps are made by <see cref="MaterialPipeline"/> off the main thread: a change asks
/// for every map at 256 px, then at the working size; only the maps downstream of the change are
/// made again. Settings live in a <see cref="PbrRecipe"/> (<see cref="Recipe"/>); every change is
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
    private Label _stepTitle = null!, _stepNote = null!;
    private MapView2D _map2d = null!;
    private CheckBox _tiled = null!, _split = null!;
    private OptionButton _mode = null!;
    private Button _importButton = null!;
    private VBoxContainer _settingsBox = null!;
    private readonly Dictionary<string, Control> _controls = new();
    private EditorFileDialog? _fileDialog;
    // the strip
    private readonly Dictionary<MaterialStep, (Button Button, TextureRect Thumb)> _strip = new();
    private Button _save = null!;
    private Label _timing = null!;

    /// <summary>The textures being worked on (IDs).</summary>
    public List<string> Queue { get; } = new();

    /// <summary>The texture shown (null: none).</summary>
    public string? Current { get; private set; }

    public MaterialStep Step { get; private set; } = MaterialStep.Albedo;

    /// <summary>The current settings (kept when the texture changes).</summary>
    public PbrRecipe Recipe { get; private set; } = new() { Version = 2 };

    public MaterialPreview Preview => _preview;

    private RgbaImage? _original;
    private readonly ImageTexture _sourceTex = new();
    private readonly MaterialPipeline _pipe = new();
    private int _run = -1;
    private readonly Dictionary<MaterialStep, ImageTexture> _maps = new();
    private readonly Dictionary<MaterialStep, RgbaImage> _pixels = new();
    private readonly Dictionary<MaterialStep, int> _shownSide = new();
    private readonly ImageTexture _orm = new();
    private RgbaImage? _ormPixels;
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
    public bool Idle => _runDone && !_pipe.Busy && _pipe.Results.IsEmpty && _saveRun < 0;

    /// <summary>The order the last run's maps arrived in (side, step): the checks see small before refined.</summary>
    public List<(int Side, MaterialStep? Step)> Arrivals { get; } = new();

    public MaterialsTab(RemasterPlugin plugin)
    {
        _plugin = plugin;
        Name = "FfxMaterials";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        Build();
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
        Add(row, "Its settings", "Load the selected texture's saved settings (its recipe) instead of the current ones", LoadSaved);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 60) };
        col.AddChild(_status);
        return col;
    }

    private Control BuildPreview()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2.2f };
        _preview = new MaterialPreview();
        col.AddChild(_preview);
        var row = new HBoxContainer();
        col.AddChild(row);
        _shape = new OptionButton { TooltipText = "The object: Object is the game piece that draws the texture (from the open map, else the first map that uses it)" };
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
        col.AddChild(new Label { Text = "Drag: orbit · wheel: zoom · middle: pan · Shift+drag: light · Alt+drag: environment · F: frame", Modulate = new Color(1, 1, 1, 0.6f) });
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
        col.AddChild(_map2d);
        var row = new HBoxContainer();
        col.AddChild(row);
        _tiled = new CheckBox { Text = "2×2", TooltipText = "Tile the map 2×2: seams show" };
        _tiled.Toggled += on => _map2d.Tiled = on;
        row.AddChild(_tiled);
        _split = new CheckBox { Text = "Before | after", TooltipText = "The source texture left of the split, the map right of it (Shift+move in the view moves the split)" };
        _split.Toggled += on => _map2d.Split = on ? 0.5f : -1f;
        row.AddChild(_split);
        Add(row, "Whole", "Back to the whole map (or double click)", () => _map2d.Reset());
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
            _strip[s] = (b, thumb);
        }
        row.AddChild(new VSeparator());
        var end = new VBoxContainer();
        row.AddChild(end);
        _save = new Button { Text = "Save", TooltipText = "Make the maps at the full size and write them and the material into the project's texture layer (one undo step)", CustomMinimumSize = new Vector2(110, 40) };
        _save.Pressed += () => Say(Save() ?? "saving...");
        end.AddChild(_save);
        _timing = new Label { Modulate = new Color(1, 1, 1, 0.7f) };
        end.AddChild(_timing);
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
            if (_savedHere.Contains(id)) marks.Add("saved");
            else if (RemasterTextures.For(id)?.HasMaterial == true) marks.Add("PBR");
            if (RemasterTextures.For(id)?.Replaced == true) marks.Add("replaced");
            int i = _queueList.AddItem((id == Current ? "▶ " : "   ") + RemasterTextures.ShortName(id) + (marks.Count > 0 ? $"  ({string.Join(", ", marks)})" : ""));
            _queueList.SetItemTooltip(i, id);
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

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (!IsVisibleInTree() || e is not InputEventKey { Pressed: true } k)
            return;
        var focus = GetViewport().GuiGetFocusOwner();
        if (focus != null && !IsAncestorOf(focus))
            return;
        if (k.Keycode is Key.Down or Key.Up && focus is not ItemList)
        {
            Next(k.Keycode == Key.Down ? 1 : -1);
            GetViewport().SetInputAsHandled();
        }
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
        _original = PbrBuilder.From(img);
        _alpha = !_original.HasAlpha ? 0 : _original.HasOnlyBinaryAlpha ? 1 : 2;
        _sourceTex.SetImage(PbrBuilder.ToImage(_original));
        foreach (var t in _maps.Values)
            t.SetImage(Image.CreateEmpty(1, 1, false, Image.Format.Rgba8));
        _pixels.Clear();
        _shownSide.Clear();
        FillQueue();
        FindObject(id);
        UpdateLight();
        Remake();
        UpdateMaterials();
        Say($"{RemasterTextures.ShortName(id)}: {_original.Width}×{_original.Height}{(_alpha == 1 ? ", cut-out" : _alpha == 2 ? ", blended alpha" : "")}");
        return null;
    }

    private void LoadSaved()
    {
        if (Current == null || TextureStore.Find(Current)?.Recipe is not { } r)
        {
            Say("no saved settings for this texture");
            return;
        }
        var copy = (PbrRecipe)r.Duplicate();
        MaterialPipeline.Migrate(copy);
        var d = new Godot.Collections.Dictionary();
        foreach (var name in RecipeProps())
            d[name] = copy.Get(name);
        Commit("Load the texture's saved settings", d);
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
        [MaterialStep.Source] = "The colour texture the maps start from (the replacement, else the classic one), upscaled; or an image of your own (Import).",
        [MaterialStep.Albedo] = "R2's de-lighting: the painted light out of the colour. Skip: the source as it is.",
        [MaterialStep.Height] = "From the albedo (or the source): blurred brightness. Skip: flat (no parallax).",
        [MaterialStep.Normal] = "The height's slope, OpenGL convention (green up, as Godot reads it). Skip: flat.",
        [MaterialStep.Roughness] = "From the albedo: darker and busier is rougher. Skip: the base value everywhere.",
        [MaterialStep.Metallic] = "Most FFX textures skip it (the value everywhere). Generate: a mask from brightness.",
        [MaterialStep.Occlusion] = "Horizon occlusion from the height. Skip: none (white).",
        [MaterialStep.Emission] = "Usually skipped. Generate: the albedo where its brightest channel passes the threshold.",
    };

    private void RebuildSettings()
    {
        _stepTitle.Text = $"{(int)Step} · {Step}";
        _stepNote.Text = Notes[Step];
        foreach (var c in _settingsBox.GetChildren())
            c.QueueFree();
        _controls.Clear();
        foreach (var st in MaterialPipeline.Settings[Step])
        {
            var row = new HBoxContainer();
            _settingsBox.AddChild(row);
            row.AddChild(new Label { Text = st.Label, CustomMinimumSize = new Vector2(130, 0), TooltipText = st.Tip, MouseFilter = MouseFilterEnum.Pass });
            var type = MaterialPipeline.Prop(st.Property).PropertyType;
            var prop = st.Property;
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
                ob.ItemSelected += i => SetSetting(prop, (int)i);
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
            var reset = new Button { Text = "↺", TooltipText = $"{st.Label} back to its default", Flat = true };
            reset.Pressed += () => ResetSetting(prop);
            row.AddChild(reset);
        }
        RefreshControls();
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
                case Range r:
                    r.SetValueNoSignal(v.AsDouble());
                    break;
            }
        }
        var mode = MaterialPipeline.ModeOf(Recipe, Step);
        _mode.Selected = (int)mode;
        _mode.SetItemDisabled((int)StepMode.Skip, Step == MaterialStep.Source);
        var file = MaterialPipeline.FileOf(Recipe, Step);
        _importButton.Text = mode == StepMode.Import && file.Length > 0 ? $"Import... ({file.GetFile()})" : "Import...";
        RefreshStrip();
    }

    private void RefreshStrip()
    {
        foreach (var s in MaterialPipeline.Steps)
        {
            var mode = MaterialPipeline.ModeOf(Recipe, s);
            string mark = mode == StepMode.Skip ? " –" : mode == StepMode.Import ? " ⇩" : _shownSide.GetValueOrDefault(s) >= _workingSide && _workingSide > 0 ? " ✓" : " …";
            _strip[s].Button.Text = $"{(int)s} {s}{mark}";
            _strip[s].Thumb.Texture = s == MaterialStep.Source ? (_maps.TryGetValue(s, out var src) ? src : _sourceTex) : _maps.GetValueOrDefault(s);
            if (s == Step)
                _strip[s].Button.SetPressedNoSignal(true);
        }
    }

    private void ShowStep()
    {
        var map = Step == MaterialStep.Source ? _maps.GetValueOrDefault(Step) ?? _sourceTex : _maps.GetValueOrDefault(Step);
        _map2d.SetMaps(map, _sourceTex);
        RefreshStrip();
    }

    // ---- settings and undo ------------------------------------------------------------------------

    private EditorUndoRedoManager Undo => _plugin.GetUndoRedo();

    /// <summary>Every recipe property the tab edits (the steps' settings, modes and files).</summary>
    public static IEnumerable<string> RecipeProps()
    {
        foreach (var s in MaterialPipeline.Steps)
        {
            var (m, f) = MaterialPipeline.ModeProps(s);
            yield return m;
            yield return f;
            foreach (var st in MaterialPipeline.Settings[s])
                yield return st.Property;
        }
    }

    /// <summary>Sets recipe properties (the undo history calls it), then makes again what they change.</summary>
    public void ApplyRecipe(Godot.Collections.Dictionary values)
    {
        foreach (var (k, v) in values)
            Recipe.Set(k.AsString(), v);
        Changed(values.Keys.Select(k => k.AsString()).ToList());
    }

    /// <summary>One undo step that sets <paramref name="after"/> (the old values from the recipe); <paramref name="execute"/>: false when they are set already (a drag's end).</summary>
    private void Commit(string action, Godot.Collections.Dictionary after, Godot.Collections.Dictionary? before = null, bool execute = true)
    {
        before ??= new Godot.Collections.Dictionary();
        foreach (var (k, _) in after)
            if (!before.ContainsKey(k))
                before[k] = Recipe.Get(k.AsString());
        Undo.CreateAction(action, UndoRedo.MergeMode.Disable, _plugin);
        Undo.AddDoMethod(this, MethodName.ApplyRecipe, after);
        Undo.AddUndoMethod(this, MethodName.ApplyRecipe, before);
        Undo.CommitAction(execute);
    }

    private static string LabelOf(string prop) =>
        MaterialPipeline.Settings.Values.SelectMany(s => s).FirstOrDefault(s => s.Property == prop)?.Label ?? prop;

    /// <summary>Sets one setting (one undo step).</summary>
    public void SetSetting(string prop, Variant value)
    {
        if (Same(Recipe.Get(prop), value))
            return;
        var after = new Godot.Collections.Dictionary { [prop] = value };
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
            Recipe.Set(prop, value);
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

    private void ChooseImport(MaterialStep s)
    {
        _fileDialog ??= new EditorFileDialog
        {
            FileMode = EditorFileDialog.FileModeEnum.OpenFile, Access = EditorFileDialog.AccessEnum.Filesystem,
            Filters = ["*.png ; PNG", "*.webp ; WebP", "*.jpg, *.jpeg ; JPEG"],
        };
        _fileDialog.Title = $"A map of your own for {s}";
        if (_fileDialog.GetParent() == null)
            AddChild(_fileDialog);
        foreach (var c in _fileDialog.GetSignalConnectionList(FileDialog.SignalName.FileSelected))
            _fileDialog.Disconnect(FileDialog.SignalName.FileSelected, c["callable"].AsCallable());
        _fileDialog.FileSelected += file => Say(Import(s, file) ?? $"{s}: {file.GetFile()}");
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
        if (_imports.TryGetValue(file, out var have))
            return have;
        var path = file.StartsWith("res://", System.StringComparison.Ordinal) || file.StartsWith("user://", System.StringComparison.Ordinal)
            || System.IO.Path.IsPathRooted(file) ? file : TextureStore.Absolute(file);
        var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(path));
        if (img == null || img.IsEmpty())
            return null;
        return _imports[file] = PbrBuilder.From(img);
    }

    /// <summary>After settings changed: the controls, then the maps (or only the material for its own settings).</summary>
    private void Changed(IReadOnlyCollection<string> props)
    {
        RefreshControls();
        if (props.All(MaterialPipeline.MaterialOnly.Contains))
            UpdateMaterials();
        else
            Remake();
    }

    // ---- making the maps --------------------------------------------------------------------------

    private ulong _requestUsec;
    private bool _gotSmall, _gotRefined;

    /// <summary>Asks for every map with the current settings: 256 px first, then the working size (the run before cancelled).</summary>
    public void Remake()
    {
        if (Current == null || _original == null)
            return;
        var snap = (PbrRecipe)Recipe.Duplicate();
        foreach (var s in MaterialPipeline.Steps)
            if (MaterialPipeline.ModeOf(snap, s) == StepMode.Import)
                LoadImport(MaterialPipeline.FileOf(snap, s));
        var inputs = new MaterialPipeline.Inputs(Current, _original, snap, new Dictionary<string, RgbaImage>(_imports));
        var (w, h, _) = MaterialPipeline.Size(inputs, Mathf.Max(MaterialPipeline.SmallSide, snap.WorkingSide));
        _workingSide = Mathf.Max(w, h);
        var (sw, sh, _) = MaterialPipeline.Size(inputs, MaterialPipeline.SmallSide);
        int small = Mathf.Max(sw, sh);
        int[] sides = small < _workingSide ? [MaterialPipeline.SmallSide, snap.WorkingSide] : [snap.WorkingSide];
        _run = _pipe.Request(inputs, Step, sides);
        _requestUsec = Time.GetTicksUsec();
        (_gotSmall, _gotRefined, _runDone) = (false, false, false);
        Arrivals.Clear();
        _sides = sides;
    }

    private int[] _sides = [];

    public void Tick(double delta)
    {
        int shown = 0;
        while (shown < 6 && _pipe.Results.TryDequeue(out var r))
        {
            if (r.Run == _saveRun)
            {
                Collect(r);
                continue;
            }
            if (r.Run != _run)
                continue;
            shown++;
            Show(r);
        }
        PollObject();
    }

    private void Show(MaterialPipeline.Result r)
    {
        Arrivals.Add((r.Side, r.Step));
        bool small = _sides.Length > 1 && r.Side == _sides[0];
        if (r.Step is { } s)
        {
            if (!_maps.TryGetValue(s, out var tex))
                _maps[s] = tex = new ImageTexture();
            var img = PbrBuilder.ToImage(r.Image);
            if (!small)
                img.GenerateMipmaps();
            tex.SetImage(img);
            _pixels[s] = r.Image;
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
            _timing.Text = $"{(_gotSmall ? $"{Latency.Small:0} ms small, " : "")}{Latency.Refined:0} ms refined, all {Latency.All:0} ms";
            if (_pipe.Error is { } err)
                Say($"the pipeline failed: {err}");
        }
        UpdateMaterials();
        RefreshStrip();
    }

    /// <summary>The pixels of <paramref name="s"/>'s map as shown (the checks).</summary>
    public RgbaImage? Pixels(MaterialStep s) => _pixels.GetValueOrDefault(s);

    /// <summary>The ORM pack as shown.</summary>
    public RgbaImage? OrmPixels => _ormPixels;

    /// <summary>The 2D view (the checks: its maps).</summary>
    public MapView2D Map2D => _map2d;

    // ---- the preview's materials -------------------------------------------------------------------

    private void UpdateMaterials()
    {
        var r = Recipe;
        float t = (float)_tile.Value;
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
        _mat.OrmTexture = _ormPixels != null ? _orm : null;
        (_mat.Roughness, _mat.Metallic, _mat.MetallicSpecular) = _ormPixels != null ? (1f, 1f, 0.5f) : (0.85f, 0f, 0.5f);
        bool height = r.HeightScale > 0 && MaterialPipeline.ModeOf(r, MaterialStep.Height) != StepMode.Skip && _maps.ContainsKey(MaterialStep.Height);
        _mat.HeightmapEnabled = height;
        _mat.HeightmapTexture = height ? _maps[MaterialStep.Height] : null;
        _mat.HeightmapScale = r.HeightScale * 100f;
        bool emission = MaterialPipeline.ModeOf(r, MaterialStep.Emission) != StepMode.Skip && _maps.ContainsKey(MaterialStep.Emission);
        _mat.EmissionEnabled = emission;
        _mat.EmissionTexture = emission ? _maps[MaterialStep.Emission] : null;
        _mat.Emission = Colors.White;
        _mat.EmissionEnergyMultiplier = r.EmissionEnergy;
        bool classic = _compare.Selected == 1;
        _preview.UseClassic = classic;
        _preview.SetMaterials(_mat, classic ? _unshaded : _raw);
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

    /// <summary>The preview's material (the checks).</summary>
    public OrmMaterial3D Material3D => _mat;

    // ---- save --------------------------------------------------------------------------------------

    private int _saveRun = -1;
    private string? _saveId;
    private PbrRecipe? _saveRecipe;
    private readonly Dictionary<MaterialStep, RgbaImage> _saveMaps = new();
    private RgbaImage? _saveOrm;

    /// <summary>Whether a save is going; the last save's outcome (null: none yet).</summary>
    public bool Saving => _saveRun >= 0;
    public string? SaveResult { get; private set; }

    /// <summary>Makes every map at the full size (in the background; not cancelled by changes), then writes them and the material (one undo step). An error or null.</summary>
    public string? Save()
    {
        if (Current == null || _original == null)
            return "nothing to save: add a texture to the queue";
        if (_saveRun >= 0)
            return "a save is going";
        _saveRecipe = (PbrRecipe)Recipe.Duplicate();
        _saveRecipe.Version = 2;
        _saveId = Current;
        _saveMaps.Clear();
        _saveOrm = null;
        SaveResult = null;
        var inputs = new MaterialPipeline.Inputs(Current, _original, _saveRecipe, new Dictionary<string, RgbaImage>(_imports));
        _saveRun = _pipe.Request(inputs, Step, [MaterialPipeline.FullSide], keep: true);
        // the preview's run was cancelled by the save's turn: asked for again after it
        _run = -1;
        _save.Disabled = true;
        return null;
    }

    private void Collect(MaterialPipeline.Result r)
    {
        if (r.Step is { } s)
            _saveMaps[s] = r.Image;
        else if (r.Side != 0)
            _saveOrm = r.Image;
        if (!r.Last)
            return;
        _saveRun = -1;
        _save.Disabled = false;
        SaveResult = _saveOrm != null && _saveMaps.Count == MaterialPipeline.Steps.Length ? Write() : $"the save failed: {_pipe.Error}";
        Say(SaveResult);
        Remake();
    }

    private string Write()
    {
        var id = _saveId!;
        var r = _saveRecipe!;
        // imported files: kept beside the maps (content-named), the recipe naming them relative to the store
        foreach (var s in MaterialPipeline.Steps)
        {
            var (_, fp) = MaterialPipeline.ModeProps(s);
            var file = MaterialPipeline.FileOf(r, s);
            if (MaterialPipeline.ModeOf(r, s) != StepMode.Import || file.Length == 0 || !_imports.TryGetValue(file, out var px))
                continue;
            var rel = TextureStore.WriteImage(id, $"import-{s.ToString().ToLowerInvariant()}", PbrBuilder.ToImage(px),
                s is MaterialStep.Source or MaterialStep.Albedo or MaterialStep.Emission ? TexturePreset.Colour : s == MaterialStep.Normal ? TexturePreset.Normal : TexturePreset.Mask);
            _imports[rel] = px;
            r.Set(fp, rel);
        }
        bool height = r.HeightScale > 0 && MaterialPipeline.ModeOf(r, MaterialStep.Height) != StepMode.Skip;
        bool emission = MaterialPipeline.ModeOf(r, MaterialStep.Emission) != StepMode.Skip && MaterialPipeline.AnyLight(_saveMaps[MaterialStep.Emission]);
        var maps = new PbrImages(PbrBuilder.ToImage(_saveMaps[MaterialStep.Albedo]), PbrBuilder.ToImage(_saveMaps[MaterialStep.Normal]),
            PbrBuilder.ToImage(_saveOrm!), height ? PbrBuilder.ToImage(_saveMaps[MaterialStep.Height]) : null,
            emission ? PbrBuilder.ToImage(_saveMaps[MaterialStep.Emission]) : null);
        var entry = TextureActions.WithMaterial(id, maps, r);
        _plugin.SetTexture(id, entry, $"Save the material of {RemasterTextures.ShortName(id)}");
        _savedHere.Add(id);
        FillQueue();
        return $"{RemasterTextures.ShortName(id)}: saved ({maps.Albedo.GetWidth()}×{maps.Albedo.GetHeight()} maps, {entry.Material.GetFile()})";
    }
}
#endif
