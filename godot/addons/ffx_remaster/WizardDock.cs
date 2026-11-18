#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using GdFfx.Remaster.Wizard;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The Wizard dock (docs/remaster-editor.md, "The remaster wizard"): the scope (the map browser's
/// map, its area, the maps the project changes, all maps), run, pause, resume and cancel with the
/// progress; the maps of the batch with their state and time; and the review of the selected map:
/// before/after pictures (classic and remastered, a view at a time), R3's colour check (a warning
/// when it fails), the lighting, and per texture the chosen profile with its score and reasons and
/// the made albedo and normal; accept or reject a texture, make it with another profile; accept
/// the map (applied to the project, one undo step) or discard it.
/// </summary>
public partial class WizardDock : EditorDock
{
    private readonly RemasterPlugin _plugin;
    private OptionButton _scope = null!;
    private CheckBox _twoD = null!;
    private Button _run = null!, _pause = null!, _resume = null!, _cancel = null!;
    private ProgressBar _progress = null!;
    private Label _status = null!, _summary = null!;
    private Tree _maps = null!;
    private Label _mapInfo = null!, _colour = null!;
    private TextureRect _before = null!, _after = null!;
    private SpinBox _view = null!;
    private Tree _textures = null!;
    private Label _why = null!;
    private OptionButton _profile = null!;
    private Button _accept = null!, _discard = null!, _again = null!;
    private string? _selectedMap;
    private readonly Dictionary<string, ImageTexture> _thumbs = new();

    public WizardDock(RemasterPlugin plugin)
    {
        _plugin = plugin;
        Name = "FfxWizard";
        Title = "Wizard";
        DefaultSlot = DockSlot.RightUl;
        Build();
    }

    public WizardDock() : this(null!) { }

    /// <summary>The map shown in the review (null: none).</summary>
    public string? SelectedMap => _selectedMap;

    /// <summary>The scopes, as <see cref="Wizard.Scope"/> takes them.</summary>
    public static readonly string[] Scopes = ["map", "area", "changed", "all"];

