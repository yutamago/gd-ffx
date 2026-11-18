using System;
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
/// Each run of draws with the same (texture, <see cref="Look"/>) is one surface of a part
/// (a child <see cref="MeshInstance3D"/>); unshaded, two-sided, depth tested, no depth write. The
/// game draws its effects after the whole scene (its transparent parts and the refraction water
/// too): the parts take the highest render priority (<see cref="Priority"/>), above the map's
/// blended parts, and keep the game's order among themselves by their sorting offset (Godot
/// sorts the transparent pass by priority, then far to near, and does not keep the order of
/// equal keys). The blend is the game's own (FXDR version 4: the D3D11 blend of
/// the material's shader pass, <see cref="LookOf"/>), with what the game's pixel shader writes
/// for the draw's flags and technique (<see cref="Form"/>): <c>Transparent</c> premultiplies
/// with source factor one, <c>OptimizedTrans</c> writes straight colour for source alpha
/// (effects-draw.md, open question 2's answer). The save sphere's two sparkle quads (flags 0xa)
/// differ: one (one, one) premultiplied, one (src alpha, one). Older frames fall back to the
/// flags (0x10 subtract, 0x2 add, else alpha). Textures come from the player's archive by the
/// name the game holds, sampled as the game does (FXDR version 5): at <c>(u, 1 - v)</c> on the
/// upright image (the game uploads rows bottom first) with the draw's sampler address modes.
/// </summary>
public partial class EffectDrawView : MeshInstance3D
{
    /// <summary>
    /// One decoded draw: its vertices (<paramref name="First"/>, <paramref name="Vertices"/>) and
    /// indices (<paramref name="IndexFirst"/>, <paramref name="IndexCount"/>, relative to its first
    /// vertex) in the frame's <see cref="Geometry"/>.
    /// </summary>
    public sealed record Draw(ulong Key, uint Flags, string Texture, int First, int Vertices, int IndexFirst, int IndexCount, int Pass = 0,
        int Technique = 0, PassBlend[]? Blends = null, int AddressU = 1, int AddressV = 1);

    /// <summary>
    /// A frame's vertices and indices, all draws' in one set of arrays (kept and reused from
    /// frame to frame: a frame has thousands of vertices, and fresh arrays that large each
    /// update land on the large object heap, which costs full collections).
    /// </summary>
    public sealed class Geometry
    {
        public Vector3[] Positions = [];
        public Color[] Colours = [];
        public Vector2[] Uvs = [];
        public int[] Indices = [];
        public int VertexCount, IndexCount;

        public void Clear() => VertexCount = IndexCount = 0;

        /// <summary>Room for <paramref name="v"/> more vertices and <paramref name="n"/> more indices.</summary>
        public void Reserve(int v, int n)
        {
            static void Grow<T>(ref T[] a, int need)
            {
                if (a.Length < need)
                    System.Array.Resize(ref a, System.Math.Max(need, a.Length * 2));
            }
            Grow(ref Positions, VertexCount + v);
            Grow(ref Colours, VertexCount + v);
            Grow(ref Uvs, VertexCount + v);
            Grow(ref Indices, IndexCount + n);
        }
    }

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
    public readonly record struct Look(int Blend, int Form, bool AlphaOne, bool ZeroColour, bool Skip)
    {
        /// <summary>The sampler's address modes, D3D11's numbers: 1 wrap, 2 mirror, 3 clamp, 4 border, 5 mirror once.</summary>
        public int AddressU { get; init; } = 1;
        public int AddressV { get; init; } = 1;
        /// <summary>Whether the draw's shader samples its texture (<c>VSP_ENABLE_TEXTURE</c> of its <see cref="Shader"/> flags).</summary>
        public bool Textured { get; init; } = true;
        /// <summary>No depth test (<c>VSP_ZTEST_DISABLE</c>, 0x800: the lens flares, the guide map).</summary>
        public bool NoDepthTest { get; init; }
        /// <summary>Writes depth (<c>VSP_ZWRITE_ENABLE</c>, 0x40).</summary>
        public bool DepthWrite { get; init; }
    }

