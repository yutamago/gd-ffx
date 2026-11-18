using System.Collections.Generic;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// Replaced music, sound effects and voice lines in the game (R7; docs/architecture.md,
/// "Replacing music, sound effects, voice and movies"). The game plays its sounds as it always
/// does; the extension notes every sound command it queues (<c>rust/ffx-gd/src/media.rs</c>) and
/// makes the replaced ids silent in the game's mix (<see cref="FfxGame.SetAudioReplaced"/>), and
/// this node plays the replacements at the same moments: a music track when the game starts it
/// (looping between its loop points, fading as the game fades it, stopping when it stops), a sound
/// effect or a voice line when it starts, each at the game's volume for it times the options'
/// volumes. Ids without a replacement stay the game's. Off with <see cref="Replacements"/> (F8):
/// the game's own sounds again, from the next start.
/// </summary>
public partial class AudioReplacementView : Node
{
    /// <summary>A sound played (for tests): kind, id, the file, when.</summary>
    public readonly record struct Played(SoundKind Kind, int Id, string Stream, double At, bool Silenced);

    public List<Played> Log { get; } = new();

    /// <summary>The newest commands seen (at most 4096): kind, action, id, whether the game's was silenced (for tests).</summary>
    public List<(int Kind, int Action, int Id, bool Silenced)> Commands { get; } = new();

    private AudioStreamPlayer _music = null!;
    private AudioStreamPlayer _voice = null!;
    private readonly List<AudioStreamPlayer> _sfx = new();
    private RemasterSound? _musicSound;
    private int _musicId = -1;
    private float _musicGame = 1f; // the game's volume for the music, 0..1
    private Tween? _musicTween;
    private int _version = -1, _flips = -1;
    private double _clock;
    // the options' volumes, 0..1 (commands 0x42..0x45)
    private float _master = 1f, _musicVol = 1f, _voiceVol = 1f, _sfxVol = 1f;

