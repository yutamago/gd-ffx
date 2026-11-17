using Godot;

namespace GdFfx;

/// <summary>
/// The held picture of a map change (docs/architecture.md, "Map transitions"): while
/// <see cref="StateView3D.Holding"/> it covers the 3D view, the screen fade and the game's
/// filters with the last frame drawn before the hold began (the viewport's picture, taken
/// once; black before any map was shown and under the headless renderer, and from the moment
/// the game's fade holds the screen black), so the new map and its characters come up together. The message boxes,
/// menus and overlays above it stay live.
/// </summary>
public partial class TransitionView : ColorRect
{
    private TextureRect _picture = null!;
    private ImageTexture? _texture;
    private StateView3D? _view;

    /// <summary>Holds begun, and the frames covered so far (for the checks).</summary>
    public (int Holds, int Frames) Shown { get; private set; }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Color = Colors.Black;
        Visible = false;
        _picture = new TextureRect
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
        };
        _picture.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_picture);
    }

    public override void _Process(double delta)
    {
        _view ??= GetTree().Root.FindChild("StateView3D", true, false) as StateView3D;
        bool hold = _view?.Holding == true;
        if (hold == Visible)
        {
            if (!hold)
                return;
            Shown = (Shown.Holds, Shown.Frames + 1);
            // the game's own picture went black: so does the held one (until the hold ends)
            if (_view!.ScreenBlack)
                _picture.Visible = false;
            return;
        }
        Visible = hold;
        if (!hold)
            return;
        Shown = (Shown.Holds + 1, Shown.Frames + 1);
        // the viewport's texture still holds the last frame drawn (this one is drawn after
        // _Process), which was not held: a picture the player saw
        _picture.Visible = false;
        if (_view?.EverShown != true || _view.ScreenBlack || DisplayServer.GetName() == "headless")
            return;
        var image = GetViewport().GetTexture().GetImage();
        if (image == null || image.IsEmpty())
            return;
        if (_texture != null && _texture.GetSize() == image.GetSize())
            _texture.Update(image);
        else
            _texture = ImageTexture.CreateFromImage(image);
        image.Dispose();
        _picture.Texture = _texture;
        _picture.Visible = true;
    }
}