    /// <summary>
    /// The VSP flags of the shaders the game's <c>vfxShader_fx.txt</c> lists (each line's
    /// <c>VSP_*</c> bits ORed; the bit values from the file's own comment block). A draw gets the
    /// shader of its flags; flags not listed get the shader of their bit 8 alone
    /// (<c>ClassDynamicGeometry_SetShader_006a4800</c>'s retry; effects-draw.md "Shaders").
    /// </summary>
    private static readonly HashSet<uint> ShaderFlags =
    [
        0x8, 0xa, 0x9, 0x1, 0xb, 0x0, 0x18, 0x28, 0x10, 0x20, 0x2, 0x48, 0x100048, 0x8048, 0x4a, 0x49, 0x4b,
        0x40, 0x58, 0x68, 0x50, 0x42, 0x8a, 0x240, 0x248, 0x8248, 0x3, 0x19, 0x29, 0x408, 0x80a, 0x802, 0x11,
        0x8b, 0x40000000, 0x808, 0x1000, 0x249, 0x848, 0x2848, 0x4000, 0x98, 0x20000000, 0x08000000,
        0x10000000, 0x800a, 0x10000, 0x80b, 0x803, 0x400, 0x208, 0x01000008, 0x02000008, 0x02000000,
        0x20248, 0x20008, 0x108, 0x40000, 0x80008, 0x80000000,
    ];

    /// <summary>The flags of the shader a draw with VSP flags <paramref name="flags"/> is drawn with (<see cref="ShaderFlags"/>).</summary>
    public static uint Shader(uint flags) => ShaderFlags.Contains(flags) ? flags : flags & 0x8;

    /// <summary>
    /// What the game's VFX pixel shader writes (effects-draw.md "What each pixel shader writes"),
    /// by the flags of the shader the draw gets (<see cref="Shader"/>): 0 <c>t·2v</c> (straight);
    /// 1 premultiplied, <c>a = t.a·2v.a</c>, <c>(a·min(t·2v, 1), a)</c> (the textured families
    /// under <c>Transparent</c>, the textured subtract family 0x18 under both); 2 the untextured
    /// additive <c>(2·v.a·v.rgb, v.a)</c> (0x2, 0x3); 3 <c>a = 2v.a</c>, <c>(a·v.rgb, a)</c> (the
    /// other untextured ones, 0x0, 0x10). The families not decoded (z-write, no z-test, clut, ...)
    /// are taken as their decoded kin: textured or not, additive or not, subtract.
    /// </summary>
    public static int Form(uint flags, int technique)
    {
        var shader = Shader(flags);
        if ((shader & 0x8) == 0)
            return (shader & 0x2) != 0 ? 2 : 3;
        if ((shader & 0x10) != 0 || technique == TechTransparent)
            return 1;
        return 0;
    }

    /// <summary>The look of a draw from its captured blend (its first shader pass), else from its flags.</summary>
    public static Look LookOf(Draw d)
    {
        var shader = Shader(d.Flags);
        return BlendLook(d) with
        {
            AddressU = d.AddressU,
            AddressV = d.AddressV,
            Textured = (shader & 0x8) != 0 || d.Texture == FrameBufferName,
            NoDepthTest = (shader & 0x800) != 0,
            DepthWrite = (shader & 0x40) != 0,
        };
    }

    private static Look BlendLook(Draw d)
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
    private readonly Dictionary<System.Type, Node?> _overlay = new();

    /// <summary>A view of the overlay by its type (found once: a search of the whole tree, the map's nodes too, is slow).</summary>
    private T? Overlay<T>() where T : Node
    {
        if (!_overlay.TryGetValue(typeof(T), out var n) || n == null || !IsInstanceValid(n))
            _overlay[typeof(T)] = n = GetTree().Root.FindChild(typeof(T).Name, true, false) as T;
        return n as T;
    }
    private readonly Dictionary<string, Texture2D?> _textures = new();
    private readonly Dictionary<(string, Look), ShaderMaterial> _materials = new();

    /// <summary>
    /// The render priority of the effects: Godot's highest, after the map's blended parts (1 up)
    /// and the refraction water (<see cref="ClassicMaterials.PostPriority"/>), as the game draws
    /// them after the scene.
    /// </summary>
    public const int Priority = 127;

