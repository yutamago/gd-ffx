using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R7's audio check (<c>--selftest-audio --devsave=7</c>; docs/remaster-editor.md, "Checks").
/// Original stand-ins made here (generated tones: nothing of the game's) replace, by id: on
/// Besaid beach the sound effect of its sea (<c>0xc355</c>, which the game starts at the map's
/// load and fades in), then, in a battle the check asks for (field 3, the deck's), every music id
/// (the battle's track, whichever it is). It checks: the sea effect is replaced (the stand-in
/// plays, following the game's fade-in) and the game's own silenced, while its second layer
/// (<c>0xc356</c>) and every other effect stay the game's (the fallback); the battle music is
/// replaced (the stand-in looping between its loop points), the game's music silent in its mix
/// (its level against the level once F8, <see cref="Replacements"/>, has brought it back and
/// stopped the stand-in). Or the editor's file (<c>--audio-file=FILE</c>): its music checked in the
/// battle. The stand-ins go to <c>user://selftest-audio</c>, removed at the end.
/// </summary>
public sealed class AudioTest(double timeout) : ITest
{
    /// <summary>Besaid beach's sea (a looping effect the game starts with the map) and its second layer.</summary>
    public const int Sea = 0xc355, SeaOther = 0xc356;

    private enum Phase { Setup, Boot, Sea, Battle, Music, Off, On, Done }

    private Phase _phase = Phase.Setup;
    private double _phaseTime, _total;
    private bool _acted;
    private readonly List<string> _seen = new();
    private readonly string _dir = ProjectSettings.GlobalizePath("user://selftest-audio");
    private readonly string? _file = GameSettings.Arg("audio-file");
    private readonly RemasterSoundSet _set = new();
    private double _levelReplaced = -1, _levelClassic = -1;
    private readonly List<double> _levels = new();
    private float _seaVolume;

    public string Note { get; private set; } = "";

