using Godot;

namespace GdFfx;

/// <summary>
/// G3: the game's screen distortion (the field's ripple, <c>yiScreff_draw</c>, turned on by script
/// native 0x01e2; docs/formats/postfx.md in ffx-rust, "Distortion"), from ffx-rust's effect capture
/// (FXDR version 2, <see cref="EffectDrawView.Distortion"/>). The game keeps a buffer across frames:
/// each frame the colour is blended into it, <c>buffer = colour × a + buffer × (1 − a)</c>
/// (<c>a = min(blur, 0.8)</c>; a plain copy on a reset), then a grid of 81 vertices that samples the
/// buffer replaces the screen: per pixel <c>buffer(uv) × vertex colour</c>.
/// Here the grid is a 2D mesh over the screen (positions and UVs in normalised screen space, y
/// down) sampling this frame's picture (the screen texture) times the vertex colour: the game's
/// copy is 80 % the new frame, so the smear is small. The smear itself (a buffer kept across
/// frames, <see cref="Smear"/>, <c>FFX_DISTORTION_SMEAR=1</c>) renders the 3D scene a second time
/// from the main camera into a viewport of its own (the clean picture, without the grid, which
/// fed back into itself when the shown frame was used) and blends it into the buffer at the copy's
/// alpha; it costs a second render while the pass is on, so it is opt-in. Hidden while the game has
/// the pass off.
/// </summary>
public partial class DistortionView : Control
{
    private MeshInstance2D _grid = null!;
    private SubViewport _buffer = null!;
    private TextureRect _feed = null!;
    private SubViewport _clean = null!;
    private Camera3D _cleanCamera = null!;

    /// <summary>
    /// Whether the buffer kept across frames (the smear) is used; off, the grid samples this
    /// frame's picture (the game's copy is 80 % the new frame).
    /// </summary>
    public static bool Smear = System.Environment.GetEnvironmentVariable("FFX_DISTORTION_SMEAR") == "1";

    /// <summary>Whether a grid was shown in the last frame, its vertex count and the copy's alpha (for tests).</summary>
    public (bool On, int Vertices, float Alpha) Shown { get; private set; }

    private static readonly Shader GridShader = new()
    {
        Code = @"shader_type canvas_item;
render_mode blend_disabled;
uniform sampler2D buffer : filter_linear;
uniform sampler2D screen : hint_screen_texture, filter_linear;
uniform bool smear = false;
void fragment() {
	vec3 c = smear ? texture(buffer, UV).rgb : texture(screen, UV).rgb;
	COLOR = vec4(c * COLOR.rgb, 1.0);
}
",
    };

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _buffer = new SubViewport
        {
            Name = "DistortionBuffer",
            RenderTargetClearMode = SubViewport.ClearMode.Never,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            TransparentBg = false,
        };
        _feed = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
        };
        _feed.SetAnchorsPreset(LayoutPreset.FullRect);
        _buffer.AddChild(_feed);
        AddChild(_buffer);
        // the clean picture (the 3D scene before any overlay): a second camera on the same world,
        // following the main one, so the buffer is not fed its own output
        _clean = new SubViewport
        {
            Name = "DistortionClean",
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            TransparentBg = false,
        };
        _cleanCamera = new Camera3D();
        _clean.AddChild(_cleanCamera);
        AddChild(_clean);
        var mat = new ShaderMaterial { Shader = GridShader };
        mat.SetShaderParameter("buffer", _buffer.GetTexture());
        mat.SetShaderParameter("smear", Smear);
        _grid = new MeshInstance2D { Material = mat, Visible = false };
        AddChild(_grid);
    }

    /// <summary>
    /// The grid this frame: <paramref name="positions"/> and <paramref name="uvs"/> two floats a
    /// vertex (normalised screen, y down), <paramref name="colours"/> four a vertex,
    /// <paramref name="indices"/> a triangle list, <paramref name="alpha"/> the copy's alpha; off
    /// hides it.
    /// </summary>
    public void Show(bool on, float[] positions, float[] uvs, float[] colours, int[] indices, float alpha)
    {
        int n = positions.Length / 2;
        if (!on || n < 3 || uvs.Length < 2 * n || indices.Length < 3)
        {
            _grid.Visible = false;
            _buffer.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            _clean.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            Shown = (false, 0, 0f);
            return;
        }
        var size = GetViewportRect().Size;
        var px = new Vector2I(Mathf.Max(1, (int)size.X), Mathf.Max(1, (int)size.Y));
        if (Smear)
        {
            if (_buffer.Size != px)
            {
                _buffer.Size = px;
                _clean.Size = px;
                alpha = 1f; // a new buffer starts from the frame
            }
            // the clean picture from the main camera's place, blended into the buffer at the copy's alpha
            if (GetViewport().GetCamera3D() is { } main)
            {
                _clean.World3D = GetViewport().FindWorld3D();
                _cleanCamera.GlobalTransform = main.GlobalTransform;
                _cleanCamera.Fov = main.Fov;
                _cleanCamera.Near = main.Near;
                _cleanCamera.Far = main.Far;
                _cleanCamera.Environment = main.Environment;
                _cleanCamera.Current = true;
            }
            _feed.Texture = _clean.GetTexture();
            _feed.Modulate = new Color(1, 1, 1, Mathf.Clamp(alpha, 0f, 1f));
            _clean.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            _buffer.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        }
        var pos = new Vector2[n];
        var uv = new Vector2[n];
        var col = new Color[n];
        for (int k = 0; k < n; k++)
        {
            pos[k] = new Vector2(positions[2 * k] * size.X, positions[2 * k + 1] * size.Y);
            uv[k] = new Vector2(uvs[2 * k], uvs[2 * k + 1]);
            col[k] = 4 * k + 3 < colours.Length
                ? new Color(colours[4 * k], colours[4 * k + 1], colours[4 * k + 2], 1f)
                : Colors.White;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = pos;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.Color] = col;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _grid.Mesh = mesh;
        _grid.Visible = true;
        Shown = (true, n, alpha);
    }
}
