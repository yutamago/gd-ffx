#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The <b>Media</b> dock (docs/remaster-editor.md, "Effects, audio and movies"; R7), three tabs:
/// <list type="bullet">
/// <item><b>Effects</b>: the effects the game recorded (id, what it is, frames, remade), open one
/// (its classic recording replayed beside the remake: play, pause, scrub), assign a remade scene,
/// back to classic;</item>
/// <item><b>Audio</b>: the game's music tracks, sound effects and voice lines by id with their names,
/// the replaced ones marked; play the classic sound or the replacement, assign a file (loop points,
/// volume), back to classic;</item>
/// <item><b>Movies</b>: the game's movies, assign an Ogg Theora video (its sound, subtitles), preview it,
/// back to classic.</item>
/// </list>
/// Every change goes through the plugin (its file written, one undo step).
/// </summary>
[Tool]
public partial class MediaDock : EditorDock
{
    private readonly RemasterPlugin _plugin;
    private TabContainer _tabs = null!;
    // effects
    private Tree _effects = null!;
    private Label _effectInfo = null!;
    private HSlider _scrub = null!;
    private Button _play = null!;
    private CheckBox _side = null!;
    // audio
    private OptionButton _kind = null!;
    private LineEdit _search = null!;
    private Tree _sounds = null!;
    private Label _soundInfo = null!;
    private CheckBox _loop = null!;
    private SpinBox _loopStart = null!, _loopEnd = null!, _volume = null!;
    private AudioStreamPlayer _player = null!;
    // movies
    private Tree _movies = null!;
    private Label _movieInfo = null!;
    private OptionButton _movieSound = null!;
    private VideoStreamPlayer _preview = null!;
    private FileDialog? _dialog;
    private bool _filling;

    /// <summary>The game's sounds by kind (read from the archive once): id, name, bank, sample.</summary>
    private readonly Dictionary<SoundKind, List<(int Id, string Name, string Bank, int Sample)>> _classic = new();

    public MediaDock(RemasterPlugin plugin)
    {
        _plugin = plugin;
        Name = "FfxMedia";
        Title = "Media";
        DefaultSlot = DockSlot.RightUl;
        Build();
    }
    public MediaDock() : this(null!) { }

    private void Build()
    {
        _tabs = new TabContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(_tabs);
        _player = new AudioStreamPlayer { Name = "Preview" };
        AddChild(_player);
        BuildEffects();
        BuildAudio();
        BuildMovies();
    }

    private static VBoxContainer Page(TabContainer tabs, string title)
    {
        var col = new VBoxContainer { Name = title, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        tabs.AddChild(col);
        return col;
    }

    private static Tree List(string[] titles)
    {
        var t = new Tree { Columns = titles.Length, HideRoot = true, CustomMinimumSize = new Vector2(0, 220), SizeFlagsVertical = SizeFlags.ExpandFill };
        t.SetColumnTitlesVisible(true);
        for (int i = 0; i < titles.Length; i++)
            t.SetColumnTitle(i, titles[i]);
        return t;
    }

    private static Button Btn(Container row, string text, string tip, System.Action act)
    {
        var b = new Button { Text = text, TooltipText = tip };
        b.Pressed += act;
        row.AddChild(b);
        return b;
    }

    // ---- effects ----------------------------------------------------------------------------

    private void BuildEffects()
    {
        var col = Page(_tabs, "Effects");
        _effects = List(["Id", "What", "Frames", "Remake"]);
        _effects.ItemActivated += () => Say(_plugin.OpenEffect(SelectedEffect() ?? "") ?? "opened");
        col.AddChild(_effects);
        var row = new HBoxContainer();
        col.AddChild(row);
        Btn(row, "Open", "Open the effect: the game's recording of it replayed, the remake beside it", () => Say(_plugin.OpenEffect(SelectedEffect() ?? "") ?? "opened"));
        Btn(row, "Assign scene...", "Assign a scene (Godot particles, meshes, lights) as the effect's remake", () => Choose("*.tscn, *.scn ; Scenes", false, p => Say(_plugin.AssignEffect(SelectedEffect() ?? "", p) ?? $"remade by {p}")));
        Btn(row, "Classic", "Back to the game's own effect (the editor's remade scene removed)", () => Say(_plugin.ClearEffect(SelectedEffect() ?? "") ?? "classic"));
        Btn(row, "Reload", "Read the game's recordings again", Refresh);
        row = new HBoxContainer();
        col.AddChild(row);
        _play = Btn(row, "Pause", "Play or pause the classic replay", () =>
        {
            if (_plugin.CurrentEffect is not { } r)
                return;
            r.Playing = !r.Playing;
            _play!.Text = r.Playing ? "Pause" : "Play";
        });
        _scrub = new HSlider { SizeFlagsHorizontal = SizeFlags.ExpandFill, MinValue = 0, MaxValue = 1, Step = 1 };
        _scrub.ValueChanged += v =>
        {
            if (_filling || _plugin.CurrentEffect is not { } r)
                return;
            r.Playing = false;
            _play.Text = "Play";
            r.ShowFrame((int)v);
        };
        row.AddChild(_scrub);
        _side = new CheckBox { Text = "Side by side", ButtonPressed = true, TooltipText = "The remake beside the classic effect (off: at the same place)" };
        _side.Toggled += on =>
        {
            if (_plugin.CurrentEffect is { } r)
            {
                r.SideBySide = on;
                r.BuildRemake();
            }
        };
        row.AddChild(_side);
        _effectInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "The game records each effect it draws (once a run) into user://remaster/effects/." };
        col.AddChild(_effectInfo);
    }