    /// <summary>The shaders by blend (0 mix, 1 add, 2 subtract, 3 premultiplied), depth test off and depth write (made when first used).</summary>
    private static readonly Shader?[] Shaders = new Shader?[16];

    private static Shader ShaderOf(Look look)
    {
        int k = look.Blend * 4 + (look.NoDepthTest ? 1 : 0) + (look.DepthWrite ? 2 : 0);
        return Shaders[k] ??= Make(look.Blend switch { 1 => "blend_add", 2 => "blend_sub", 3 => "blend_premul_alpha", _ => "blend_mix" }
            + (look.DepthWrite ? ", depth_draw_always" : ", depth_draw_never")
            + (look.NoDepthTest ? ", depth_test_disabled" : ""));
    }

    private static Shader Make(string modes)
    {
        // the global uniforms (ffx_linear_out) exist before a shader reads them
        ClassicMaterials.EnsureGlobals();
        return MakeShader(modes);
    }

    private static Shader MakeShader(string modes) => new()
    {
        Code = "shader_type spatial;\nrender_mode unshaded, cull_disabled, " + modes + ";\n" + @"
uniform sampler2D tex : filter_linear_mipmap, repeat_enable;
uniform sampler2D tex_clamp : filter_linear_mipmap, repeat_disable;
uniform bool flip_v = true;
uniform int address_u = 1;
uniform int address_v = 1;
// the sampler's address mode outside 0..1 (D3D11: 1 wrap, 2 mirror, 3 clamp, 4 border as clamp, 5 mirror once)
float ffx_address(float t, int mode) {
	if (mode == 2) return 1.0 - abs(mod(t, 2.0) - 1.0);
	if (mode == 5) return clamp(abs(t), 0.0, 1.0);
	return mode == 1 ? t : clamp(t, 0.0, 1.0);
}
uniform sampler2D screen : hint_screen_texture, filter_linear;
uniform bool textured = true;
uniform bool frame_buffer = false;
uniform int form = 0;
uniform bool alpha_one = false;
uniform bool zero_colour = false;
// 1 while the scene draws remastered (ClassicMaterials' ffx_out): the game's values written as linear
global uniform float ffx_linear_out;
void fragment() {
	// the game samples file textures with v counted from the stored first row, the image's
	// bottom (the PNG is upright): (u, 1 - v)
	vec2 uv = flip_v ? vec2(UV.x, 1.0 - UV.y) : UV;
	bool wrap = address_u == 1 && address_v == 1;
	vec2 fu = vec2(ffx_address(uv.x, address_u), ffx_address(uv.y, address_v));
	vec4 t = frame_buffer ? vec4(texture(screen, UV).rgb, 1.0)
		: textured ? (wrap ? texture(tex, uv) : texture(tex_clamp, fu)) : vec4(1.0);
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
	ALBEDO = zero_colour ? vec3(0.0) : ffx_linear_out < 0.5 ? o.rgb
		: mix(o.rgb / 12.92, pow((o.rgb + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), o.rgb));
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

    /// <summary>
    /// Decodes a frame (<c>FXDR</c>, versions 1 to 5); null when it is not one. Its geometry goes
    /// into <paramref name="into"/> (cleared first), else a new <see cref="Geometry"/>.
    /// </summary>
    public static (uint Update, List<Draw> Draws, Geometry Geometry)? Decode(byte[] b, Geometry? into = null) =>
        Decode(b, b.Length, into);

    /// <summary>
    /// <see cref="Decode(byte[], Geometry?)"/> of the first <paramref name="length"/> bytes of
    /// <paramref name="b"/> (a kept buffer, longer than the frame).
    /// </summary>
    public static (uint Update, List<Draw> Draws, Geometry Geometry)? Decode(byte[] b, int length, Geometry? into = null)
    {
        if (length < 24 || length > b.Length || b[0] != (byte)'F' || b[1] != (byte)'X' || b[2] != (byte)'D' || b[3] != (byte)'R')
            return null;
        int at = 4;
        ushort U16() { var v = System.BitConverter.ToUInt16(b, at); at += 2; return v; }
        uint U32() { var v = System.BitConverter.ToUInt32(b, at); at += 4; return v; }
        float F() { var v = System.BitConverter.ToSingle(b, at); at += 4; return v; }
        var version = U16();
        if (version is not (1 or 2 or 3 or 4 or 5))
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
        var geo = into ?? new Geometry();
        geo.Clear();
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
            int addressU = 1, addressV = 1;
            if (version >= 5 && b[at++] == 1)
            {
                // address U, V, W, then filter, bias, anisotropy, border (4 bytes each)
                addressU = b[at];
                addressV = b[at + 1];
                at += 3 + 16;
            }
            at += 48; // world (positions are already placed)
            int tl = U16();
            var texture = TextureName(new System.ReadOnlySpan<byte>(b, at, tl));
            at += tl;
            int v = (int)U32(), n = (int)U32();
            if (at + v * 36 + n * 2 > length)
                throw new System.ArgumentException("FXDR frame cut short");
            geo.Reserve(v, n);
            int first = geo.VertexCount, ifirst = geo.IndexCount;
            // little-endian floats, as the vectors lay them out: copied whole
            var span = new System.ReadOnlySpan<byte>(b, at, v * 36);
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Vector3>(span[..(v * 12)]).CopyTo(geo.Positions.AsSpan(first, v));
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Color>(span.Slice(v * 12, v * 16)).CopyTo(geo.Colours.AsSpan(first, v));
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Vector2>(span.Slice(v * 28, v * 8)).CopyTo(geo.Uvs.AsSpan(first, v));
            at += v * 36;
            for (int i = 0; i < n; i++) geo.Indices[ifirst + i] = U16();
            geo.VertexCount += v;
            geo.IndexCount += n;
            draws.Add(new Draw(hi << 32 | lo, flags, texture, first, v, ifirst, n, pass, technique, blends, addressU, addressV));
        }
        LastDistortion = null;
        if (version >= 2 && at + 16 <= length)
        {
            var bits = U32();
            var blur = F();
            int dn = (int)U32(), di = (int)U32();
            if ((bits & 1) != 0 && at + dn * 32 + di * 2 <= length)
            {
                var p = new float[2 * dn]; for (int i = 0; i < p.Length; i++) p[i] = F();
                var u = new float[2 * dn]; for (int i = 0; i < u.Length; i++) u[i] = F();
                var c = new float[4 * dn]; for (int i = 0; i < c.Length; i++) c[i] = F();
                var ix = new int[di]; for (int i = 0; i < di; i++) ix[i] = U16();
                LastDistortion = new Distortion((bits & 2) != 0, (bits & 4) != 0, blur, p, u, c, ix);
            }
        }
        return (update, draws, geo);
    }

    private static readonly Dictionary<int, string[]> Names = new();

    /// <summary>A texture name, the same string each time it comes (no new string a draw a frame).</summary>
    private static string TextureName(System.ReadOnlySpan<byte> utf8)
    {
        var hash = new System.HashCode();
        hash.AddBytes(utf8);
        var h = hash.ToHashCode();
        if (Names.TryGetValue(h, out var list))
            foreach (var s in list)
                if (System.Text.Encoding.UTF8.GetByteCount(s) == utf8.Length && Same(s, utf8))
                    return s;
        var name = System.Text.Encoding.UTF8.GetString(utf8);
        Names[h] = list == null ? [name] : [.. list, name];
        return name;
    }

    private static bool Same(string s, System.ReadOnlySpan<byte> utf8)
    {
        System.Span<byte> buf = utf8.Length <= 256 ? stackalloc byte[utf8.Length] : new byte[utf8.Length];
        return System.Text.Encoding.UTF8.GetBytes(s, buf) == utf8.Length && buf.SequenceEqual(utf8);
    }

    /// <summary>The texture name of draws that sample the frame copy (the DTT pass).</summary>
    public const string FrameBufferName = "FrameBuffer";

    /// <summary>Textures being read on worker threads (their image, mipmaps made).</summary>
    private readonly Dictionary<string, System.Threading.Tasks.Task<Image?>> _loading = new();

    /// <summary>Every view's texture reads under way (<see cref="WaitPending"/>).</summary>
    private static readonly List<System.Threading.Tasks.Task> Reads = new();

    /// <summary>Waits up to <paramref name="ms"/> for the texture reads under way (Godot quits).</summary>
    public static void WaitPending(int ms)
    {
        Reads.RemoveAll(t => t.IsCompleted);
        if (Reads.Count > 0)
            System.Threading.Tasks.Task.WaitAll(Reads.ToArray(), ms);
        Reads.Clear();
    }

    /// <summary>
    /// A draw's texture: true with it (null: none or not in the archive) once read; false while it
    /// is still being read in the background (its draws wait a frame or two rather than the frame
    /// waiting for the read).
    /// </summary>
    private bool TryTex(string name, out Texture2D? t)
    {
        if (_textures.TryGetValue(name, out t))
        {
            // a texture not found is read again a few times (a second apart) before it is given up
            if (t != null || !_failed.TryGetValue(name, out var f) || f.Tries >= ReadTries || Time.GetTicksMsec() < f.Next)
                return true;
            _textures.Remove(name);
        }
        if (name.Length == 0 || name == "NoTexture")
        {
            _textures[name] = null;
            _failed[name] = (ReadTries, 0);
            return true;
        }
        if (!_loading.TryGetValue(name, out var task))
        {
            _loading[name] = task = System.Threading.Tasks.Task.Run(() => ReadImage(name));
            Reads.RemoveAll(r => r.IsCompleted);
            Reads.Add(task);
        }
        if (!task.IsCompleted)
            return false;
        _loading.Remove(name);
        var img = task.IsCompletedSuccessfully ? task.Result : null;
        t = img == null ? null : ImageTexture.CreateFromImage(img);
        _textures[name] = t;
        if (t == null)
        {
            var tries = (_failed.TryGetValue(name, out var f) ? f.Tries : 0) + 1;
            _failed[name] = (tries, Time.GetTicksMsec() + 1000);
            if (tries == ReadTries && Missing.Add(name))
                GD.Print($"ffx effects: texture \"{name}\" not found in the archive: its draws are not drawn");
        }
        else
            _failed.Remove(name);
        return true;
    }

    /// <summary>How often a texture that is not found is read before its draws are given up.</summary>
    private const int ReadTries = 3;

    /// <summary>Textures not found so far: the reads made and when the next may be.</summary>
    private readonly Dictionary<string, (int Tries, ulong Next)> _failed = new();

    /// <summary>The texture names no read found (logged once each; for tests).</summary>
    public static HashSet<string> Missing { get; } = new();

    /// <summary>A run-time texture's image from the archive, with mipmaps (a worker thread).</summary>
    private static Image? ReadImage(string name)
    {
        if (TextureFetcher.Fetch("runtime", name) is not { } px)
            return null;
        var img = px.ToImage();
        img.GenerateMipmaps();
        return img;
    }

    /// <summary>
    /// The material of a (texture, look), null while its texture is still being read or when a
    /// textured shader's texture is not found (<see cref="Missing"/>: such a draw is not drawn, as
    /// a flat quad of the vertex colour, the white boxes of the sun's glare, is never right).
    /// </summary>
    private ShaderMaterial? Material(string texture, Look look)
    {
        // an untextured shader samples nothing, whatever name the segment holds
        if (!look.Textured)
            texture = "";
        if (_materials.TryGetValue((texture, look), out var m))
            return m;
        Texture2D? t = null;
        if (look.Textured && texture != FrameBufferName && !TryTex(texture, out t))
            return null;
        if (look.Textured && texture != FrameBufferName && t == null)
            return null;
        m = MakeMaterial(look, t, texture == FrameBufferName);
        _materials[(texture, look)] = m;
        return m;
    }

    /// <summary>
    /// A material that draws an effect draw of <paramref name="look"/> with texture
    /// <paramref name="t"/> as the game does (<paramref name="frameBuffer"/>: the DTT pass's frame
    /// copy); the editor's classic effect replay uses it too (R7).
    /// </summary>
    public static ShaderMaterial MakeMaterial(Look look, Texture2D? t, bool frameBuffer = false)
    {
        var m = new ShaderMaterial { Shader = ShaderOf(look), RenderPriority = Priority };
        m.SetShaderParameter("form", look.Form);
        m.SetShaderParameter("alpha_one", look.AlphaOne);
        m.SetShaderParameter("zero_colour", look.ZeroColour);
        m.SetShaderParameter("address_u", look.AddressU);
        m.SetShaderParameter("address_v", look.AddressV);
        // the DTT pass's draws sample the frame copy (the picture so far)
        if (frameBuffer)
        {
            m.SetShaderParameter("frame_buffer", true);
            return m;
        }
        m.SetShaderParameter("textured", t != null);
        if (t != null)
        {
            m.SetShaderParameter("tex", t);
            m.SetShaderParameter("tex_clamp", t);
        }
        return m;
    }

    /// <summary><c>FFX_EFFECT_LOG=1</c>: each kind of draw (flags, technique, texture, look) printed once.</summary>
    private static readonly bool LogDraws = System.Environment.GetEnvironmentVariable("FFX_EFFECT_LOG") == "1";
    private static readonly HashSet<(uint, int, string, Look)> Logged = new();
    private static readonly HashSet<(uint, int, string)> LoggedOdd = new();

    /// <summary>
    /// Whether a captured vertex colour is one the game never writes: not finite, below 0 or above
    /// 8 (the PS2's colours are 0..0xff, 0..2 here). Some draws index vertices whose colour array
    /// slot holds the allocator's fill (<c>0xcdcdcdcd</c>, -4.3e8 as a float) or stale numbers
    /// (Besaid village's lens flare, bsvr00 key lo 0xd). ffx-rust (PR 58) found them in the
    /// arrays the game itself draws, only in collapsed triangles (a repeated index), which are
    /// dropped first; the only others are the <c>0x40000000</c> full-screen quads, drawn with
    /// write mask 0 (skipped). This check stays as a guard.
    /// </summary>
    public static bool Unset(Color c) =>
        !(c.R >= -1e-3f && c.R <= 8f && c.G >= -1e-3f && c.G <= 8f && c.B >= -1e-3f && c.B <= 8f && c.A >= -1e-3f && c.A <= 8f);

    /// <summary><c>--keep-unset-colours</c>: draws the triangles with unset colours too (for comparing).</summary>
    private static readonly bool KeepUnset = GameSettings.Flag("keep-unset-colours");

    /// <summary>Draws of the last frame with triangles not drawn for an unset colour, and such draws since the start.</summary>
    public int UnsetDraws { get; private set; }
    public static int UnsetSeen { get; private set; }

    /// <summary>Draws of the last frame shown not drawn because their texture was not found (for tests).</summary>
    public int MissingDraws { get; private set; }

    public override void _Process(double delta)
    {
        var host = GameHost.Instance;
        var s = host?.View.Current;
        if (host?.Game == null || s == null || s.Update == _update)
            return;
        _update = s.Update;
        var t0 = Perf.Start();
        // the frame copied into a kept buffer (a new 344 KiB array an update at Besaid made gen2 collections)
        int len = host.Game.EffectDrawsInto(s.Update, ref _frameBytes);
        var frame = Decode(_frameBytes, len, _geometry);
        Perf.End(Perf.Section.EffectDecode, t0);
        t0 = Perf.Start();
        try
        {
            Build(host.Game, s, frame);
        }
        finally
        {
            Perf.End(Perf.Section.EffectMesh, t0);
        }
    }

    private byte[] _frameBytes = [];
    private readonly Geometry _geometry = new();
    private readonly Geometry _surface = new();
    private readonly List<((string, Look) Key, List<Draw> Draws)> _groups = new();
    private readonly Godot.Collections.Array _arrays = new();

    /// <summary>The parts, one a run of draws (kept from frame to frame; the unused ones hidden).</summary>
    private readonly List<(MeshInstance3D Node, ArrayMesh Mesh)> _parts = new();

    /// <summary>The part <paramref name="k"/> (made when first needed), drawn after parts 0..k-1.</summary>
    private (MeshInstance3D Node, ArrayMesh Mesh) Part(int k)
    {
        while (_parts.Count <= k)
        {
            var mesh = new ArrayMesh();
            // all parts at the same place: the offset alone orders them (a larger one draws later)
            var node = new MeshInstance3D
            {
                Name = $"Part{_parts.Count}",
                Mesh = mesh,
                SortingUseAabbCenter = false,
                SortingOffset = _parts.Count + 1,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(node);
            _parts.Add((node, mesh));
        }
        return _parts[k];
    }

    /// <summary>The parts in use (for tests).</summary>
    public int Parts { get; private set; }

    /// <summary>Draws of the last frame left out because a remade effect stands in for them (R7; for tests).</summary>
    public int RemadeSkipped { get; private set; }

    private Remaster.EffectRemakeView? _remakes;

    /// <summary>The remade effects' view, a sibling (R7; made when missing).</summary>
    private Remaster.EffectRemakeView? Remakes()
    {
        if (_remakes != null && IsInstanceValid(_remakes))
            return _remakes;
        if (GetParent() is not { } parent)
            return null;
        _remakes = parent.GetNodeOrNull<Remaster.EffectRemakeView>("EffectRemakeView");
        if (_remakes == null)
        {
            _remakes = new Remaster.EffectRemakeView { Name = "EffectRemakeView" };
            parent.AddChild(_remakes);
        }
        return _remakes;
    }

    private void Build(FfxGame game, GdFfx.Api.Snapshot s, (uint Update, List<Draw> Draws, Geometry Geometry)? frame)
    {
        // the screen distortion of the same frame (DistortionView, in the overlay)
        if (Overlay<DistortionView>() is { } dv)
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
        if (Overlay<ScreenFadeView>() is { } fv)
            fv.Show(fade);
        if (frame == null || frame.Value.Draws.Count == 0)
        {
            HideParts(0);
            MissingDraws = 0;
            UnsetDraws = 0;
            Shown = (0, 0, s.Update);
            Textures.Clear();
            return;
        }
        // a run of draws with the same (texture, look) is one part, in the game's draw order; the
        // lists, the parts and their arrays are kept from frame to frame
        var groups = _groups;
        foreach (var (_, list) in groups)
            list.Clear();
        int used = 0;
        var geo = frame.Value.Geometry;
        // R7: who made each draw; the remade effects started, moved, stopped; their classic draws left out
        var remakes = Remakes();
        remakes?.Frame(game, s.Update, s.World.Level, frame.Value.Draws, geo);
        int skipped = 0;
        for (int di = 0; di < frame.Value.Draws.Count; di++)
        {
            var d = frame.Value.Draws[di];
            if (remakes != null && remakes.Hidden.Count > 0 && remakes.Ids[di] is { } rid && remakes.Hidden.Contains(rid))
            {
                skipped++;
                continue;
            }
            if (d.Pass is >= 0 and < 5)
                PassDraws[d.Pass]++;
            if (d.IndexCount == 0 || d.Vertices == 0 || !Drawn(d.Pass))
                continue;
            var look = LookOf(d);
            if (LogDraws && Logged.Add((d.Flags, d.Technique, d.Texture, look)))
                GD.Print($"ffx effects: flags 0x{d.Flags:x} (shader 0x{Shader(d.Flags):x}) technique {d.Technique} pass {d.Pass} key 0x{d.Key:x} "
                    + $"texture \"{d.Texture}\" {look}, colour {geo.Colours[d.First]}, at {geo.Positions[d.First]}");
            if (LogDraws)
                for (int ki = 0; ki < d.IndexCount; ki++)
                    if (geo.Indices[d.IndexFirst + ki] is var i && i < d.Vertices && geo.Colours[d.First + i] is var c && Unset(c))
                    {
                        if (LoggedOdd.Add((d.Flags, d.Technique, d.Texture)))
                            GD.Print($"ffx effects: odd colour {c} at indexed vertex {i} of {d.Vertices} ({d.IndexCount} indices; at {geo.Positions[d.First + i]}), flags 0x{d.Flags:x} technique {d.Technique} key 0x{d.Key:x} texture \"{d.Texture}\", update {s.Update}");
                        break;
                    }
            if (look.Skip)
                continue;
            var k = (d.Texture, look);
            if (used == 0 || groups[used - 1].Key != k)
            {
                if (used == groups.Count)
                    groups.Add((k, new List<Draw>()));
                else
                    groups[used] = (k, groups[used].Draws);
                used++;
            }
            groups[used - 1].Draws.Add(d);
        }
        if (_arrays.Count != (int)Mesh.ArrayType.Max)
            _arrays.Resize((int)Mesh.ArrayType.Max);
        int textured = 0, parts = 0, missing = 0, unsetDraws = 0;
        Textures.Clear();
        var surf = _surface;
        for (int gi = 0; gi < used; gi++)
        {
            var ((tex, look), list) = groups[gi];
            surf.Clear();
            foreach (var d in list)
            {
                surf.Reserve(d.Vertices, d.IndexCount);
                int b = surf.VertexCount;
                geo.Positions.AsSpan(d.First, d.Vertices).CopyTo(surf.Positions.AsSpan(b));
                geo.Colours.AsSpan(d.First, d.Vertices).CopyTo(surf.Colours.AsSpan(b));
                geo.Uvs.AsSpan(d.First, d.Vertices).CopyTo(surf.Uvs.AsSpan(b));
                surf.VertexCount += d.Vertices;
                // triangle by triangle: one with a vertex out of the draw's range or a repeated
                // index is not drawn (the fills leave reserved quads collapsed, all indices 0,
                // over vertices they never colour: ffx-rust's effects-draw.md, "Which arrays the
                // draw reads"; they draw nothing in the game either); then, as a guard (unless
                // --keep-unset-colours), one with a colour no vertex has (Unset)
                bool unset = false;
                for (int k = 0; k + 2 < d.IndexCount; k += 3)
                {
                    int i0 = geo.Indices[d.IndexFirst + k], i1 = geo.Indices[d.IndexFirst + k + 1], i2 = geo.Indices[d.IndexFirst + k + 2];
                    if (i0 >= d.Vertices || i1 >= d.Vertices || i2 >= d.Vertices || i0 == i1 || i1 == i2 || i0 == i2)
                        continue;
                    if (!KeepUnset && (Unset(geo.Colours[d.First + i0]) || Unset(geo.Colours[d.First + i1]) || Unset(geo.Colours[d.First + i2])))
                    {
                        unset = true;
                        continue;
                    }
                    surf.Indices[surf.IndexCount++] = b + i0;
                    surf.Indices[surf.IndexCount++] = b + i1;
                    surf.Indices[surf.IndexCount++] = b + i2;
                }
                if (unset)
                {
                    unsetDraws++;
                    UnsetSeen++;
                }
            }
            if (surf.IndexCount == 0)
                continue;
            if (Material(tex, look) is not { } mat)
            {
                // its texture is still being read, or not found (not drawn)
                if (Missing.Contains(tex))
                    missing += list.Count;
                continue;
            }
            // straight from the kept arrays into Godot's packed arrays (no array of the exact size made)
            // (the variants disposed once copied, no blend shapes or LODs made: Variants)
            Variants.Set(_arrays, (int)Mesh.ArrayType.Vertex, surf.Positions.AsSpan(0, surf.VertexCount));
            Variants.Set(_arrays, (int)Mesh.ArrayType.Color, surf.Colours.AsSpan(0, surf.VertexCount));
            Variants.Set(_arrays, (int)Mesh.ArrayType.TexUV, surf.Uvs.AsSpan(0, surf.VertexCount));
            Variants.Set(_arrays, (int)Mesh.ArrayType.Index, surf.Indices.AsSpan(0, surf.IndexCount));
            var (node, mesh) = Part(parts++);
            mesh.ClearSurfaces();
            Variants.AddSurface(mesh, Mesh.PrimitiveType.Triangles, _arrays);
            mesh.SurfaceSetMaterial(0, mat);
            node.Visible = true;
            if (tex == FrameBufferName || _textures.TryGetValue(tex, out var t) && t != null)
            {
                textured += list.Count;
                Textures.Add(tex);
            }
        }
        HideParts(parts);
        Parts = parts;
        RemadeSkipped = skipped;
        MissingDraws = missing;
        UnsetDraws = unsetDraws;
        Shown = (frame.Value.Draws.Count, textured, s.Update);
    }

    /// <summary>Hides the parts from <paramref name="from"/> on (their meshes emptied).</summary>
    private void HideParts(int from)
    {
        for (int k = from; k < _parts.Count; k++)
            if (_parts[k].Node.Visible)
            {
                _parts[k].Node.Visible = false;
                _parts[k].Mesh.ClearSurfaces();
            }
        if (from == 0)
            Parts = 0;
    }
}
