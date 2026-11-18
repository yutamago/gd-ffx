using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// One piece of the native screens' art replaced (R7b; docs/remaster-editor.md, "UI art"): a
/// Flash movie's character by the id the screens ask for (<see cref="UiArt"/>: a <c>SymbolClass</c>
/// name such as <c>saveload_jp_fla.arrow_21</c>, or <c>#&lt;id&gt;</c>; with <c>@&lt;label&gt;</c> or
/// <c>@&lt;frame&gt;</c> for one frame of a sprite), drawn with the user's image where the classic art
/// would be.
/// </summary>
[Tool, GlobalClass]
public partial class RemasterUiArt : Resource
{
    /// <summary>The movie: its file name (<c>saveload_kr.swf</c>), or without its language (<c>saveload</c>: every language's).</summary>
    [Export] public string Movie { get; set; } = "";

    /// <summary>The art's id in the movie (<see cref="UiArt.ArtId"/>).</summary>
    [Export] public string Art { get; set; } = "";

    /// <summary>The image (PNG, WebP, JPEG; <c>res://</c>, a pack's, or a file), relative to the set's folder or absolute.</summary>
    [Export(PropertyHint.File, "*.png,*.webp,*.jpg,*.jpeg")] public string Image { get; set; } = "";

    /// <summary>Back to the classic art (a pack undoing another's).</summary>
    [Export] public bool Classic { get; set; }

    [Export(PropertyHint.MultilineText)] public string Note { get; set; } = "";
}

