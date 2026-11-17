using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// The autosave prompt (<c>chocobo.swf</c>, G5), natively. When an autosave starts the game's
/// <c>PhyreScene_showAutoSavingPrompt</c> (<c>0x0066f480</c>) deletes the last prompt and opens
/// the movie anew at 1220, 660, 50 x 50 on the 1280 x 720 screen; the movie has no calls: its
/// 70 frames (29.97 a second, 2.3 s) fade a running chocobo in over 4 frames, run it, and fade
/// it out over frames 66 to 70, where it stops. Each new <c>chocobo.swf</c> the game opens
/// (<see cref="FlashScreens.AutosavePrompts"/>) plays it here, every frame drawn from the
/// player's own movie (<see cref="FfxGame.FlashRender"/>, at twice the size for sharpness).
/// </summary>
public partial class AutosaveView : Control
{
    public const string Movie = "chocobo.swf";
    /// <summary>Where the game opens it (<c>IggyPlayerOpenScaled(path, 1220, 660, 50, 50)</c>).</summary>
    public static readonly Rect2 At = new(1220, 660, 50, 50);

    private readonly List<ImageTexture> _frames = new();
    private float _fps = 29.96875f;
    private int _seen;
    private double _t = -1;

    /// <summary>Prompts played so far, and the frame shown now (0: none), for tests.</summary>
    public int Played { get; private set; }
    public int Frame { get; private set; }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    private bool Load(FfxGame game)
    {
        if (_frames.Count > 0)
            return true;
        if (game.FlashInfo(Movie) is not { } info)
            return false;
        _fps = info.Fps > 0 ? info.Fps : _fps;
        int px = (int)(At.Size.X * 2);
        float scale = px / info.Width;
        for (int f = 1; f <= info.Frames; f++)
            if (game.FlashRender(Movie, 0, f, Vector2.Zero, px, px, scale) is { } img)
                _frames.Add(ImageTexture.CreateFromImage(img));
        return _frames.Count > 0;
    }

    public override void _Process(double delta)
    {
        var game = GameHost.Instance?.Game;
        if (game == null)
            return;
        if (FlashScreens.AutosavePrompts > _seen)
        {
            _seen = FlashScreens.AutosavePrompts;
            if (Load(game))
            {
                _t = 0;
                Played++;
                GD.Print($"ffx: autosave prompt {Played} ({_frames.Count} frames)");
            }
        }
        if (_t < 0)
            return;
        _t += delta;
        Frame = (int)(_t * _fps) + 1;
        if (Frame > _frames.Count)
        {
            // the movie stops on its last frame (transparent)
            _t = -1;
            Frame = 0;
        }
        QueueRedraw();
        if (Frame == 30)
            FlashScreens.Shot("autosave");
    }

    public override void _Draw()
    {
        if (Frame >= 1 && Frame <= _frames.Count)
            DrawTextureRect(_frames[Frame - 1], At, false);
    }
}
