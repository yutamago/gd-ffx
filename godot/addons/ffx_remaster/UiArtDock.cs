#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The <b>UI Art</b> dock (docs/remaster-editor.md, "UI art"; R7b): the game's Flash movies (the
/// native screens draw their art), a movie's art by the ids the screens ask for
/// (<see cref="UiArt.ArtId"/>: the classes, a sprite's labels, the bitmaps, the main timeline's
/// frames) with thumbnails drawn from the player's copy (in memory, never saved), the replaced ones
/// marked; <b>Assign image...</b> for every language's movie or this one, <b>Classic</b>, the fonts
/// by role; the preview draws the piece classic and replaced, and for the save screen a row of it
/// (portraits, thumbnail, cursor) as the screen draws it. Each change is one undo step
/// (<see cref="RemasterPlugin.AssignUiArt"/>).
/// </summary>
[Tool]
public partial class UiArtDock : EditorDock
{
    private readonly RemasterPlugin _plugin;
    private Tree _movies = null!, _art = null!;
    private CheckBox _family = null!;
    private Label _info = null!;
    private UiArtPreview _preview = null!;
    private EditorFileDialog? _dialog;
    private string _movie = "";

    /// <summary>One art id of the movie shown: its character, the frame drawn for it.</summary>
    public sealed record ArtItem(string Id, int Character, int Frame, string Kind);

    private readonly List<ArtItem> _items = new();
    private readonly Dictionary<(string, string), ImageTexture?> _thumbs = new();

    public UiArtDock(RemasterPlugin plugin)
    {
        _plugin = plugin;
        Name = "FfxUiArt";
        Title = "UI Art";
        DefaultSlot = DockSlot.RightUl;
        Build();
    }
    public UiArtDock() : this(null!) { }

