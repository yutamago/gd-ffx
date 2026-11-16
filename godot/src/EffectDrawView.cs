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
/// one <see cref="ArrayMesh"/>, a surface per (texture, <see cref="Look"/>); unshaded, two-sided,
/// depth tested, no depth write. The blend is the game's own (FXDR version 4: the D3D11 blend of
/// the material's shader pass, <see cref="LookOf"/>), with what the game's pixel shader writes
/// for the draw's flags and technique (<see cref="Form"/>): <c>Transparent</c> premultiplies
/// with source factor one, <c>OptimizedTrans</c> writes straight colour for source alpha
/// (effects-draw.md, open question 2's answer). The save sphere's two sparkle quads (flags 0xa)
/// differ: one (one, one) premultiplied, one (src alpha, one). Older frames fall back to the
/// flags (0x10 subtract, 0x2 add, else alpha). Textures come from the player's archive by the
/// name the game holds.
/// </summary>
public partial class EffectDrawView : MeshInstance3D
{
    /// <summary>One decoded draw.</summary>
    public sealed record Draw(ulong Key, uint Flags, string Texture, Vector3[] Positions, Color[] Colours, Vector2[] Uvs, int[] Indices, int Pass = 0,
        int Technique = 0, PassBlend[]? Blends = null);

    /// <summary>
    /// One shader pass's D3D11 blend (FXDR version 4): enabled, colour source, destination and op,
    /// alpha source, destination and op (D3D11's numbers), the write mask.
    /// </summary>
    public readonly record struct PassBlend(bool Enabled, int Src, int Dest, int Op, int SrcAlpha, int DestAlpha, int OpAlpha, int WriteMask);

    /// <summary>The techniques: none, <c>Transparent</c>, <c>OptimizedTrans</c>, other.</summary>
    public const int TechNone = 0, TechTransparent = 1, TechOptimized = 2, TechOther = 3;

    /// <summary>D3D11's blend factors and ops used here.</summary>
    private const int Zero = 1, One = 2, SrcAlpha = 5, InvSrcAlpha = 6, OpAdd = 1, OpRevSubtract = 3;

    /// <summary>
    /// How a draw is drawn: the Godot blend (0 mix, 1 add, 2 subtract, 3 premultiplied alpha), what
    /// the pixel shader writes (<see cref="Form"/>), whether the alpha is forced to 1 (a source
    /// factor of one under Godot's add and subtract, which weight by the alpha) or the colour to 0,
    /// or not drawn at all (write mask 0).
    /// </summary>
    public readonly record struct Look(int Blend, int Form, bool AlphaOne, bool ZeroColour, bool Skip);

    /// <summary>
    /// What the game's VFX pixel shader writes (effects-draw.md "What each pixel shader writes"):
    /// 0 <c>t·2v</c> (straight); 1 premultiplied, <c>a = t.a·2v.a</c>, <c>(a·min(t·2v, 1), a)</c>
    /// (the textured families under <c>Transparent</c>); 2 the untextured additive
    /// <c>(2·v.a·v.rgb, v.a)</c> (0x2, 0x3); 3 <c>a = 2v.a</c>, <c>(a·v.rgb, a)</c> (0x0, 0x10).
    /// </summary>
    public static int Form(uint flags, int technique)
    {
        var family = flags & 0x7fffffff & ~0x1000u;
        if (family is 0x2 or 0x3)
            return 2;
        if (family is 0x0 or 0x10)
            return 3;
        if (family is 0x8 or 0x9 or 0xa or 0xb or 0x18 && technique == TechTransparent)
            return 1;
        return 0;
    }

    /// <summary>The look of a draw from its captured blend (its first shader pass), else from its flags.</summary>
    public static Look LookOf(Draw d)
    {
        // the DTT pass's FrameBuffer shader (two passes) is not decoded: the frame copy mixed in
        if (d.Texture == FrameBufferName)
            return new Look(0, 0, false, false, false);
        var form = Form(d.Flags, d.Technique);
        if (d.Blends is not { Length: > 0 } bs)
            return new Look(Blend(d.Flags), 0, false, false, false);
        var b = bs[0];
        if (b.WriteMask == 0)
            return new Look(0, form, false, false, true);
        if (!b.Enabled)
            return new Look(0, form, true, false, false);
        return (b.Src, b.Dest, b.Op) switch
        {
            (SrcAlpha, InvSrcAlpha, OpAdd) => new Look(0, form, false, false, false),
            (One, InvSrcAlpha, OpAdd) => new Look(3, form, false, false, false),
            (Zero, InvSrcAlpha, OpAdd) => new Look(3, form, false, true, false),
            (SrcAlpha, One, OpAdd) => new Look(1, form, false, false, false),
            (One, One, OpAdd) => new Look(1, form, true, false, false),
            (One, One, OpRevSubtract) => new Look(2, form, true, false, false),
            (SrcAlpha, One, OpRevSubtract) => new Look(2, form, false, false, false),
            _ => new Look(Blend(d.Flags), form, false, false, false),
        };
    }

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
    private readonly Dictionary<(string, Look), ShaderMaterial> _materials = new();

