using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// G3: the game's own effects, drawn from the geometry the game builds for them each frame
/// (ffx-rust's effect-draw capture, <c>ffx_host::vfx</c>, FXDR version 3; docs/state-api.md
/// "Effect draws" there): every draw of the VFX lists with its pass, positions (render space),
/// colours, UVs, indices, texture and VSP flags, for the frame of the shown snapshot's update.
/// Drawn: the main pass (<c>DrawMostVFX</c>), the DTT pass (its <c>FrameBuffer</c> draws sample
/// the picture: the screen texture here) and the after-post pass, all in the 3D scene; not the
/// UI 2D pass (it uses the scene's second camera, which the capture does not carry) nor the
/// list no pass draws. Of the draw events, the screen fade (kind 2) is drawn: a full-screen
/// colour over the picture (<see cref="Fade"/>, in the overlay); the others act on buffers or
/// characters gd-ffx draws its own way (copy buffer, PS2 lighting, specular, character draws).
/// Each frame is
/// one <see cref="ArrayMesh"/>, a surface per (texture, blend); unshaded, two-sided, depth
/// tested, no depth write, the vertex colour doubled (0.5 is the PS2's 0x80, which the game's
/// VFX shaders double). Blend from the flags: bit 0x10 subtracts (0x18), else bit 0x2 adds
/// (0xa, 0x2; as (src alpha, one): (one, one), the spec's other source factor, burns the
/// save sphere's sparkles far brighter than the original), else alpha (0x8, 0x9). Textures come from the player's archive by the name the
/// game holds.
/// </summary>
public partial class EffectDrawView : MeshInstance3D
{
    /// <summary>One decoded draw.</summary>
    public sealed record Draw(ulong Key, uint Flags, string Texture, Vector3[] Positions, Color[] Colours, Vector2[] Uvs, int[] Indices, int Pass = 0);

    /// <summary>A draw event (FXDR version 3): kind, place, the two words and the actor slot (-1 none).</summary>
    public sealed record Event(uint Kind, uint At, uint Word0, uint Word1, int Actor);

    /// <summary>The passes: main, DTT, after post-processing, UI 2D, undrawn (ffx-rust's <c>vfx::Pass</c>).</summary>
    public const int PassMain = 0, PassDtt = 1, PassAfterPost = 2, PassUi2d = 3, PassUndrawn = 4;

    /// <summary>Whether this frontend draws a pass.</summary>
    public static bool Drawn(int pass) => pass is PassMain or PassDtt or PassAfterPost;

    /// <summary>The screen fade of a frame (event kind 2): RGBA (1.0 the PS2's 0x80) and blend (2 sub, 1 add, 0 alpha).</summary>
    public readonly record struct ScreenFade(Color Colour, int Blend);

    /// <summary>The screen fade of the last event of kind 2 in a frame (null none).</summary>
    public static ScreenFade? Fade(IEnumerable<Event> events)
    {
        ScreenFade? f = null;
        foreach (var e in events)
            if (e.Kind == 2)
                f = new ScreenFade(new Color((e.Word0 & 0xff) / 128f, (e.Word0 >> 8 & 0xff) / 128f,
                    (e.Word0 >> 16 & 0xff) / 128f, (e.Word0 >> 24) / 128f), Blend(e.Word1));
        return f;
    }

    /// <summary>The events of the last decoded frame.</summary>
    public static List<Event> LastEvents { get; private set; } = new();

    /// <summary>Since start: frames with a screen fade, and draws seen per pass (for tests).</summary>
    public static int FadeFrames { get; private set; }
    public static int[] PassDraws { get; } = new int[5];

    /// <summary>Draws in the last frame shown, and how many had a texture (for tests).</summary>
    public (int Draws, int Textured, uint Update) Shown { get; private set; }

    /// <summary>The texture names of the last frame shown (for tests).</summary>
    public HashSet<string> Textures { get; } = new();

    private uint _update = uint.MaxValue;
    private readonly Dictionary<string, Texture2D?> _textures = new();
    private readonly Dictionary<(string, int), ShaderMaterial> _materials = new();

    private static readonly Shader[] Shaders =
    [
        Make("blend_mix"), Make("blend_add"), Make("blend_sub"),
    ];

