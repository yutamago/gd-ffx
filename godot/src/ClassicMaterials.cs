using System;
using System.Linq;
using System.Collections.Generic;
using Godot;

namespace GdFfx;

/// <summary>
/// The classic content's materials as the game draws them (its own shaders,
/// read from the bytecode; their states from the effect variant: docs/importer.md "Material
/// states", docs/architecture.md "Map materials"). The glTF import gives Godot's lit standard
/// materials; these replace them:
/// <list type="bullet">
/// <item><b>maps</b> (<c>PhyreDefaultLitShader</c>, <c>PhyreUVAnimationShader</c>): not lit at
/// all, the lighting is baked into the vertex colours; texture x MaterialColour x
/// vertex colour x (1 + emissiveness), then the game's tone curve; blend, cull, depth write,
/// fog and the alpha test as the variant's pass says, scrolling UVs for the clouds.</item>
/// <item><b>water</b> (<c>PhyreWaterShader</c>, <c>PhyreWaveShader</c>): the game's math with the
/// map's cube map (see <c>WaterBody</c>).</item>
/// <item><b>2D layers</b> (<c>PhyrePrerenderedShader</c>): the painted backgrounds, placed on the
/// screen in PS2 pixels at the main camera's depth (<see cref="MapLayers"/>).</item>
/// <item><b>characters</b> (<c>PhyreChrLitShader</c>): texture x MaterialColour x
/// VertexColorOrigin (not the vertex colour stream) x the game's half-Lambert
/// squared lights plus ambient.</item>
/// </list>
/// </summary>
public static class ClassicMaterials
{
    private const string Curve = @"
// the game's tone curve (phyredefaultlitshader's last step)
vec3 ffx_curve(vec3 x) {
	return min(x, vec3(0.4)) * 2.1 + clamp(x, vec3(0.4), vec3(0.58)) * 1.08 - vec3(0.47);
}
vec3 ffx_to_linear(vec3 c) {
	c = clamp(c, vec3(0.0), vec3(1.0));
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}
// the game computes on the stored (gamma) values and shows the result as
// it is; ffx_gamma_out says whether this renderer converts linear to sRGB on
// output (then the result goes back to linear first)
uniform bool ffx_gamma_out = false;
vec3 ffx_out(vec3 c) { return ffx_gamma_out ? ffx_to_linear(c) : c; }
// the scene's look (the game-state API's Look; ClassicMaterials.SetLook)
global uniform vec3 ffx_colour_scale;
global uniform vec3 ffx_fog_colour;
global uniform vec4 ffx_fog; // near, far, curve, limit
// the shaders' GlobalTime (Look.global_time, interpolated between snapshots; SetTime)
global uniform float ffx_time;
// the game's fog: min(((depth - near) / (far - near))^curve, limit) x fgEff,
// mixed in before the colour scale and the tone curve
uniform float fog_effect = 1.0;
vec3 ffx_fog_apply(vec3 c, float depth) {
	float t = clamp((depth - ffx_fog.x) / max(ffx_fog.y - ffx_fog.x, 0.01), 0.0, 1.0);
	float f = min(pow(t, max(ffx_fog.z, 0.001)), ffx_fog.w) * fog_effect;
	return mix(c, ffx_fog_colour, f);
}

";

    private static Shader MakeShader(string modes, string body, string defines = "") =>
        new() { Code = "shader_type spatial;\nrender_mode " + modes + ";\n" + defines + Curve + body };

    // The map shaders (phyredefaultlitshader, phyreuvanimationshader; read from the bytecode with
    // ffx-rust's ffx-dxbc, `ffx-import effect --wgsl`): c = texture x MaterialColour x vertex
    // colour (all four components), then the fog x fgEff when the program reads fgEff
    // (IGNORE_FOG materials have fgEff 0), x clrScaleFactor, the tone curve. A blended variant
    // (ALPHA_ENABLED) outputs alpha =texture a x MaterialColour a x vertex a x clrScaleFactor a (taken as 1) and tests
    // nothing; an alpha-to-coverage variant (Opaque pass; its OpaqueNoMSAA twin, which the maps
    // draw with) discards where texture a < AlphaThreshold; the others ignore alpha.
    // UV_ANIM (phyreuvanimationshader): TEXCOORD0 + ScrollSpeed x GlobalTime in the vertex
    // program; the export flips v (v' = 1 - v), so the v scroll is negated here.
    // The morphs the game drives per object (docs/importer.md, "Morphing meshes"; the weights
    // per mesh instance, StateView3D): UV_MORPH, TEXCOORD0 + UVMorphWeight x (TEXCOORD5 -
    // TEXCOORD0); VERTEX_COLOR_MORPH (COLOR_MORPH here), COLOR0 + VertexColorMorphWeight x (COLOR1 -
    // COLOR0), COLOR1 imported as CUSTOM0; UV_SCROLL, TEXCOORD0 + (UvOffsetX, UvOffsetY), v
    // negated for the export's flip.
    // TEXCOORD0 reads the UV set the instance binds (the import's ffx_texcoords; UV0_SET1: glTF
    // TEXCOORD_1, Godot's UV2): bsil06's ground keeps its UVs in set 1, set 0 all zero.
    private const string MapBody = @"
#if defined(UV_MORPH) || defined(COLOR_MORPH)
instance uniform float ffx_morph = 0.0;
#endif
#ifdef UV_MORPH
// TEXCOORD5's set (0 UV, 1 UV2)
uniform float uv_morph_set = 1.0;
#endif
#ifdef UV_SCROLL
instance uniform vec2 ffx_uv_offset = vec2(0.0);
#endif
#if defined(UV_MORPH) || defined(COLOR_MORPH) || defined(UV_SCROLL) || defined(UV0_SET1)
void vertex() {
	vec2 uv_set0 = UV;
#ifdef UV0_SET1
	UV = UV2;
#endif
#ifdef UV_MORPH
	UV += (mix(uv_set0, UV2, uv_morph_set) - UV) * ffx_morph;
#endif
#ifdef COLOR_MORPH
	COLOR += (CUSTOM0 - COLOR) * ffx_morph;
#endif
#ifdef UV_SCROLL
	UV += vec2(ffx_uv_offset.x, -ffx_uv_offset.y);
#endif
}
#endif
#ifdef TEX_CLAMP
uniform sampler2D tex : filter_linear_mipmap_anisotropic, repeat_disable;
#else
uniform sampler2D tex : filter_linear_mipmap_anisotropic, repeat_enable;
#endif
uniform vec4 material_colour = vec4(1.0);
uniform vec3 emissiveness = vec3(0.0);
uniform float alpha_threshold = 0.0;
uniform vec2 scroll = vec2(0.0);
void fragment() {
	vec2 uv = UV;
#ifdef UV_ANIM
	uv += vec2(fract(scroll.x * ffx_time), -fract(scroll.y * ffx_time));
#endif
	vec4 t = texture(tex, uv);
#ifdef ALPHA_TEST
	if (t.a < alpha_threshold) { discard; }
#endif
	vec4 c = t * material_colour * COLOR;
	c.rgb *= vec3(1.0) + emissiveness;
#ifdef FOG
	c.rgb = ffx_fog_apply(c.rgb, -VERTEX.z);
#endif
	ALBEDO = ffx_out(ffx_curve(c.rgb * ffx_colour_scale));
#ifdef BLEND
	ALPHA = clamp(c.a, 0.0, 1.0);
#endif
}
";

