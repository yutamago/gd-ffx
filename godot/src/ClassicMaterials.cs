using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace GdFfx;

/// <summary>
/// The classic content's materials as the game draws them (its own shaders,
/// read from the bytecode: docs/importer.md "Materials"). The glTF import gives
/// Godot's lit standard materials; these replace them:
/// <list type="bullet">
/// <item><b>maps</b> (<c>PhyreDefaultLitShader</c>, <c>PhyreUVAnimationShader</c>): not lit at
/// all, the lighting is baked into the vertex colours; texture x MaterialColour x
/// vertex colour x (1 + emissiveness), then the game's tone curve.</item>
/// <item><b>water</b> (<c>PhyreWaterShader</c>): the game's math with the cube map's mean colour
/// for its reflection (the cube map is not imported yet; see <c>WaterBody</c>).</item>
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
// the game's fog: min(((depth - near) / (far - near))^curve, limit) x fgEff,
// mixed in before the colour scale and the tone curve
uniform float fog_effect = 1.0;
vec3 ffx_fog_apply(vec3 c, float depth) {
	float t = clamp((depth - ffx_fog.x) / max(ffx_fog.y - ffx_fog.x, 0.01), 0.0, 1.0);
	float f = min(pow(t, max(ffx_fog.z, 0.001)), ffx_fog.w) * fog_effect;
	return mix(c, ffx_fog_colour, f);
}

";

    private static Shader MakeShader(string modes, string body) =>
        new() { Code = "shader_type spatial;\nrender_mode " + modes + ";\n" + Curve + body };

    private const string MapOpaqueBody = @"
uniform sampler2D tex : filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 material_colour = vec4(1.0);
uniform vec3 emissiveness = vec3(0.0);
uniform float alpha_threshold = 0.0;
void fragment() {
	vec4 c = texture(tex, UV) * material_colour * COLOR;
	c.rgb *= vec3(1.0) + emissiveness;
	if (alpha_threshold > 0.0 && texture(tex, UV).a < alpha_threshold) { discard; }
	ALBEDO = ffx_out(ffx_curve(ffx_fog_apply(c.rgb, -VERTEX.z) * ffx_colour_scale));
}
";

    // PhyreWaterShader (both bsil00 variants, read from the bytecode): the colour is the cube
    // map's reflection x SpecularShininess x SpecularColor x the vertex colour, then the fog,
    // clrScaleFactor and the tone curve; alpha (cube alpha x SpecularShininess +
    // MultiTexBlendFactor x MaterialColour.a x cube alpha) x the vertex alpha; blended
    // SRC_ALPHA / INV_SRC_ALPHA, double-sided, no depth write. The cube map is not imported
    // yet: its mean colour (env_colour) stands in for the reflection, so the normal maps,
    // which only bend the reflection's lookup, drop out. The refraction variant (the beach
    // sea) mixes the same colour over the scene behind it by the same alpha.
    private const string WaterBody = @"