    private static Shader Make(string blend) => new()
    {
        Code = "shader_type spatial;\nrender_mode unshaded, cull_disabled, depth_draw_never, " + blend + ";\n" + @"
uniform sampler2D tex : filter_linear_mipmap, repeat_enable;
uniform sampler2D screen : hint_screen_texture, filter_linear;
uniform bool textured = true;
uniform bool frame_buffer = false;
void fragment() {
	vec4 t = frame_buffer ? vec4(texture(screen, UV).rgb, 1.0) : textured ? texture(tex, UV) : vec4(1.0);
	vec4 c = t * COLOR * 2.0;
	ALBEDO = clamp(c.rgb, 0.0, 1.0);
	ALPHA = clamp(c.a, 0.0, 1.0);
}
",
    };

    /// <summary>Blend from the VSP flags: 2 subtract, 1 add, 0 alpha.</summary>
    public static int Blend(uint flags) => (flags & 0x10) != 0 ? 2 : (flags & 0x2) != 0 ? 1 : 0;

    /// <summary>The screen distortion of a frame (FXDR version 2; ffx-rust's <c>vfx::Distortion</c>).</summary>
    public sealed record Distortion(bool Skipped, bool Reset, float Blur, float[] Positions, float[] Uvs, float[] Colours, int[] Indices)
    {
        /// <summary>The copy's alpha, <c>min(blur, 0.8)</c> (a plain copy on <see cref="Reset"/>).</summary>
        public float CopyAlpha => Reset ? 1f : Mathf.Min(Blur, 0.8f);
    }

    /// <summary>The distortion of the last decoded frame (null: the pass is off).</summary>
    public static Distortion? LastDistortion { get; private set; }

    /// <summary>Decodes a frame (<c>FXDR</c>, version 1 or 2); null when it is not one.</summary>
    public static (uint Update, List<Draw> Draws)? Decode(byte[] b)
    {
        if (b.Length < 24 || b[0] != (byte)'F' || b[1] != (byte)'X' || b[2] != (byte)'D' || b[3] != (byte)'R')
            return null;
        int at = 4;
        ushort U16() { var v = System.BitConverter.ToUInt16(b, at); at += 2; return v; }
        uint U32() { var v = System.BitConverter.ToUInt32(b, at); at += 4; return v; }
        float F() { var v = System.BitConverter.ToSingle(b, at); at += 4; return v; }
        var version = U16();
        if (version is not (1 or 2 or 3))
            return null;
        U16();
        var update = U32();
        U32(); // unresolved
        var events = U32();
        var count = U32();
        var evs = new List<Event>();
        for (int e = 0; e < events; e++)
        {
            uint kind = U32(), place = U32(), w0 = U32(), w1 = U32();
            int actor = -1;
            if (version >= 3)
            {
                actor = (short)U16();
                U16();
            }
            evs.Add(new Event(kind, place, w0, w1, actor));
        }
        LastEvents = evs;
        var draws = new List<Draw>((int)count);
        for (int d = 0; d < count; d++)
        {
            U32(); // place
            ulong lo = U32(), hi = U32();
            U32(); // geometry
            var flags = U32();
            U16(); at += 1; // segment, pooled
            int pass = version >= 3 ? b[at] : PassMain;
            at += 1;
            at += 48; // world (positions are already placed)
            int tl = U16();
            var texture = System.Text.Encoding.UTF8.GetString(b, at, tl);
            at += tl;
            int v = (int)U32(), n = (int)U32();
            var pos = new Vector3[v];
            for (int i = 0; i < v; i++) pos[i] = new Vector3(F(), F(), F());
            var col = new Color[v];
            for (int i = 0; i < v; i++) col[i] = new Color(F(), F(), F(), F());
            var uv = new Vector2[v];
            for (int i = 0; i < v; i++) uv[i] = new Vector2(F(), F());
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = U16();
            draws.Add(new Draw(hi << 32 | lo, flags, texture, pos, col, uv, idx, pass));
        }
        LastDistortion = null;
        if (version >= 2 && at + 16 <= b.Length)
        {
            var bits = U32();
            var blur = F();
            int dn = (int)U32(), di = (int)U32();
            if ((bits & 1) != 0 && at + dn * 32 + di * 2 <= b.Length)
            {
                var p = new float[2 * dn]; for (int i = 0; i < p.Length; i++) p[i] = F();
                var u = new float[2 * dn]; for (int i = 0; i < u.Length; i++) u[i] = F();
                var c = new float[4 * dn]; for (int i = 0; i < c.Length; i++) c[i] = F();
                var ix = new int[di]; for (int i = 0; i < di; i++) ix[i] = U16();
                LastDistortion = new Distortion((bits & 2) != 0, (bits & 4) != 0, blur, p, u, c, ix);
            }
        }
        return (update, draws);
    }

