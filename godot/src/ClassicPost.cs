using Godot;

namespace GdFfx;

/// <summary>
/// The game's colours under Forward+ (docs/architecture.md, "Colours"). The classic shaders
/// compute on the stored (gamma) values and write the result as it is, as the game does into
/// its <c>R8G8B8A8_UNORM</c> targets; Forward+ keeps a linear buffer and encodes it to sRGB at
/// the end (the tone map pass), which brightened and washed out every picture. With this effect
/// the buffer holds the game's values all along, so every blend in the scene adds and mixes them
/// as the game does; after the transparent pass (Godot's <c>POST_TRANSPARENT</c>) it runs the
/// game's glow on them (<see cref="Glow"/>), then decodes the buffer (sRGB to linear), so the
/// tone map's encode gives the game's values back. The Compatibility renderer writes the shaders'
/// values as they are (no encode) and does not run compositor effects: there the environment's
/// glow stands in (<see cref="StateView3D"/>).
/// <para>
/// The glow, as <c>PostProcessManager_render</c> draws it (ffx-rust's docs/formats/postfx.md,
/// "Glow map", "Glow"; the shaders of <c>phyreglow.fx</c>): the glow map, per pixel
/// <c>t · c / max(c.r, c.g, c.b)</c> with <c>t = saturate((dot(c, (0.3, 0.5, 0.2)) - min(F, N))
/// / (F - N))</c> (F <c>FullGlowThreshold</c>, N <c>NoGlowThreshold</c>), at the picture's size;
/// then at half the size (level 0, the game's 640 x 360) a horizontal and a vertical 7-tap blur
/// (weights 1/3, 4/21, 2/21, 1/21 one texel apart) each times <c>s = spread + 1</c>, then the
/// up pass: a horizontal blur, and a vertical one added to the picture times <c>GlowGain</c>.
/// Each target is 8-bit: every pass's output is clamped to 0..1. The game's levels 1 and 2 are
/// drawn but nothing reads them. The level-0 blurs read the glow map between four of its
/// pixels (their mean); the add reads level 0 at the picture's pixels (bilinear).
/// </para>
/// </summary>
public partial class ClassicPost : CompositorEffect
{
    /// <summary>The glow's values (the map's <c>Look.glow_*</c>; off: no glow).</summary>
    public bool GlowOn;
    public float GlowFull = 1f, GlowNone = 0.5f, GlowGain, GlowSpread;

    private RenderingDevice? _rd;
    private Rid _shader, _pipeline, _sampler;
    private Rid _colour, _a, _b, _setAb, _setBa;
    private readonly byte[] _push = new byte[48];
    private bool _failed;

    private static readonly StringName Context = "ffx_classic_post", NameA = "glow_a", NameB = "glow_b";

    /// <summary>Whether the effect ran in the last frame (for tests).</summary>
    public static bool Ran { get; private set; }

    public ClassicPost()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;
        AccessResolvedColor = true;
    }

    /// <summary>Whether this renderer needs it: Forward+ (its buffer linear, encoded to sRGB at the end).</summary>
    public static bool Needed() => RenderingServer.GetCurrentRenderingMethod() == "forward_plus";

    private const string Code = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(rgba16f, set = 0, binding = 0) uniform image2D colour;
layout(rgba16f, set = 0, binding = 1) uniform image2D dst;
layout(set = 0, binding = 2) uniform sampler2D src;
layout(push_constant, std430) uniform Params {
	ivec4 a; // pass, picture width, height
	ivec4 b; // level 0's width, height
	vec4 c;  // scale, gain, FullGlowThreshold, NoGlowThreshold
} p;

const float W[4] = float[4](0.33333334, 0.19047619, 0.0952381, 0.04761905);

vec3 to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

// phyreglow's GenerateGlowMap
vec3 glow_map(vec3 c) {
	c = clamp(c, 0.0, 1.0);
	float F = p.c.z, N = p.c.w, d = F - N;
	float lum = dot(c, vec3(0.3, 0.5, 0.2)) - min(F, N);
	float t = d != 0.0 ? clamp(lum / d + (d > 0.0 ? 0.0 : 1.0), 0.0, 1.0) : (lum >= 0.0 ? 1.0 : 0.0);
	float m = max(max(c.r, c.g), c.b);
	float on = m > 0.0 ? 1.0 : 0.0;
	return clamp(t * on / (abs(m) - on + 1.0) * c, 0.0, 1.0);
}

vec4 tap(ivec2 at) {
	return texelFetch(src, clamp(at, ivec2(0), p.b.xy - 1), 0);
}

