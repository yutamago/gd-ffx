#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using GdFfx.Remaster.Materialize;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// Regions within one texture (docs/remaster-materials.md, phase 6): a texture split into regions,
/// each with its own profile and exceptions, made by its own settings and composited over the
/// texture's maps with a feathered mask (<see cref="MaterializeGraph"/>). The Regions box in the
/// Source step lists them ("Whole texture" first: the texture's own settings, the queue's with its
/// exceptions), colour-coded in the 2D map; choosing one makes the other steps edit it (its profile,
/// its exceptions: every change of a region's setting is its own). Regions are proposed from the
/// game's UV islands (the parts that draw the texture), made from a picked colour, or drawn by hand
/// (rectangle, polygon, brush and eraser, optionally snapped to the islands). Every action is one
/// undo step (the list swapped as dictionaries; a mask named by its content,
/// <see cref="MaterialRegionStore"/>); Save, Apply to queue and Make again write them on the entry.
/// </summary>
public partial class MaterialsTab
{
    /// <summary>Each texture's regions as edited in this session (else its entry's).</summary>
    private readonly Dictionary<string, Godot.Collections.Array> _regions = new();

    /// <summary>The region the steps edit (-1: the whole texture's own settings).</summary>
    public int Region { get; private set; } = -1;

    /// <summary>The texture's own settings (the queue's with its exceptions), whatever region is edited.</summary>
    public PbrRecipe BaseRecipe { get; private set; } = new() { Version = 3 };

    /// <summary>Texture <paramref name="id"/>'s regions (this session's, else its entry's).</summary>
    public Godot.Collections.Array RegionsOf(string id)
    {
        if (!_regions.TryGetValue(id, out var r))
            _regions[id] = r = MaterialRegionStore.Copy(TextureStore.Find(id)?.Regions);
        return r;
    }

    /// <summary>The current texture's regions (none: empty).</summary>
    public Godot.Collections.Array Regions => Current != null ? RegionsOf(Current) : new Godot.Collections.Array();

    private Godot.Collections.Dictionary? RegionDict(int i) =>
        i >= 0 && i < Regions.Count && Regions[i].VariantType == Variant.Type.Dictionary ? Regions[i].AsGodotDictionary() : null;

    /// <summary>The region being edited's dictionary (null: the whole texture).</summary>
    private Godot.Collections.Dictionary? EditedRegion => RegionDict(Region);

    /// <summary>Whether <paramref name="prop"/> is the texture's even while a region is edited (the Source step's, the material's own).</summary>
    public static bool SharedProp(string prop) => System.Array.IndexOf(MaterialRegions.Shared, prop) >= 0;

    /// <summary>The graph's regions of the current texture (masks loaded).</summary>
    private List<RegionInput> RegionInputs() => Current == null ? new List<RegionInput>() : MaterialRegionStore.Inputs(RegionsOf(Current));

    /// <summary>The recipe the material is written and shown with: the texture's, with every map a region makes on.</summary>
    public PbrRecipe MaterialRecipe(PbrRecipe baseRecipe, Godot.Collections.Array? regions) =>
        regions is { Count: > 0 } ? MaterialRegions.Union(baseRecipe, regions.Where(v => v.VariantType == Variant.Type.Dictionary).Select(v => MaterialRegionStore.Recipe(v.AsGodotDictionary()))) : baseRecipe;

    /// <summary>Edits region <paramref name="i"/> from now on (-1: the whole texture).</summary>
    public void EditRegion(int i)
    {
        Region = i >= 0 && i < Regions.Count ? i : -1;
        _isolate = null;
        Recompute();
        RebuildSettings();
        RefreshOverlay();
        Remake();
        Say(Region < 0 ? "editing the whole texture's settings" : $"editing region {RegionName(Region)} (its own profile and settings)");
    }

    public string RegionName(int i) => RegionDict(i) is { } d ? MaterialRegionStore.NameOf(d) : "Whole texture";

    // ---- undo ------------------------------------------------------------------------------------