    // PhyreWaterShader (read from the bytecode of the bsil00, kino02 and bika00 variants): the
    // base ((TextureSampler(uv0) + TextureSampler1(uv1) when TEXTURE_ENABLED) + (0, 0, 0, 1)) x
    // MultiTexBlendFactor x MaterialColour, times the cube map at the bent normal, plus the
    // cube map at the reflection x SpecularShininess x SpecularColor (rgb; alpha x shininess);
    // x the vertex colour, the fog, clrScaleFactor (clamped), the tone curve; alpha the
    // result's. The normal is the vertex normal bent by the two normal maps
    // (normalize(n1 + n2 - 1) in the tangent frame). Each texture reads its own UV set: the
    // vertex program's TEXCOORD0, 4, 6, 7, bound by the instance to the segment's stream sets
    // (the import's ffx_texcoords: 0 UV, 1 UV2), plus its ScrollSpeed x GlobalTime (v negated
    // for the export's v flip). The material names no cube map (bound outside it): the map's
    // reflection cube map; without one, env_colour stands in for it.
    // The refraction variant (REFRACTION_ENABLED, drawn after the scene in postTransparent; the
    // bsil00 variant's pixel program) writes mix(the colour buffer at an offset, the toned colour,
    // its alpha). The offset: n' = normalize(8 N.x, N.y, 8 N.z) of the bent normal through
    // WorldView (the instance's world too, as the game does), (-0.0125 n'x, 0.00625 (-dot(N, V))
    // n'y) / max(view depth, 10), clamped to +-0.05, its y faded by clamp((clip y + 0.9) 10);
    // the buffer read at (u + x, 1 - (v + y)) of the y-up screen uv. Its depth test reads the w
    // of the depth buffer (R24G8: 1), so it always passes. Godot's 3D screen texture S is a copy
    // taken before the transparent pass, while the game's buffer F holds the transparent draws
    // too (bsil00's shallow-water layer under the beach sea): a mix over S would wipe them out.
    // So the frame keeps them at offset 0 and gets the shift of what is behind them, D = S(at)
    // - S(here): a x water + (1 - a) x (F + D), in two passes so the signed D survives an 8-bit
    // buffer: this material (premultiplied) adds a x water + (1 - a) max(D, 0), its next pass
    // (REFRACTION_SUB, subtract, drawn after it: PostPriority + 1) takes (1 - a) max(-D, 0).
    private const string WaterBody = @"
uniform vec4 material_colour = vec4(1.0);
uniform vec3 specular_colour = vec3(0.65);
uniform float shininess = 0.59;
uniform float multitex_blend = 0.128;
uniform vec3 env_colour = vec3(0.56, 0.70, 0.80);
uniform bool has_cube = false;
uniform samplerCube cube : filter_linear_mipmap;
uniform sampler2D nml1 : filter_linear_mipmap, repeat_enable;
uniform sampler2D nml2 : filter_linear_mipmap, repeat_enable;
uniform sampler2D tex0 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D tex1 : filter_linear_mipmap_anisotropic, repeat_enable;
// the UV set of tex0, tex1, nml1, nml2 (0: UV, 1: UV2)
uniform vec4 uv_sets = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec2 scroll_tex0 = vec2(0.0);
uniform vec2 scroll_tex1 = vec2(0.0);
uniform vec2 scroll1 = vec2(0.0);
uniform vec2 scroll2 = vec2(0.0);
// the cube map's axes against render space (D3D's left-handed faces in Godot's cube)
uniform vec3 cube_axes = vec3(1.0, 1.0, -1.0);
#ifdef REFRACTION
uniform sampler2D ffx_screen : hint_screen_texture, filter_linear;
#endif
vec2 ffx_scroll(vec2 s, float t) { return vec2(fract(s.x * t), -fract(s.y * t)); }
void fragment() {
	vec3 N = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	vec3 T = (INV_VIEW_MATRIX * vec4(TANGENT, 0.0)).xyz;
	if (dot(T, T) < 1e-6) { T = vec3(1.0, 0.0, 0.0); }
	T = normalize(T);
	vec3 B = cross(T, N);
	vec3 Nw = N;
	vec4 cN = vec4(1.0);
	vec4 spec;
	if (has_cube) {
		vec3 a = texture(nml1, mix(UV, UV2, uv_sets.z) + ffx_scroll(scroll1, ffx_time)).rgb;
		vec3 b = texture(nml2, mix(UV, UV2, uv_sets.w) + ffx_scroll(scroll2, ffx_time)).rgb;
		vec3 n = normalize(a + b - vec3(1.0));
		Nw = normalize(n.x * T + n.y * B + n.z * N);
		vec3 P = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		vec3 I = normalize(P - CAMERA_POSITION_WORLD);
		vec3 R = I - 2.0 * dot(I, Nw) * Nw;
		vec4 cR = texture(cube, R * cube_axes);
		cN = texture(cube, Nw * cube_axes);
		spec = vec4(cR.rgb * shininess * specular_colour, cR.a * shininess);
	} else {
		cN = vec4(env_colour, 1.0);
		spec = vec4(env_colour * shininess * specular_colour, shininess);
	}
	vec4 base = vec4(0.0, 0.0, 0.0, 1.0);
#ifdef TEXTURE
	base += texture(tex0, mix(UV, UV2, uv_sets.x) + ffx_scroll(scroll_tex0, ffx_time));
	base += texture(tex1, mix(UV, UV2, uv_sets.y) + ffx_scroll(scroll_tex1, ffx_time));
#endif
	vec4 c = base * multitex_blend * material_colour * cN + spec;
	c *= COLOR;
#ifdef FOG
	c.rgb = ffx_fog_apply(c.rgb, -VERTEX.z);
#endif
	c = clamp(c * vec4(ffx_colour_scale, 1.0), 0.0, 1.0);
	vec3 o = ffx_out(ffx_curve(c.rgb));
#ifdef REFRACTION
	vec3 Pw = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 V = normalize(CAMERA_POSITION_WORLD - Pw);
	vec3 nf = normalize(vec3(8.0 * Nw.x, Nw.y, 8.0 * Nw.z));
	vec2 nv = (VIEW_MATRIX * (MODEL_MATRIX * vec4(nf, 0.0))).xy;
	vec2 off = clamp(vec2(-0.0125 * nv.x, 0.00625 * -dot(Nw, V) * nv.y) / max(-VERTEX.z, 10.0), -0.05, 0.05);
	off.y *= clamp((abs(PROJECTION_MATRIX[1][1]) * VERTEX.y + 0.9) * 10.0, 0.0, 1.0);
	vec3 d = texture(ffx_screen, SCREEN_UV + vec2(off.x, -off.y)).rgb - texture(ffx_screen, SCREEN_UV).rgb;
#ifdef REFRACTION_SUB
	ALBEDO = (1.0 - c.a) * max(-d, vec3(0.0));
	ALPHA = 1.0;
#else
	ALBEDO = c.a * o + (1.0 - c.a) * max(d, vec3(0.0));
	ALPHA = c.a;
#endif
#else
	ALBEDO = o;
	ALPHA = c.a;
#endif
}
";

