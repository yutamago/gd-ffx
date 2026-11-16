using Godot;

namespace GdFfx;

/// <summary>
/// G3: the game's screen distortion (heat shimmer, spell ripples; docs/formats/postfx.md in
/// ffx-rust, "Distortion"): the game copies the frame into a distortion buffer and draws it back
/// through a grid whose vertices it moves (<c>Distortion_updateDistortionVertex</c>). Here the
/// grid is a 2D mesh over the screen whose fragments read the screen texture at the grid's UVs:
/// vertex k at <c>positions[k]</c> (normalised screen, 0..1, y down) shows the frame at
/// <c>uvs[k]</c>. Fed per frame by <see cref="Show"/>; hidden while the game has the pass off.
/// (The copy's blend over the previous copy, a short smear, is not reproduced yet.)
/// </summary>
public partial class DistortionView : Control
{
    private MeshInstance2D _grid = null!;

    /// <summary>Whether a grid was shown in the last frame, and its vertex count (for tests).</summary>
    public (bool On, int Vertices) Shown { get; private set; }

    private static readonly Shader GridShader = new()
    {
        Code = @"shader_type canvas_item;
uniform sampler2D screen : hint_screen_texture, filter_linear;
void fragment() {
	COLOR = vec4(texture(screen, UV).rgb, 1.0);
}
",
    };

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _grid = new MeshInstance2D { Material = new ShaderMaterial { Shader = GridShader }, Visible = false };
        AddChild(_grid);
    }

    /// <summary>
    /// The grid this frame: <paramref name="positions"/> and <paramref name="uvs"/> two floats a
    /// vertex (normalised screen, y down), <paramref name="indices"/> a triangle list; off hides it.
    /// </summary>
    public void Show(bool on, float[] positions, float[] uvs, int[] indices)
    {
        int n = positions.Length / 2;
        if (!on || n < 3 || uvs.Length < 2 * n || indices.Length < 3)
        {
            _grid.Visible = false;
            Shown = (false, 0);
            return;
        }
        var size = GetViewportRect().Size;
        var pos = new Vector2[n];
        var uv = new Vector2[n];
        for (int k = 0; k < n; k++)
        {
            pos[k] = new Vector2(positions[2 * k] * size.X, positions[2 * k + 1] * size.Y);
            uv[k] = new Vector2(uvs[2 * k], uvs[2 * k + 1]);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = pos;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _grid.Mesh = mesh;
        _grid.Visible = true;
        Shown = (true, n);
    }
}
