using System.Runtime.CompilerServices;
using Godot;

namespace GdFfx;

/// <summary>
/// Label updates that do nothing when nothing changed (G5). The views set their texts each
/// frame; a colour override set again, even the same, makes Godot re-theme the label and lay
/// out its container again (in battle 2.8 ms a frame), and each text set crosses into Godot.
/// </summary>
public static class Ui
{
    private sealed class Last
    {
        public string? Text;
        public Color? Colour;
    }

    private static readonly ConditionalWeakTable<Label, Last> Seen = new();

    /// <summary>Sets <paramref name="l"/>'s text when it differs from the last one set here.</summary>
    public static void SetText(Label l, string text)
    {
        var last = Seen.GetOrCreateValue(l);
        if (last.Text == text)
            return;
        last.Text = text;
        l.Text = text;
    }

    /// <summary>Sets <paramref name="l"/>'s <c>font_color</c> override when it differs from the last one set here.</summary>
    public static void SetColour(Label l, Color colour)
    {
        var last = Seen.GetOrCreateValue(l);
        if (last.Colour == colour)
            return;
        last.Colour = colour;
        l.AddThemeColorOverride("font_color", colour);
    }
}