    /// <summary>Sets texture <paramref name="id"/>'s regions (the undo history calls it), then makes again.</summary>
    public void ApplyRegions(string id, Godot.Collections.Array regions, int select)
    {
        _regions[id] = MaterialRegionStore.Copy(regions);
        if (id != Current)
        {
            FillQueue();
            return;
        }
        Region = select >= -1 && select < regions.Count ? select : Mathf.Min(Region, regions.Count - 1);
        Recompute();
        RebuildSettings();
        RefreshOverlay();
        FillQueue();
        Remake();
    }

    /// <summary>One undo step that sets the current texture's regions to <paramref name="after"/> (and edits region <paramref name="select"/> after it).</summary>
    private void CommitRegions(string action, Godot.Collections.Array after, int select)
    {
        if (Current == null)
            return;
        var before = MaterialRegionStore.Copy(RegionsOf(Current));
        Undo.CreateAction($"Materials: {action}", UndoRedo.MergeMode.Disable, _plugin);
        Undo.AddDoMethod(this, MethodName.ApplyRegions, Current, MaterialRegionStore.Copy(after), select);
        Undo.AddUndoMethod(this, MethodName.ApplyRegions, Current, before, Region);
        Undo.CommitAction();
    }

    // ---- making regions --------------------------------------------------------------------------

    /// <summary>The texture's mask size (its own pixels).</summary>
    public (int W, int H) MaskSize => _original is { } o ? (o.Width, o.Height) : (1, 1);

    /// <summary>A new region's dictionary over <paramref name="mask"/>: the texture's settings now (its profile, its differences as exceptions).</summary>
    private Godot.Collections.Dictionary NewRegion(string name, byte[] mask, string origin)
    {
        var (w, h) = MaskSize;
        var prof = MaterialProfiles.Load(ProfileName);
        return new Godot.Collections.Dictionary
        {
            [MaterialRegionStore.Name] = name,
            [MaterialRegionStore.Colour] = MaterialRegionStore.Palette[Regions.Count % MaterialRegionStore.Palette.Length],
            [MaterialRegionStore.Profile] = prof != null ? ProfileName : "",
            [MaterialRegionStore.Exceptions] = MaterialRegionStore.Own(MaterialProfiles.Differences(BaseRecipe, prof ?? new PbrRecipe())),
            [MaterialRegionStore.Mask] = MaterialRegionStore.Hold(mask, w, h),
            [MaterialRegionStore.Feather] = MaterialRegionStore.DefaultFeather,
            [MaterialRegionStore.Origin] = origin,
        };
    }

    private string UniqueName(string want)
    {
        var names = Regions.Where(v => v.VariantType == Variant.Type.Dictionary).Select(v => MaterialRegionStore.NameOf(v.AsGodotDictionary())).ToHashSet();
        if (!names.Contains(want))
            return want;
        for (int k = 2; ; k++)
            if (!names.Contains($"{want} {k}"))
                return $"{want} {k}";
    }

    /// <summary>Adds a region over <paramref name="mask"/> (one undo step) and edits it. An error or null.</summary>
    public string? AddRegion(string name, byte[] mask, string origin)
    {
        if (Current == null || _original == null)
            return "no texture";
        var after = MaterialRegionStore.Copy(Regions);
        after.Add(NewRegion(UniqueName(name), mask, origin));
        CommitRegions($"add region {name}", after, after.Count - 1);
        return null;
    }

    /// <summary>An empty region (to draw by hand).</summary>
    public string? AddEmptyRegion()
    {
        var (w, h) = MaskSize;
        return AddRegion("Region", new byte[w * h], "by hand");
    }

    /// <summary>Removes region <paramref name="i"/> (one undo step).</summary>
    public string? RemoveRegion(int i)
    {
        if (RegionDict(i) is not { } d)
            return "no such region";
        var after = MaterialRegionStore.Copy(Regions);
        after.RemoveAt(i);
        CommitRegions($"remove region {MaterialRegionStore.NameOf(d)}", after, -1);
        return null;
    }

    public string? RenameRegion(int i, string name)
    {
        if (RegionDict(i) is not { } d || name.Trim().Length == 0)
            return "no such region (or no name)";
        var after = MaterialRegionStore.Copy(Regions);
        after[i].AsGodotDictionary()[MaterialRegionStore.Name] = name.Trim();
        CommitRegions($"rename region {MaterialRegionStore.NameOf(d)}", after, i);
        return null;
    }