    private const string CharBody = @"
uniform sampler2D tex : filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 material_colour = vec4(1.0);
uniform vec4 vertex_colour = vec4(1.0);
uniform float alpha_threshold = 0.5;
// the game's lights (PhyreChrLitShader): GlobalAmbientColor + CharacterAmbientLight
// + the three lights' colour x level x (dot(L, N) x 0.5 + 0.5)^2; L as the game
// keeps it, times ffx_light_sign
global uniform vec3 ffx_ambient;
global uniform mat3 ffx_light_dirs;    // columns: lights 0-2
global uniform mat3 ffx_light_colours; // columns: colour x level
global uniform float ffx_light_sign;
void fragment() {
	vec4 t = texture(tex, UV);
	if (t.a < alpha_threshold) { discard; }
	vec3 n = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	vec3 lit = ffx_ambient;
	for (int i = 0; i < 3; i++) {
		float h = dot(ffx_light_sign * ffx_light_dirs[i], n) * 0.5 + 0.5;
		lit += ffx_light_colours[i] * h * h;
	}
	vec4 c = t * vertex_colour * material_colour;
	vec3 o = min(c.rgb * lit, vec3(1.0));
	ALBEDO = ffx_out(ffx_fog_apply(o, -VERTEX.z));
}
";

    // The 2D layers (phyreprerenderedshader; ffx-rust's docs/formats/scene-maps.md, "How the
    // game draws the 2D layers"). A layer's vertices are its PS2 screen pixels (x, y on the
    // 512 x 448 screen, y down) at its depth z. The game draws them through an orthographic
    // camera that puts the 512 columns across the window and, squeezed for 16:9, 336 of the
    // 448 rows from 112 - 336 b down (b the pan, scene +0xf9bc): clip x = (x - 256) / 256,
    // clip y = (-(y - 224) / 224) x 4/3 + 1/3 - 2b, after a shift (scene +0xfa34, +0xfa38).
    // The depth is the main camera's at view distance 10 z (the shader takes (x, y, -10 z, 1)
    // and its depth from the main projection), so the characters drawn with that camera go
    // behind nearer layers. The colour is the texture x the vertex colour, as it is.
    // Every variant blends (D3D11 blend states of all ten variants): source alpha over the
    // picture, or added (SRC_ALPHA, ONE) with ADDITIVE_BLENDING; it writes depth unless
    // MASK_DEPTH; depth test LESS_EQUAL. The pass the maps use, preTransparentATC, adds
    // alpha to coverage: taken here as a cut at alpha 0.5 for the layers that write depth
    // (cut_alpha), so the cut-outs' holes leave the depth alone.
    // ffx_layer_window: (y offset, y scale, x shift, y shift); ffx_layer_far: the farthest
    // view distance to draw at (inside the camera's far plane).
    private const string LayerBody = @"
uniform sampler2D tex : filter_linear_mipmap, repeat_disable;
uniform vec4 material_colour = vec4(1.0);
uniform float cut_alpha = 0.5;
global uniform vec4 ffx_layer_window;
global uniform float ffx_layer_far;
void vertex() {
	float x = (VERTEX.x - 256.0) / 256.0 + ffx_layer_window.z;
	float y = (-(VERTEX.y - 224.0) / 224.0 + ffx_layer_window.w) * ffx_layer_window.y + ffx_layer_window.x;
	vec4 d = PROJECTION_MATRIX * vec4(0.0, 0.0, -min(10.0 * VERTEX.z, ffx_layer_far), 1.0);
	// the renderer's own clip-space conventions (y flip, depth range) come with its projection
	POSITION = vec4(x * sign(PROJECTION_MATRIX[0][0]), y * sign(PROJECTION_MATRIX[1][1]), d.z / d.w, 1.0);
}
void fragment() {
	vec4 c = texture(tex, UV) * material_colour * COLOR;
	if (c.a < cut_alpha) { discard; }
	ALBEDO = ffx_out(c.rgb);
	ALPHA = c.a;
}
";