uniform vec4 material_colour = vec4(1.0);
uniform vec3 specular_colour = vec3(0.65);
uniform float shininess = 0.59;
uniform float multitex_blend = 0.128;
uniform vec3 env_colour = vec3(0.56, 0.70, 0.80);
void fragment() {
	vec4 c;
	c.rgb = env_colour * shininess * specular_colour;
	c.a = shininess + multitex_blend * material_colour.a;
	c *= COLOR;
	c.rgb = ffx_fog_apply(c.rgb, -VERTEX.z);
	c = clamp(c * vec4(ffx_colour_scale, 1.0), 0.0, 1.0);
	ALBEDO = ffx_out(ffx_curve(c.rgb));
	ALPHA = c.a;
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

    /// <summary>Which way the game's light directions point for the shader's dot(L, N): +1 or -1.</summary>
    public static float LightSign = System.Environment.GetEnvironmentVariable("FFX_LIGHT_SIGN") == "1" ? 1f : -1f;

    private static readonly bool Globals = Register();

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
        return true;
    }

    /// <summary>The scene's look from the game-state API, into the shaders' globals.</summary>
    public static void SetLook(GdFfx.Api.Look? l)
    {
        _ = Globals;
        if (l == null || !l.Valid)
            return;
        RenderingServer.GlobalShaderParameterSet("ffx_colour_scale", l.ColourScale);
        RenderingServer.GlobalShaderParameterSet("ffx_fog_colour", l.FogColour);
        RenderingServer.GlobalShaderParameterSet("ffx_fog", new Vector4(l.FogNear, l.FogFar, l.FogCurve, l.FogLimit));
        if (l.Lights.Count == 0)
            return;
        RenderingServer.GlobalShaderParameterSet("ffx_ambient", l.Ambient + l.CharacterAmbient);
        Vector3 Dir(int i) => i < l.Lights.Count ? Turn(l.Lights[i]) : Vector3.Zero;
        Vector3 Col(int i) => i < l.Lights.Count ? l.Lights[i].Colour * l.Lights[i].Level : Vector3.Zero;
        // Basis(x, y, z) takes columns
        RenderingServer.GlobalShaderParameterSet("ffx_light_dirs", new Basis(Dir(0), Dir(1), Dir(2)));
        RenderingServer.GlobalShaderParameterSet("ffx_light_colours", new Basis(Col(0), Col(1), Col(2)));
        RenderingServer.GlobalShaderParameterSet("ffx_light_sign", LightSign);
    }

    /// <summary>A light's direction turned by the map's yaw and pitch: Rx(pitch) Ry(yaw) d.</summary>
    private static Vector3 Turn(GdFfx.Api.Light x)
    {
        var d = x.Direction.Length() > 1e-6f ? x.Direction.Normalized() : Vector3.Zero;
        if (Mathf.Abs(x.Yaw) > 0.001f || Mathf.Abs(x.Pitch) > 0.001f)
            d = new Basis(Vector3.Right, Mathf.DegToRad(x.Pitch)) * (new Basis(Vector3.Up, Mathf.DegToRad(x.Yaw)) * d);
        return d;
    }

    private static readonly Shader MapOpaque = MakeShader("unshaded, cull_back, depth_draw_opaque", MapOpaqueBody);
    private static readonly Shader MapOpaqueTwoSided = MakeShader("unshaded, cull_disabled, depth_draw_opaque", MapOpaqueBody);
    private static readonly Shader Char = MakeShader("unshaded, cull_back", CharBody);
    private static readonly Shader Water = MakeShader("unshaded, cull_disabled, blend_mix, depth_draw_never", WaterBody);

    /// <summary>Materials made, by kind (for tests and the overlay).</summary>
    public static (int Map, int Blend, int Char) Made;

    private static float[] Floats(Dictionary p, string name, float[] fallback)
    {
        if (!p.TryGetValue(name, out var v))
            return fallback;
        var a = v.AsGodotArray();
        var f = new float[a.Count];
        for (int i = 0; i < a.Count; i++)
            f[i] = (float)a[i].AsDouble();
        return f.Length >= fallback.Length ? f : fallback;
    }

    /// <summary>
    /// Replaces the generated scene's materials with the game's (glTF material i ↔
    /// <paramref name="state"/>'s material i, by its <c>extras</c>).
    /// </summary>
    public static void Apply(GltfState state, Node root)
    {
        var json = state.Json;
        if (!json.TryGetValue("materials", out var mv))
            return;
        var mats = mv.AsGodotArray();
        var godotMats = state.GetMaterials();
        var replace = new System.Collections.Generic.Dictionary<Material, Material>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < mats.Count && i < godotMats.Count; i++)
        {
            var m = mats[i].AsGodotDictionary();
            if (godotMats[i] is not BaseMaterial3D std)
                continue;
            var extras = m.TryGetValue("extras", out var ev) ? ev.AsGodotDictionary() : new Dictionary();
            var p = extras.TryGetValue("ffx_parameters", out var pv) ? pv.AsGodotDictionary() : new Dictionary();
            var effect = extras.TryGetValue("ffx_effect", out var fx) ? fx.AsString().ToLowerInvariant() : "";
            var colour = Floats(p, "MaterialColour", [1, 1, 1, 1]);
            var threshold = Floats(p, "AlphaThreshold", [0])[0];
            ShaderMaterial s;
            if (effect.Contains("chrlit") || (effect.Length == 0 && p.ContainsKey("VertexColorOrigin")))
            {
                s = new ShaderMaterial { Shader = Char };
                s.SetShaderParameter("vertex_colour", ToColor(Floats(p, "VertexColorOrigin", [1, 1, 1, 1])));
                s.SetShaderParameter("alpha_threshold", threshold);
                Made.Char++;
            }
            else
            {
                var masked = m.TryGetValue("alphaMode", out var am) && am.AsString() == "MASK";
                var water = effect.Contains("water");
                s = new ShaderMaterial { Shader = water ? Water : masked ? MapOpaqueTwoSided : MapOpaque };
                if (water)
                {
                    var sc = Floats(p, "SpecularColor", [0.65f, 0.65f, 0.65f]);
                    s.SetShaderParameter("specular_colour", new Vector3(sc[0], sc[1], sc[2]));
                    s.SetShaderParameter("shininess", Floats(p, "SpecularShininess", [0.59f])[0]);
                    s.SetShaderParameter("multitex_blend", Floats(p, "MultiTexBlendFactor", [0.128f])[0]);
                }
                var e = Floats(p, "MaterialEmissiveness", [0, 0, 0, 0]);
                s.SetShaderParameter("emissiveness", new Vector3(e[0], e[1], e[2]));
                s.SetShaderParameter("alpha_threshold", masked ? threshold : 0f);
                var fg = Floats(p, "fgEff", [1, 1, 1]);
                s.SetShaderParameter("fog_effect", (fg[0] + fg[1] + fg[2]) / 3f);
                if (water) Made.Blend++; else Made.Map++;
            }
            s.SetShaderParameter("tex", std.AlbedoTexture);
            s.SetShaderParameter("material_colour", ToColor(colour));
            replace[godotMats[i]] = s;
        }
        Swap(root, replace);
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
        foreach (var c in n.GetChildren())
            Swap(c, replace);
    }
}