    /// <summary>Moves region <paramref name="i"/> one place up (-1) or down (+1): a later region lies over an earlier one.</summary>
    public string? MoveRegion(int i, int by)
    {
        int j = i + by;
        if (RegionDict(i) == null || j < 0 || j >= Regions.Count)
            return "cannot move it there";
        var after = MaterialRegionStore.Copy(Regions);
        (after[i], after[j]) = (after[j], after[i]);
        CommitRegions($"move region {RegionName(i)}", after, j);
        return null;
    }

    /// <summary>Region <paramref name="i"/> made with profile <paramref name="name"/> (its exceptions cleared: the profile's settings; one undo step).</summary>
    public string? SetRegionProfile(int i, string name)
    {
        if (RegionDict(i) is not { } d)
            return "no such region";
        if (name.Length > 0 && MaterialProfiles.Load(name) == null)
            return $"no profile {name}";
        var after = MaterialRegionStore.Copy(Regions);
        var nd = after[i].AsGodotDictionary();
        nd[MaterialRegionStore.Profile] = name;
        nd[MaterialRegionStore.Exceptions] = name.Length > 0 ? new Godot.Collections.Dictionary() : MaterialRegionStore.Own(MaterialProfiles.Differences(MaterialRegionStore.Recipe(d), new PbrRecipe()));
        CommitRegions($"region {MaterialRegionStore.NameOf(d)}: the profile {MaterialProfiles.Find(name)?.Title ?? (name.Length > 0 ? name : "none")}", after, i);
        return null;
    }

    public string? SetRegionFeather(int i, float feather)
    {
        if (RegionDict(i) is not { } d)
            return "no such region";
        var after = MaterialRegionStore.Copy(Regions);
        after[i].AsGodotDictionary()[MaterialRegionStore.Feather] = Mathf.Clamp(feather, 0, 32);
        CommitRegions($"region {MaterialRegionStore.NameOf(d)}: feather", after, i);
        return null;
    }

    /// <summary>Region <paramref name="i"/>'s mask set to <paramref name="mask"/> (one undo step).</summary>
    public string? SetRegionMask(int i, byte[] mask, string action)
    {
        if (RegionDict(i) is not { } d)
            return "no such region";
        var (w, h) = MaskSize;
        var after = MaterialRegionStore.Copy(Regions);
        after[i].AsGodotDictionary()[MaterialRegionStore.Mask] = MaterialRegionStore.Hold(mask, w, h);
        CommitRegions($"region {MaterialRegionStore.NameOf(d)}: {action}", after, i);
        return null;
    }

    /// <summary>Region <paramref name="i"/>'s mask (a copy), or null.</summary>
    public byte[]? RegionMask(int i) =>
        RegionDict(i) is { } d && MaterialRegionStore.Load(d.TryGetValue(MaterialRegionStore.Mask, out var m) ? m.AsString() : "") is { } got ? got.Mask : null;

    // ---- from the UV islands ---------------------------------------------------------------------

    /// <summary>The current texture's UV islands (for the proposals and the snapping), and where they came from.</summary>
    private List<MaterialRegions.Island>? _islands;
    private string? _islandsFor;
    public string IslandsNote { get; private set; } = "";

    /// <summary>
    /// The UV islands of the parts that draw the current texture: from the open map when it draws
    /// it, else from the first map the where-used index names (its classic import, loaded here;
    /// null while it is not imported yet).
    /// </summary>
    public List<MaterialRegions.Island>? Islands()
    {
        if (Current == null || _original == null)
            return null;
        if (_islandsFor == Current && _islands != null)
            return _islands;
        Node3D? map = null;
        bool free = false;
        string level = "";
        if (_plugin.Current is { Built: true, Map: { } open } root && TextureActions.OnMap(root).ContainsKey(Current))
            (map, level) = (open, root.Key);
        else if (_plugin.Uses.Uses(Current).FirstOrDefault() is { } use && _plugin.Session.Catalogue?.Find(use.Level)?.Level is { } lv
                 && _plugin.Session.Classic is { } classic && classic.Path("map", lv) is { } path)
        {
            map = ClassicLibrary.Load(path, $"PS3Data{lv}tex/");
            (free, level) = (map != null, use.Level);
        }
        if (map == null)
        {
            IslandsNote = "no map that draws this texture is loaded (the where-used index, the classic import)";
            return null;
        }
        var surfaces = RegionSurfaces.Of(map, Current);
        if (free)
            map.Free();
        var (w, h) = MaskSize;
        _islands = MaterialRegions.Islands(surfaces, w, h, BaseRecipe.Wrap);
        _islandsFor = Current;
        IslandsNote = $"{surfaces.Count} surface(s) of {surfaces.Select(s => s.Part).Distinct().Count()} part(s) in {PartIds.LevelKey(level)}, {_islands.Count} UV island(s)";
        return _islands;
    }

