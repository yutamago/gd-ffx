using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// The start-up opening (<c>openingscreen.swf</c>, G5), natively. The game opens the movie in
/// <c>PhyreScene_beginRenderOpeningScreen</c> and loops in <c>graphicInitScene</c> until the
/// movie calls its <c>Finish</c> callback; with <see cref="FfxGame.SetNativeOpening"/> the
/// extension calls it when this view has played the movie's 263 frames (29.97 a second, 8.8 s):
/// black; the SQUARE ENIX logo (the movie's bitmap, drawn from the player's copy) fading in over
/// frames 6 to 34, held, fading out to frame 155; then the autosave notice: the game's text
/// <c>Loc_Texture_Saveload_Attention_Before_Game</c> (localisation line 0x1f9, the name the
/// movie asks <c>getLocByStr</c> for, mapped by <c>FFX.exe</c>) over the running chocobo, a black
/// cover fading out over frames 156 to 170 and back in from 249. The fades are the movie's own
/// (<see cref="FfxGame.FlashAlphas"/>). The original has no key to skip it.
/// </summary>
public partial class OpeningView : Control
{
    public const string Movie = "openingscreen.swf";
    /// <summary>The movie's frames and rate.</summary>
    private int _frames = 263;
    private float _fps = 29.96875f;
    /// <summary>1920 x 1080 stage to the 1280 x 720 overlay.</summary>
    private const float StageScale = 2f / 3f;
    /// <summary>The localisation line of the notice.</summary>
    public const int NoticeLine = 0x1f9;

    private double _t = -1;
    private bool _finished, _done;
    private float[] _logoAlpha = [], _coverAlpha = [];
    private ImageTexture? _logo;
    private Rect2 _logoRect;
    private readonly List<ImageTexture> _bird = new();
    private Rect2 _birdRect;
    private string _notice = "";
    private Label _text = null!;

    /// <summary>The frame shown now (0 before the opening), and what the view showed (for tests).</summary>
    public int Frame { get; private set; }
    public bool LogoShown { get; private set; }
    public bool NoticeShown { get; private set; }
    public static OpeningView? Instance { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        _text = Panels.Text("", 26, new Color(1, 1, 1), MessageView.UiFont);
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(_text);
        // the black cover over the notice (above the text)
        _cover = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        _cover.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_cover);
    }

    private ColorRect _cover = null!;

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    private void Load(FfxGame game)
    {
        if (game.FlashInfo(Movie) is { } info)
        {
            _frames = info.Frames;
            _fps = info.Fps > 0 ? info.Fps : _fps;
        }
        // depth 1: the logo (frames 6..155); depth 5: the black cover over the notice
        _logoAlpha = game.FlashAlphas(Movie, 1);
        _coverAlpha = game.FlashAlphas(Movie, 5);
        // the logo: the main timeline at its full frame (34), only where it draws
        var b = game.FlashBounds(Movie, 0, 34);
        if (b.Size.X > 0)
        {
            // at the stage's own size (1920 x 1080), drawn at the overlay's
            if (game.FlashRender(Movie, 0, 34, b.Position.Floor(), (int)Mathf.Ceil(b.Size.X) + 2,
                    (int)Mathf.Ceil(b.Size.Y) + 2, 1f) is { } img)
            {
                _logo = ImageTexture.CreateFromImage(img);
                _logoRect = new Rect2(b.Position.Floor() * StageScale, new Vector2(img.GetWidth(), img.GetHeight()) * StageScale);
            }
        }
        // the running chocobo (sprite 438, 8 frames), drawn at twice the overlay's size
        var bird = 438;
        var bb = game.FlashBounds(Movie, bird, 1);
        for (int f = 1; f <= 8 && bb.Size.X > 0; f++)
        {
            var pad = new Vector2(8, 8);
            var origin = bb.Position - pad;
            var size = bb.Size + pad * 2;
            const float k = 2 * StageScale;
            if (game.FlashRender(Movie, bird, f, origin, (int)(size.X * k), (int)(size.Y * k), k) is { } img)
            {
                _bird.Add(ImageTexture.CreateFromImage(img));
                // the group (sprite 439, 561 x 151 stage pixels: the text line on top, the chocobo
                // at 241.7, 72.75 under it) centred on the screen
                var group = new Vector2(1920 - 561, 1080 - 151) / 2;
                _birdRect = new Rect2((group + new Vector2(241.7f, 72.75f) + origin) * StageScale, size * StageScale);
            }
        }
        _notice = game.FlashLines(NoticeLine, BootLanguage());
    }

    public override void _Process(double delta)
    {
        var host = GameHost.Instance;
        var game = host?.Game;
        if (game == null || _done || host?.NativeOpening != true)
        {
            Visible = false;
            return;
        }
        if (_t < 0)
        {
            // the game opened the movie: the opening runs
            if (!FlashScreens.Movies.Exists(m => m.Name == Movie) && !game.OpeningState().Waiting)
                return;
            Load(game);
            _t = 0;
            GD.Print($"ffx: opening: {_frames} frames at {_fps:0.##} fps (logo {(_logo != null ? "drawn" : "missing")}, chocobo {_bird.Count} frames)");
        }
        _t += delta;
        Frame = Mathf.Min((int)(_t * _fps) + 1, _frames);
        if ((int)(_t * _fps) + 1 > _frames && !_finished)
        {
            _finished = true;
            game.OpeningFinish();
            GD.Print("ffx: opening: played; Finish asked");
        }
        if (_finished && game.OpeningState().Ended)
        {
            _done = true;
            Visible = false;
            GD.Print("ffx: opening: the game went on");
            return;
        }
        Visible = true;
        QueueRedraw();
        var notice = Frame >= 156;
        _text.Visible = notice;
        _cover.Visible = notice;
        _cover.Modulate = new Color(1, 1, 1, Cover);
        if (notice)
        {
            NoticeShown = true;
            Ui.SetText(_text, _notice);
            var size = _text.GetCombinedMinimumSize();
            var group = new Vector2(1920 - 561, 1080 - 151) / 2 * StageScale;
            _text.Size = size;
            _text.Position = new Vector2((1280 - size.X) / 2, group.Y + 20 * StageScale - size.Y / 2);
        }
    }

    private static float At(float[] a, int frame) => frame >= 1 && frame <= a.Length ? a[frame - 1] : 0;

    public override void _Draw()
    {
        var view = GetViewportRect().Size;
        DrawRect(new Rect2(Vector2.Zero, view), Colors.Black);
        if (Frame < 156)
        {
            var a = At(_logoAlpha, Frame);
            if (_logo != null && a > 0)
            {
                DrawTextureRect(_logo, _logoRect, false, new Color(1, 1, 1, a));
                LogoShown = true;
            }
            return;
        }
        if (_bird.Count > 0)
            DrawTextureRect(_bird[(Frame - 156) % _bird.Count], _birdRect, false);
    }

    /// <summary>
    /// The game's language before its first snapshot: <c>GameSetting.ini</c>'s (the game reads it
    /// at boot) as the localisation kit's code.
    /// </summary>
    public static string BootLanguage() => GameSettings.GameLanguage(GameSettings.DocumentsDir()) switch
    {
        "jp" => "jp",
        "fr" => "fr",
        "de" => "de",
        "it" => "it",
        "es" => "sp",
        "kr" => "kr",
        "ch" => "ch",
        _ => "us",
    };

    /// <summary>The black cover's alpha now (the notice fading in and out).</summary>
    public float Cover => Frame >= 156 ? At(_coverAlpha, Frame) : 0;
}