    /// <summary>Which way the game's light directions point for the shader's dot(L, N): +1 or -1.</summary>
    public static float LightSign = System.Environment.GetEnvironmentVariable("FFX_LIGHT_SIGN") == "1" ? 1f : -1f;

    private static readonly bool Globals = Register();

    /// <summary>Registers the shaders' global uniforms (before anything sets one).</summary>
    public static void EnsureGlobals() => _ = Globals;

    private static bool Register()
    {
        void Add(string name, RenderingServer.GlobalShaderParameterType type, Variant value)
        {
            RenderingServer.GlobalShaderParameterAdd(name, type, value);
        }
        Add("ffx_colour_scale", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.One);
        Add("ffx_fog_colour", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        Add("ffx_fog", RenderingServer.GlobalShaderParameterType.Vec4, new Vector4(65536, 65537, 1, 0));
        Add("ffx_ambient", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.42f, 0.42f, 0.42f));
        Add("ffx_light_dirs", RenderingServer.GlobalShaderParameterType.Mat3,
            new Basis(new Vector3(-0.4f, 0.8f, 0.45f).Normalized(), Vector3.Zero, Vector3.Zero));
        Add("ffx_light_colours", RenderingServer.GlobalShaderParameterType.Mat3,
            new Basis(new Vector3(0.75f, 0.75f, 0.75f), Vector3.Zero, Vector3.Zero));
        Add("ffx_light_sign", RenderingServer.GlobalShaderParameterType.Float, LightSign);
        Add("ffx_layer_window", RenderingServer.GlobalShaderParameterType.Vec4, new Vector4(1f / 3f - 2f * 0.035f, 4f / 3f, 0, 0));
        Add("ffx_layer_far", RenderingServer.GlobalShaderParameterType.Float, 19000f);
        Add("ffx_time", RenderingServer.GlobalShaderParameterType.Float, 0f);
        return true;
    }

    // made once: a string passed for a StringName makes a new (finalizable) one each call
    private static readonly StringName G_ffx_time = "ffx_time", G_ffx_colour_scale = "ffx_colour_scale", G_ffx_fog_colour = "ffx_fog_colour", G_ffx_fog = "ffx_fog", G_ffx_ambient = "ffx_ambient", G_ffx_light_dirs = "ffx_light_dirs", G_ffx_light_colours = "ffx_light_colours", G_ffx_light_sign = "ffx_light_sign";

    /// <summary>
    /// The shaders' clock (<c>ffx_time</c>): the game's <c>GlobalTime</c> (<c>Look.global_time</c>,
    /// the clock of the scrolling UVs and the water), interpolated between the last two
    /// snapshots by <paramref name="alpha"/>. Across a jump (0 again after a map change, the wrap
    /// at 2400 s, a pause or a load) the newest value is taken as it is. Once a frame.
    /// </summary>
    public static float SetTime(GdFfx.Api.Snapshot? previous, GdFfx.Api.Snapshot? current, float alpha)
    {
        _ = Globals;
        if (current?.Look is not { Valid: true } now)
            return -1f;
        float t = now.GlobalTime;
        if (previous?.Look is { Valid: true } was && was.GlobalTime <= t && t - was.GlobalTime < 0.5f)
            t = Mathf.Lerp(was.GlobalTime, t, alpha);
        RenderingServer.GlobalShaderParameterSet(G_ffx_time, t);
        return t;
    }

    /// <summary>The scene's look from the game-state API, into the shaders' globals.</summary>
    public static void SetLook(GdFfx.Api.Look? l)
    {
        _ = Globals;
        if (l == null || !l.Valid)
            return;
        RenderingServer.GlobalShaderParameterSet(G_ffx_colour_scale, l.ColourScale);
        RenderingServer.GlobalShaderParameterSet(G_ffx_fog_colour, l.FogColour);
        RenderingServer.GlobalShaderParameterSet(G_ffx_fog, new Vector4(l.FogNear, l.FogFar, l.FogCurve, l.FogLimit));
        if (l.Lights.Count == 0)
            return;
        RenderingServer.GlobalShaderParameterSet(G_ffx_ambient, l.Ambient + l.CharacterAmbient);
        Vector3 Dir(int i) => i < l.Lights.Count ? Turn(l.Lights[i]) : Vector3.Zero;
        Vector3 Col(int i) => i < l.Lights.Count ? l.Lights[i].Colour * l.Lights[i].Level : Vector3.Zero;
        // Basis(x, y, z) takes columns
        // (a Basis variant holds native memory: disposed at once, not left to a finalizer)
        Variants.SetGlobal(G_ffx_light_dirs, new Basis(Dir(0), Dir(1), Dir(2)));
        Variants.SetGlobal(G_ffx_light_colours, new Basis(Col(0), Col(1), Col(2)));
        RenderingServer.GlobalShaderParameterSet(G_ffx_light_sign, LightSign);
    }

    /// <summary>A light's direction turned by the map's yaw and pitch: Rx(pitch) Ry(yaw) d.</summary>
    private static Vector3 Turn(GdFfx.Api.Light x)
    {
        var d = x.Direction.Length() > 1e-6f ? x.Direction.Normalized() : Vector3.Zero;
        if (Mathf.Abs(x.Yaw) > 0.001f || Mathf.Abs(x.Pitch) > 0.001f)
            d = new Basis(Vector3.Right, Mathf.DegToRad(x.Pitch)) * (new Basis(Vector3.Up, Mathf.DegToRad(x.Yaw)) * d);
        return d;
    }

    private static readonly Shader Char = MakeShader("unshaded, cull_back", CharBody);

    /// <summary>The map and water shaders made so far, by render modes and defines.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Shader> MapShaders = new();

    private static Shader MapShader(string modes, string body, IEnumerable<string> defines)
    {
        var d = string.Concat(defines.Select(x => $"#define {x}\n"));
        var key = (body == WaterBody ? "water|" : "map|") + modes + "|" + d;
        // any thread: the classic content imports on workers
        lock (MapShaders)
        {
            if (!MapShaders.TryGetValue(key, out var sh))
            {
                MapShaders[key] = sh = MakeShader(modes, body, d);
                var list = defines.ToList();
                bool weight = list.Contains("UV_MORPH") || list.Contains("COLOR_MORPH"), offset = list.Contains("UV_SCROLL");
                if (weight || offset)
                    MorphShaders[sh] = (weight, offset);
            }
            return sh;
        }
    }

