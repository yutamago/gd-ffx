#if TOOLS
using System.Linq;
using GdFfx.Characters;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The <b>Characters</b> dock (docs/remaster-editor.md, "Characters"): the game's characters
/// (model, the game's name, kind, remake status) with search and a kind filter; open one; the open
/// character's motions (play, pause, scrub); assign a remade model; the bone mapping (automatic,
/// by hand, positions), the alignment, the cloth chains' modes, the face rows and attachments;
/// status and notes; save. Every change goes through the plugin (one undo step each).
/// </summary>
[Tool]
public partial class CharacterDock : EditorDock
{
    private readonly RemasterPlugin _plugin;
    private LineEdit _search = null!;
    private OptionButton _kind = null!;
    private CheckBox _changedOnly = null!;
    private Tree _list = null!;
    private Label _info = null!;
    private OptionButton _motions = null!;
    private Button _play = null!;
    private HSlider _scrub = null!;
    private Label _time = null!;
    private Tree _bones = null!;
    private SpinBox _element = null!;
    private OptionButton _positions = null!;
    private Label _extra = null!;
    private OptionButton _status = null!;
    private LineEdit _notes = null!;
    private FileDialog? _dialog;
    private bool _filling;

    public static readonly string[] Kinds = ["all", "party", "monster", "person", "aeon", "weapon", "object", "skeleton"];