    /// <summary>The music replacement playing (for tests).</summary>
    public AudioStreamPlayer MusicPlayer => _music;
    public int MusicId => _musicId;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _music = new AudioStreamPlayer { Name = "Music" };
        _voice = new AudioStreamPlayer { Name = "Voice" };
        AddChild(_music);
        AddChild(_voice);
    }

    /// <summary>Tells the extension which ids are silenced (the merged layers, or none when replacements are off).</summary>
    private void Push(FfxGame game)
    {
        var ids = new List<int>[3] { new(), new(), new() };
        if (Replacements.On)
            foreach (var ((kind, id), _) in AudioReplacements.Sounds)
                ids[(int)kind].Add(id);
        for (int k = 0; k < 3; k++)
            game.SetAudioReplaced(k, ids[k].ToArray());
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        var game = GameHost.Instance?.Game;
        if (game == null)
            return;
        if (_version != AudioReplacements.Version || _flips != Replacements.Changed)
        {
            _version = AudioReplacements.Version;
            bool flipped = _flips != Replacements.Changed;
            _flips = Replacements.Changed;
            Push(game);
            if (flipped && !Replacements.On)
                StopAll();
            // replacements on again: the track the game is playing gets its stand-in again
            else if (flipped && _gameMusic >= 0)
                Music(Start, 0x27, _gameMusic, 0, 0, true);
        }
        var ev = game.AudioEvents();
        for (int at = 0; at + 7 < ev.Length; at += 8)
            Handle(ev[at], ev[at + 1], ev[at + 2], ev[at + 3], ev[at + 4], ev[at + 5], ev[at + 6], ev[at + 7] != 0);
        // a music loop with an end point of its own
        if (_musicSound is { Loop: true, LoopEnd: > 0 } ms && _music.Playing && _music.GetPlaybackPosition() >= ms.LoopEnd)
            _music.Seek((float)ms.LoopStart);
    }

    private const int Start = 1, Stop = 2, Volume = 3, FadeOut = 4, Pause = 5, StopEverything = 6;

    private void Handle(int kind, int action, int cmd, int id, int a0, int a1, int a2, bool silenced)
    {
        if (kind < 3)
        {
            // the newest 4096 (a long session queues hundreds a minute)
            if (Commands.Count >= 4096)
                Commands.RemoveRange(0, 1024);
            Commands.Add((kind, action, id, silenced));
        }
        switch (kind)
        {
            case 0:
                Music(action, cmd, id, a0, a1, silenced);
                break;
            case 1 when action == Start:
            {
                var s = Replacements.On ? AudioReplacements.Find(SoundKind.Sfx, id) : null;
                if (s != null)
                    PlaySfx(s, a0 / 127f, silenced);
                break;
            }
            // the game's volume for an effect: at once (iSfxSetVolume) or over a1 frames (translate)
            case 1 when action == Volume:
                foreach (var (p, sid) in _sfxOf)
                    if (sid == id && p.Playing)
                        SfxVolume(p, Mathf.Clamp(a0 / 127f, 0f, 1f), a1 / 30.0);
                break;
            case 1 when action == Stop:
                foreach (var (p, sid) in _sfxOf)
                    if (sid == id && p.Playing)
                        p.Stop();
                break;
            case 2 when action == Stop:
                _voice.Stop();
                break;
            case 2 when action == Start:
            {
                var s = Replacements.On ? AudioReplacements.Find(SoundKind.Voice, id) : null;
                if (s != null && Prepare(_voice, s))
                {
                    _voice.VolumeDb = Mathf.LinearToDb(Mathf.Max(a1 / 127f * _voiceVol * _master, 1e-5f)) + s.VolumeDb;
                    _voice.Play();
                    Log.Add(new Played(SoundKind.Voice, id, s.Stream, _clock, silenced));
                }
                else if (_voice.Playing)
                    _voice.Stop();
                break;
            }
            case 3:
            {
                float v = Mathf.Clamp(a0 / 100f, 0f, 1f);
                switch (cmd)
                {
                    case 0x42: _master = v; break;
                    case 0x43: _musicVol = v; break;
                    case 0x44: _voiceVol = v; break;
                    case 0x45: _sfxVol = v; break;
                }
                ApplyMusicVolume();
                break;
            }
        }
    }

    /// <summary>The music the game is playing (-1 none), whether replaced or not.</summary>
    private int _gameMusic = -1;

    private void Music(int action, int cmd, int id, int a0, int a1, bool silenced)
    {
        if (action == Start)
            _gameMusic = id;
        else if (action == StopEverything || (action == Stop && id == _gameMusic))
            _gameMusic = -1;
        switch (action)
        {
            case Start:
            {
                var s = Replacements.On ? AudioReplacements.Find(SoundKind.Music, id) : null;
                StopMusic(0);
                if (s == null || !Prepare(_music, s))
                    return;
                _musicSound = s;
                _musicId = id;
                // the game starts a track at full volume (a cross play at 0, then raises it)
                _musicGame = cmd == 0x17 ? 0f : 1f;
                ApplyMusicVolume();
                _music.Play();
                if (cmd == 0x17 && a0 > 0)
                    Fade(1f, a0 / 30.0);
                Log.Add(new Played(SoundKind.Music, id, s.Stream, _clock, silenced));
                GD.Print($"ffx: audio: music {id:x} replaced by {s.Stream}{(silenced ? " (the game's silenced)" : "")}");
                break;
            }
            case Stop when id == _musicId:
            case StopEverything:
                StopMusic(0);
                break;
            case Volume when id == _musicId:
                // iMusicVolumeDirect (a1 0) or iMusicVolumeTranslate over a1 frames
                Fade(Mathf.Clamp(a0 / 127f, 0f, 1f), a1 / 30.0);
                break;
            case FadeOut when id == _musicId:
                Fade(0f, a0 / 30.0, stop: true);
                break;
            case Pause:
                _music.StreamPaused = a0 != 0;
                break;
        }
    }

    private void Fade(float to, double seconds, bool stop = false)
    {
        _musicTween?.Kill();
        if (seconds <= 0)
        {
            _musicGame = to;
            ApplyMusicVolume();
            if (stop)
                StopMusic(0);
            return;
        }
        _musicTween = CreateTween();
        _musicTween.TweenMethod(Callable.From<float>(v => { _musicGame = v; ApplyMusicVolume(); }), _musicGame, to, seconds);
        if (stop)
            _musicTween.TweenCallback(Callable.From(() => StopMusic(0)));
    }

    private void ApplyMusicVolume()
    {
        if (_musicSound == null)
            return;
        _music.VolumeDb = Mathf.LinearToDb(Mathf.Max(_musicGame * _musicVol * _master, 1e-5f)) + _musicSound.VolumeDb;
    }

    private void StopMusic(double _)
    {
        _musicTween?.Kill();
        _musicTween = null;
        if (_music.Playing)
            _music.Stop();
        _musicSound = null;
        _musicId = -1;
    }

    private void StopAll()
    {
        StopMusic(0);
        _voice.Stop();
        foreach (var p in _sfx)
            p.Stop();
    }

    private void PlaySfx(RemasterSound s, float gameVolume, bool silenced)
    {
        AudioStreamPlayer? p = null;
        foreach (var x in _sfx)
            if (!x.Playing)
            {
                p = x;
                break;
            }
        if (p == null)
        {
            if (_sfx.Count >= 16)
                p = _sfx[0];
            else
            {
                p = new AudioStreamPlayer { Name = $"Sfx{_sfx.Count}" };
                AddChild(p);
                _sfx.Add(p);
            }
        }
        if (!Prepare(p, s))
            return;
        _sfxOf[p] = s.Id;
        _sfxGame[p] = gameVolume;
        _tweens.Remove(p, out var old);
        old?.Kill();
        p.VolumeDb = SfxDb(p);
        p.Play();
        Log.Add(new Played(SoundKind.Sfx, s.Id, s.Stream, _clock, silenced));
    }

    // which effect each pool player plays, the game's volume for it (0..1), its fade
    private readonly Dictionary<AudioStreamPlayer, int> _sfxOf = new();
    private readonly Dictionary<AudioStreamPlayer, float> _sfxGame = new();
    private readonly Dictionary<AudioStreamPlayer, Tween> _tweens = new();

    private float SfxDb(AudioStreamPlayer p) =>
        Mathf.LinearToDb(Mathf.Max(_sfxGame.GetValueOrDefault(p) * _sfxVol * _master, 1e-5f))
        + (AudioReplacements.Find(SoundKind.Sfx, _sfxOf.GetValueOrDefault(p, -1)) is { } s ? s.VolumeDb : 0f);

    private void SfxVolume(AudioStreamPlayer p, float to, double seconds)
    {
        _tweens.Remove(p, out var old);
        old?.Kill();
        if (seconds <= 0)
        {
            _sfxGame[p] = to;
            p.VolumeDb = SfxDb(p);
            return;
        }
        var t = CreateTween();
        t.TweenMethod(Callable.From<float>(v => { _sfxGame[p] = v; p.VolumeDb = SfxDb(p); }), _sfxGame.GetValueOrDefault(p), to, seconds);
        _tweens[p] = t;
    }

    /// <summary>The game's volume (0..1) of the replaced effect <paramref name="id"/> playing, or -1 (for tests).</summary>
    public float SfxGameVolume(int id)
    {
        foreach (var (p, sid) in _sfxOf)
            if (sid == id && p.Playing)
                return _sfxGame.GetValueOrDefault(p);
        return -1;
    }

    /// <summary>Whether a replaced effect <paramref name="id"/> is playing (for tests).</summary>
    public bool SfxPlaying(int id)
    {
        foreach (var (p, sid) in _sfxOf)
            if (sid == id && p.Playing)
                return true;
        return false;
    }

    private readonly Dictionary<string, AudioStream?> _streams = new();

    /// <summary>Gives <paramref name="p"/> the stream of <paramref name="s"/> with its loop set; false when the file is missing.</summary>
    private bool Prepare(AudioStreamPlayer p, RemasterSound s)
    {
        var key = $"{s.Stream}|{s.Loop}|{s.LoopStart}|{s.LoopEnd}";
        if (!_streams.TryGetValue(key, out var stream))
        {
            stream = AudioReplacements.LoadStream(s.Stream);
            if (stream != null)
            {
                stream = (AudioStream)stream.Duplicate();
                SetLoop(stream, s);
            }
            else
                GD.PrintErr($"ffx: audio: {s.Kind} {s.Id}: {s.Stream} not found; the game's own instead");
            _streams[key] = stream;
        }
        if (stream == null)
            return false;
        p.Stream = stream;
        return true;
    }

    /// <summary>Sets a stream's loop from <paramref name="s"/> (an end point other than the file's end is kept by <see cref="_Process"/>).</summary>
    public static void SetLoop(AudioStream stream, RemasterSound s)
    {
        switch (stream)
        {
            case AudioStreamOggVorbis ogg:
                ogg.Loop = s.Loop;
                ogg.LoopOffset = s.LoopStart;
                break;
            case AudioStreamMP3 mp3:
                mp3.Loop = s.Loop;
                mp3.LoopOffset = s.LoopStart;
                break;
            case AudioStreamWav wav:
                wav.LoopMode = s.Loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled;
                int rate = wav.MixRate, frames = WavFrames(wav);
                wav.LoopBegin = (int)(s.LoopStart * rate);
                wav.LoopEnd = s.LoopEnd > 0 ? System.Math.Min((int)(s.LoopEnd * rate), frames) : frames;
                break;
        }
    }

    private static int WavFrames(AudioStreamWav w)
    {
        int bytes = w.Data.Length, ch = w.Stereo ? 2 : 1;
        int per = w.Format switch { AudioStreamWav.FormatEnum.Format8Bits => 1, AudioStreamWav.FormatEnum.Format16Bits => 2, _ => 2 };
        return bytes / (per * ch);
    }
}
