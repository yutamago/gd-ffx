#if TOOLS
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The step panel's map in 2D (docs/remaster-materials.md, "The view"): the current step's map,
/// kept to its shape, zoomed with the wheel and panned by dragging (a double click: back to the
/// whole map), tiled 2x2 to show seams, and a before/after split (the source colour texture left
/// of it, the map right of it; the split follows the mouse while Shift is held, else its slider).
/// R2b phase 2: a click picks a colour (<see cref="Picking"/>: the point's UV goes to
/// <see cref="Picked"/>), and in the normal step an arrow shows the painted light's direction
/// (<see cref="Arrow"/>; drag its head to turn it).
/// </summary>
[Tool]
public partial class MapView2D : ColorRect
{
    private const string Code = @"shader_type canvas_item;
uniform sampler2D map : filter_linear_mipmap, repeat_enable;
uniform sampler2D before : filter_linear_mipmap, repeat_enable;
uniform vec2 fit = vec2(1.0);
uniform float zoom = 1.0;
uniform vec2 pan = vec2(0.0);
uniform float tiles = 1.0;
uniform float split = -1.0;
void fragment() {
	vec2 uv = ((UV - 0.5) / fit) / zoom + 0.5 + pan;
	vec2 t = uv * tiles;
	bool inside = all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)));
	vec4 c = UV.x < split ? texture(before, t) : texture(map, t);
	float chk = mod(floor(UV.x * 40.0) + floor(UV.y * 40.0 * fit.y / max(fit.x, 0.001)), 2.0);
	vec3 bg = mix(vec3(0.16), vec3(0.2), chk);
	COLOR.rgb = inside ? mix(bg, c.rgb, c.a) : bg * 0.6;
	if (split >= 0.0 && abs(UV.x - split) < 0.002) COLOR.rgb = vec3(1.0, 0.85, 0.2);
	COLOR.a = 1.0;
}";

    private readonly ShaderMaterial _mat = new() { Shader = new Shader { Code = Code } };
    private Texture2D? _map, _before;
    private float _zoom = 1;
    private Vector2 _pan;
    private bool _dragging;
    private bool _arrowDrag;
    private readonly ArrowOverlay _overlay;

    /// <summary>
    /// Left clicks and drags pick (the map's UV, 0-1, to <see cref="PickDrag"/>) instead of
    /// panning; the cursor a cross, the pick's disc and its colour drawn under it.
    /// </summary>
    public bool Picking
    {
        get => _picking;
        set
        {
            _picking = value;
            _pickDrag &= value;
            MouseDefaultCursorShape = value ? CursorShape.Cross : CursorShape.Arrow;
            if (!value)
                _overlay.PickAt = null;
            _overlay.QueueRedraw();
        }
    }

    private bool _picking, _pickDrag;

    /// <summary>A pick: the UV under the mouse and the phase (0 pressed, 1 dragged, 2 released).</summary>
    public event System.Action<Vector2, int>? PickDrag;

    /// <summary>The pick's radius in map pixels (the disc drawn under the cursor).</summary>
    public int PickRadius { get; set; } = 1;

    /// <summary>The colour a pick at a UV would take (the swatch beside the cursor), or null.</summary>
    public System.Func<Vector2, Color?>? Sampler { get; set; }

    /// <summary>The painted light's angle (degrees anticlockwise from the right; null: no arrow).</summary>
    public float? Arrow
    {
        get => _overlay.Angle;
        set
        {
            _overlay.Angle = value;
            _overlay.QueueRedraw();
        }
    }

    /// <summary>The arrow dragged (live) and let go (the angle).</summary>
    public event System.Action<float>? ArrowMoved, ArrowReleased;

    /// <summary>Draws the light's arrow over the map (its own canvas item: the map's shader does not touch it).</summary>
    private sealed partial class ArrowOverlay : Control
    {
        public float? Angle;
        /// <summary>The pick cursor: where, the disc's radius on screen, the colour there.</summary>
        public Vector2? PickAt;
        public float PickR;
        public Color? Swatch;

        public Vector2 Tip => Size / 2 + Dir * Radius;
        public Vector2 Dir => Angle is { } a ? new Vector2(Mathf.Cos(Mathf.DegToRad(a)), -Mathf.Sin(Mathf.DegToRad(a))) : Vector2.Zero;
        public float Radius => Mathf.Min(Size.X, Size.Y) * 0.36f;

        public override void _Draw()
        {
            if (PickAt is { } at)
            {
                // the eyedropper's disc and the colour it takes
                DrawArc(at, Mathf.Max(2, PickR), 0, Mathf.Tau, 32, Colors.Black, 3, true);
                DrawArc(at, Mathf.Max(2, PickR), 0, Mathf.Tau, 32, Colors.White, 1.5f, true);
                if (Swatch is { } sw)
                {
                    var box = new Rect2(at + new Vector2(14, -34), new Vector2(26, 26));
                    DrawRect(box.Grow(2), Colors.Black);
                    DrawRect(box, new Color(sw, 1));
                }
            }
            if (Angle == null)
                return;
            var c = Size / 2;
            var col = new Color(1, 0.85f, 0.2f);
            DrawLine(c - Dir * Radius * 0.3f, Tip, col, 3, true);
            var side = new Vector2(-Dir.Y, Dir.X);
            DrawColoredPolygon([Tip + Dir * 4, Tip - Dir * 14 + side * 8, Tip - Dir * 14 - side * 8], col);
            DrawCircle(Tip, 9, new Color(col, 0.35f));
            DrawString(ThemeDB.FallbackFont, Tip + Dir * 14 - new Vector2(16, -6), "light", HorizontalAlignment.Left, -1, 13, col);
        }
    }

    public MapView2D()
    {
        Material = _mat;
        CustomMinimumSize = new Vector2(0, 260);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        // a click gives the tab the keyboard (its shortcuts)
        FocusMode = FocusModeEnum.Click;
        Resized += Fit;
        Split = -1;
        _overlay = new ArrowOverlay { MouseFilter = MouseFilterEnum.Ignore };
        _overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_overlay);
    }

    /// <summary>The map's UV (0-1) under a point of the view (null: outside the map).</summary>
    public Vector2? UvAt(Vector2 pos)
    {
        var fit = _mat.GetShaderParameter("fit").AsVector2();
        var v = pos / Size;
        var uv = (v - new Vector2(0.5f, 0.5f)) / fit / _zoom + new Vector2(0.5f, 0.5f) + _pan;
        if (uv.X < 0 || uv.Y < 0 || uv.X > 1 || uv.Y > 1)
            return null;
        var t = uv * (Tiled ? 2f : 1f);
        return new Vector2(t.X - Mathf.Floor(t.X), t.Y - Mathf.Floor(t.Y));
    }

    private Vector2 _lastPick;

    /// <summary>One map pixel's size on screen.</summary>
    private float TexelOnScreen()
    {
        var tex = _map ?? _before;
        if (tex == null || tex.GetWidth() <= 0)
            return 1;
        var fit = _mat.GetShaderParameter("fit").AsVector2();
        return Size.X * fit.X * _zoom / (tex.GetWidth() * (Tiled ? 2f : 1f));
    }

    /// <summary>The view's point over a UV (0-1; the checks put the mouse there), the tiling's first tile.</summary>
    public Vector2 PointOf(Vector2 uv)
    {
        var fit = _mat.GetShaderParameter("fit").AsVector2();
        var u = uv / (Tiled ? 2f : 1f);
        return ((u - _pan - new Vector2(0.5f, 0.5f)) * _zoom * fit + new Vector2(0.5f, 0.5f)) * Size;
    }

    /// <summary>Tiled 2x2 (seams show).</summary>
    public bool Tiled
    {
        get => _mat.GetShaderParameter("tiles").AsSingle() > 1.5f;
        set => _mat.SetShaderParameter("tiles", value ? 2f : 1f);
    }

    /// <summary>The before/after split in the view's width (0..1; below 0: off).</summary>
    public float Split
    {
        get => _mat.GetShaderParameter("split").AsSingle();
        set => _mat.SetShaderParameter("split", value);
    }

    public void SetMaps(Texture2D? map, Texture2D? before)
    {
        _map = map;
        _before = before;
        _mat.SetShaderParameter("map", map ?? before ?? new PlaceholderTexture2D());
        _mat.SetShaderParameter("before", before ?? map ?? new PlaceholderTexture2D());
        Fit();
    }

    /// <summary>Back to the whole map.</summary>
    public void Reset()
    {
        (_zoom, _pan) = (1, Vector2.Zero);
        Apply();
    }

    private void Fit()
    {
        // the map's shape kept inside the view
        var tex = _map ?? _before;
        var size = Size;
        var fit = Vector2.One;
        if (tex != null && size.X > 1 && size.Y > 1 && tex.GetWidth() > 0 && tex.GetHeight() > 0)
        {
            float ta = (float)tex.GetWidth() / tex.GetHeight(), va = size.X / size.Y;
            fit = ta > va ? new Vector2(1, va / ta) : new Vector2(ta / va, 1);
        }
        _mat.SetShaderParameter("fit", fit);
        Apply();
    }

    private void Apply()
    {
        _mat.SetShaderParameter("zoom", _zoom);
        _mat.SetShaderParameter("pan", _pan);
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _zoom = Mathf.Min(_zoom * 1.15f, 32f);
                Apply();
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _zoom = Mathf.Max(_zoom / 1.15f, 0.25f);
                Apply();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } pick when Picking || _pickDrag:
                if (pick.Pressed && UvAt(pick.Position) is { } puv)
                {
                    _pickDrag = true;
                    PickDrag?.Invoke(puv, 0);
                }
                else if (!pick.Pressed && _pickDrag)
                {
                    _pickDrag = false;
                    PickDrag?.Invoke(UvAt(pick.Position) ?? _lastPick, 2);
                }
                AcceptEvent();
                break;
            case InputEventMouseMotion pm when Picking:
                if (UvAt(pm.Position) is { } muv)
                {
                    _lastPick = muv;
                    if (_pickDrag)
                        PickDrag?.Invoke(muv, 1);
                    _overlay.PickAt = pm.Position;
                    _overlay.PickR = (PickRadius + 0.5f) * TexelOnScreen();
                    _overlay.Swatch = Sampler?.Invoke(muv);
                }
                else
                    _overlay.PickAt = null;
                _overlay.QueueRedraw();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } ab when Arrow != null && (_arrowDrag || (ab.Pressed && ab.Position.DistanceTo(_overlay.Tip) < 16)):
                _arrowDrag = ab.Pressed;
                if (!ab.Pressed)
                    ArrowReleased?.Invoke(Arrow.Value);
                AcceptEvent();
                break;
            case InputEventMouseMotion am when _arrowDrag:
                var d = am.Position - Size / 2;
                float angle = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(-d.Y, d.X)), 360f);
                Arrow = Mathf.Round(angle);
                ArrowMoved?.Invoke(Arrow.Value);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Middle } b:
                if (b.DoubleClick)
                    Reset();
                _dragging = b.Pressed;
                AcceptEvent();
                break;
            case InputEventMouseMotion m when _dragging:
                var fit = _mat.GetShaderParameter("fit").AsVector2();
                _pan -= m.Relative / Size / fit / _zoom;
                Apply();
                AcceptEvent();
                break;
            case InputEventMouseMotion m when m.ShiftPressed && Split >= 0:
                Split = Mathf.Clamp(m.Position.X / Size.X, 0, 1);
                break;
        }
    }
}
#endif