    /// <summary>
    /// The render pass a map material draws in, of its effect variant's (the import's
    /// <c>extras.ffx_passes</c>): the maps draw OpaqueNoMSAA, TransparentNoMSAA and, for the
    /// refraction water, postTransparent (ffx-rust's docs/formats/scene.md, "Passes").
    /// </summary>
    private static PassStates? MapPass(MaterialExtras extras)
    {
        if (extras.Passes is not { } passes)
            return null;
        foreach (var want in PassOrder)
            foreach (var x in passes)
                if (x.Name == want)
                    return x;
        return passes.Length > 0 ? passes[0] : null;
    }

    private static readonly string[] PassOrder = ["OpaqueNoMSAA", "TransparentNoMSAA", "postTransparent", "Opaque", "Transparent"];

    /// <summary>
    /// Godot's render modes for a pass's D3D11 states: the cull mode (the game's FRONT, with
    /// clockwise front faces, is Godot's back faces for the exported winding), the blend
    /// (render target 0's factors), depth write and test.
    /// </summary>
    private static (string Modes, bool Blends) PassModes(PassStates? pass, bool forceBlend)
    {
        var modes = new List<string> { "unshaded" };
        if (pass == null)
        {
            modes.Add("cull_back");
            modes.Add(forceBlend ? "blend_mix, depth_draw_never" : "depth_draw_opaque");
            return (string.Join(", ", modes), forceBlend);
        }
        var cull = pass.Cull ?? "BACK";
        bool ccw = pass.FrontCcw ?? false;
        modes.Add(cull switch
        {
            "NONE" => "cull_disabled",
            "FRONT" => ccw ? "cull_front" : "cull_back",
            _ => ccw ? "cull_back" : "cull_front",
        });
        bool blends = forceBlend;
        if (pass.Blend is { } b)
        {
            var (src, dst, op) = (b.Src ?? "", b.Dst ?? "", b.Op ?? "ADD");
            blends = true;
            modes.Add((src, dst, op) switch
            {
                (_, _, "REV_SUBTRACT") => "blend_sub",
                ("SRC_ALPHA" or "ONE", "ONE", _) => "blend_add",
                ("DEST_COLOR", "ZERO", _) or ("ZERO", "SRC_COLOR", _) => "blend_mul",
                ("ONE", "INV_SRC_ALPHA", _) => "blend_premul_alpha",
                _ => "blend_mix",
            });
        }
        else if (forceBlend)
            modes.Add("blend_mix");
        bool write = pass.DepthWrite ?? true;
        modes.Add(!write ? "depth_draw_never" : blends ? "depth_draw_always" : "depth_draw_opaque");
        if (!(pass.DepthTest ?? true) || (pass.DepthFunc ?? "LESS_EQUAL") == "ALWAYS")
            modes.Add("depth_test_disabled");
        return (string.Join(", ", modes), blends);
    }

    /// <summary>Whether the pass's pixel program reads constant <paramref name="name"/> (the import's <c>ps_reads</c>); <paramref name="unknown"/> without the list.</summary>
    private static bool Reads(PassStates? pass, string name, bool unknown) =>
        pass?.PsReads is { } r ? System.Array.IndexOf(r, name) >= 0 : unknown;

    /// <summary>The glTF set a vertex program input reads (the import's <c>ffx_texcoords</c>, <c>ffx_colors</c>).</summary>
    private static int InputSet(System.Collections.Generic.Dictionary<string, int> sets, string input, int fallback) =>
        sets.TryGetValue(input, out var k) ? k : fallback;

    /// <summary>Whether a material draws with the per-instance morph weight (<c>ffx_morph</c>) or UV offset (<c>ffx_uv_offset</c>).</summary>
    public static (bool Weight, bool UvOffset) Morphs(Material? m)
    {
        if (m is not ShaderMaterial { Shader: { } sh })
            return (false, false);
        lock (MapShaders)
            return MorphShaders.TryGetValue(sh, out var x) ? x : (false, false);
    }

    /// <summary>The map shaders with per-instance morph uniforms (<see cref="Morphs"/>).</summary>
    private static readonly System.Collections.Generic.Dictionary<Shader, (bool, bool)> MorphShaders = new();

    /// <summary>The variant's switches (the import's <c>extras.ffx_switches</c>).</summary>
    private static bool Switch(MaterialExtras extras, string name) => extras.Switches.Contains(name);
    private static readonly Shader[] LayerShaders =
    [
        MakeShader("unshaded, cull_disabled, blend_mix, depth_draw_always, shadows_disabled", LayerBody),
        MakeShader("unshaded, cull_disabled, blend_mix, depth_draw_never, shadows_disabled", LayerBody),
        MakeShader("unshaded, cull_disabled, blend_add, depth_draw_always, shadows_disabled", LayerBody),
        MakeShader("unshaded, cull_disabled, blend_add, depth_draw_never, shadows_disabled", LayerBody),
    ];

    /// <summary>The effect variant switches the layers' look depends on.</summary>
    private static readonly string[] LayerSwitches = ["MASK_DEPTH", "ADDITIVE_BLENDING"];

    /// <summary>Whether <paramref name="m"/> is a 2D layer's material (made by <see cref="Apply"/>).</summary>
    public static bool IsLayer(Material? m) => m is ShaderMaterial s && System.Array.IndexOf(LayerShaders, s.Shader) >= 0;

    /// <summary>
    /// A 2D layer's material for the pass: in <c>preTransparentATC</c> (<paramref name="atc"/>)
    /// the layers that write depth cut at alpha 0.5; in <c>preTransparent</c> nothing is cut.
    /// </summary>
    public static void LayerPass(ShaderMaterial m, bool atc)
    {
        bool writes = m.Shader == LayerShaders[0] || m.Shader == LayerShaders[2];
        m.SetShaderParameter(Variants.Name("cut_alpha"), atc && writes ? 0.5f : 0f);
    }

