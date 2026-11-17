namespace GdFfx.Remaster;

/// <summary>A map's remaster status (docs/remake.md, "Overrides"), set by hand in its <see cref="MapOverrides"/>.</summary>
public enum MapStatus
{
    /// <summary>Not started: the classic map as imported.</summary>
    Original,
    InProgress,
    Remastered,
    /// <summary>Reviewed and accepted.</summary>
    Approved,
}

/// <summary>How a map is drawn: the classic look (the game's shaders) or Godot's physically based pipeline (R2, R3).</summary>
public enum RenderPath
{
    Classic,
    Remastered,
}

/// <summary>A layer's say on removing a part: none (an earlier layer's stands), remove it, or keep it (undoes an earlier removal).</summary>
public enum RemoveOp
{
    Unset,
    Remove,
    Keep,
}

/// <summary>A layer's say on showing a part: none, hidden, or shown (undoes an earlier layer's hiding; the game still shows and hides it as it does).</summary>
public enum VisibilityOp
{
    Unset,
    Hidden,
    Shown,
}

/// <summary>
/// A layer's say on a part's placement: none; an offset on top of the game's placement (in world
/// axes about the part's origin: the world basis turned and scaled by the transform's basis,
/// its origin moved by the transform's origin, render units); an absolute world transform
/// (render space, the map's scale of 10 included for a classic part); or the game's own
/// placement (undoes an earlier layer's).
/// </summary>
public enum TransformOp
{
    Unset,
    Offset,
    Absolute,
    Game,
}

/// <summary>A layer's say on replacing a part: none, by a scene, or the original (undoes an earlier replacement).</summary>
public enum ReplaceOp
{
    Unset,
    Scene,
    Original,
}