    /// <summary>The texture name of draws that sample the frame copy (the DTT pass).</summary>
    public const string FrameBufferName = "FrameBuffer";

    private Texture2D? Tex(FfxGame game, string name)
    {
        if (_textures.TryGetValue(name, out var t))
            return t;
        t = null;
        if (name.Length > 0 && name != "NoTexture")
        {
            var png = game.RuntimeTexture(name);
            var img = new Image();
            if (png.Length > 0 && img.LoadPngFromBuffer(png) == Error.Ok)
            {
                img.GenerateMipmaps();
                t = ImageTexture.CreateFromImage(img);
            }
        }
        _textures[name] = t;
        return t;
    }

    private ShaderMaterial Material(FfxGame game, string texture, int blend)
    {
        if (_materials.TryGetValue((texture, blend), out var m))
            return m;
        m = new ShaderMaterial { Shader = Shaders[blend] };
        // the DTT pass's draws sample the frame copy (the picture so far)
        if (texture == FrameBufferName)
        {
            m.SetShaderParameter("frame_buffer", true);
            _materials[(texture, blend)] = m;
            return m;
        }
        var t = Tex(game, texture);
        m.SetShaderParameter("textured", t != null);
        if (t != null)
            m.SetShaderParameter("tex", t);
        _materials[(texture, blend)] = m;
        return m;
    }

    public override void _Process(double delta)
    {
        var host = GameHost.Instance;
        var s = host?.View.Current;
        if (host?.Game == null || s == null || s.Update == _update)
            return;
        _update = s.Update;
        var frame = Decode(host.Game.EffectDraws(s.Update));
        // the screen distortion of the same frame (DistortionView, in the overlay)
        if (GetTree().Root.FindChild("DistortionView", true, false) is DistortionView dv)
        {
            var d = LastDistortion;
            if (d == null || d.Skipped)
                dv.Show(false, [], [], [], [], 0f);
            else
                dv.Show(true, d.Positions, d.Uvs, d.Colours, d.Indices, d.CopyAlpha);
        }
        // the screen fade event, over the picture (ScreenFadeView, in the overlay)
        var fade = frame == null ? null : Fade(LastEvents);
        if (fade != null)
            FadeFrames++;
        if (GetTree().Root.FindChild("ScreenFadeView", true, false) is ScreenFadeView fv)
            fv.Show(fade);
        if (frame == null || frame.Value.Draws.Count == 0)
        {
            Mesh = null;
            Shown = (0, 0, s.Update);
            Textures.Clear();
            return;
        }
        // a surface per (texture, blend), in the order first met (the game's draw order)
        var groups = new List<((string, int) Key, List<Draw> Draws)>();
        var index = new Dictionary<(string, int), int>();
        foreach (var d in frame.Value.Draws)
        {
            if (d.Pass is >= 0 and < 5)
                PassDraws[d.Pass]++;
            if (d.Indices.Length == 0 || d.Positions.Length == 0 || !Drawn(d.Pass))
                continue;
            var k = (d.Texture, Blend(d.Flags));
            if (!index.TryGetValue(k, out var g))
            {
                index[k] = g = groups.Count;
                groups.Add((k, new List<Draw>()));
            }
            groups[g].Draws.Add(d);
        }
        var mesh = new ArrayMesh();
        int textured = 0;
        Textures.Clear();
        foreach (var ((tex, blend), list) in groups)
        {
            var pos = new List<Vector3>(); var col = new List<Color>(); var uv = new List<Vector2>(); var idx = new List<int>();
            foreach (var d in list)
            {
                int b = pos.Count;
                pos.AddRange(d.Positions); col.AddRange(d.Colours); uv.AddRange(d.Uvs);
                foreach (var i in d.Indices)
                    if (i < d.Positions.Length)
                        idx.Add(b + i);
            }
            if (idx.Count == 0)
                continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = pos.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = col.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var mat = Material(host.Game, tex, blend);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
            if (tex == FrameBufferName || _textures.TryGetValue(tex, out var t) && t != null)
            {
                textured += list.Count;
                Textures.Add(tex);
            }
        }
        Mesh = mesh;
        Shown = (frame.Value.Draws.Count, textured, s.Update);
    }
}