    private static readonly System.Collections.Generic.Dictionary<string, string[]> Switches = new();

    /// <summary>
    /// The layer shader for the material's effect variant (its blend and depth write). The
    /// switches come from the extension, which only answers on Godot's main thread: on a
    /// worker (the background import) an unknown variant gets the plain shader and
    /// <see cref="FixLayers"/> sets the right one once the scene is on the main thread.
    /// </summary>
    private static Shader LayerShader(string effect)
    {
        string[]? on;
        lock (Switches)
            Switches.TryGetValue(effect, out on);
        if (on == null)
        {
            if (OS.GetThreadCallerId() != OS.GetMainThreadId())
                return LayerShaders[0];
            on = GameHost.Instance?.Game?.ClassicEffectSwitches(effect, LayerSwitches) ?? [];
            lock (Switches)
                Switches[effect] = on;
        }
        bool add = System.Array.IndexOf(on, "ADDITIVE_BLENDING") >= 0;
        bool noDepth = System.Array.IndexOf(on, "MASK_DEPTH") >= 0;
        return LayerShaders[(add ? 2 : 0) + (noDepth ? 1 : 0)];
    }

    /// <summary>
    /// The 2D layers' shaders under <paramref name="root"/> by their variants' switches (main
    /// thread: an imported scene built on a worker, <see cref="LayerShader"/>).
    /// </summary>
    public static void FixLayers(Node root)
    {
        foreach (var n in root.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (n is not MeshInstance3D mi || mi.Mesh == null)
                continue;
            for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
                if ((mi.GetSurfaceOverrideMaterial(i) ?? mi.Mesh.SurfaceGetMaterial(i)) is ShaderMaterial sm
                    && IsLayer(sm) && sm.HasMeta(Variants.Name("ffx_effect")))
                {
                    using var effect = sm.GetMeta(Variants.Name("ffx_effect"));
                    var shader = LayerShader(effect.AsString());
                    if (sm.Shader != shader)
                    {
                        sm.Shader = shader;
                        LayerPass(sm, true); // MapLayers sets the map's pass afterwards
                    }
                }
        }
    }

    /// <summary>Materials made, by kind (for tests and the overlay).</summary>
    public static (int Map, int Blend, int Char, int Layer) Made;
    private static readonly object MadeLock = new();

    /// <summary>
    /// Replaces the generated scene's materials with the game's (glTF material i ↔
    /// <paramref name="state"/>'s material i, by its <c>extras</c>, read by <see cref="GltfExtras"/>:
    /// no Godot dictionary, array or string variant made for the JSON's values).
    /// </summary>
    public static void Apply(GltfState state, GltfExtras json, Node root, string? texDir = null)
    {
        var mats = json.Materials;
        if (mats.Length == 0)
            return;
        var godotMats = state.GetMaterials();
        var replace = new System.Collections.Generic.Dictionary<Material, Material>(ReferenceEqualityComparer.Instance);
        var transparent = new HashSet<int>();
        // drawn after the scene (postTransparent): after every other transparent part
        var last = new HashSet<int>();
        for (int i = 0; i < mats.Length && i < godotMats.Count; i++)
        {
            var extras = mats[i];
            if (godotMats[i] is not BaseMaterial3D std)
                continue;
            var effect = extras.Effect?.ToLowerInvariant() ?? "";
            var colour = extras.Floats("MaterialColour", [1, 1, 1, 1]);
            var threshold = extras.Floats("AlphaThreshold", [0])[0];
            ShaderMaterial s;
            Shader? refractionSub = null;
            if (effect.Contains("prerendered"))
            {
                s = new ShaderMaterial { Shader = LayerShader(extras.Effect!) };
                using (var fx = Variant.CreateFrom(extras.Effect!))
                    s.SetMeta(Variants.Name("ffx_effect"), fx);
                LayerPass(s, true);
                lock (MadeLock)
                    Made.Layer++;
            }
            else if (effect.Contains("chrlit") || (effect.Length == 0 && extras.Parameters.ContainsKey("VertexColorOrigin")))
            {
                s = new ShaderMaterial { Shader = Char };
                s.SetShaderParameter(Variants.Name("vertex_colour"), ToColor(extras.Floats("VertexColorOrigin", [1, 1, 1, 1])));
                s.SetShaderParameter(Variants.Name("alpha_threshold"), threshold);
                lock (MadeLock)
                    Made.Char++;
            }
            else
            {
                var water = effect.Contains("water") || effect.Contains("wave");
                var pass = MapPass(extras);
                bool known = pass != null;
                var masked = extras.AlphaMode == "MASK";
                var defines = new List<string>();
                // without the import's states (an older cache): the old guesses
                bool blendSwitch = known ? Switch(extras, "ALPHA_ENABLED") : water;
                // fog where the pixel program reads fgEff, scaled by it: IGNORE_FOG variants still
                // fog in their programs, their materials have fgEff 0
                if (Reads(pass, "fgEff", true))
                    defines.Add("FOG");
                var (modes, blends) = PassModes(pass, !known && water);
                if (!known && !water && masked)
                    modes = modes.Replace("cull_back", "cull_disabled");
                if (water)
                {
                    if (Switch(extras, "TEXTURE_ENABLED")) defines.Add("TEXTURE");
                    bool refraction = Switch(extras, "REFRACTION_ENABLED");
                    if (refraction)
                    {
                        last.Add(i);
                        defines.Add("REFRACTION");
                        modes = modes.Replace("blend_mix", "blend_premul_alpha");
                    }
                    s = new ShaderMaterial { Shader = MapShader(modes, WaterBody, defines) };
                    if (refraction)
                        refractionSub = MapShader(modes.Replace("blend_premul_alpha", "blend_sub"), WaterBody, [.. defines, "REFRACTION_SUB"]);
                    WaterTextures(s, extras, texDir);
                    var sc = extras.Floats("SpecularColor", [0.65f, 0.65f, 0.65f]);
                    s.SetShaderParameter(Variants.Name("specular_colour"), new Vector3(sc[0], sc[1], sc[2]));
                    s.SetShaderParameter(Variants.Name("shininess"), extras.Floats("SpecularShininess", [0.59f])[0]);
                    s.SetShaderParameter(Variants.Name("multitex_blend"), extras.Floats("MultiTexBlendFactor", [0.128f])[0]);
                    lock (MadeLock)
                        Made.Blend++;
                }
                else
                {
                    if (blends || blendSwitch) defines.Add("BLEND");
                    // the alpha test: the alpha-to-coverage kind's OpaqueNoMSAA program reads AlphaThreshold
                    if (known ? Reads(pass, "AlphaThreshold", false) && !blends : masked)
                        defines.Add("ALPHA_TEST");
                    if (effect.Contains("uvanimation")) defines.Add("UV_ANIM");
                    // the morphs (the weights are the instance's: StateView3D); a colour morph whose
                    // COLOR1 reads the first colour stream changes nothing (no CUSTOM0 imported)
                    if (Switch(extras, "UV_MORPH")) defines.Add("UV_MORPH");
                    if (Switch(extras, "VERTEX_COLOR_MORPH") && InputSet(extras.Colors, "COLOR1", 0) > 0) defines.Add("COLOR_MORPH");
                    if (Switch(extras, "UV_SCROLL")) defines.Add("UV_SCROLL");
                    if (InputSet(extras.Texcoords, "TEXCOORD0", 0) == 1) defines.Add("UV0_SET1");
                    // the sampler's address modes (the import's ffx_wrap): Godot has one for both axes
                    if (extras.Wrap.TryGetValue("TextureSampler", out var tw) && tw.Any(x => x == "CLAMP" || x == "BORDER"))
                        defines.Add("TEX_CLAMP");
                    if (blends && !modes.Contains("blend_")) modes += ", blend_mix";
                    s = new ShaderMaterial { Shader = MapShader(modes, MapBody, defines) };
                    var sx = extras.Floats("ScrollSpeed_X", [0f]); var sy = extras.Floats("ScrollSpeed_Y", [0f]);
                    s.SetShaderParameter(Variants.Name("scroll"), new Vector2(sx[0], sy[0]));
                    if (defines.Contains("UV_MORPH"))
                        s.SetShaderParameter(Variants.Name("uv_morph_set"), (float)Math.Clamp(InputSet(extras.Texcoords, "TEXCOORD5", 1), 0, 1));
                    lock (MadeLock)
                        if (blends) Made.Blend++; else Made.Map++;
                }
                var e = extras.Floats("MaterialEmissiveness", [0, 0, 0, 0]);
                if (!Reads(pass, "MaterialEmissiveness", true)) e = [0, 0, 0, 0];
                s.SetShaderParameter(Variants.Name("emissiveness"), new Vector3(e[0], e[1], e[2]));
                s.SetShaderParameter(Variants.Name("alpha_threshold"), threshold);
                var fg = extras.Floats("fgEff", [1, 1, 1]);
                s.SetShaderParameter(Variants.Name("fog_effect"), (fg[0] + fg[1] + fg[2]) / 3f);
                if (blends) transparent.Add(i);
            }
            Variants.SetParameter(s, "tex", std.AlbedoTexture);
            s.SetShaderParameter(Variants.Name("material_colour"), ToColor(colour));
            // the refraction's second pass: the same parameters, its own shader
            if (refractionSub != null && s.Duplicate() is ShaderMaterial sub)
            {
                sub.Shader = refractionSub;
                sub.RenderPriority = PostPriority + 1;
                s.NextPass = sub;
            }
            replace[godotMats[i]] = s;
        }
        Swap(root, replace);
        SortTransparent(root, godotMats, replace, transparent, last);
    }