    private void Build()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(col);
        col.AddChild(new Label { Text = "The game's Flash movies (the native screens draw their art)" });
        _movies = new Tree { Columns = 1, HideRoot = true, CustomMinimumSize = new Vector2(0, 110) };
        _movies.ItemSelected += () => { if (_movies.GetSelected()?.GetMetadata(0).AsString() is { } m) ShowMovie(m); };
        col.AddChild(_movies);
        _art = new Tree { Columns = 3, HideRoot = true, CustomMinimumSize = new Vector2(0, 220), SizeFlagsVertical = SizeFlags.ExpandFill };
        _art.SetColumnTitlesVisible(true);
        _art.SetColumnTitle(0, "Art");
        _art.SetColumnTitle(1, "Kind");
        _art.SetColumnTitle(2, "Replaced by");
        _art.ItemSelected += () => _preview.Show(_movie, SelectedArt());
        col.AddChild(_art);
        var row = new HBoxContainer();
        col.AddChild(row);
        _family = new CheckBox { Text = "every language", ButtonPressed = true, TooltipText = "Assign to every language's movie (saveload for saveload_jp, _kr, _ch), else to this file only" };
        row.AddChild(_family);
        Btn(row, "Assign image...", "Draw your image where the selected art is drawn (its size)", () => Choose("*.png, *.webp, *.jpg, *.jpeg ; Images", p => Say(Assign(SelectedArt()?.Id ?? "", p) ?? $"replaced by {p}")));
        Btn(row, "Classic", "The game's own art again", () => Say(Clear(SelectedArt()?.Id ?? "") ?? "classic"));
        row = new HBoxContainer();
        col.AddChild(row);
        Btn(row, "UI font...", "The native screens' text font (the role ui; the system's fonts stay behind it for the glyphs it lacks)", () => Choose("*.ttf, *.otf, *.woff, *.woff2 ; Fonts", p => Say(_plugin.AssignUiFont("ui", p) ?? $"the ui font: {p}")));
        Btn(row, "System font", "The system's font again", () => Say(_plugin.AssignUiFont("ui", "") ?? "the system font"));
        Btn(row, "Reload", "List the movies again", Refresh);
        _preview = new UiArtPreview { CustomMinimumSize = new Vector2(0, 200) };
        col.AddChild(_preview);
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(_info);
    }

    private static Button Btn(Container row, string text, string tip, System.Action act)
    {
        var b = new Button { Text = text, TooltipText = tip };
        b.Pressed += act;
        row.AddChild(b);
        return b;
    }

    public void Say(string text) => _info.Text = text;

    private void Choose(string filter, System.Action<string> then)
    {
        _dialog?.QueueFree();
        _dialog = new EditorFileDialog { FileMode = EditorFileDialog.FileModeEnum.OpenFile, Access = EditorFileDialog.AccessEnum.Filesystem };
        _dialog.AddFilter(filter);
        _dialog.FileSelected += p => then(p);
        AddChild(_dialog);
        _dialog.PopupCentered(new Vector2I(900, 600));
    }

    // ---- lists ---------------------------------------------------------------------------------

    /// <summary>The movies the archive has (file names), sorted.</summary>
    public List<string> Movies()
    {
        var game = _plugin.Session.Game;
        if (game == null || !_plugin.Session.IsOpen)
            return [];
        return game.ClassicList("flash/").Where(p => p.EndsWith(".swf")).Select(p => p[(p.LastIndexOf('/') + 1)..]).Distinct().OrderBy(n => n).ToList();
    }

    public void Refresh()
    {
        UiArt.Invalidate();
        _movies.Clear();
        var top = _movies.CreateItem();
        foreach (var m in Movies())
        {
            var it = _movies.CreateItem(top);
            int replaced = UiArt.Entries.Keys.Count(k => k.Movie == UiArt.MovieKey(m) || k.Movie == AudioReplacements.BaseMovieKey(UiArt.MovieKey(m)));
            it.SetText(0, replaced > 0 ? $"{m}  ({replaced} replaced)" : m);
            it.SetMetadata(0, m);
            if (m == _movie)
                it.Select(0);
        }
        if (_movie.Length > 0)
            ShowMovie(_movie);
    }

    /// <summary>The art ids of movie <paramref name="movie"/>: its classes (and a sprite's labels), its bitmaps, the main timeline's labels.</summary>
    public static List<ArtItem> ArtOf(FfxGame game, string movie)
    {
        var list = new List<ArtItem>();
        var syms = game.FlashSymbols(movie);
        foreach (var s in syms.Where(s => s.Class.Length > 0).OrderBy(s => s.Class))
        {
            list.Add(new ArtItem(s.Class, s.Id, 1, s.Kind));
            foreach (var (label, frame) in s.Labels)
                list.Add(new ArtItem(UiArt.ArtId(s.Class, label), s.Id, frame, "frame"));
        }
        foreach (var s in syms.Where(s => s.Class.Length == 0 && s.Kind == "bitmap"))
            list.Add(new ArtItem($"#{s.Id}", s.Id, 1, "bitmap"));
        if (game.FlashInfo(movie) is { } info && info.Frames > 1)
            list.Add(new ArtItem("#0@1", 0, 1, "timeline"));
        return list;
    }

    public void ShowMovie(string movie)
    {
        // the selection kept over a refresh (an edit, its undo)
        var keep = movie == _movie ? SelectedArt()?.Id : null;
        _movie = movie;
        _items.Clear();
        _art.Clear();
        var top = _art.CreateItem();
        if (_plugin.Session.Game is not { } game)
            return;
        _items.AddRange(ArtOf(game, movie));
        foreach (var a in _items)
        {
            var it = _art.CreateItem(top);
            it.SetText(0, a.Id);
            it.SetText(1, a.Kind);
            it.SetMetadata(0, a.Id);
            if (UiArt.Find(movie, a.Id) is { } e)
                it.SetText(2, e.Image.GetFile());
            if (Thumbnail(game, movie, a) is { } t)
            {
                it.SetIcon(0, t);
                it.SetIconMaxWidth(0, 40);
            }
        }
        Say($"{movie}: {_items.Count} art id(s)");
        if (keep != null)
            Select(keep);
        else
            _preview.Show(_movie, null);
    }

    /// <summary>The art ids listed for the movie shown (the checks).</summary>
    public IReadOnlyList<ArtItem> Items => _items;

    public string Movie => _movie;

    public void Select(string id)
    {
        for (var it = _art.GetRoot()?.GetFirstChild(); it != null; it = it.GetNext())
            if (it.GetMetadata(0).AsString() == id)
            {
                it.Select(0);
                _preview.Show(_movie, SelectedArt());
                return;
            }
    }

    public ArtItem? SelectedArt()
    {
        var id = _art.GetSelected()?.GetMetadata(0).AsString();
        return _items.FirstOrDefault(i => i.Id == id);
    }

    /// <summary>A piece drawn from the player's movie, about 48 pixels (in memory; null when it draws nothing).</summary>
    public ImageTexture? Thumbnail(FfxGame game, string movie, ArtItem a, int size = 48)
    {
        if (_thumbs.TryGetValue((movie, a.Id + "|" + size), out var t))
            return t;
        t = null;
        var b = game.FlashBounds(movie, a.Character, a.Frame);
        if (b.Size.X > 0 && b.Size.Y > 0)
        {
            float k = Mathf.Min(4f, size / Mathf.Max(b.Size.X, b.Size.Y));
            if (game.FlashRender(movie, a.Character, a.Frame, b.Position, Mathf.Max(1, (int)Mathf.Ceil(b.Size.X * k)), Mathf.Max(1, (int)Mathf.Ceil(b.Size.Y * k)), k) is { } img)
                t = ImageTexture.CreateFromImage(img);
        }
        _thumbs[(movie, a.Id + "|" + size)] = t;
        return t;
    }

    private string MovieFor() => _family.ButtonPressed ? AudioReplacements.BaseMovieKey(UiArt.MovieKey(_movie)) : _movie;

    /// <summary>Assigns <paramref name="image"/> to art <paramref name="id"/> of the movie shown (every language's unless unticked).</summary>
    public string? Assign(string id, string image)
    {
        if (id.Length == 0 || _movie.Length == 0)
            return "select a movie's art first";
        return _plugin.AssignUiArt(MovieFor(), id, image);
    }

    public string? Clear(string id)
    {
        if (id.Length == 0 || _movie.Length == 0)
            return "select a movie's art first";
        return _plugin.ClearUiArt(MovieFor(), id) ?? (_family.ButtonPressed ? _plugin.ClearUiArt(_movie, id) : null);
    }

    public UiArtPreview Preview => _preview;
}

#endif