    /// <summary>The regions the UV islands propose (grouped by the part that draws them), or an empty list.</summary>
    public List<MaterialRegions.Proposal> Proposals()
    {
        if (Islands() is not { } isl)
            return new List<MaterialRegions.Proposal>();
        var (w, h) = MaskSize;
        return MaterialRegions.Propose(isl, w, h);
    }

    /// <summary>Every proposed region added (one undo step). How many, or an error.</summary>
    public string AddProposals()
    {
        if (Current == null)
            return "no texture";
        var props = Proposals();
        if (props.Count == 0)
            return $"no regions proposed ({IslandsNote}): the texture is drawn as one piece, or repeats";
        var after = MaterialRegionStore.Copy(Regions);
        foreach (var p in props)
        {
            var label = string.Join(", ", p.Parts.Select(ShortPart).Distinct().Take(3)) + (p.Parts.Count > 3 ? ", ..." : "");
            var d = NewRegion(UniqueName(p.Name), p.Mask, $"UV islands of {label}");
            d[MaterialRegionStore.Colour] = MaterialRegionStore.Palette[after.Count % MaterialRegionStore.Palette.Length];
            after.Add(d);
        }
        CommitRegions($"{props.Count} region(s) from the UV islands", after, Regions.Count);
        return $"{props.Count} region(s) from the UV islands ({IslandsNote})";
    }

    /// <summary>A part ID's last segment (a short name for a region's origin).</summary>
    private static string ShortPart(string part) => part.Contains('#') ? part[(part.LastIndexOf('#') + 1)..] : part.Split('/').Last();

    // ---- by colour -------------------------------------------------------------------------------

    /// <summary>The colour the next "By colour" region takes (picked in the 2D map, or a picker).</summary>
    public Color RegionColour { get; set; } = new(0.4f, 0.6f, 0.3f);

    /// <summary>How alike a pixel must be to the colour (the mask's soft threshold's start; full 0.09 above).</summary>
    public float RegionThreshold { get; set; } = 0.86f;

    /// <summary>The pick name that picks <see cref="RegionColour"/>.</summary>
    public const string RegionPick = "@region-colour";

    /// <summary>A region of the texture's pixels of colour <paramref name="c"/> (the colour mask over the source, specks and pinholes cleaned; one undo step).</summary>
    public string? AddRegionByColour(Color c)
    {
        if (_original == null)
            return "no texture";
        var mask = MaterialRegions.ByColour(_original, new System.Numerics.Vector3(c.R, c.G, c.B), RegionThreshold, Mathf.Min(1, RegionThreshold + 0.09f));
        if (MaterialRegions.Coverage(mask) < 0.001)
            return $"no pixel is #{c.ToHtml(false)} (lower the threshold)";
        return AddRegion($"#{c.ToHtml(false)}", mask, $"colour #{c.ToHtml(false)} (from {RegionThreshold:0.00})");
    }

    private void RegionPicked(Vector2 uv, int phase)
    {
        if (Sample(_original, uv) is { } c)
            RegionColour = c;
        if (phase != 2)
            return;
        EndPick();
        RebuildSettings();
        Say($"region colour #{RegionColour.ToHtml(false)}: Add makes a region of it");
    }

    // ---- by hand ---------------------------------------------------------------------------------

    public MapView2D.RegionTool Tool
    {
        get => _map2d.Tool;
        set => _map2d.Tool = value;
    }