    /// <summary>
    /// The map's blended surfaces in the game's order: the transparent pass sorts by
    /// m_render_order (ro / 1e8 == 1 first, then ro % 1000, then (ro % 100000) / 1000; ffx-rust's
    /// docs/formats/scene.md, "Sort"), then far to near, after the characters' transparent
    /// parts. Godot sorts transparent draws by render priority, then far to near: each distinct
    /// order gets a priority from 1 up (the characters keep 0), each (material, priority) its own
    /// copy of the material.
    /// </summary>
    private static void SortTransparent(Node root, Godot.Collections.Array<Material> godotMats,
        System.Collections.Generic.Dictionary<Material, Material> replace, HashSet<int> transparent, HashSet<int> last)
    {
        if (transparent.Count == 0)
            return;
        // a glTF mesh's extras: set on the mesh by ClassicLibrary.Load (MeshExtras)
        // m_DObjKind 1 (sky domes, ...): the game puts the bounds in the first class at map init
        static (long Order, bool First) Order(Mesh m)
        {
            var x = MeshExtras.Of(m);
            return (x.RenderOrder ?? 0, x.DobjKind == 1);
        }
        static (int, long, long) Key((long Order, bool First) r) =>
            (r.First || r.Order / 100000000 == 1 ? 0 : 1, r.Order % 1000, r.Order % 100000 / 1000);
        var placed = new List<(MeshInstance3D Mi, (long, bool) Order)>();
        void Walk(Node n)
        {
            if (n is MeshInstance3D mi && mi.Mesh != null)
                placed.Add((mi, Order(mi.Mesh)));
            // by index: GetChildren makes a Godot array (finalizable) a node
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(root);
        var keys = placed.Select(x => Key(x.Order)).Distinct().Order().ToList();
        var copies = new System.Collections.Generic.Dictionary<(Material, int), Material>();
        HashSet<Material> Of(IEnumerable<int> set) =>
            new(set.Where(i => i < godotMats.Count && replace.ContainsKey(godotMats[i])).Select(i => replace[godotMats[i]]), ReferenceEqualityComparer.Instance);
        var blended = Of(transparent);
        var post = Of(last);
        foreach (var (mi, order) in placed)
        {
            int sorted = Math.Clamp(1 + keys.IndexOf(Key(order)), 1, PostPriority - 1);
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                if (mi.GetSurfaceOverrideMaterial(s) is not { } m || !blended.Contains(m))
                    continue;
                int priority = post.Contains(m) ? PostPriority : sorted;
                if (!copies.TryGetValue((m, priority), out var c))
                {
                    c = (Material)m.Duplicate();
                    c.RenderPriority = priority;
                    copies[(m, priority)] = c;
                }
                mi.SetSurfaceOverrideMaterial(s, c);
            }
        }
    }