void main() {
	ivec2 at = ivec2(gl_GlobalInvocationID.xy);
	int pass = p.a.x;
	ivec2 full = p.a.yz;
	if (pass == 1 || pass == 2 || pass == 3) {
		if (any(greaterThanEqual(at, p.b.xy))) return;
		vec4 o;
		if (pass == 1) {
			// the glow map read between four of its pixels (the level-0 pixel's centre)
			vec3 s = vec3(0.0);
			for (int y = 0; y < 2; y++)
				for (int x = 0; x < 2; x++)
					s += glow_map(imageLoad(colour, min(at * 2 + ivec2(x, y), full - 1)).rgb);
			o = vec4(s * 0.25, 1.0);
		} else {
			ivec2 step = pass == 2 ? ivec2(1, 0) : ivec2(0, 1);
			o = W[0] * tap(at);
			for (int k = 1; k < 4; k++)
				o += W[k] * (tap(at - k * step) + tap(at + k * step));
			o = clamp(o * vec4(vec3(p.c.x), 1.0), 0.0, 1.0);
		}
		imageStore(dst, at, o);
		return;
	}
	if (any(greaterThanEqual(at, full))) return;
	vec4 c = imageLoad(colour, at);
	vec3 rgb = clamp(c.rgb, 0.0, 1.0);
	if (pass == 4) {
		// the up pass's vertical blur at the picture's pixel, added with GlowGain
		vec2 uv = (vec2(at) + 0.5) / vec2(full);
		float dy = 1.0 / float(p.b.y);
		vec3 g = W[0] * texture(src, uv).rgb;
		for (int k = 1; k < 4; k++)
			g += W[k] * (texture(src, uv - vec2(0.0, k * dy)).rgb + texture(src, uv + vec2(0.0, k * dy)).rgb);
		rgb = clamp(rgb + clamp(g * p.c.y, 0.0, 1.0), 0.0, 1.0);
	}
	imageStore(colour, at, vec4(to_linear(rgb), c.a));
}
";

    private bool Init()
    {
        if (_rd != null || _failed)
            return !_failed;
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null)
        {
            _failed = true;
            return false;
        }
        var src = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = Code };
        var spirv = _rd.ShaderCompileSpirVFromSource(src);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            GD.PrintErr($"ffx: the classic post shader: {spirv.CompileErrorCompute}");
            _failed = true;
            return false;
        }
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        _pipeline = _rd.ComputePipelineCreate(_shader);
        _sampler = _rd.SamplerCreate(new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Linear,
            MagFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        });
        GD.Print("ffx: Forward+: the game's colours and glow by the classic post effect");
        return true;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete && _rd != null)
        {
            if (_pipeline.IsValid) _rd.FreeRid(_pipeline);
            if (_shader.IsValid) _rd.FreeRid(_shader);
            if (_sampler.IsValid) _rd.FreeRid(_sampler);
        }
    }

    /// <summary>The uniform set of (colour, image <paramref name="dst"/>, sampled <paramref name="src"/>).</summary>
    private Rid Set(Rid dst, Rid src)
    {
        var u0 = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u0.AddId(_colour);
        var u1 = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 };
        u1.AddId(dst);
        var u2 = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 2 };
        u2.AddId(_sampler);
        u2.AddId(src);
        var list = new Godot.Collections.Array<RDUniform> { u0, u1, u2 };
        return _rd!.UniformSetCreate(list, _shader, 0);
    }

    private void Pass(int pass, Rid set, Vector2I full, Vector2I level, float scale)
    {
        var rd = _rd!;
        var size = pass is >= 1 and <= 3 ? level : full;
        System.Span<int> ints = stackalloc int[8] { pass, full.X, full.Y, 0, level.X, level.Y, 0, 0 };
        System.Span<float> floats = stackalloc float[4] { scale, GlowGain, GlowFull, GlowNone };
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(ints).CopyTo(_push);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(floats).CopyTo(new System.Span<byte>(_push, 32, 16));
        var cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, _pipeline);
        rd.ComputeListBindUniformSet(cl, set, 0);
        rd.ComputeListSetPushConstant(cl, _push, (uint)_push.Length);
        rd.ComputeListDispatch(cl, (uint)(size.X + 7) / 8, (uint)(size.Y + 7) / 8, 1);
        rd.ComputeListEnd();
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        Ran = false;
        if (effectCallbackType != (int)EffectCallbackTypeEnum.PostTransparent || !Init())
            return;
        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers)
            return;
        var full = buffers.GetInternalSize();
        if (full.X <= 0 || full.Y <= 0)
            return;
        var level = new Vector2I(Mathf.Max(1, full.X / 2), Mathf.Max(1, full.Y / 2));
        // level 0's two targets, kept by the buffers (made again at their new size on a resize);
        // the decode alone binds them too
        if (!buffers.HasTexture(Context, NameA))
        {
            const uint usage = (uint)(RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit);
            foreach (var name in new[] { NameA, NameB })
                buffers.CreateTexture(Context, name, RenderingDevice.DataFormat.R16G16B16A16Sfloat, usage,
                    RenderingDevice.TextureSamples.Samples1, level, 1, 1, true, false);
        }
        Rid a = buffers.GetTexture(Context, NameA), b = buffers.GetTexture(Context, NameB);
        for (uint view = 0; view < buffers.GetViewCount(); view++)
        {
            var colour = buffers.GetColorLayer(view);
            if (colour != _colour || a != _a || b != _b || !_rd!.UniformSetIsValid(_setAb) || !_rd.UniformSetIsValid(_setBa))
            {
                if (_rd!.UniformSetIsValid(_setAb)) _rd.FreeRid(_setAb);
                if (_rd.UniformSetIsValid(_setBa)) _rd.FreeRid(_setBa);
                (_colour, _a, _b) = (colour, a, b);
                _setAb = Set(a, b);
                _setBa = Set(b, a);
            }
            if (GlowOn)
            {
                float s = GlowSpread + 1f;
                Pass(1, _setAb, full, level, 1f); // the glow map into a
                Pass(2, _setBa, full, level, s);  // a -> b, horizontal, x s
                Pass(3, _setAb, full, level, s);  // b -> a, vertical, x s
                Pass(2, _setBa, full, level, 1f); // a -> b, the up pass's horizontal
                Pass(4, _setAb, full, level, 1f); // b's vertical added, then the decode
            }
            else
                Pass(0, _setAb, full, level, 1f);
        }
        Ran = true;
    }
}