    /// <summary>The brush's radius (mask pixels) and hardness (1: hard), and whether a shape or stroke takes the whole UV islands it touches.</summary>
    public float BrushRadius { get => _map2d.BrushRadius; set => _map2d.BrushRadius = Mathf.Clamp(value, 1, 256); }
    public float BrushHardness { get; set; } = 0.5f;
    public bool SnapToIslands { get; set; }

    private byte[]? _strokeMask;
    private Vector2 _strokeLast;

    private void WireRegionTools()
    {
        _map2d.RectDrawn += (a, b) => Say(DrawRegion(m =>
        {
            var (w, h) = MaskSize;
            MaterialRegions.FillRect(m, w, h, a.X, a.Y, b.X, b.Y);
        }, "a rectangle") ?? "");
        _map2d.PolygonDrawn += pts => Say(DrawRegion(m =>
        {
            var (w, h) = MaskSize;
            MaterialRegions.FillPolygon(m, w, h, pts.Select(p => (p.X, p.Y)).ToList());
        }, "a polygon") ?? "");
        _map2d.Stroke += OnStroke;
    }

    /// <summary>A shape drawn into the edited region's mask (snapped to the islands when on): one undo step. An error or null.</summary>
    private string? DrawRegion(System.Action<byte[]> draw, string what)
    {
        if (Region < 0 || RegionMask(Region) is not { } mask)
            return "choose a region to draw into (or + Region)";
        var (w, h) = MaskSize;
        var shape = new byte[w * h];
        draw(shape);
        if (SnapToIslands && Islands() is { } isl)
            shape = MaterialRegions.Snap(shape, isl.Select(i => i.Mask).ToList());
        for (int i = 0; i < mask.Length; i++)
            mask[i] = System.Math.Max(mask[i], shape[i]);
        return SetRegionMask(Region, mask, what + (SnapToIslands ? " (snapped to the islands)" : ""));
    }

    private void OnStroke(Vector2 uv, int phase)
    {
        if (Region < 0)
        {
            if (phase == 0)
                Say("choose a region to paint into (or + Region)");
            return;
        }
        var (w, h) = MaskSize;
        bool erase = Tool == MapView2D.RegionTool.Erase;
        if (phase == 0)
        {
            _strokeMask = RegionMask(Region);
            if (_strokeMask == null)
                return;
            MaterialRegions.Dab(_strokeMask, w, h, uv.X, uv.Y, BrushRadius, BrushHardness, erase, BaseRecipe.Wrap);
        }
        else if (_strokeMask != null)
            MaterialRegions.Stroke(_strokeMask, w, h, (_strokeLast.X, _strokeLast.Y), (uv.X, uv.Y), BrushRadius, BrushHardness, erase, BaseRecipe.Wrap);
        _strokeLast = uv;
        if (_strokeMask == null)
            return;
        // live: the overlay follows the stroke; the maps and the undo step at its end
        RefreshOverlay(_strokeMask);
        if (phase != 2)
            return;
        var done = _strokeMask;
        _strokeMask = null;
        if (!erase && SnapToIslands && Islands() is { } isl)
            done = MaterialRegions.Snap(done, isl.Select(i => i.Mask).ToList());
        Say(SetRegionMask(Region, done, erase ? "erased" : "painted") ?? $"region {RegionName(Region)}: {(erase ? "erased" : "painted")}");
    }

    // ---- the overlay -----------------------------------------------------------------------------

    private readonly ImageTexture _overlayTex = new();

    /// <summary>Whether the 2D map shows the regions' colours.</summary>
    public bool OverlayShown { get; set; } = true;

