using Godot;
using Godot.Collections;

namespace GdFfx.Remaster;

/// <summary>What a sound id is: music, a sound effect or a voice line (the game's FMOD command families).</summary>
public enum SoundKind
{
    Music,
    Sfx,
    Voice,
}

/// <summary>
/// One replaced sound (R7; docs/remaster-editor.md, "Audio"): the game's music track, sound effect
/// or voice line <see cref="Id"/> (the id the game queues it by: <c>iMusicStart(id)</c>,
/// <c>iSfxStart(id, ..)</c>, <c>iVoiceRead(id)</c>) played from <see cref="Stream"/> (an Ogg Vorbis,
/// WAV or MP3 file of your own: <c>res://</c>, or an absolute path) while the game's own is silent in
/// its mix. Music loops from <see cref="LoopStart"/> to <see cref="LoopEnd"/> (0: the end).
/// <see cref="Classic"/> pins the id to the game's sound (a later layer undoes an earlier one).
/// </summary>
[Tool, GlobalClass]
public partial class RemasterSound : Resource
{
    [Export] public SoundKind Kind { get; set; }

    [Export] public int Id { get; set; }

    [Export(PropertyHint.File, "*.ogg,*.wav,*.mp3")] public string Stream { get; set; } = "";

    /// <summary>Loops (music by default; a sound effect or voice line plays once).</summary>
    [Export] public bool Loop { get; set; } = true;

    /// <summary>Where a loop starts again, seconds.</summary>
    [Export(PropertyHint.Range, "0,3600,0.001,suffix:s")] public double LoopStart { get; set; }

    /// <summary>Where a loop ends, seconds (0: the end of the file).</summary>
    [Export(PropertyHint.Range, "0,3600,0.001,suffix:s")] public double LoopEnd { get; set; }

    /// <summary>Added to the game's volume for it, dB.</summary>
    [Export(PropertyHint.Range, "-40,12,0.1,suffix:dB")] public float VolumeDb { get; set; }

    /// <summary>The game's own sound (undoes an earlier layer's replacement).</summary>
    [Export] public bool Classic { get; set; }

    [Export(PropertyHint.MultilineText)] public string Note { get; set; } = "";

    /// <summary>The key a layer is merged by.</summary>
    public (SoundKind, int) Key => (Kind, Id);
}

/// <summary>A replaced movie's sound.</summary>
public enum MovieSound
{
    /// <summary>The game's movie sound keeps playing.</summary>
    Game,
    /// <summary>The video's own sound track (the game's silenced).</summary>
    Video,
    /// <summary>A separate file (the game's silenced).</summary>
    File,
    /// <summary>None (the game's silenced).</summary>
    None,
}
