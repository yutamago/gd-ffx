#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The UI Art dock's preview: the selected piece classic and with its replacement at the classic
/// size, and for the save screen (<c>saveload_*.swf</c>) one row as <see cref="SaveLoadView"/> lays it
/// out (the cursor, three portraits, the thumbnail), each piece replaced where the project says.
/// </summary>
[Tool]
public partial class UiArtPreview : Control
{
    private string _movie = "";
    private UiArtDock.ArtItem? _item;

    /// <summary>The pieces the last drawing drew replaced (the checks).</summary>
    public readonly List<string> Replaced = new();

    public void Show(string movie, UiArtDock.ArtItem? item)
    {
        _movie = movie;
        _item = item;
        QueueRedraw();
        Measure();
    }

    private (Texture2D? Classic, Texture2D? Replaced) Piece(UiArtDock dock, FfxGame game, UiArtDock.ArtItem a, int size)
    {
        var classic = dock.Thumbnail(game, _movie, a, size);
        return (classic, UiArt.Replacement(_movie, a.Id));
    }

    /// <summary>The pieces the preview draws replaced (also without a renderer).</summary>
    public void Measure()
    {
        Replaced.Clear();
        foreach (var a in Pieces())
            if (UiArt.Replacement(_movie, a.Id) != null)
                Replaced.Add(a.Id);
    }

    /// <summary>What the preview draws: the selected piece, and on the save screen its row's pieces.</summary>
    private IEnumerable<UiArtDock.ArtItem> Pieces()
    {
        if (RemasterPlugin.Instance?.UiArtDock is not { } dock)
            yield break;
        if (_item != null)
            yield return _item;
        if (!UiArt.MovieKey(_movie).StartsWith("saveload"))
            yield break;
        foreach (var id in new[] { SaveLoadView.ArrowClass, UiArt.ArtId(SaveLoadView.FaceClass, "_1"), UiArt.ArtId(SaveLoadView.FaceClass, "_2"), UiArt.ArtId(SaveLoadView.FaceClass, "_3") })
            if (dock.Items.FirstOrDefault(i => i.Id == id) is { } it && it != _item)
                yield return it;
        if (dock.Items.FirstOrDefault(i => i.Id.Length > 1 && i.Id[0] == 'm' && char.IsDigit(i.Id[1])) is { } thumb && thumb != _item)
            yield return thumb;
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.42f, 0.45f, 0.64f));
        if (RemasterPlugin.Instance is not { UiArtDock: { } dock, Session.Game: { } game } || _movie.Length == 0)
            return;
        var font = ThemeDB.FallbackFont;
        if (_item != null)
        {
            var (c, r) = Piece(dock, game, _item, 96);
            DrawString(font, new Vector2(8, 16), "classic", HorizontalAlignment.Left, -1, 12, Colors.White);
            DrawString(font, new Vector2(120, 16), r != null ? "replaced" : "(not replaced)", HorizontalAlignment.Left, -1, 12, Colors.White);
            var size = c?.GetSize() ?? new Vector2(96, 96);
            if (c != null)
                DrawTextureRect(c, new Rect2(new Vector2(8, 22), size), false);
            if (r != null)
                DrawTextureRect(r, new Rect2(new Vector2(120, 22), size), false);
        }
        if (!UiArt.MovieKey(_movie).StartsWith("saveload"))
            return;
        // one save row as the screen lays it out (SaveLoadView), at half size
        var at = new Vector2(8, 130);
        var rect = new Rect2(at, new Vector2(519, 50));
        DrawRect(rect, new Color(0.5f, 0.53f, 0.72f, 0.55f));
        DrawRect(rect, new Color(0.85f, 0.88f, 1f, 0.9f), false, 1f);
        DrawString(font, at + new Vector2(5, 12), "1   the save screen's row (preview)", HorizontalAlignment.Left, -1, 11, Colors.White);
        Texture2D? Get(string id, int size)
        {
            if (dock.Items.FirstOrDefault(i => i.Id == id) is not { } it)
                return null;
            var (c, r) = Piece(dock, game, it, size);
            return r ?? c;
        }
        for (int p = 0; p < 3; p++)
            if (Get(UiArt.ArtId(SaveLoadView.FaceClass, $"_{p + 1}"), 64) is { } face)
                DrawTextureRect(face, new Rect2(at + new Vector2(1 + 35 * p, 15), new Vector2(34, 34)), false);
        if (dock.Items.FirstOrDefault(i => i.Id.Length > 1 && i.Id[0] == 'm' && char.IsDigit(i.Id[1])) is { } th && Get(th.Id, 96) is { } thumb)
            DrawTextureRect(thumb, new Rect2(at + new Vector2(433, 0), new Vector2(85, 49)), false);
        if (Get(SaveLoadView.ArrowClass, 48) is { } arrow)
        {
            var s = arrow.GetSize() * 0.5f;
            DrawTextureRect(arrow, new Rect2(at + new Vector2(5 - s.X, 26 - s.Y / 2), s), false);
        }
    }
}
#endif