    /// <summary>The regions' masks in their colours over the 2D map (the edited one stronger; <paramref name="live"/>: its mask as being painted).</summary>
    public void RefreshOverlay(byte[]? live = null)
    {
        if (Current == null || _original == null || Regions.Count == 0 || !OverlayShown)
        {
            _map2d.Overlay = null;
            return;
        }
        var (w, h) = MaskSize;
        var px = new byte[w * h * 4];
        for (int r = 0; r < Regions.Count; r++)
        {
            var mask = r == Region && live != null ? live : RegionMask(r);
            if (mask == null || RegionDict(r) is not { } d)
                continue;
            var c = MaterialRegionStore.ColourOf(d, r);
            float strength = r == Region ? 0.6f : 0.35f;
            for (int i = 0; i < mask.Length; i++)
            {
                float a = mask[i] / 255f * strength;
                if (a <= 0)
                    continue;
                // a later region over an earlier: "over" compositing
                float a0 = px[i * 4 + 3] / 255f, ao = a + a0 * (1 - a);
                for (int k = 0; k < 3; k++)
                {
                    float cv = k == 0 ? c.R : k == 1 ? c.G : c.B;
                    px[i * 4 + k] = (byte)Mathf.Clamp((cv * a + px[i * 4 + k] / 255f * a0 * (1 - a)) / Mathf.Max(1e-4f, ao) * 255, 0, 255);
                }
                px[i * 4 + 3] = (byte)Mathf.Clamp(ao * 255, 0, 255);
            }
        }
        _overlayTex.SetImage(Image.CreateFromData(w, h, false, Image.Format.Rgba8, px));
        _map2d.MaskWidth = w;
        _map2d.Overlay = _overlayTex;
    }

    // ---- the checks ------------------------------------------------------------------------------

    /// <summary>What the maps are made from now (the texture's settings, its regions, the imports): the checks build the graph themselves.</summary>
    public MaterialPipeline.Inputs? CurrentInputs() =>
        Current == null || _original == null ? null : new MaterialPipeline.Inputs(Current, _original, (PbrRecipe)BaseRecipe.Duplicate(), new Dictionary<string, RgbaImage>(_imports), RegionInputs());

    /// <summary>Forgets this session's regions of <paramref name="id"/> (and the held masks): its entry's are read again (the checks).</summary>
    public void ForgetRegions(string id)
    {
        _regions.Remove(id.ToLowerInvariant());
        MaterialRegionStore.Forget();
        if (id.ToLowerInvariant() == Current)
        {
            Region = -1;
            Recompute();
            RebuildSettings();
            RefreshOverlay();
            Remake();
        }
    }

    /// <summary>The 2D map's region list (the checks).</summary>
    public ItemList? RegionList => _regionList;

    // ---- the boxes -------------------------------------------------------------------------------

    private ItemList? _regionList;

    /// <summary>Which settings the steps edit (atop every step's panel when the texture has regions).</summary>
    private void BuildRegionPicker()
    {
        if (Regions.Count == 0)
            return;
        var row = new HBoxContainer();
        _settingsBox.AddChild(row);
        row.AddChild(new Label { Text = "Editing", CustomMinimumSize = new Vector2(130, 0), TooltipText = "The whole texture's own settings, or a region's (its own profile and exceptions)" });
        var ob = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Choose the region whose settings the steps edit" };
        ob.AddItem("Whole texture");
        for (int i = 0; i < Regions.Count; i++)
            ob.AddItem($"■ {RegionName(i)}");
        ob.Selected = Region + 1;
        ob.ItemSelected += i => EditRegion((int)i - 1);
        row.AddChild(ob);
        if (Region >= 0 && !MaterialPipeline.Settings[Step].All(s => SharedProp(s.Property)))
            _settingsBox.AddChild(new Label
            {
                Text = Step == MaterialStep.Source ? "The Source step's settings are the whole texture's (every region's maps line up)."
                    : $"Changes here are region {RegionName(Region)}'s own (its exceptions to its profile); the material's own amounts stay the whole texture's.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 0.85f, 0.5f),
            });
    }

