using System.Collections.Generic;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// Replaced movies in the game (R7; docs/architecture.md, "Replacing music, sound effects, voice
/// and movies"). The game plays its movie as it always does (its scripts wait on its frames); the
/// extension says which movie it opened and where the movie is (<see cref="FfxGame.MovieEvents"/>,
/// <see cref="FfxGame.MovieState"/>), and while the movie has a replacement
/// (<see cref="AudioReplacements.Movies"/>) this view shows the replacement's video over the
/// picture, 16:9 with bars, its sound as the replacement says (the game's movie sound silenced
/// unless kept), subtitles from its <c>.srt</c>; paused when the game pauses the movie, gone when
/// the game ends or skips it. A movie without a replacement stays the game's (gd-ffx draws no
/// classic movie picture yet: docs/remaster-plan.md, R7 "Open"). Off with
/// <see cref="Replacements"/> (F8).
/// </summary>
public partial class MovieView : Control
{
    /// <summary>A replacement shown (for tests): the game's movie, the video, when, whether the game's sound was silenced.</summary>
    public readonly record struct Shown(string Movie, string Video, double At, bool Silenced);

    public List<Shown> Log { get; } = new();

    /// <summary>Every movie the game opened since the start (names), for tests and the editor's list.</summary>
    public List<string> Opened { get; } = new();

    private VideoStreamPlayer _video = null!;
    private AudioStreamPlayer _sound = null!;
    private Label _subtitle = null!;
    private ColorRect _back = null!;
    private RemasterMovie? _playing;
    private string _movie = "";
    private int _player;
    private double _clock, _since;
    private List<(double From, double To, string Text)> _subs = new();
    private int _flips = -1;