    private static readonly Shader[] Shaders =
    [
        Make("blend_mix"), Make("blend_add"), Make("blend_sub"), Make("blend_premul_alpha"),
    ];

    private static Shader Make(string blend) => new()
    {
        Code = "shader_type spatial;\nrender_mode unshaded, cull_disabled, depth_draw_never, " + blend + ";\n" + @"
uniform sampler2D tex : filter_linear_mipmap, repeat_enable;
uniform sampler2D screen : hint_screen_texture, filter_linear;
uniform bool textured = true;
uniform bool frame_buffer = false;
uniform int form = 0;
uniform bool alpha_one = false;
uniform bool zero_colour = false;
void fragment() {
	vec4 t = frame_buffer ? vec4(texture(screen, UV).rgb, 1.0) : textured ? texture(tex, UV) : vec4(1.0);
	vec4 v = COLOR;
	vec4 c = t * v * 2.0;
	vec4 o = c;
	if (form == 1) {
		float a = c.a;
		o = vec4(a * min(c.rgb, vec3(1.0)), a);
	} else if (form == 2) {
		o = vec4(2.0 * v.a * v.rgb, v.a);
	} else if (form == 3) {
		float a = 2.0 * v.a;
		o = vec4(a * v.rgb, a);
	}
	o = clamp(o, 0.0, 1.0); // the target is UNORM: clamped before the blend
	ALBEDO = zero_colour ? vec3(0.0) : o.rgb;
	ALPHA = alpha_one ? 1.0 : o.a;
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

    /// <summary>Decodes a frame (<c>FXDR</c>, versions 1 to 4); null when it is not one.</summary>
    public static (uint Update, List<Draw> Draws)? Decode(byte[] b)
    {
        if (b.Length < 24 || b[0] != (byte)'F' || b[1] != (byte)'X' || b[2] != (byte)'D' || b[3] != (byte)'R')
            return null;
        int at = 4;
        ushort U16() { var v = System.BitConverter.ToUInt16(b, at); at += 2; return v; }
        uint U32() { var v = System.BitConverter.ToUInt32(b, at); at += 4; return v; }
        float F() { var v = System.BitConverter.ToSingle(b, at); at += 4; return v; }
        var version = U16();
        if (version is not (1 or 2 or 3 or 4))
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
            int technique = TechNone;
            PassBlend[]? blends = null;
            if (version >= 4)
            {
                technique = b[at];
                int np = b[at + 1];
                at += 2;
                blends = new PassBlend[np];
                for (int k = 0; k < np; k++, at += 8)
                    blends[k] = new PassBlend(b[at] != 0, b[at + 1], b[at + 2], b[at + 3], b[at + 4], b[at + 5], b[at + 6], b[at + 7]);
            }
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
            draws.Add(new Draw(hi << 32 | lo, flags, texture, pos, col, uv, idx, pass, technique, blends));
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

    private ShaderMaterial Material(FfxGame game, string texture, Look look)
    {
        if (_materials.TryGetValue((texture, look), out var m))
            return m;
        m = new ShaderMaterial { Shader = Shaders[look.Blend] };
        m.SetShaderParameter("form", look.Form);
        m.SetShaderParameter("alpha_one", look.AlphaOne);
        m.SetShaderParameter("zero_colour", look.ZeroColour);
        // the DTT pass's draws sample the frame copy (the picture so far)
        if (texture == FrameBufferName)
        {
            m.SetShaderParameter("frame_buffer", true);
            _materials[(texture, look)] = m;
            return m;
        }
        var t = Tex(game, texture);
        m.SetShaderParameter("textured", t != null);
        if (t != null)
            m.SetShaderParameter("tex", t);
        _materials[(texture, look)] = m;
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
        var groups = new List<((string, Look) Key, List<Draw> Draws)>();
        var index = new Dictionary<(string, Look), int>();
        foreach (var d in frame.Value.Draws)
        {
            if (d.Pass is >= 0 and < 5)
                PassDraws[d.Pass]++;
            if (d.Indices.Length == 0 || d.Positions.Length == 0 || !Drawn(d.Pass))
                continue;
            var look = LookOf(d);
            if (look.Skip)
                continue;
            var k = (d.Texture, look);
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
        foreach (var ((tex, look), list) in groups)
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
            var mat = Material(host.Game, tex, look);
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