    private void Build()
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(box);
        var row = new HBoxContainer();
        box.AddChild(row);
        _scope = new OptionButton { TooltipText = "Which maps: the map selected in the Remaster dock, its area, the maps the project changes, all maps" };
        foreach (var s in new[] { "The selected map", "Its area", "The changed maps", "All maps" })
            _scope.AddItem(s);
        row.AddChild(_scope);
        _run = new Button { Text = "Run", TooltipText = "A first automated pass over the maps: profiles chosen per texture, materials made, lighting from the game's look, pictures; staged for review" };
        _run.Pressed += () => Say(RunScope(Scopes[_scope.Selected]));
        row.AddChild(_run);
        _pause = new Button { Text = "Pause", TooltipText = "Pause after the map being made (resumes in this session or the next)" };
        _pause.Pressed += () => _plugin.Wizard?.Pause();
        row.AddChild(_pause);
        _resume = new Button { Text = "Resume" };
        _resume.Pressed += () => Say(_plugin.Wizard?.Resume());
        row.AddChild(_resume);
        _cancel = new Button { Text = "Cancel", TooltipText = "Drop the map being made and pause" };
        _cancel.Pressed += () => _plugin.Wizard?.Cancel();
        row.AddChild(_cancel);
        _twoD = new CheckBox { Text = "2D-layer maps: note as kept classic (else skipped)" };
        box.AddChild(_twoD);
        _progress = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, CustomMinimumSize = new Vector2(0, 14) };
        box.AddChild(_progress);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_status);
        _summary = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(0.8f, 0.8f, 0.8f) };
        box.AddChild(_summary);
        _maps = new Tree { Columns = 4, HideRoot = true, CustomMinimumSize = new Vector2(0, 120), SelectMode = Tree.SelectModeEnum.Row };
        _maps.SetColumnTitlesVisible(true);
        foreach (var (i, t) in new[] { (0, "Map"), (1, "State"), (2, "Time"), (3, "Colour") })
            _maps.SetColumnTitle(i, t);
        _maps.ItemSelected += () => SelectMap(_maps.GetSelected()?.GetMetadata(0).AsString());
        box.AddChild(_maps);
        // the review
        _mapInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_mapInfo);
        _colour = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_colour);
        var pics = new HBoxContainer();
        box.AddChild(pics);
        _before = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(160, 90), SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Classic" };
        _after = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(160, 90), SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Remastered (staged)" };
        pics.AddChild(_before);
        pics.AddChild(_after);
        var vrow = new HBoxContainer();
        box.AddChild(vrow);
        vrow.AddChild(new Label { Text = "View" });
        _view = new SpinBox { MinValue = 0, MaxValue = 0, Step = 1 };
        _view.ValueChanged += _ => ShowPictures();
        vrow.AddChild(_view);
        _again = new Button { Text = "Pictures again", TooltipText = "Draw the remastered pictures again with the textures as decided now" };
        _again.Pressed += () => Say(_selectedMap != null ? _plugin.Wizard?.Repicture(_selectedMap) : "no map");
        vrow.AddChild(_again);
        _textures = new Tree { Columns = 5, HideRoot = true, CustomMinimumSize = new Vector2(0, 200), SizeFlagsVertical = SizeFlags.ExpandFill, SelectMode = Tree.SelectModeEnum.Row };
        _textures.SetColumnTitlesVisible(true);
        foreach (var (i, t) in new[] { (0, "Albedo"), (1, "Normal"), (2, "Texture"), (3, "Profile (score)"), (4, "Decision") })
            _textures.SetColumnTitle(i, t);
        _textures.ItemSelected += ShowWhy;
        box.AddChild(_textures);
        _why = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_why);
        var trow = new HBoxContainer();
        box.AddChild(trow);
        var acc = new Button { Text = "Accept", TooltipText = "Keep this texture's material as made" };
        acc.Pressed += () => Decide("accepted");
        trow.AddChild(acc);
        var rej = new Button { Text = "Reject (classic)", TooltipText = "No material for this texture: it stays classic" };
        rej.Pressed += () => Decide("rejected");
        trow.AddChild(rej);
        _profile = new OptionButton();
        trow.AddChild(_profile);
        var make = new Button { Text = "Make with", TooltipText = "Make this texture again with the chosen profile" };
        make.Pressed += Remake;
        trow.AddChild(make);
        var mrow = new HBoxContainer();
        box.AddChild(mrow);
        _accept = new Button { Text = "Accept map", TooltipText = "Apply the pass to the project: the materials, render path remastered, status in progress, the notes, the lighting (one undo step)" };
        _accept.Pressed += () => Say(_selectedMap != null ? _plugin.WizardAccept(_selectedMap) : "no map");
        mrow.AddChild(_accept);
        _discard = new Button { Text = "Discard", TooltipText = "Drop the staged pass (the project unchanged)" };
        _discard.Pressed += () => Say(_selectedMap != null ? _plugin.WizardDiscard(_selectedMap) : "no map");
        mrow.AddChild(_discard);
        var open = new Button { Text = "Open map" };
        open.Pressed += () =>
        {
            if (_selectedMap != null && _plugin.Session.Catalogue?.Find(_selectedMap) is { } e)
                Say(_plugin.OpenMap(e));
        };
        mrow.AddChild(open);
    }

    private void Say(string? message)
    {
        if (message != null)
            _status.Text = message;
    }

    /// <summary>Runs the wizard over <paramref name="scope"/> (<see cref="Scopes"/>) from the map browser's selection. An error or null.</summary>
    public string? RunScope(string scope)
    {
        if (_plugin.Wizard is not { } w)
            return "no wizard";
        var sel = _plugin.Dock?.Selected;
        var keys = w.Scope(scope, sel?.Key, sel?.Area);
        if (keys.Count == 0)
            return scope is "map" or "area" ? "select a map in the Remaster dock" : "no maps in that scope";
        return w.Start(keys, _twoD.ButtonPressed ? "classic" : "skip");
    }

    private double _tick;

    public void Tick()
    {
        if (_plugin.Wizard is not { } w)
            return;
        _tick -= 1.0 / 60;
        if (_tick > 0)
            return;
        _tick = 0.25;
        var b = w.State.Batch;
        var bake = w.Bake;
        double overall = b == null || b.Maps.Count == 0 ? 0 : (b.Next + (bake?.Progress ?? 0)) / b.Maps.Count;
        _progress.Value = overall;
        if (w.Status.Length > 0)
            _status.Text = w.Status;
        _pause.Disabled = b == null || b.Finished || b.Paused;
        _resume.Disabled = b == null || b.Finished || !b.Paused || w.Busy;
        _cancel.Disabled = !w.Busy;
        _run.Disabled = w.Busy;
    }

    /// <summary>Lists the maps again and the review of the selected one.</summary>
    public void Refresh()
    {
        if (_plugin.Wizard is not { } w)
            return;
        _summary.Text = w.State.Maps.Count > 0 || w.State.Batch != null ? w.State.Summary() : "No pass yet: choose the maps and Run.";
        _maps.Clear();
        var root = _maps.CreateItem();
        var order = (w.State.Batch?.Maps ?? new List<string>()).Concat(w.State.Maps.Keys).Distinct();
        foreach (var key in order)
        {
            var m = w.State.Maps.GetValueOrDefault(key);
            var it = _maps.CreateItem(root);
            it.SetText(0, key);
            it.SetMetadata(0, key);
            it.SetText(1, m?.State ?? (w.Making == key ? "making" : "queued"));
            it.SetText(2, m is { Ms: > 0 } ? $"{m.Ms / 1000:0.0} s" : "");
            it.SetText(3, m == null || m.Colour.Length == 0 ? "" : m.ColourOk ? "ok" : "check");
            if (m is { ColourOk: false })
                it.SetCustomColor(3, new Color(1f, 0.6f, 0.2f));
            if (key == _selectedMap)
                it.Select(0);
        }
        _profile.Clear();
        foreach (var p in MaterialProfiles.All.Keys.OrderBy(k => k))
            _profile.AddItem(p);
        ShowMap();
    }

    public void SelectMap(string? key)
    {
        _selectedMap = key;
        ShowMap();
    }

    private WizardMapState? Map => _selectedMap != null ? _plugin.Wizard?.State.Maps.GetValueOrDefault(_selectedMap) : null;

    private void ShowMap()
    {
        var m = Map;
        _textures.Clear();
        _why.Text = "";
        _accept.Disabled = _discard.Disabled = m is not { State: "staged" };
        if (m == null)
        {
            _mapInfo.Text = _selectedMap != null ? $"{_selectedMap}: not made yet" : "";
            _colour.Text = "";
            _before.Texture = _after.Texture = null;
            return;
        }
        var light = m.LightingResource();
        _mapInfo.Text = $"{m.Key}: {m.State}{(m.Reason.Length > 0 ? $" ({m.Reason})" : "")}, {m.When}, {m.Ms / 1000:0.0} s (materials {m.BakeMs / 1000:0.0} s). "
            + $"Lighting: the game's look, GI {light.GiMode}, exposure {light.ToneExposure:0.##}. {m.Note}";
        _colour.Text = m.Colour.Length > 0 ? $"Colour check: {m.Colour}" : "";
        _colour.Modulate = m.ColourOk ? Colors.White : new Color(1f, 0.6f, 0.2f);
        _view.MaxValue = Mathf.Max(0, Mathf.Min(m.Before.Count, m.After.Count) - 1);
        ShowPictures();
        var root = _textures.CreateItem();
        foreach (var t in m.Textures)
        {
            var it = _textures.CreateItem(root);
            it.SetMetadata(0, t.Id);
            if (Thumb(t.Albedo) is { } a)
                it.SetIcon(0, a);
            if (Thumb(t.Normal) is { } n)
                it.SetIcon(1, n);
            it.SetText(2, RemasterTextures.ShortName(t.Id));
            it.SetText(3, (t.Profile.Length > 0 ? t.Profile : $"classic ({t.Kind})") + $" ({t.Score:0.0}, lead {t.Margin:0.0})" + (t.Error.Length > 0 ? $" {t.Error}" : ""));
            it.SetText(4, t.Applies ? t.Decision == "auto" ? "made" : t.Decision : t.Decision == "rejected" ? "rejected: classic" : "classic");
        }
    }

    private void ShowPictures()
    {
        var m = Map;
        int i = (int)_view.Value;
        _before.Texture = m != null && i < m.Before.Count ? Thumb(m.Before[i], false) : null;
        _after.Texture = m != null && i < m.After.Count ? Thumb(m.After[i], false) : null;
    }

    private ImageTexture? Thumb(string path, bool cache = true)
    {
        if (path.Length == 0)
            return null;
        if (cache && _thumbs.TryGetValue(path, out var t))
            return t;
        var abs = GameSettings.Globalize(path);
        if (!System.IO.File.Exists(abs) || Image.LoadFromFile(abs) is not { } img || img.IsEmpty())
            return null;
        var tex = ImageTexture.CreateFromImage(img);
        if (cache)
            _thumbs[path] = tex;
        return tex;
    }

    private string? SelectedTexture => _textures.GetSelected()?.GetMetadata(0).AsString();

    private void ShowWhy()
    {
        var t = Map?.Textures.FirstOrDefault(x => x.Id == SelectedTexture);
        _why.Text = t == null ? "" : $"{t.Id}\n{t.Why}\n{t.Surfaces} surface(s), area {t.Area:0}";
        if (t != null)
            for (int i = 0; i < _profile.ItemCount; i++)
                if (_profile.GetItemText(i) == (t.Profile.Length > 0 ? t.Profile : t.AutoProfile))
                    _profile.Select(i);
    }

    private void Decide(string decision)
    {
        if (_selectedMap == null || SelectedTexture is not { } id)
            return;
        Say(_plugin.WizardDecide(_selectedMap, id, decision));
    }

    private void Remake()
    {
        if (_selectedMap == null || SelectedTexture is not { } id || _profile.Selected < 0)
            return;
        Say(_plugin.Wizard?.Remake(_selectedMap, id, _profile.GetItemText(_profile.Selected)));
    }
}
#endif