    /// <summary>The Regions box (the Source step): the list, add and remove, from the islands, by colour, the profile and feather, the drawing tools.</summary>
    private void BuildRegionBox()
    {
        var head = new HBoxContainer();
        _settingsBox.AddChild(head);
        head.AddChild(new Label { Text = "Regions", ThemeTypeVariation = "HeaderSmall", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Parts of the texture with their own profile and settings (an atlas: rock, grass and wood side by side), composited with soft borders" });
        var show = new CheckBox { Text = "Overlay", ButtonPressed = OverlayShown, TooltipText = "The regions' colours over the 2D map" };
        show.Toggled += on =>
        {
            OverlayShown = on;
            RefreshOverlay();
        };
        head.AddChild(show);
        _regionList = new ItemList { CustomMinimumSize = new Vector2(0, 92), SelectMode = ItemList.SelectModeEnum.Single, AutoHeight = true };
        _regionList.AddItem("Whole texture (the texture's own settings)");
        for (int i = 0; i < Regions.Count; i++)
        {
            var d = RegionDict(i)!;
            var cov = RegionMask(i) is { } m ? MaterialRegions.Coverage(m) * 100 : 0;
            var prof = MaterialRegionStore.ProfileOf(d);
            int ex = MaterialRegionStore.ExceptionsOf(d).Count;
            int at = _regionList.AddItem($"{RegionName(i)}: {(prof.Length > 0 ? MaterialProfiles.Find(prof)?.Title ?? prof : "own settings")}{(ex > 0 ? $" ● {ex}" : "")}, {cov:0} %");
            var sw = Image.CreateEmpty(12, 12, false, Image.Format.Rgba8);
            sw.Fill(MaterialRegionStore.ColourOf(d, i));
            _regionList.SetItemIcon(at, ImageTexture.CreateFromImage(sw));
            _regionList.SetItemTooltip(at, d.TryGetValue(MaterialRegionStore.Origin, out var o) ? o.AsString() : "");
        }
        _regionList.Select(Region + 1);
        _regionList.ItemSelected += i => EditRegion((int)i - 1);
        _settingsBox.AddChild(_regionList);
        var row = new HBoxContainer();
        _settingsBox.AddChild(row);
        Add(row, "+ Region", "An empty region, to draw by hand (rectangle, polygon, brush)", () => Say(AddEmptyRegion() ?? "region added: draw it"));
        Add(row, "From UV islands", "Propose regions from the game's UV islands: the parts that draw this texture each cover a part of it (one undo step)", () => Say(AddProposals()));
        Add(row, "Remove", "Remove the chosen region (one undo step)", () => Say(RemoveRegion(Region) ?? "region removed"));
        Add(row, "Rename...", "Rename the chosen region", () =>
        {
            if (Region >= 0)
                AskName("Rename the region", RegionName(Region), t => Say(RenameRegion(Region, t) ?? "renamed"));
        });
        Add(row, "▲", "Earlier in the list: under the regions after it", () => Say(MoveRegion(Region, -1) ?? ""));
        Add(row, "▼", "Later in the list: over the regions before it", () => Say(MoveRegion(Region, 1) ?? ""));
        // by colour
        row = new HBoxContainer();
        _settingsBox.AddChild(row);
        row.AddChild(new Label { Text = "By colour", CustomMinimumSize = new Vector2(80, 0), TooltipText = "A region of the pixels of one colour (the colour mask the samples use)" });
        var cp = new ColorPickerButton { Color = RegionColour, EditAlpha = false, CustomMinimumSize = new Vector2(40, 0) };
        cp.ColorChanged += c => RegionColour = c;
        row.AddChild(cp);
        var pick = new Button { Text = "Pick", ToggleMode = true, TooltipText = "Click in the 2D map: the colour there (the mean over the pick radius)" };
        pick.Toggled += on =>
        {
            if (on)
                StartPick(RegionPick);
            else
                EndPick();
        };
        row.AddChild(pick);
        var th = new SpinBox { MinValue = 0.5, MaxValue = 0.99, Step = 0.01, Value = RegionThreshold, TooltipText = "How alike (lower: more pixels)" };
        th.ValueChanged += v => RegionThreshold = (float)v;
        row.AddChild(th);
        Add(row, "Add", "Add the region of this colour (one undo step)", () => Say(AddRegionByColour(RegionColour) ?? "region by colour added"));
        if (EditedRegion is { } ed)
        {
            // the edited region's profile and feather
            row = new HBoxContainer();
            _settingsBox.AddChild(row);
            row.AddChild(new Label { Text = "Its profile", CustomMinimumSize = new Vector2(80, 0) });
            var ob = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "The region's profile (its exceptions cleared: the profile's settings; one undo step)" };
            ob.AddItem("(none: its own settings)");
            ob.SetItemMetadata(0, "");
            int sel = 0;
            foreach (var (name, r) in MaterialProfiles.All)
            {
                ob.AddItem(r.Title);
                ob.SetItemMetadata(ob.ItemCount - 1, name);
                if (name == MaterialRegionStore.ProfileOf(ed))
                    sel = ob.ItemCount - 1;
            }
            ob.Selected = sel;
            ob.ItemSelected += i => Say(SetRegionProfile(Region, ob.GetItemMetadata((int)i).AsString()) ?? "");
            row.AddChild(ob);
            row.AddChild(new Label { Text = "Feather" });
            var fe = new SpinBox { MinValue = 0, MaxValue = 32, Step = 0.5, Value = MaterialRegionStore.FeatherOf(ed), TooltipText = "The border's softness (classic texels)" };
            fe.ValueChanged += v => Say(SetRegionFeather(Region, (float)v) ?? "");
            row.AddChild(fe);
        }
        // the tools
        row = new HBoxContainer();
        _settingsBox.AddChild(row);
        var group = new ButtonGroup { AllowUnpress = true };
        foreach (var (tool, label, tip) in new[]
        {
            (MapView2D.RegionTool.Rect, "▭", "Rectangle: drag in the 2D map"),
            (MapView2D.RegionTool.Polygon, "⬠", "Polygon: click its corners; a double click, Enter or a right click closes it"),
            (MapView2D.RegionTool.Brush, "Brush", "Paint the region (the middle button pans)"),
            (MapView2D.RegionTool.Erase, "Erase", "Erase from the region"),
        })
        {
            var b = new Button { Text = label, ToggleMode = true, ButtonGroup = group, TooltipText = tip, ButtonPressed = Tool == tool };
            var t = tool;
            b.Toggled += on => Tool = on ? t : (Tool == t ? MapView2D.RegionTool.None : Tool);
            row.AddChild(b);
        }
        row.AddChild(new Label { Text = "Size" });
        var size = new SpinBox { MinValue = 1, MaxValue = 256, Step = 1, Value = BrushRadius, TooltipText = "The brush's radius (texture pixels)" };
        size.ValueChanged += v => BrushRadius = (float)v;
        row.AddChild(size);
        var snap = new CheckBox { Text = "Snap", ButtonPressed = SnapToIslands, TooltipText = "A shape or stroke takes the whole UV islands it touches" };
        snap.Toggled += on => SnapToIslands = on;
        row.AddChild(snap);
        _settingsBox.AddChild(new HSeparator());
    }
}

/// <summary>The UV surfaces of the parts of a map that draw a texture (R2b phase 6: the regions' islands).</summary>
public static class RegionSurfaces
{
    /// <summary>Every triangle surface of <paramref name="map"/> drawing <paramref name="id"/> (its classic or lit material), with its part.</summary>
    public static List<MaterialRegions.UvSurface> Of(Node3D map, string id)
    {
        id = id.ToLowerInvariant();
        var r = new List<MaterialRegions.UvSurface>();
        var look = MapLook.Find(map);
        void Walk(Node n)
        {
            if (n.HasMeta(MapEditRoot.EditorNode))
                return;
            if (n is MeshInstance3D { Mesh: { } mesh } mi)
            {
                var parts = PartIds.GetStrings(mi, PartIds.Parts);
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    var m = mi.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s);
                    bool uses = RemasterTextures.ColourId(m) == id || (m != null && PartIds.GetString(m, RemasterLook.LitTextureMeta) == id)
                        || RemasterTextures.ColourId(look?.ClassicOf(mi, s)) == id;
                    if (!uses || mesh is ArrayMesh am && am.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles)
                        continue;
                    var arrays = mesh.SurfaceGetArrays(s);
                    if (arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil)
                        continue;
                    var uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                    var ix = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? Enumerable.Range(0, uv.Length).ToArray() : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                    var flat = new float[uv.Length * 2];
                    for (int i = 0; i < uv.Length; i++)
                        (flat[i * 2], flat[i * 2 + 1]) = (uv[i].X, uv[i].Y);
                    var part = parts != null && s < parts.Length ? parts[s] : PartOf(mi) ?? mi.Name.ToString();
                    r.Add(new MaterialRegions.UvSurface(part, flat, ix));
                }
            }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(map);
        return r;
    }

    private static string? PartOf(Node n)
    {
        for (var c = n; c != null; c = c.GetParent())
            if (PartIds.Of(c) is { } p)
                return p;
        return null;
    }
}
#endif
