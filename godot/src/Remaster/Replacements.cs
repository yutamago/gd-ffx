using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R7's compare switch: remade effects, replaced music, sound effects, voice and movies on, or
/// everything classic (docs/remaster-editor.md, "Effects, audio and movies"). <see cref="Key"/>
/// (F8) flips it for the run; <c>--no-replacements</c> starts with it off.
/// </summary>
public static class Replacements
{
    /// <summary>gd-ffx's key for the compare (never the game's).</summary>
    public const Key Key = Godot.Key.F8;

    private static bool? _on;

    /// <summary>Whether replacements are drawn and played.</summary>
    public static bool On
    {
        get => _on ??= !GameSettings.Flag("no-replacements");
        set
        {
            if (On == value)
                return;
            _on = value;
            Changed++;
            GD.Print($"ffx: replacements {(value ? "on" : "off (everything classic)")}");
        }
    }

    /// <summary>Counts the flips (views compare it to see one).</summary>
    public static int Changed { get; private set; }

    public static void Toggle() => On = !On;
}
