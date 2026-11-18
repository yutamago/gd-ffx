#if TOOLS
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The step panel's map in 2D (docs/remaster-materials.md, "The view"): the current step's map,
/// kept to its shape, zoomed with the wheel and panned by dragging (a double click: back to the
/// whole map), tiled 2x2 to show seams, and a before/after split (the source colour texture left
/// of it, the map right of it; the split follows the mouse while Shift is held, else its slider).
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

    public MapView2D()
    {
        Material = _mat;
        CustomMinimumSize = new Vector2(0, 260);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        Resized += Fit;
        Split = -1;
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
