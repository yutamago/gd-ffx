using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// One replaced movie (R7; docs/remaster-editor.md, "Movies"): the game's movie <see cref="Movie"/>
/// (its file name without the extension, as the game opens it: <c>IWK_us</c>) played from
/// <see cref="Video"/> (an Ogg Theora <c>.ogv</c> of your own). Its sound: the game's
/// (<see cref="MovieSound.Game"/>), the video's own track (<see cref="MovieSound.Video"/>, the game's
/// silenced), a separate file (<see cref="MovieSound.File"/>, <see cref="Sound"/>) or none.
/// <see cref="Subtitles"/>: an optional SubRip <c>.srt</c> drawn over it.
/// </summary>
[Tool, GlobalClass]
public partial class RemasterMovie : Resource
{
    [Export] public string Movie { get; set; } = "";

    [Export(PropertyHint.File, "*.ogv")] public string Video { get; set; } = "";

    [Export] public MovieSound Sound { get; set; } = MovieSound.Video;

    [Export(PropertyHint.File, "*.ogg,*.wav,*.mp3")] public string SoundFile { get; set; } = "";

    [Export(PropertyHint.File, "*.srt")] public string Subtitles { get; set; } = "";

    [Export(PropertyHint.Range, "-40,12,0.1,suffix:dB")] public float VolumeDb { get; set; }

    /// <summary>The game's own movie (undoes an earlier layer's replacement).</summary>
    [Export] public bool Classic { get; set; }

    [Export(PropertyHint.MultilineText)] public string Note { get; set; } = "";
}
