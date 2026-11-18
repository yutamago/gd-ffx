using Godot;

namespace GdFfx.Remaster;

/// <summary>A font of the native screens replaced: by its role (<c>ui</c>: every screen's text; <c>message</c>: the message boxes').</summary>
[Tool, GlobalClass]
public partial class RemasterUiFont : Resource
{
    [Export] public string Role { get; set; } = "ui";

    /// <summary>The font file (TTF, OTF, WOFF, WOFF2), relative to the set's folder or absolute.</summary>
    [Export(PropertyHint.File, "*.ttf,*.otf,*.woff,*.woff2")] public string File { get; set; } = "";

    [Export] public bool Classic { get; set; }

    [Export(PropertyHint.MultilineText)] public string Note { get; set; } = "";
}