    /// <summary>The replacement playing now (null: none) and its player, for tests.</summary>
    public RemasterMovie? Playing => _playing;
    public VideoStreamPlayer Video => _video;
    /// <summary>The game's movie frame the last time it was read.</summary>
    public long GameFrame { get; private set; } = -1;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        ProcessMode = ProcessModeEnum.Always;
        _back = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        _back.SetAnchorsPreset(LayoutPreset.FullRect);
        _video = new VideoStreamPlayer { Expand = true, MouseFilter = MouseFilterEnum.Ignore };
        _sound = new AudioStreamPlayer { Name = "MovieSound" };
        _subtitle = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _subtitle.AddThemeFontSizeOverride("font_size", 28);
        _subtitle.AddThemeColorOverride("font_outline_color", Colors.Black);
        _subtitle.AddThemeConstantOverride("outline_size", 6);
        AddChild(_back);
        AddChild(_video);
        AddChild(_subtitle);
        AddChild(_sound);
        Visible = false;
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        var game = GameHost.Instance?.Game;
        if (game == null)
            return;
        foreach (var e in game.MovieEvents())
        {
            bool opened = e["opened"].AsBool();
            var name = e["name"].AsString();
            int player = (int)e["player"].AsInt64();
            if (opened && !AudioReplacements.MovieKey(name).Contains("_480"))
            {
                Opened.Add(name);
                Begin(game, name, player);
            }
            else if (!opened && player == _player)
                End(game);
        }
        if (_flips != Replacements.Changed)
        {
            _flips = Replacements.Changed;
            if (!Replacements.On && _playing != null)
                End(game);
        }
        if (_playing == null)
            return;
        var st = game.MovieState();
        GameFrame = st["frame"].AsInt64();
        bool paused = st["paused"].AsBool();
        bool ended = st["ended"].AsBool() || GameFrame == 0xfffe;
        if (ended || (!st["playing"].AsBool() && _clock - _since > 2.0))
        {
            End(game);
            return;
        }
        _video.Paused = paused;
        _sound.StreamPaused = paused;
        Layout();
        Subtitle(_video.StreamPosition);
    }

    private void Begin(FfxGame game, string name, int player)
    {
        if (_playing != null)
            End(game);
        if (!Replacements.On || AudioReplacements.FindMovie(name) is not { } movie)
            return;
        var stream = AudioReplacements.LoadVideo(movie.Video);
        if (stream == null)
        {
            GD.PrintErr($"ffx: movies: {name}: {movie.Video} not found; the game's own instead");
            return;
        }
        var m = (Movie: movie, Layer: "");
        _playing = m.Movie;
        _movie = name;
        _player = player;
        _since = _clock;
        _video.Stream = stream;
        // the video's own sound track only when asked for
        _video.VolumeDb = m.Movie.Sound == MovieSound.Video ? m.Movie.VolumeDb : -80;
        _video.Play();
        if (m.Movie.Sound == MovieSound.File && AudioReplacements.LoadStream(m.Movie.SoundFile) is { } snd)
        {
            _sound.Stream = snd;
            _sound.VolumeDb = m.Movie.VolumeDb;
            _sound.Play();
        }
        bool silence = m.Movie.Sound != MovieSound.Game;
        game.SetMovieMuted(silence);
        _subs = m.Movie.Subtitles.Length > 0 ? ReadSrt(m.Movie.Subtitles) : new();
        Visible = true;
        Layout();
        Log.Add(new Shown(name, m.Movie.Video, _clock, silence));
        GD.Print($"ffx: movies: {name} replaced by {m.Movie.Video} (sound: {m.Movie.Sound}{(silence ? ", the game's silenced" : "")})");
    }

    private void End(FfxGame game)
    {
        if (_playing == null)
            return;
        GD.Print($"ffx: movies: {_movie} ended (the replacement at {_video.StreamPosition:F1} s)");
        _video.Stop();
        _video.Stream = null;
        _sound.Stop();
        game.SetMovieMuted(false);
        _playing = null;
        _movie = "";
        _player = 0;
        Visible = false;
    }

    /// <summary>The video 16:9 in the middle of the window, with bars (as the game's picture).</summary>
    private void Layout()
    {
        var size = GetViewportRect().Size;
        float w = size.X, h = size.X * 9f / 16f;
        if (h > size.Y)
        {
            h = size.Y;
            w = h * 16f / 9f;
        }
        _video.Position = new Vector2((size.X - w) / 2, (size.Y - h) / 2);
        _video.Size = new Vector2(w, h);
        _subtitle.Position = _video.Position + new Vector2(w * 0.1f, h * 0.75f);
        _subtitle.Size = new Vector2(w * 0.8f, h * 0.2f);
    }

    private void Subtitle(double t)
    {
        string text = "";
        foreach (var (from, to, s) in _subs)
            if (t >= from && t < to)
            {
                text = s;
                break;
            }
        if (_subtitle.Text != text)
            _subtitle.Text = text;
    }

    /// <summary>The cue shown now (for tests).</summary>
    public string SubtitleText => _subtitle.Text;

    /// <summary>Reads a SubRip file: (from, to, text) per cue.</summary>
    public static List<(double From, double To, string Text)> ReadSrt(string path)
    {
        var cues = new List<(double, double, string)>();
        if (!FileAccess.FileExists(path))
            return cues;
        var blocks = FileAccess.GetFileAsString(path).Replace("\r", "").Split("\n\n", System.StringSplitOptions.RemoveEmptyEntries);
        foreach (var b in blocks)
        {
            var lines = b.Trim().Split('\n');
            int t = System.Array.FindIndex(lines, l => l.Contains("-->"));
            if (t < 0)
                continue;
            var parts = lines[t].Split("-->");
            if (parts.Length != 2 || Time(parts[0]) is not { } from || Time(parts[1]) is not { } to)
                continue;
            cues.Add((from, to, string.Join("\n", lines[(t + 1)..])));
        }
        return cues;
    }

    private static double? Time(string s)
    {
        // hh:mm:ss,mmm
        var p = s.Trim().Replace(',', '.').Split(':');
        if (p.Length != 3 || !int.TryParse(p[0], out var h) || !int.TryParse(p[1], out var m)
            || !double.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec))
            return null;
        return h * 3600 + m * 60 + sec;
    }
}