    public CharacterDock(RemasterPlugin plugin)
    {
        _plugin = plugin;
        Name = "FfxCharacters";
        Title = "Characters";
        DefaultSlot = DockSlot.RightUl;
        Build();
    }
    public CharacterDock() : this(null!) { }

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
        _search = new LineEdit { PlaceholderText = "search: model, name", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _search.TextChanged += _ => Fill();
        row.AddChild(_search);
        _kind = new OptionButton { TooltipText = "The kind of character" };
        foreach (var k in Kinds)
            _kind.AddItem(k);
        _kind.ItemSelected += _ => Fill();
        row.AddChild(_kind);
        _changedOnly = new CheckBox { Text = "Remade", TooltipText = "Only the characters with a remake" };
        _changedOnly.Toggled += _ => Fill();
        row.AddChild(_changedOnly);
        _list = new Tree { Columns = 4, HideRoot = true, CustomMinimumSize = new Vector2(0, 200), SizeFlagsVertical = SizeFlags.ExpandFill };
        _list.SetColumnTitlesVisible(true);
        string[] titles = ["Model", "Name", "Kind", "Remake"];
        for (int i = 0; i < titles.Length; i++)
            _list.SetColumnTitle(i, titles[i]);
        _list.ItemActivated += () => OpenSelected();
        col.AddChild(_list);
        row = new HBoxContainer();
        col.AddChild(row);
        var open = new Button { Text = "Open", TooltipText = "Open the character: the classic model with its motions, the remake beside it" };
        open.Pressed += () => OpenSelected();
        row.AddChild(open);
        var assign = new Button { Text = "Assign model...", TooltipText = "Assign a scene or glTF (any skeleton) as the open character's remake; its rig is made automatically" };
        assign.Pressed += ChooseModel;
        row.AddChild(assign);
        var save = new Button { Text = "Save", TooltipText = "Save the open character's rig" };
        save.Pressed += () => Say(_plugin.SaveCharacter() ?? "saved");
        row.AddChild(save);
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "Open the archive in the Remaster dock." };
        col.AddChild(_info);
        // motions
        col.AddChild(new Label { Text = "Motions" });
        row = new HBoxContainer();
        col.AddChild(row);
        _motions = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "The game's motions (group/clip and the motion numbers that play it)" };
        _motions.ItemSelected += i => _plugin.CurrentCharacter?.Play(_motions.GetItemText((int)i));
        row.AddChild(_motions);
        _play = new Button { Text = "Pause" };
        _play.Pressed += () =>
        {
            if (_plugin.CurrentCharacter is not { } r)
                return;
            if (r.Playing) r.Pause(); else r.Resume();
            _play.Text = r.Playing ? "Pause" : "Play";
        };
        row.AddChild(_play);
        row = new HBoxContainer();
        col.AddChild(row);
        _scrub = new HSlider { SizeFlagsHorizontal = SizeFlags.ExpandFill, MinValue = 0, MaxValue = 1, Step = 0.001 };
        _scrub.ValueChanged += v =>
        {
            if (!_filling && _plugin.CurrentCharacter is { } r)
                r.Scrub(v * r.Length);
        };
        row.AddChild(_scrub);
        _time = new Label { Text = "0.00 s" };
        row.AddChild(_time);
        // the mapping
        col.AddChild(new Label { Text = "Bones (remade bone, game element, part, position)" });
        row = new HBoxContainer();
        col.AddChild(row);
        var auto = new Button { Text = "Auto map", TooltipText = "Map the bones by name patterns, hierarchy and rest positions (rows set by hand are kept)" };
        auto.Pressed += () => Say(_plugin.AutoMapCharacter() ?? "mapped");
        row.AddChild(auto);
        var align = new Button { Text = "Auto align", TooltipText = "Turn and scale the remade model to the game's (up, facing, height)" };
        align.Pressed += () => Say(_plugin.AutoAlignCharacter() ?? "aligned");
        row.AddChild(align);
        _bones = new Tree { Columns = 4, HideRoot = true, CustomMinimumSize = new Vector2(0, 180), SizeFlagsVertical = SizeFlags.ExpandFill };
        _bones.SetColumnTitlesVisible(true);
        string[] bt = ["Bone", "Element", "Part", "Position"];
        for (int i = 0; i < bt.Length; i++)
            _bones.SetColumnTitle(i, bt[i]);
        _bones.ItemSelected += OnBoneSelected;
        col.AddChild(_bones);
        row = new HBoxContainer();
        col.AddChild(row);
        _element = new SpinBox { MinValue = -1, MaxValue = 1023, Step = 1, TooltipText = "The game's element (SkeletonNode_i) for the selected bone; -1 none" };
        row.AddChild(_element);
        var set = new Button { Text = "Set element", TooltipText = "Map the selected bone to this element by hand" };
        set.Pressed += () =>
        {
            if (SelectedBone() is { } b)
                Say(_plugin.SetBoneElement(b, (int)_element.Value) ?? $"{b} -> element {(int)_element.Value}");
        };
        row.AddChild(set);
        _positions = new OptionButton { TooltipText = "Where the bone's position comes from" };
        foreach (var p in System.Enum.GetNames<BonePositions>())
            _positions.AddItem(p);
        _positions.ItemSelected += i =>
        {
            if (!_filling && SelectedBone() is { } b)
                Say(_plugin.SetBonePositions(b, (BonePositions)(int)i) ?? "set");
        };
        row.AddChild(_positions);
        _extra = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_extra);
        row = new HBoxContainer();
        col.AddChild(row);
        _status = new OptionButton();
        foreach (var s in System.Enum.GetNames<Remaster.MapStatus>())
            _status.AddItem(s);
        row.AddChild(_status);
        _notes = new LineEdit { PlaceholderText = "notes", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(_notes);
        var setStatus = new Button { Text = "Set" };
        setStatus.Pressed += () => Say(_plugin.SetCharacterStatus((Remaster.MapStatus)_status.Selected, _notes.Text) ?? "status set");
        row.AddChild(setStatus);
    }

    private void Say(string text) => _info.Text = text;

    /// <summary>The list again (the archive opened, a remake assigned).</summary>
    public void Fill()
    {
        _list.Clear();
        var root = _list.CreateItem();
        var cat = _plugin.Characters;
        if (cat == null)
            return;
        var q = _search.Text.Trim().ToLowerInvariant();
        var kind = Kinds[_kind.Selected];
        foreach (var e in cat.Entries)
        {
            if (kind != "all" && e.Kind != kind)
                continue;
            var name = e.Title();
            if (q.Length > 0 && !e.Name.Contains(q) && !e.Names.Values.Any(n => n.ToLowerInvariant().Contains(q)))
                continue;
            var rig = CharacterStore.Scene(e.Name) != null ? CharacterStore.Rig(e.Name) : null;
            bool remade = CharacterStore.Scene(e.Name) != null;
            if (_changedOnly.ButtonPressed && !remade)
                continue;
            var it = _list.CreateItem(root);
            it.SetText(0, e.Name);
            it.SetText(1, name);
            it.SetText(2, e.Kind);
            it.SetText(3, remade ? (rig != null ? $"{rig.Status}, rig" : "remade") : e.Motions ? "" : "no motions");
            it.SetMetadata(0, e.Name);
        }
    }

    /// <summary>The ids the list shows (the checks).</summary>
    public string[] Listed()
    {
        var root = _list.GetRoot();
        return root == null ? [] : root.GetChildren().Select(i => i.GetText(0)).ToArray();
    }

    public void SetSearch(string text)
    {
        _search.Text = text;
        Fill();
    }

    private string? Selected() => _list.GetSelected()?.GetMetadata(0).AsString();

    private void OpenSelected()
    {
        if (Selected() is { } id)
            Say(_plugin.OpenCharacter(id) ?? $"opening {id}");
    }

    private void ChooseModel()
    {
        _dialog?.QueueFree();
        _dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Resources, Title = "The remade model" };
        _dialog.Filters = ["*.tscn, *.scn, *.glb, *.gltf ; Scenes"];
        _dialog.FileSelected += path => Say(_plugin.AssignModel(path) ?? $"{path} assigned");
        EditorInterface.Singleton.PopupDialogCentered(_dialog, new Vector2I(700, 500));
    }

    private string? SelectedBone() => _bones.GetSelected()?.GetText(0);

    private void OnBoneSelected()
    {
        var b = SelectedBone();
        if (b == null || _plugin.CurrentCharacter is not { } root || CharacterStore.Rig(root.Character)?.Find(b) is not { } m)
            return;
        _filling = true;
        _element.Value = m.Element;
        _positions.Selected = (int)m.Positions;
        _filling = false;
    }

    /// <summary>The open character changed (opened, built, its rig edited): motions and mapping again.</summary>
    public void CharacterChanged()
    {
        _filling = true;
        var root = _plugin.CurrentCharacter;
        _motions.Clear();
        _bones.Clear();
        if (root != null)
        {
            foreach (var m in root.Motions())
                _motions.AddItem(m);
            var rig = CharacterStore.Rig(root.Character);
            var top = _bones.CreateItem();
            if (rig != null)
            {
                foreach (var m in rig.Bones)
                {
                    var it = _bones.CreateItem(top);
                    it.SetText(0, m.Bone);
                    it.SetText(1, m.Element >= 0 ? m.Element.ToString() : "-");
                    it.SetText(2, m.ByHand ? $"{m.Label} (by hand)" : m.Label);
                    it.SetText(3, m.Positions.ToString());
                }
                _status.Selected = (int)rig.Status;
                _notes.Text = rig.Notes;
                _extra.Text = string.Join("\n", rig.Cloth.Select(c => $"cloth {c.Name}: {c.Mode}" + (c.Mode == ClothMode.Game ? $" (segment {c.Model}/{c.Piece})" : ""))
                    .Concat(rig.Face.Select(f => $"face {f.Name}: element {f.Element} {f.Source} -> {f.Target} {f.TargetName}"))
                    .Concat(rig.Attach.Select(a => $"attach: point {a.Point} (element {a.Element}, by {a.Via}) -> {a.Bone}")));
            }
            Say(root.Status);
        }
        _filling = false;
        Fill();
    }

    public override void _Process(double delta)
    {
        if (_plugin?.CurrentCharacter is { Built: true } r && r.Length > 0)
        {
            _filling = true;
            _scrub.Value = r.Time / r.Length;
            _time.Text = $"{r.Time:F2} s";
            _filling = false;
        }
    }
}
#endif