    /// <summary>The selected effect id (null none).</summary>
    public string? SelectedEffect() => _effects.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } s ? s : _plugin.CurrentEffect?.Effect;

    /// <summary>The ids listed (for tests).</summary>
    public List<string> ListedEffects()
    {
        var l = new List<string>();
        for (var it = _effects.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
            l.Add(it.GetMetadata(0).AsString());
        return l;
    }

    public void SelectEffect(string id)
    {
        for (var it = _effects.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
            if (it.GetMetadata(0).AsString() == id)
            {
                it.Select(0);
                return;
            }
    }

    private void FillEffects()
    {
        var keep = SelectedEffect();
        _effects.Clear();
        var root = _effects.CreateItem();
        var ids = MediaStore.RecordedEffects();
        // remade ids the game has not recorded (yet) too
        if (DirAccess.DirExistsAbsolute(GameSettings.Globalize(MediaStore.EffectScenesDir)))
            foreach (var f in DirAccess.GetFilesAt(MediaStore.EffectScenesDir))
                if (f.EndsWith(".tscn") && !ids.Any(x => x.Id == f[..^5]))
                    ids.Add((f[..^5], EffectOwners.Describe(f[..^5]), 0, ""));
        foreach (var (id, what, frames, _) in ids)
        {
            var it = _effects.CreateItem(root);
            it.SetText(0, id);
            it.SetMetadata(0, id);
            it.SetText(1, what);
            it.SetText(2, frames > 0 ? frames.ToString() : "-");
            it.SetText(3, MediaStore.EffectScene(id) is { } s ? System.IO.Path.GetFileName(s) : "");
            if (id == keep)
                it.Select(0);
        }
    }

    /// <summary>The open effect changed (built): its frames on the slider.</summary>
    public void EffectChanged()
    {
        var r = _plugin.CurrentEffect;
        _filling = true;
        _scrub.MaxValue = System.Math.Max(1, (r?.Recording?.Frames.Count ?? 1) - 1);
        _scrub.Value = r?.Frame ?? 0;
        _filling = false;
        if (r != null)
            _effectInfo.Text = r.Status;
    }

    // ---- audio ------------------------------------------------------------------------------

    private void BuildAudio()
    {
        var col = Page(_tabs, "Audio");
        var row = new HBoxContainer();
        col.AddChild(row);
        _kind = new OptionButton();
        foreach (var k in new[] { "Music", "Sound effects", "Voice" })
            _kind.AddItem(k);
        _kind.ItemSelected += _ => FillSounds();
        row.AddChild(_kind);
        _search = new LineEdit { PlaceholderText = "search: id (hex), name", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _search.TextChanged += _ => FillSounds();
        row.AddChild(_search);
        _sounds = List(["Id", "Name", "Replacement"]);
        col.AddChild(_sounds);
        row = new HBoxContainer();
        col.AddChild(row);
        Btn(row, "Play classic", "Play the game's own sound (from your copy)", () => Say(PlayClassic() ?? "playing the classic sound"));
        Btn(row, "Play replacement", "Play the replacement", () => Say(PlayReplacement() ?? "playing the replacement"));
        Btn(row, "Stop", "Stop the preview", () => _player.Stop());
        row = new HBoxContainer();
        col.AddChild(row);
        _loop = new CheckBox { Text = "Loop", ButtonPressed = true };
        row.AddChild(_loop);
        row.AddChild(new Label { Text = "from" });
        _loopStart = new SpinBox { MinValue = 0, MaxValue = 3600, Step = 0.001, Suffix = "s" };
        row.AddChild(_loopStart);
        row.AddChild(new Label { Text = "to" });
        _loopEnd = new SpinBox { MinValue = 0, MaxValue = 3600, Step = 0.001, Suffix = "s", TooltipText = "0: the end of the file" };
        row.AddChild(_loopEnd);
        _volume = new SpinBox { MinValue = -40, MaxValue = 12, Step = 0.1, Suffix = "dB" };
        row.AddChild(_volume);
        row = new HBoxContainer();
        col.AddChild(row);
        Btn(row, "Assign file...", "Replace the selected sound by an audio file of your own (Ogg Vorbis, WAV, MP3)", () => Choose("*.ogg, *.wav, *.mp3 ; Audio", true, p =>
        {
            if (SelectedSound() is not { } s)
            {
                Say("select a sound first");
                return;
            }
            Say(_plugin.AssignSound(Kind, s, p, _loop.ButtonPressed, _loopStart.Value, _loopEnd.Value, (float)_volume.Value) ?? $"replaced by {p}");
        }));
        Btn(row, "Classic", "Back to the game's own sound", () => Say(SelectedSound() is { } s ? _plugin.ClearSound(Kind, s) ?? "classic" : "select a sound first"));
        _soundInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_soundInfo);
    }

    public SoundKind Kind
    {
        get => (SoundKind)_kind.Selected;
        set
        {
            _kind.Selected = (int)value;
            FillSounds();
        }
    }

    public void SetSoundSearch(string text)
    {
        _search.Text = text;
        FillSounds();
    }

    /// <summary>The game's sounds of a kind (read from the archive once; empty without it).</summary>
    public List<(int Id, string Name, string Bank, int Sample)> Classic(SoundKind kind)
    {
        if (_classic.TryGetValue(kind, out var l) && l.Count > 0)
            return l;
        var game = _plugin.Session.Game;
        if (game == null || !_plugin.Session.IsOpen)
            return new();
        l = game.ClassicSounds(kind switch { SoundKind.Music => "music", SoundKind.Sfx => "sfx", _ => "voice" }, "us");
        _classic[kind] = l;
        return l;
    }

    public int? SelectedSound() => _sounds.GetSelected() is { } it ? it.GetMetadata(0).AsInt32() : null;

    public void SelectSound(int id)
    {
        for (var it = _sounds.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
            if (it.GetMetadata(0).AsInt32() == id)
            {
                it.Select(0);
                return;
            }
    }

    public List<int> ListedSounds()
    {
        var l = new List<int>();
        for (var it = _sounds.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
            l.Add(it.GetMetadata(0).AsInt32());
        return l;
    }

    private void FillSounds()
    {
        var keep = SelectedSound();
        _sounds.Clear();
        var root = _sounds.CreateItem();
        var set = MediaStore.LoadSounds();
        var replaced = set.Sounds.Where(s => s != null && s.Kind == Kind).ToDictionary(s => s.Id, s => s);
        var rows = Classic(Kind).Select(c => (c.Id, c.Name)).ToList();
        foreach (var id in replaced.Keys)
            if (!rows.Any(r => r.Id == id))
                rows.Add((id, ""));
        rows.Sort((a, b) => a.Id.CompareTo(b.Id));
        var q = _search.Text.Trim().ToLowerInvariant();
        int shown = 0;
        foreach (var (id, name) in rows)
        {
            if (q.Length > 0 && !id.ToString("x").Contains(q) && !name.ToLowerInvariant().Contains(q) && !id.ToString().Contains(q))
                continue;
            if (++shown > 2000)
                break;
            var it = _sounds.CreateItem(root);
            it.SetText(0, $"{id:x}");
            it.SetMetadata(0, id);
            it.SetText(1, name);
            it.SetText(2, replaced.TryGetValue(id, out var r) ? System.IO.Path.GetFileName(r.Stream) : "");
            if (id == keep)
                it.Select(0);
        }
        _soundInfo.Text = $"{rows.Count} {Kind.ToString().ToLowerInvariant()} ids, {replaced.Count} replaced ({MediaStore.AudioFile})";
    }

    /// <summary>The selected sound's classic stream (null: none), for the preview and tests.</summary>
    public AudioStreamWav? ClassicStream(SoundKind kind, int id)
    {
        var c = Classic(kind).FirstOrDefault(x => x.Id == id);
        if (c.Bank == null || _plugin.Session.Game is not { } game)
            return null;
        return game.ClassicSound(c.Bank, c.Sample);
    }

    private string? PlayClassic()
    {
        if (SelectedSound() is not { } id)
            return "select a sound first";
        var s = ClassicStream(Kind, id);
        if (s == null)
            return "the game's sound is not found in your copy";
        _player.Stream = s;
        _player.Play();
        return null;
    }

    private string? PlayReplacement()
    {
        if (SelectedSound() is not { } id)
            return "select a sound first";
        var r = MediaStore.LoadSounds().Sounds.FirstOrDefault(x => x != null && x.Key == (Kind, id));
        if (r == null || AudioReplacements.LoadStream(r.Stream) is not { } st)
            return "no replacement";
        st = (AudioStream)st.Duplicate();
        AudioReplacementView.SetLoop(st, r);
        _player.Stream = st;
        _player.Play();
        return null;
    }

    /// <summary>The preview player (for tests).</summary>
    public AudioStreamPlayer Player => _player;

    // ---- movies -----------------------------------------------------------------------------

    private void BuildMovies()
    {
        var col = Page(_tabs, "Movies");
        _movies = List(["Movie", "Replacement"]);
        col.AddChild(_movies);
        var row = new HBoxContainer();
        col.AddChild(row);
        row.AddChild(new Label { Text = "Sound" });
        _movieSound = new OptionButton { TooltipText = "The replacement's sound: the game's movie sound, the video's own, a file, none" };
        foreach (var s in new[] { "the game's", "the video's", "a file", "none" })
            _movieSound.AddItem(s);
        _movieSound.Selected = 1;
        row.AddChild(_movieSound);
        row = new HBoxContainer();
        col.AddChild(row);
        Btn(row, "Assign video...", "Replace the selected movie (every language's) by an Ogg Theora video of your own", () => Choose("*.ogv ; Ogg Theora", true, p =>
        {
            if (SelectedMovie() is not { } m)
            {
                Say("select a movie first");
                return;
            }
            Say(_plugin.AssignMovie(m, p, (MovieSound)_movieSound.Selected) ?? $"replaced by {p}");
        }));
        Btn(row, "Preview", "Play the replacement here", () => Say(Preview() ?? "previewing"));
        Btn(row, "Stop", "Stop the preview", () => _preview.Stop());
        Btn(row, "Classic", "Back to the game's own movie", () => Say(SelectedMovie() is { } m ? _plugin.ClearMovie(m) ?? "classic" : "select a movie first"));
        _preview = new VideoStreamPlayer { CustomMinimumSize = new Vector2(320, 180), Expand = true };
        col.AddChild(_preview);
        _movieInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_movieInfo);
    }

    public VideoStreamPlayer MoviePreview => _preview;

    public string? SelectedMovie() => _movies.GetSelected()?.GetMetadata(0).AsString();

    public void SelectMovie(string name)
    {
        for (var it = _movies.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
            if (it.GetMetadata(0).AsString() == name)
            {
                it.Select(0);
                return;
            }
    }

    public List<string> ListedMovies()
    {
        var l = new List<string>();
        for (var it = _movies.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
            l.Add(it.GetMetadata(0).AsString());
        return l;
    }

    /// <summary>The game's movies (their names without the language: <c>LCI</c>), with their languages; empty without the archive.</summary>
    public SortedDictionary<string, List<string>> ClassicMovies()
    {
        var d = new SortedDictionary<string, List<string>>();
        if (_plugin.Session.Game is not { } game || !_plugin.Session.IsOpen)
            return d;
        foreach (var p in game.ClassicList("ps3data/video/"))
        {
            if (!p.EndsWith(".webm") || p.Contains("_480"))
                continue;
            // the archive's paths are lower case; the game opens the files by their names in
            // capitals with the language in lower case (LCI_us.webm)
            var file = System.IO.Path.GetFileNameWithoutExtension(p);
            var key = AudioReplacements.BaseMovieKey(file.ToLowerInvariant());
            var name = key.ToUpperInvariant();
            file = name + file[key.Length..];
            if (!d.TryGetValue(name, out var l))
                d[name] = l = new();
            l.Add(file);
        }
        return d;
    }

    private void FillMovies()
    {
        var keep = SelectedMovie();
        _movies.Clear();
        var root = _movies.CreateItem();
        var set = MediaStore.LoadMovies();
        var names = ClassicMovies().Keys.ToList();
        foreach (var m in set.Movies)
            if (m != null && !names.Any(n => AudioReplacements.MovieKey(n) == AudioReplacements.MovieKey(m.Movie)))
                names.Add(m.Movie);
        foreach (var n in names)
        {
            var it = _movies.CreateItem(root);
            it.SetText(0, n);
            it.SetMetadata(0, n);
            var r = set.Movies.FirstOrDefault(m => m != null && AudioReplacements.MovieKey(m.Movie) == AudioReplacements.MovieKey(n));
            it.SetText(1, r != null ? System.IO.Path.GetFileName(r.Video) : "");
            if (n == keep)
                it.Select(0);
        }
        _movieInfo.Text = $"{names.Count} movies, {set.Movies.Count} replaced ({MediaStore.MoviesFile}). gd-ffx does not draw the game's own movie pictures yet: the preview plays the replacement.";
    }

    private string? Preview()
    {
        if (SelectedMovie() is not { } m)
            return "select a movie first";
        var r = MediaStore.LoadMovies().Movies.FirstOrDefault(x => x != null && AudioReplacements.MovieKey(x.Movie) == AudioReplacements.MovieKey(m));
        if (r == null || AudioReplacements.LoadVideo(r.Video) is not { } v)
            return "no replacement";
        _preview.Stream = v;
        _preview.Play();
        return null;
    }

    /// <summary>Previews movie <paramref name="name"/>'s replacement (tests); an error or null.</summary>
    public string? PreviewMovie(string name)
    {
        SelectMovie(name);
        return Preview();
    }

    // ---- common -----------------------------------------------------------------------------

    /// <summary>Fills every tab again (files changed).</summary>
    public void Refresh()
    {
        FillEffects();
        FillSounds();
        FillMovies();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged && IsVisibleInTree())
            Refresh();
    }

    private void Choose(string filter, bool anyFile, System.Action<string> picked)
    {
        _dialog?.QueueFree();
        _dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = anyFile ? FileDialog.AccessEnum.Filesystem : FileDialog.AccessEnum.Resources,
            Filters = [filter],
        };
        _dialog.FileSelected += p => picked(p);
        AddChild(_dialog);
        _dialog.PopupCentered(new Vector2I(800, 500));
    }

    private void Say(string text)
    {
        var l = _tabs.CurrentTab switch { 0 => _effectInfo, 1 => _soundInfo, _ => _movieInfo };
        l.Text = text;
    }
}
#endif