    /// <summary>
    /// The render priority of the parts the game draws after the scene (postTransparent: the
    /// refraction water; its second pass one more); only the effects come later
    /// (<see cref="EffectDrawView.Priority"/>).
    /// </summary>
    public const int PostPriority = EffectDrawView.Priority - 2;

    /// <summary>Face 0 of cube maps already read by <see cref="MapCube"/>.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TextureFetcher.Pixels> FaceZero = new();

    /// <summary>The textures read so far (locked: maps and characters load on worker threads).</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Texture?> Textures = new();

    /// <summary>A material texture (its path in <c>extras.ffx_textures</c>) from the player's archive: a cube map's six faces, or a 2D texture with mipmaps; null when it cannot be read.</summary>
    private static Texture? ClassicTexture(string path)
    {
        lock (Textures)
            if (Textures.TryGetValue(path, out var known))
                return known;
        Texture? t = null;
        // any thread: the classic content loads on worker threads (TextureFetcher)
        var first = FaceZero.TryRemove(path, out var known0) ? known0 : TextureFetcher.Fetch("face", path, 0);
        if (first is { Faces: 6 })
        {
            // the other five faces asked at once
            var rest = TextureFetcher.FetchMany("face", path, [1, 2, 3, 4, 5]);
            var faces = new Godot.Collections.Array<Image>();
            foreach (var px in new[] { first }.Concat(rest))
                if (px != null)
                {
                    var img = px.ToImage();
                    // the faces come upright like 2D textures; the game uploads a cube's
                    // rows as stored, so D3D's faces are these mirrored vertically
                    img.FlipY();
                    img.GenerateMipmaps();
                    faces.Add(img);
                }
            if (faces.Count == 6)
            {
                var cube = new Cubemap();
                if (cube.CreateFromImages(faces) == Error.Ok)
                    t = cube;
            }
        }
        else if (first != null)
        {
            var img = first.ToImage();
            img.GenerateMipmaps();
            t = ImageTexture.CreateFromImage(img);
        }
        // two loads may read the same texture at once: the first one kept is used
        lock (Textures)
        {
            if (Textures.TryGetValue(path, out var kept))
                return kept;
            Textures[path] = t;
        }
        return t;
    }

    /// <summary>The map's reflection cube map: the first of reflection, reflection1..4 in its texture folder.</summary>
    private static string? MapCube(string? texDir)
    {
        if (texDir == null || GameHost.Instance?.Game == null)
            return null;
        foreach (var n in new[] { "reflection", "reflection1", "reflection2", "reflection3", "reflection4" })
        {
            var path = $"{texDir}{n}.dds";
            if (TextureFetcher.Fetch("face", path, 0) is { Faces: 6 } px)
            {
                // ClassicTexture takes these pixels instead of reading them again
                FaceZero[path] = px;
                return path;
            }
        }
        return null;
    }

    /// <summary>
    /// The water's cube map, normal maps, colour textures, their UV sets (the import's
    /// <c>extras.ffx_texcoords</c>: the glTF set each of the vertex program's TEXCOORD0, 4, 6, 7
    /// reads) and scrolls (falls back to the cube's mean colour without a cube map).
    /// </summary>
    private static void WaterTextures(ShaderMaterial s, MaterialExtras extras, string? texDir)
    {
        float Set(string input, float fallback) =>
            extras.Texcoords.TryGetValue(input, out var k) ? Math.Clamp(k, 0, 1) : fallback;
        s.SetShaderParameter(Variants.Name("uv_sets"), new Vector4(Set("TEXCOORD0", 0), Set("TEXCOORD4", 0), Set("TEXCOORD6", 0), Set("TEXCOORD7", 1)));
        Vector2 Scroll(string name) => new(extras.Floats(name + "_X", [0f])[0], extras.Floats(name + "_Y", [0f])[0]);
        s.SetShaderParameter(Variants.Name("scroll1"), Scroll("ScrollSpeed_nml1"));
        s.SetShaderParameter(Variants.Name("scroll2"), Scroll("ScrollSpeed_nml2"));
        s.SetShaderParameter(Variants.Name("scroll_tex0"), Scroll("ScrollSpeed_tex0"));
        s.SetShaderParameter(Variants.Name("scroll_tex1"), Scroll("ScrollSpeed_tex1"));
        if (!extras.HasTextures)
            return;
        string? Path(string sampler) => extras.Textures.TryGetValue(sampler, out var v) ? v : null;
        if (Path("TextureSampler") is { } t0p && ClassicTexture(t0p) is Texture2D t0)
            Variants.SetParameter(s, "tex0", t0);
        if (Path("TextureSampler1") is { } t1p && ClassicTexture(t1p) is Texture2D t1)
            Variants.SetParameter(s, "tex1", t1);
        // the material names no cube map (its sampler is bound outside the material): the map's
        // own reflection cube map (bsil00 has one, reflection3, the one its water reflects)
        var cubePath = Path("CubeMapSampler") ?? MapCube(texDir);
        if (cubePath is { } cp && ClassicTexture(cp) is Cubemap cube
            && Path("NormalMapSampler") is { } n1p && ClassicTexture(n1p) is { } n1
            && Path("NormalMap2Sampler") is { } n2p && ClassicTexture(n2p) is { } n2)
        {
            Variants.SetParameter(s, "cube", cube);
            Variants.SetParameter(s, "nml1", n1);
            Variants.SetParameter(s, "nml2", n2);
            s.SetShaderParameter(Variants.Name("has_cube"), true);
        }
    }

    private static Color ToColor(float[] c) => new(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1);

    private static void Swap(Node n, System.Collections.Generic.Dictionary<Material, Material> replace)
    {
        if (n is MeshInstance3D mi && mi.Mesh != null)
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var m = mi.GetSurfaceOverrideMaterial(s) ?? mi.Mesh.SurfaceGetMaterial(s);
                if (m != null && replace.TryGetValue(m, out var r))
                    mi.SetSurfaceOverrideMaterial(s, r);
            }
        for (int i = 0, k = n.GetChildCount(); i < k; i++)
            Swap(n.GetChild(i), replace);
    }
}