    private static AudioReplacementView? Player =>
        (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("AudioReplacementView", true, false) as AudioReplacementView;

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)}; {string.Join("; ", _seen)}; commands: {Commands()})");
        var p = Player;
        switch (_phase)
        {
            case Phase.Setup:
                if (Setup() is { } err)
                    return Done(false, err);
                Next(Phase.Boot);
                break;
            case Phase.Boot when v.Current is { World.Map: BesaidTest.MapBeach, World.PlayerControl: true } && p != null && _phaseTime > 1.0:
                Next(_file != null ? Phase.Battle : Phase.Sea);
                break;
            case Phase.Sea:
            {
                var sea = p!.Log.FirstOrDefault(x => x.Kind == SoundKind.Sfx && x.Id == Sea);
                if (sea == default)
                {
                    if (_phaseTime > 10)
                        return Done(false, $"the sea effect {Sea:x} was not replaced");
                    break;
                }
                if (!sea.Silenced)
                    return Done(false, "the sea effect: the game's own was not silenced");
                _seaVolume = Mathf.Max(_seaVolume, p.SfxGameVolume(Sea));
                if (_phaseTime < 4)
                    break;
                if (!p.SfxPlaying(Sea))
                    return Done(false, "the sea's stand-in stopped");
                if (_seaVolume <= 0.05f)
                    return Done(false, $"the sea's stand-in did not follow the game's fade-in (volume {_seaVolume:F2})");
                var classic = p.Commands.Where(c => c.Kind == 1 && c.Action == 1 && c.Id != Sea).ToList();
                if (classic.Any(c => c.Silenced))
                    return Done(false, "another sound effect was silenced");
                if (!classic.Any(c => c.Id == SeaOther))
                    return Done(false, $"the sea's second layer {SeaOther:x} did not start");
                _seen.Add($"the sea effect {Sea:x} replaced by the stand-in (the game's silenced; the game's fade-in followed, volume {_seaVolume:F2}); "
                    + $"{classic.Select(c => c.Id).Distinct().Count()} other effect id(s) the game's own, {SeaOther:x} among them");
                Next(Phase.Battle);
                break;
            }
            case Phase.Battle when !_acted:
                _acted = true;
                if (!game.RequestBattle(3, 0, 0))
                    return Done(false, "the battle was not accepted");
                break;
            case Phase.Battle when v.Current is { World.Battle: true } && _phaseTime > 1.0:
                Next(Phase.Music);
                break;
            case Phase.Music:
            {
                var music = p!.Log.FirstOrDefault(x => x.Kind == SoundKind.Music);
                if (music == default)
                {
                    if (_phaseTime > 30)
                        return Done(false, "no music started in the battle");
                    break;
                }
                if (!p.MusicPlayer.Playing)
                    return Done(false, $"music {music.Id:x}'s stand-in is not playing");
                if (!music.Silenced)
                    return Done(false, $"music {music.Id:x}: the game's own was not silenced");
                // the battle's start (its whoosh) over first, then the mix measured
                if (_phaseTime > 6)
                    _levels.Add(game.AudioMixLevel());
                if (_phaseTime < 10)
                    break;
                var holds = game.AudioHolds();
                if (!holds["music_held"].AsBool() || holds["music"].AsDouble() > 1e-4)
                    return Done(false, $"the game's music volume is {holds["music"].AsDouble():F2} (held {holds["music_held"].AsBool()})");
                _levelReplaced = _levels.Average();
                var loop = p.MusicPlayer.Stream switch
                {
                    AudioStreamWav w => $"loop {w.LoopMode} {w.LoopBegin}..{w.LoopEnd}",
                    AudioStreamOggVorbis o => $"loop {o.Loop} from {o.LoopOffset:F1} s",
                    _ => "",
                };
                _seen.Add($"music {music.Id:x} replaced by {System.IO.Path.GetFileName(music.Stream)} ({p.MusicPlayer.GetPlaybackPosition():F1} s in, {loop}), "
                    + $"the game's music volume 0, its mix at {_levelReplaced:F5} RMS");
                Next(Phase.Off);
                break;
            }
            case Phase.Off when !_acted:
                _acted = true;
                _levels.Clear();
                Replacements.Toggle();
                break;
            case Phase.Off:
            {
                if (_phaseTime > 1)
                    _levels.Add(game.AudioMixLevel());
                if (_phaseTime < 5)
                    break;
                if (p!.MusicPlayer.Playing)
                    return Done(false, "F8: the stand-in still plays");
                var holds = game.AudioHolds();
                if (holds["music_held"].AsBool() || holds["music"].AsDouble() < 0.5)
                    return Done(false, $"F8: the game's music volume is {holds["music"].AsDouble():F2} (held {holds["music_held"].AsBool()})");
                _levelClassic = _levels.Average();
                _seen.Add($"F8: the game's music back (volume {holds["music"].AsDouble():F2}), the mix at {_levelClassic:F5} RMS");
                Next(Phase.On);
                break;
            }
            case Phase.On when !_acted:
                _acted = true;
                _levels.Clear();
                Replacements.Toggle();
                break;
            case Phase.On:
            {
                if (_phaseTime > 1)
                    _levels.Add(game.AudioMixLevel());
                if (_phaseTime < 5)
                    break;
                if (!p!.MusicPlayer.Playing)
                    return Done(false, "F8 again: the stand-in did not play again");
                var holds = game.AudioHolds();
                if (!holds["music_held"].AsBool() || holds["music"].AsDouble() > 1e-4)
                    return Done(false, $"F8 again: the game's music volume is {holds["music"].AsDouble():F2}");
                double again = _levels.Average();
                _seen.Add($"F8 again: the stand-in again, the game's music silent (the mix at {again:F5} RMS)");
                if (_levelClassic < 1e-4)
                    return Done(false, $"F8: the game's mix is silent ({_levelClassic:F6})");
                if (System.Math.Max(_levelReplaced, again) > _levelClassic * 0.5)
                    return Done(false, $"the game's music was not silent while replaced: {_levelReplaced:F5} and {again:F5} against {_levelClassic:F5}");
                Next(Phase.Done);
                break;
            }
            case Phase.Done:
                Cleanup();
                return Done(true, string.Join("; ", _seen));
        }
        return null;
    }

    private string Commands() => Player is { } p
        ? string.Join(", ", p.Commands.GroupBy(c => (c.Kind, c.Action)).Select(g => $"{g.Key.Kind}/{g.Key.Action} x{g.Count()} ({string.Join(" ", g.Select(c => c.Id.ToString("x")).Distinct().Take(6))})"))
        : "-";

    private string? Setup()
    {
        if (_file != null)
        {
            AudioReplacements.Register(_file, false, "selftest-audio");
            _seen.Add($"the editor's file {_file}: {AudioReplacements.Sounds.Count} sound(s)");
            return AudioReplacements.Sounds.Keys.Any(k => k.Item1 == SoundKind.Music) ? null : "the editor's file replaces no music";
        }
        DirAccess.MakeDirRecursiveAbsolute(_dir);
        if (Tone($"{_dir}/chord.wav", 4.0, [220, 330], 0.25f, true) is { } e1)
            return e1;
        if (Tone($"{_dir}/sea.wav", 2.0, [140, 147], 0.3f, true) is { } e2)
            return e2;
        for (int id = 0; id < 0x100; id++)
            _set.Sounds.Add(new RemasterSound { Kind = SoundKind.Music, Id = id, Stream = $"{_dir}/chord.wav", Loop = true, LoopStart = 1.0, LoopEnd = 3.0 });
        _set.Sounds.Add(new RemasterSound { Kind = SoundKind.Sfx, Id = Sea, Stream = $"{_dir}/sea.wav", Loop = true });
        var path = $"{_dir}/audio.tres";
        if (ResourceSaver.Save(_set, path) != Error.Ok)
            return $"cannot write {path}";
        AudioReplacements.Register(path, false, "selftest-audio");
        _seen.Add("stand-ins made (a chord for every music id, a hum for the sea)");
        return null;
    }

    /// <summary>Writes a generated tone (sines at <paramref name="hz"/>, faded in and out) as a 16-bit stereo WAV at 48 kHz.</summary>
    public static string? Tone(string path, double seconds, double[] hz, float amp, bool loopable)
    {
        const int rate = 48000;
        int n = (int)(seconds * rate);
        var data = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate, a = amp;
            if (!loopable)
                a *= System.Math.Min(1, System.Math.Min(t / 0.01, (seconds - t) / 0.05));
            double x = 0;
            foreach (var f in hz)
                x += System.Math.Sin(2 * System.Math.PI * f * t);
            short v = (short)System.Math.Clamp(x / hz.Length * a * 32767, -32767, 32767);
            System.BitConverter.TryWriteBytes(new System.Span<byte>(data, i * 4, 2), v);
            System.BitConverter.TryWriteBytes(new System.Span<byte>(data, i * 4 + 2, 2), v);
        }
        var wav = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, Stereo = true, MixRate = rate, Data = data };
        return wav.SaveToWav(path) == Error.Ok ? null : $"cannot write {path}";
    }

    private void Cleanup()
    {
        if (_file != null || !DirAccess.DirExistsAbsolute(_dir))
            return;
        foreach (var f in DirAccess.GetFilesAt(_dir))
            DirAccess.RemoveAbsolute($"{_dir}/{f}");
        DirAccess.RemoveAbsolute(_dir);
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx selftest: {_phase} -> {p} ({_total:0.0} s)");
        _phase = p;
        _phaseTime = 0;
        _acted = false;
    }

    private bool Done(bool pass, string note)
    {
        if (!pass)
            Cleanup();
        Note = note;
        return pass;
    }
}
