using Godot;

namespace GdFfx;

/// <summary>
/// The game's main projection on a Godot camera (docs/architecture.md, "The camera's
/// projection"; ffx-rust's docs/state-api.md, API 11, "The camera's adjustment"). The game
/// draws with its camera's perspective (vertical field of view <c>fov_y</c>, its own aspect
/// <c>a</c>) times the shift, then, when squeezed, <c>PhyreScene_GetScreenAdjustedView</c>'s
/// <c>[1 0 0 0; 0 4/3 0 0; 0 0 1 0; 0 d 0 1]</c>: in clip space
/// <c>x' = x + sx w</c>, <c>y' = (y + sy w) 4/3 + d w</c> (squeezed; else <c>y + sy w</c>).
/// That is an off-centre frustum, Godot's <see cref="Camera3D.ProjectionType.Frustum"/>:
/// at the near plane n it is <c>2 n tan(fov_y / 2) / k</c> high (k = 4/3 squeezed, else
/// 1) and offset by minus the clip offsets times half its size. Godot takes the frustum's
/// width from the viewport's aspect (16:9, the window's bars: "The window"), which is the
/// game's horizontal scale exactly when <c>a = 16/9 / k</c>: the 3D maps (16/9, not
/// squeezed) and the 2D-background rooms (4/3, squeezed). Another pairing (the game's
/// <c>+0xf9c7</c> set in a 2D room: 4/3 not squeezed) keeps the height and the offsets and
/// is counted in <see cref="Mismatched"/> while a map is shown (before one, in the opening's
/// movie and the loads, the scene is 16/9 squeezed with nothing 3D drawn).
/// </summary>
public static class GameProjection
{
    /// <summary>The squeeze's y scale (the <c>f32</c> at <c>0x00b43d34</c>).</summary>
    public const float Squeeze = 4f / 3f;

    /// <summary>Frames on a map whose camera aspect and squeeze Godot's frustum could not both follow (for the checks).</summary>
    public static long Mismatched { get; private set; }

    private static (float, bool) _mismatch;

    /// <summary>The last frame's projection: the game's aspect, squeezed, the clip offsets (for the overlay and the checks).</summary>
    public static (float Aspect, bool Squeezed, Vector2 Offset) Last { get; private set; }

    /// <summary>
    /// Sets <paramref name="camera"/>'s projection to the game's for <paramref name="frame"/>;
    /// <paramref name="onMap"/>: a map is shown (a mismatch is counted and logged).
    /// </summary>
    public static void Apply(Camera3D camera, in GameStateView.CameraFrame frame, bool onMap)
    {
        float k = frame.Squeezed ? Squeeze : 1f;
        // the clip-space offsets the game adds (times w)
        var offset = new Vector2(frame.Shift.X, frame.Squeezed ? frame.Shift.Y * Squeeze + frame.OffsetY : frame.Shift.Y);
        var rect = camera.GetViewport()?.GetVisibleRect().Size ?? new Vector2(16, 9);
        float viewAspect = rect.Y > 0 ? rect.X / rect.Y : 16f / 9f;
        if (onMap && frame.Aspect > 0 && Mathf.Abs(frame.Aspect * k / viewAspect - 1f) > 0.01f)
        {
            if (Mismatched++ == 0 || (frame.Aspect, frame.Squeezed) != _mismatch)
                GD.Print($"ffx: camera aspect {frame.Aspect:F3}{(frame.Squeezed ? " squeezed" : "")} on a {viewAspect:F3} view: "
                    + "the height and offsets kept, the width is the view's");
            _mismatch = (frame.Aspect, frame.Squeezed);
        }
        Last = (frame.Aspect, frame.Squeezed, offset);
        float n = camera.Near;
        float h = 2f * n * Mathf.Tan(frame.FovY / 2f) / k;
        float w = h * viewAspect;
        camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
        camera.Fov = Mathf.RadToDeg(frame.FovY);
        if (offset == Vector2.Zero && !frame.Squeezed)
        {
            // the plain perspective (the 3D maps): Godot's own
            camera.Projection = Camera3D.ProjectionType.Perspective;
            return;
        }
        camera.Projection = Camera3D.ProjectionType.Frustum;
        camera.Size = h;
        camera.FrustumOffset = new Vector2(-offset.X * w / 2f, -offset.Y * h / 2f);
    }

    /// <summary>Gives <paramref name="to"/> <paramref name="from"/>'s projection (a second view of the same picture).</summary>
    public static void Copy(Camera3D from, Camera3D to)
    {
        to.KeepAspect = from.KeepAspect;
        to.Projection = from.Projection;
        to.Fov = from.Fov;
        to.Size = from.Size;
        to.FrustumOffset = from.FrustumOffset;
        to.Near = from.Near;
        to.Far = from.Far;
    }
}
