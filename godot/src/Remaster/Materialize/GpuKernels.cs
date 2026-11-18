using System.Collections.Generic;

namespace GdFfx.Remaster.Materialize;

/// <summary>
/// The kernels as canvas-item fragment shaders (Godot's shading language; both renderers): the
/// same math as <see cref="CpuBackend"/>'s C# (held together by the parity check). Every pass
/// draws one full-size rectangle into its own offscreen target; a pixel is <c>P</c> (integer),
/// its centre <c>uv</c>; inputs are <c>t0</c> to <c>t8</c>, read exactly (<c>fet</c>: texel
/// fetch, wrapped or clamped) or bilinearly (<c>bil</c>: the hardware's filter, the coordinate
/// clamped to the texel centres when the texture does not tile).
/// </summary>
public static class GpuKernels
{
    private const string Header = @"shader_type canvas_item;
render_mode blend_disabled, unshaded;
uniform vec2 size = vec2(1.0);
uniform bool wrap = true;
uniform sampler2D t0 : filter_linear, repeat_enable;
uniform sampler2D t1 : filter_linear, repeat_enable;
uniform sampler2D t2 : filter_linear, repeat_enable;
uniform sampler2D t3 : filter_linear, repeat_enable;
uniform sampler2D t4 : filter_linear, repeat_enable;
uniform sampler2D t5 : filter_linear, repeat_enable;
uniform sampler2D t6 : filter_linear, repeat_enable;
uniform sampler2D t7 : filter_linear, repeat_enable;
uniform sampler2D t8 : filter_linear, repeat_enable;

vec4 fet(sampler2D t, ivec2 p) {
	ivec2 s = textureSize(t, 0);
	if (wrap) { p = (p + s * 64) % s; } else { p = clamp(p, ivec2(0), s - ivec2(1)); }
	return texelFetch(t, p, 0);
}
vec4 bil(sampler2D t, vec2 c) {
	if (!wrap) { vec2 h = 0.5 / vec2(textureSize(t, 0)); c = clamp(c, h, vec2(1.0) - h); }
	return texture(t, c);
}
float lin1(float c) { return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4); }
vec3 lin(vec3 c) { return vec3(lin1(c.r), lin1(c.g), lin1(c.b)); }
float enc1(float l) { if (l <= 0.0) return 0.0; if (l >= 1.0) return 1.0; return l <= 0.0031308 ? l * 12.92 : 1.055 * pow(l, 1.0 / 2.4) - 0.055; }
float lumlin(vec3 l) { return 0.2126 * l.r + 0.7152 * l.g + 0.0722 * l.b; }
float lum(vec3 srgb) { return enc1(lumlin(lin(srgb))); }
vec3 hsl(vec3 c) {
	float mx = max(c.r, max(c.g, c.b)); float mn = min(c.r, min(c.g, c.b));
	float l = (mx + mn) * 0.5; float d = mx - mn; float den = 1.0 - abs(2.0 * l - 1.0);
	float s = (d <= 1e-6 || den <= 1e-6) ? 0.0 : min(1.0, d / den);
	float h = 0.0;
	if (d > 1e-6) {
		if (mx == c.r) { h = (c.g - c.b) / d; } else if (mx == c.g) { h = 2.0 + (c.b - c.r) / d; } else { h = 4.0 + (c.r - c.g) / d; }
		h /= 6.0; if (h < 0.0) h += 1.0;
	}
	return vec3(h, s, l);
}
float sstep(float lo, float hi, float x) {
	if (hi <= lo) return x >= lo ? 1.0 : 0.0;
	float t = clamp((x - lo) / (hi - lo), 0.0, 1.0); return t * t * (3.0 - 2.0 * t);
}
float cmask(vec3 c, vec3 s, float wh, float ws, float wl, float lo, float hi) {
	vec3 a = hsl(c); vec3 b = hsl(s);
	float dh = abs(a.x - b.x); dh = min(dh, 1.0 - dh);
	float hs = 1.0 - 2.0 * dh; float ss = 1.0 - abs(a.y - b.y); float ls = 1.0 - abs(a.z - b.z);
	float whe = wh * min(1.0, 4.0 * min(a.y, b.y));
	float sim = (whe * hs + ws * ss + wl * ls) / max(1e-4, whe + ws + wl);
	return sstep(lo, hi, sim);
}
float ramp(float v, float th, float soft) {
	float lo = th - soft / 2.0; float hi = th + soft / 2.0;
	float t = hi <= lo ? (v >= th ? 1.0 : 0.0) : clamp((v - lo) / (hi - lo), 0.0, 1.0);
	return t * t * (3.0 - 2.0 * t);
}
float cbrt1(float x) { return sign(x) * pow(abs(x), 1.0 / 3.0); }
vec3 oklab(vec3 c) {
	float l = cbrt1(0.4122214708 * c.r + 0.5363325363 * c.g + 0.0514459929 * c.b);
	float m = cbrt1(0.2119034982 * c.r + 0.6806995451 * c.g + 0.1073969566 * c.b);
	float s = cbrt1(0.0883024619 * c.r + 0.2817188376 * c.g + 0.6299787005 * c.b);
	return vec3(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s, 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
		0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
}
vec3 from_oklab(vec3 c) {
	float l = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
	float m = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
	float s = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;
	l = l * l * l; m = m * m * m; s = s * s * s;
	return vec3(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s, -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
		-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
}
uint hash1(uint x) { x ^= x >> 16u; x *= 2146121005u; x ^= x >> 15u; x *= 2221713035u; x ^= x >> 16u; return x; }
float rnd(ivec2 p, uint salt) { return float(hash1(uint(p.x) + hash1(uint(p.y) + hash1(salt))) >> 8u) / 16777216.0; }
";

    private static readonly Dictionary<string, string> Bodies = new()
    {
        ["const"] = @"uniform vec4 u_value;
vec4 run(ivec2 P, vec2 uv) { return u_value; }",
        ["copy"] = @"vec4 run(ivec2 P, vec2 uv) { return fet(t0, P); }",
        ["lanczos"] = @"uniform int u_dir; uniform int u_taps; uniform int u_mode; uniform float u_scale; uniform float u_width; uniform float u_support;
float lk(float x) { x = abs(x); if (x < 1e-6) return 1.0; if (x >= 3.0) return 0.0; float px = 3.14159265358979 * x; return 3.0 * sin(px) * sin(px / 3.0) / (px * px); }
vec4 run(ivec2 P, vec2 uv) {
	ivec2 ts = textureSize(t0, 0);
	int oi = u_dir == 0 ? P.x : P.y; int n = u_dir == 0 ? ts.x : ts.y;
	float center = (float(oi) + 0.5) * u_scale - 0.5;
	int first = int(floor(center - u_support)) + 1;
	vec4 sum = vec4(0.0); float ws = 0.0;
	for (int k = 0; k < u_taps; k++) {
		float wk = lk((float(first + k) - center) / u_width);
		int i = clamp(first + k, 0, n - 1);
		vec4 v = texelFetch(t0, u_dir == 0 ? ivec2(i, P.y) : ivec2(P.x, i), 0);
		if (u_mode == 0) { v = vec4(lin(v.rgb) * v.a, v.a); }
		sum += wk * v; ws += wk;
	}
	vec4 r = ws != 0.0 ? sum / ws : vec4(0.0);
	if (u_mode == 1) {
		float a = clamp(r.a, 0.0, 1.0); float m = a > 1.0 / 1024.0 ? 1.0 / a : 0.0;
		r = vec4(enc1(clamp(r.r * m, 0.0, 1.0)), enc1(clamp(r.g * m, 0.0, 1.0)), enc1(clamp(r.b * m, 0.0, 1.0)), a);
	}
	return r;
}",
        ["lum"] = @"vec4 run(ivec2 P, vec2 uv) { return vec4(lum(fet(t0, P).rgb), 0.0, 0.0, 1.0); }",
        ["reduce"] = @"vec4 run(ivec2 P, vec2 uv) {
	float k[4] = float[4](1.0, 3.0, 3.0, 1.0);
	vec4 s = vec4(0.0);
	for (int j = 0; j < 4; j++) { for (int i = 0; i < 4; i++) { s += k[i] * k[j] * fet(t0, 2 * P + ivec2(i - 1, j - 1)); } }
	return s / 64.0;
}",
        ["gauss"] = @"uniform int u_dir; uniform int u_taps; uniform float u_sigma;
vec4 run(ivec2 P, vec2 uv) {
	vec4 s = vec4(0.0); float tot = 0.0;
	for (int k = -u_taps; k <= u_taps; k++) {
		float w = exp(-float(k * k) / (2.0 * u_sigma * u_sigma));
		s += w * fet(t0, P + (u_dir == 0 ? ivec2(k, 0) : ivec2(0, k))); tot += w;
	}
	return s / tot;
}",
        ["wmean8"] = @"vec4 run(ivec2 P, vec2 uv) {
	ivec2 ts = textureSize(t0, 0);
	vec3 acc = vec3(0.0); float wa = 0.0; int cnt = 0;
	for (int j = 0; j < 8; j++) { for (int i = 0; i < 8; i++) {
		ivec2 q = P * 8 + ivec2(i, j);
		if (q.x < ts.x && q.y < ts.y) { vec4 v = texelFetch(t0, q, 0); acc += v.rgb * v.a; wa += v.a; cnt++; }
	} }
	return vec4(wa > 0.0 ? acc / wa : vec3(0.0), cnt > 0 ? wa / float(cnt) : 0.0);
}",
        ["alb_y"] = @"vec4 run(ivec2 P, vec2 uv) { return vec4(log(max(1e-3, lumlin(lin(fet(t0, P).rgb)))), 0.0, 0.0, 1.0); }",
        ["alb_chr"] = @"vec4 run(ivec2 P, vec2 uv) { vec4 c = fet(t0, P); vec3 l = lin(c.rgb); float y = max(1e-3, lumlin(l)); return vec4(l / y, 0.02 + c.a); }",
        ["resid2"] = @"vec4 run(ivec2 P, vec2 uv) { float d = fet(t0, P).r - bil(t1, uv).r; return vec4(d * d, 0.0, 0.0, 1.0); }",
        ["alb_ab"] = @"uniform float u_eps;
vec4 run(ivec2 P, vec2 uv) { float m = bil(t0, uv).r; float v = max(0.0, bil(t1, uv).r); float a = v / (v + u_eps); return vec4(a, m - a * m, 0.0, 1.0); }",
        ["ao"] = @"uniform float u_radius; uniform float u_depth; uniform float u_nshare; uniform int u_dirs; uniform int u_first; uniform int u_count; uniform bool u_bent;
vec4 run(ivec2 P, vec2 uv) {
	float h0 = bil(t0, uv).r;
	ivec2 ns = textureSize(t1, 0);
	vec3 n = (ns.x == 1 && ns.y == 1) ? vec3(0.5, 0.5, 1.0) : fet(t1, P).rgb;
	float nx = n.r * 2.0 - 1.0; float ny = -(n.g * 2.0 - 1.0); float nz = n.b * 2.0 - 1.0;
	float r1 = rnd(P, 1u); float r2 = rnd(P, 2u);
	float acc = 0.0; vec3 b = vec3(0.0);
	for (int i = u_first; i < u_first + u_count; i++) {
		float th = 6.2831853 * (float(i) + r1) / float(u_dirs);
		vec2 d = vec2(cos(th), sin(th));
		float best = 0.0;
		for (int j = 0; j < 12; j++) {
			float dist = max(1.0, u_radius * (float(j) + r2) / 12.0);
			vec2 q = vec2(P) + 0.5 + d * dist;
			best = max(best, (bil(t0, q / size).r - h0) * u_depth / dist);
		}
		float oh = best / sqrt(1.0 + best * best);
		float sl = max(0.0, -(nx * d.x + ny * d.y) / max(0.05, nz));
		float on = sl / sqrt(1.0 + sl * sl);
		acc += oh + (on - oh) * u_nshare;
		if (u_bent) {
			float a = atan(best); float hz = (1.0 - oh * oh) * 0.5;
			b += vec3(hz * d.x, hz * d.y, (1.5707963 - a) * 0.5 + sin(2.0 * a) * 0.25);
		}
	}
	float inv = u_count > 0 ? 1.0 / float(u_count) : 0.0;
	return vec4(acc * inv, b.x * inv, b.y * inv, u_bent ? b.z * inv : 1.0);
}",
        ["alb_o1"] = @"uniform float u_shading; uniform float u_ek; uniform float u_occ; uniform float u_cav;
vec4 run(ivec2 P, vec2 uv) {
	float y = fet(t0, P).r; float o = y;
	if (u_shading > 0.0) {
		float m = bil(t1, uv).r; float light = m;
		if (u_ek > 0.0) { vec4 ab = bil(t2, uv); light = m + u_ek * ((ab.r * y + ab.g) - m); }
		o -= u_shading * light;
	}
	if (u_occ > 0.0) { o -= u_occ * log(max(0.2, clamp(1.0 - fet(t3, P).r, 0.0, 1.0))); }
	if (u_cav > 0.0) { float d = clamp((bil(t5, uv).r - bil(t4, uv).r) * 8.0, 0.0, 1.0); o -= u_cav * log(1.0 - 0.5 * d); }
	return vec4(o, 0.0, 0.0, 1.0);
}",
        ["alb_o2"] = @"uniform float u_hi; uniform float u_dark; uniform float u_knee;
vec4 run(ivec2 P, vec2 uv) {
	float o = fet(t0, P).r;
	if (u_hi > 0.0 || u_dark > 0.0) {
		float ar = bil(t1, uv).r;
		float over = o - ar - u_knee;
		if (u_hi > 0.0 && over > 0.0) { o -= u_hi * (over - over / (1.0 + 4.0 * over)); }
		float under = ar - o - u_knee;
		if (u_dark > 0.0 && under > 0.0) { o += u_dark * (under - under / (1.0 + 4.0 * under)); }
	}
	return vec4(fet(t2, P).r - o, o, 0.0, 0.02 + fet(t3, P).a);
}",
        ["albedo"] = @"uniform float u_flatten; uniform float u_neutral;
vec4 run(ivec2 P, vec2 uv) {
	vec4 c = fet(t0, P);
	vec3 l = lin(c.rgb);
	float shift = texelFetch(t2, ivec2(0), 0).r;
	vec3 gm = texelFetch(t3, ivec2(0), 0).rgb;
	vec3 f = vec3(1.0);
	if (u_flatten > 0.0) { vec3 b = bil(t4, uv).rgb; f = pow(gm / max(vec3(1e-3), b), vec3(u_flatten)); }
	if (u_neutral > 0.0) { f *= pow(vec3(1.0) / max(vec3(1e-3), gm), vec3(u_neutral)); }
	vec3 rr = l * f;
	float ly = lumlin(rr);
	float k = ly > 1e-6 ? exp(fet(t1, P).g + shift) / ly : 0.0;
	float mx = max(rr.r, max(rr.g, rr.b)) * k;
	if (mx > 1.0) k /= mx;
	return vec4(enc1(rr.r * k), enc1(rr.g * k), enc1(rr.b * k), c.a);
}",
        ["oklab_lc"] = @"vec4 run(ivec2 P, vec2 uv) { vec4 c = fet(t0, P); vec3 lab = oklab(lin(c.rgb)); return vec4(lab.x, length(lab.yz), 0.0, 0.02 + c.a); }",
        ["colour_match"] = @"uniform float u_match;
vec4 run(ivec2 P, vec2 uv) {
	vec4 c = fet(t0, P);
	if (u_match <= 0.0) return c;
	vec4 s = texelFetch(t1, ivec2(0), 0); vec4 p = texelFetch(t2, ivec2(0), 0);
	float dl = u_match * (s.r - p.r);
	float fc = 1.0 + u_match * (clamp(s.g / max(1e-4, p.g), 0.0, 4.0) - 1.0);
	vec3 lab = oklab(lin(c.rgb));
	lab = vec3(lab.x + dl, lab.y * fc, lab.z * fc);
	vec3 l = from_oklab(lab);
	return vec4(enc1(clamp(l.r, 0.0, 1.0)), enc1(clamp(l.g, 0.0, 1.0)), enc1(clamp(l.b, 0.0, 1.0)), c.a);
}",
        ["h_grey"] = @"uniform vec4 u_s1; uniform vec4 u_s2; uniform vec4 u_s3; uniform float u_v1; uniform float u_v2; uniform float u_v3; uniform float u_blend;
uniform float u_mh; uniform float u_ms; uniform float u_ml; uniform float u_lo; uniform float u_hi;
vec4 run(ivec2 P, vec2 uv) {
	vec3 c = fet(t0, P).rgb;
	float g = lum(c);
	if (u_s1.a > 0.0) g += (u_v1 - g) * cmask(c, u_s1.rgb, u_mh, u_ms, u_ml, u_lo, u_hi) * u_blend;
	if (u_s2.a > 0.0) g += (u_v2 - g) * cmask(c, u_s2.rgb, u_mh, u_ms, u_ml, u_lo, u_hi) * u_blend;
	if (u_s3.a > 0.0) g += (u_v3 - g) * cmask(c, u_s3.rgb, u_mh, u_ms, u_ml, u_lo, u_hi) * u_blend;
	return vec4(g, 0.0, 0.0, 1.0);
}",
        ["h_combine"] = @"uniform float u_bw[7]; uniform float u_bc[7]; uniform float u_gain_amp; uniform bool u_invert; uniform float u_contrast; uniform float u_bias; uniform float u_gain;
vec4 run(ivec2 P, vec2 uv) {
	float a = bil(t7, uv).r;
	float g[7] = float[7](bil(t0, uv).r, bil(t1, uv).r, bil(t2, uv).r, bil(t3, uv).r, bil(t4, uv).r, bil(t5, uv).r, bil(t6, uv).r);
	float sum = 0.0; float ws = 0.0;
	for (int k = 0; k < 7; k++) { sum += u_bw[k] * u_bc[k] * (g[k] - a); ws += u_bw[k]; }
	float h = 0.5 + u_gain_amp * sum / max(1e-4, ws);
	if (u_invert) h = 1.0 - h;
	h = clamp((h - 0.5) * u_contrast + 0.5 + u_bias, 0.0, 1.0);
	if (u_gain != 0.0) { float k = pow(4.0, u_gain); h = h < 0.5 ? 0.5 * pow(2.0 * h, k) : 1.0 - 0.5 * pow(2.0 - 2.0 * h, k); }
	return vec4(h, h, h, 1.0);
}",
        ["n_slope"] = @"uniform float u_k; uniform bool u_scharr; uniform float u_shape; uniform float u_lx; uniform float u_ly; uniform float u_shape_k;
float hh(ivec2 P, int dx, int dy) { return bil(t0, (vec2(P) + 0.5 + vec2(float(dx), float(dy))) / size).r; }
vec4 run(ivec2 P, vec2 uv) {
	float side = u_scharr ? 3.0 : 1.0; float mid = u_scharr ? 10.0 : 2.0;
	float gx = side * (hh(P, 1, -1) - hh(P, -1, -1)) + mid * (hh(P, 1, 0) - hh(P, -1, 0)) + side * (hh(P, 1, 1) - hh(P, -1, 1));
	float gy = side * (hh(P, -1, 1) - hh(P, -1, -1)) + mid * (hh(P, 0, 1) - hh(P, 0, -1)) + side * (hh(P, 1, 1) - hh(P, 1, -1));
	vec2 s = vec2(gx * u_k, gy * u_k);
	if (u_shape > 0.0) {
		float hp = fet(t1, P).r - bil(t2, uv).r;
		vec2 d = vec2(u_lx, u_ly);
		float along = -hp * u_shape_k; float cur = dot(s, d);
		s += u_shape * (along - cur) * d;
	}
	return vec4(s, 0.0, 1.0);
}",
        ["n_combine"] = @"uniform float u_bw[7]; uniform float u_ang; uniform float u_contrast;
vec4 run(ivec2 P, vec2 uv) {
	vec2 b[7] = vec2[7](bil(t0, uv).rg, bil(t1, uv).rg, bil(t2, uv).rg, bil(t3, uv).rg, bil(t4, uv).rg, bil(t5, uv).rg, bil(t6, uv).rg);
	float ws = 0.0; vec2 s = vec2(0.0);
	for (int i = 0; i < 7; i++) { ws += u_bw[i]; s += u_bw[i] * b[i]; }
	if (ws < 1e-4) { s = b[0]; } else { s /= ws; }
	float len = length(s);
	if (u_ang > 0.0 && len > 1e-6) { s = mix(s, s / len, u_ang * sstep(0.0, 0.1, len)); }
	vec3 n = normalize(vec3(-s, 1.0));
	if (u_contrast != 1.0) {
		n = vec3(n.xy * u_contrast, pow(max(n.z, 0.0), u_contrast));
		n = dot(n, n) > 1e-12 ? normalize(n) : vec3(0.0, 0.0, 1.0);
	}
	return vec4(n.x * 0.5 + 0.5, -n.y * 0.5 + 0.5, n.z * 0.5 + 0.5, 1.0);
}",
        ["curv"] = @"uniform float u_s;
vec4 run(ivec2 P, vec2 uv) {
	float div = (fet(t0, P + ivec2(1, 0)).r - fet(t0, P - ivec2(1, 0)).r) - (fet(t0, P + ivec2(0, 1)).g - fet(t0, P - ivec2(0, 1)).g);
	return vec4(clamp(0.5 + div * u_s * 0.5, 0.0, 1.0), 0.0, 0.0, 1.0);
}",
        ["ao_sum"] = @"uniform float u_counts[8]; uniform int u_done;
vec4 run(ivec2 P, vec2 uv) {
	vec4 p[8] = vec4[8](fet(t0, P), fet(t1, P), fet(t2, P), fet(t3, P), fet(t4, P), fet(t5, P), fet(t6, P), fet(t7, P));
	vec4 s = vec4(0.0); float n = 0.0;
	for (int c = 0; c < 8; c++) { if (c < u_done) { s += p[c] * u_counts[c]; n += u_counts[c]; } }
	return n > 0.0 ? s / n : vec4(0.0);
}",
        ["ao_out"] = @"uniform float u_strength; uniform float u_power; uniform float u_bias;
vec4 run(ivec2 P, vec2 uv) {
	float a = clamp(1.0 - u_strength * fet(t0, P).r, 0.0, 1.0);
	a = clamp(pow(a, u_power) + u_bias, 0.0, 1.0);
	return vec4(a, a, a, 1.0);
}",
        ["rough"] = @"uniform float u_base; uniform float u_range; uniform float u_dw; uniform bool u_invert;
uniform vec4 u_s1; uniform vec4 u_s2; uniform vec4 u_s3; uniform float u_v1; uniform float u_v2; uniform float u_v3;
uniform float u_mh; uniform float u_ms; uniform float u_ml; uniform float u_lo; uniform float u_hi;
uniform float u_metal_r; uniform float u_contrast; uniform float u_bias; uniform float u_overlay; uniform float u_wear; uniform bool u_ai;
vec4 run(ivec2 P, vec2 uv) {
	float l = fet(t1, P).r; float v = max(0.0, bil(t2, uv).r);
	float detail = v < 1e-6 ? 0.0 : min(1.0, sqrt(v) * 4.0);
	float t = (1.0 - u_dw) * (1.0 - l) + u_dw * detail;
	float r = u_ai ? fet(t6, P).r : clamp(u_base + u_range * (t - 0.5), 0.0, 1.0);
	if (u_invert) r = 1.0 - r;
	vec3 c = fet(t0, P).rgb;
	if (u_s1.a > 0.0) r += (u_v1 - r) * cmask(c, u_s1.rgb, u_mh, u_ms, u_ml, u_lo, u_hi);
	if (u_s2.a > 0.0) r += (u_v2 - r) * cmask(c, u_s2.rgb, u_mh, u_ms, u_ml, u_lo, u_hi);
	if (u_s3.a > 0.0) r += (u_v3 - r) * cmask(c, u_s3.rgb, u_mh, u_ms, u_ml, u_lo, u_hi);
	r += (u_metal_r - r) * fet(t4, P).r;
	r = (r - 0.5) * u_contrast + 0.5 + u_bias;
	if (u_overlay != 0.0) r *= 1.0 + (l - bil(t3, uv).r) * u_overlay;
	if (u_wear > 0.0) r -= u_wear * max(0.0, (fet(t5, P).r - 0.5) * 2.0);
	r = clamp(r, 0.0, 1.0);
	return vec4(r, r, r, 1.0);
}",
        ["metal"] = @"uniform float u_value; uniform bool u_colour_mode; uniform vec4 u_smp; uniform float u_threshold; uniform float u_softness; uniform bool u_invert;
uniform float u_contrast; uniform float u_bias; uniform float u_overlay;
uniform float u_mh; uniform float u_ms; uniform float u_ml; uniform float u_lo; uniform float u_hi;
vec4 run(ivec2 P, vec2 uv) {
	vec3 c = bil(t0, uv).rgb;
	float m;
	if (u_colour_mode) { m = cmask(c, u_smp.rgb, u_mh, u_ms, u_ml, u_lo, u_hi); }
	else if (u_threshold > 1.0) { m = 1.0; }
	else { m = ramp(lum(c), u_threshold, u_softness); if (u_invert) m = 1.0 - m; }
	float v = m * u_value;
	v = (v - 0.5) * u_contrast + 0.5 + u_bias;
	if (u_overlay != 0.0) v *= 1.0 + (fet(t1, P).r - bil(t2, uv).r) * u_overlay;
	v = clamp(v, 0.0, 1.0);
	return vec4(v, v, v, 1.0);
}",
        ["orm"] = @"vec4 run(ivec2 P, vec2 uv) { return vec4(fet(t0, P).r, fet(t1, P).r, fet(t2, P).r, 1.0); }",
        ["emis"] = @"uniform float u_threshold; uniform float u_softness;
vec4 run(ivec2 P, vec2 uv) { vec4 c = fet(t0, P); float m = ramp(max(c.r, max(c.g, c.b)), u_threshold, u_softness); return vec4(c.rgb * m, 1.0); }",
        ["mask_view"] = @"uniform vec4 u_smp; uniform float u_mh; uniform float u_ms; uniform float u_ml; uniform float u_lo; uniform float u_hi;
vec4 run(ivec2 P, vec2 uv) { float m = u_smp.a > 0.0 ? cmask(bil(t0, uv).rgb, u_smp.rgb, u_mh, u_ms, u_ml, u_lo, u_hi) : 0.0; return vec4(m, m, m, 1.0); }",
        ["bent_out"] = @"uniform float u_strength; uniform float u_from_normals;
vec3 snorm(vec3 v) { return dot(v, v) > 1e-12 ? normalize(v) : vec3(0.0, 0.0, 1.0); }
vec4 run(ivec2 P, vec2 uv) {
	vec3 b = fet(t0, P).gba;
	float l = length(b);
	b = l > 1e-6 ? b / l : vec3(0.0, 0.0, 1.0);
	ivec2 ns = textureSize(t1, 0);
	vec3 n = (ns.x == 1 && ns.y == 1) ? vec3(0.5, 0.5, 1.0) : fet(t1, P).rgb;
	vec3 nn = vec3(n.r * 2.0 - 1.0, -(n.g * 2.0 - 1.0), n.b * 2.0 - 1.0);
	b = snorm(b + u_from_normals * (nn - vec3(0.0, 0.0, 1.0)));
	b = snorm(mix(vec3(0.0, 0.0, 1.0), b, u_strength));
	return vec4(b.x * 0.5 + 0.5, -b.y * 0.5 + 0.5, b.z * 0.5 + 0.5, 1.0);
}",
        ["seam"] = @"uniform int u_dir; uniform float u_o; uniform float u_k; uniform float u_s; uniform bool u_normal;
vec4 renorm(vec4 c) { vec3 v = c.rgb * 2.0 - 1.0; v = dot(v, v) > 1e-12 ? normalize(v) : vec3(0.0, 0.0, 1.0); return vec4(v * 0.5 + 0.5, c.a); }
vec4 run(ivec2 P, vec2 uv) {
	int n = u_dir == 0 ? int(size.x) : int(size.y);
	int i = u_dir == 0 ? P.x : P.y;
	ivec2 Q = u_dir == 0 ? ivec2((i + n / 2) % n, P.y) : ivec2(P.x, (i + n / 2) % n);
	float dist = min(float(i) + 0.5, float(n - i) - 0.5);
	float ramp = clamp(2.0 * (dist - 1.0) / max(1.0, u_o - 1.0) - 1.0, -1.0, 1.0);
	vec4 a = fet(t0, P);
	if (ramp >= 1.0) return a;
	vec4 b = fet(t0, Q);
	float wa = sstep(-u_s, u_s, (fet(t1, P).r - fet(t1, Q).r) * u_k + ramp * (u_s + u_k));
	vec4 c = mix(b, a, wa);
	return u_normal ? renorm(c) : c;
}",
        ["scatter"] = @"uniform int u_grid; uniform float u_scale; uniform float u_rot; uniform float u_rrot; uniform float u_jitter; uniform int u_seed; uniform float u_soft; uniform float u_k; uniform bool u_normal;
vec4 renorm(vec4 c) { vec3 v = c.rgb * 2.0 - 1.0; v = dot(v, v) > 1e-12 ? normalize(v) : vec3(0.0, 0.0, 1.0); return vec4(v * 0.5 + 0.5, c.a); }
vec4 run(ivec2 P, vec2 uv) {
	int n = u_grid; float cell = 1.0 / float(n); float sz = u_scale * cell;
	uint salt = uint(u_seed) * 8u;
	float lo = sz * 0.75 < 0.5 ? sz * 0.75 : 0.5; float span = 1.0 - 2.0 * lo;
	int jq = int(floor(uv.y * float(n)));
	vec4 acc = vec4(0.0); float wsum = 0.0;
	for (int dj = -2; dj <= 2; dj++) {
		int j = jq + dj;
		float off = ((j + 4 * n) & 1) == 1 ? 0.5 * cell : 0.0;
		int iq = int(floor((uv.x - off) * float(n)));
		for (int di = -2; di <= 2; di++) {
			int i = iq + di;
			ivec2 w = ivec2((i + 4 * n) % n, (j + 4 * n) % n);
			float cx = (float(i) + 0.5) * cell + off + (rnd(w, salt + 1u) - 0.5) * u_jitter * cell;
			float cy = (float(j) + 0.5) * cell + (rnd(w, salt + 2u) - 0.5) * u_jitter * cell;
			float th = u_rot + u_rrot * (2.0 * rnd(w, salt + 3u) - 1.0);
			float cs = cos(th); float sn = sin(th);
			float dx = uv.x - cx; float dy = uv.y - cy;
			float lx = (cs * dx + sn * dy) / sz; float ly = (-sn * dx + cs * dy) / sz;
			float rr = 2.0 * sqrt(lx * lx + ly * ly);
			if (rr >= 1.5) continue;
			vec2 su = vec2(lo + span * rnd(w, salt + 4u) + lx * sz, lo + span * rnd(w, salt + 5u) + ly * sz);
			float m = 1.0 - sstep(1.0 - u_soft, 1.0, rr);
			float wt = (m + 1e-4 * (1.5 - rr)) * exp(u_k * (bil(t1, su).r - 1.0));
			vec4 c = bil(t0, su);
			if (u_normal) {
				float nx = c.r * 2.0 - 1.0; float ny = -(c.g * 2.0 - 1.0);
				c = vec4((cs * nx - sn * ny) * 0.5 + 0.5, -(sn * nx + cs * ny) * 0.5 + 0.5, c.b, c.a);
			}
			acc += wt * c; wsum += wt;
		}
	}
	if (wsum < 1e-20) return fet(t0, P);
	vec4 r = acc / wsum;
	return u_normal ? renorm(r) : r;
}",
        ["xmask"] = @"uniform int u_src; uniform vec4 u_smp; uniform float u_threshold; uniform float u_softness; uniform bool u_invert; uniform float u_value;
uniform bool u_rgb; uniform vec4 u_tint; uniform float u_from_albedo;
uniform float u_mh; uniform float u_ms; uniform float u_ml; uniform float u_lo; uniform float u_hi;
float tilt(vec4 n) { vec2 v = n.rg * 2.0 - 1.0; return min(1.0, 2.0 * length(v)); }
vec4 run(ivec2 P, vec2 uv) {
	float m = 1.0;
	if (u_src == 1) { m = ramp(lum(bil(t0, uv).rgb), u_threshold, u_softness); }
	else if (u_src == 2) { m = cmask(bil(t0, uv).rgb, u_smp.rgb, u_mh, u_ms, u_ml, u_lo, u_hi); }
	else if (u_src == 3) { m = ramp(bil(t1, uv).r, u_threshold, u_softness); }
	else if (u_src == 4) { m = ramp(tilt(fet(t2, P)), u_threshold, u_softness); }
	if (u_src != 0 && u_invert) m = 1.0 - m;
	m *= u_value;
	if (!u_rgb) return vec4(m, 1.0, 0.0, 1.0);
	vec3 al = lin(fet(t3, P).rgb); float mx = max(al.r, max(al.g, al.b));
	vec3 c = mix(lin(u_tint.rgb), mx > 1e-4 ? al / mx : vec3(1.0), u_from_albedo) * m;
	return vec4(c, 1.0);
}",
        ["st"] = @"float L(ivec2 P, int dx, int dy) { return fet(t0, P + ivec2(dx, dy)).r; }
vec4 run(ivec2 P, vec2 uv) {
	float gx = 2.0 * (L(P, 1, -1) + 2.0 * L(P, 1, 0) + L(P, 1, 1) - L(P, -1, -1) - 2.0 * L(P, -1, 0) - L(P, -1, 1));
	float gy = 2.0 * (L(P, -1, 1) + 2.0 * L(P, 0, 1) + L(P, 1, 1) - L(P, -1, -1) - 2.0 * L(P, 0, -1) - L(P, 1, -1));
	return vec4(gx * gx, gy * gy, gx * gy, 1.0);
}",
        ["aniso"] = @"uniform int u_mode; uniform float u_angle; uniform bool u_along; uniform float u_coh;
vec4 run(ivec2 P, vec2 uv) {
	float th = u_angle; float str = 1.0;
	if (u_mode == 0) {
		vec4 j = bil(t0, uv);
		float tr = j.r + j.g;
		if (tr > 1e-6) {
			th += 0.5 * atan(2.0 * j.b, j.r - j.g);
			float d = sqrt((j.r - j.g) * (j.r - j.g) + 4.0 * j.b * j.b);
			str = 1.0 + (d / tr - 1.0) * u_coh;
		} else { str = 1.0 - u_coh; }
	}
	if (u_along) th += 1.5707963;
	float fx = cos(th); float fy = -sin(th);
	if (fx < 0.0 || (fx == 0.0 && fy < 0.0)) { fx = -fx; fy = -fy; }
	return vec4(fx * 0.5 + 0.5, fy * 0.5 + 0.5, 0.0, clamp(str, 0.0, 1.0));
}",
        ["transmit"] = @"uniform int u_tint; uniform vec4 u_colour; uniform float u_sat; uniform int u_thick; uniform bool u_invert; uniform float u_contrast; uniform float u_bias;
vec4 run(ivec2 P, vec2 uv) {
	vec3 t = lin(u_colour.rgb);
	if (u_tint == 0) {
		vec3 l = lin(fet(t0, P).rgb); float y = lumlin(l);
		l = max(vec3(0.0), mix(vec3(y), l, u_sat));
		float mx = max(l.r, max(l.g, l.b));
		t = mx > 1e-4 ? l / mx : vec3(1.0);
	}
	float th = u_thick == 0 ? fet(t1, P).r : u_thick == 1 ? fet(t2, P).r : 0.5;
	float thin = u_invert ? th : 1.0 - th;
	return vec4(t, clamp((thin - 0.5) * u_contrast + 0.5 + u_bias, 0.0, 1.0));
}",
        // R2b phase 6: regions (a mask at the working size, the composite, the height's level under the border) and an AI normal map's slopes
        ["rmask"] = @"vec4 run(ivec2 P, vec2 uv) { return vec4(clamp(bil(t0, uv).r, 0.0, 1.0), 0.0, 0.0, 1.0); }",
        ["rg_hstat"] = @"vec4 run(ivec2 P, vec2 uv) { float m = clamp(fet(t2, P).r, 0.0, 1.0); return vec4(fet(t0, P).r, fet(t1, P).r, 0.0, 4.0 * m * (1.0 - m)); }",
        ["rcomp"] = @"uniform int u_mode; uniform bool u_match;
vec4 run(ivec2 P, vec2 uv) {
	vec4 a = fet(t0, P); vec4 b = fet(t1, P); float m = clamp(fet(t2, P).r, 0.0, 1.0);
	if (u_mode == 1) {
		vec3 na = a.rgb * 2.0 - 1.0; vec3 nb = b.rgb * 2.0 - 1.0;
		vec2 d = mix(na.xy / max(na.z, 0.05), nb.xy / max(nb.z, 0.05), m);
		vec3 n = normalize(vec3(d, 1.0));
		return vec4(n * 0.5 + 0.5, mix(a.a, b.a, m));
	}
	if (u_mode == 2) {
		float off = 0.0;
		if (u_match) { vec4 s = texelFetch(t3, ivec2(0), 0); off = s.a > 1e-6 ? s.r - s.g : 0.0; }
		float h = mix(a.r, clamp(b.r + off, 0.0, 1.0), m);
		return vec4(h, h, h, 1.0);
	}
	return mix(a, b, m);
}",
        ["n_from_map"] = @"uniform float u_k;
vec4 run(ivec2 P, vec2 uv) { vec3 n = fet(t0, P).rgb * 2.0 - 1.0; float z = max(n.z, 0.05); return vec4(-n.x / z * u_k, n.y / z * u_k, 0.0, 1.0); }",
    };

    private const string Footer = @"
void fragment() {
	ivec2 P = ivec2(floor(UV * size));
	vec2 uv = (vec2(P) + 0.5) / size;
	COLOR = run(P, uv);
}
";

    private static readonly Dictionary<string, Godot.Shader> Shaders = new();

    /// <summary>The shader of <paramref name="kernel"/> (made once).</summary>
    public static Godot.Shader Shader(string kernel)
    {
        if (Shaders.TryGetValue(kernel, out var s))
            return s;
        s = new Godot.Shader { Code = Header + Bodies[kernel] + Footer };
        return Shaders[kernel] = s;
    }

    public static IEnumerable<string> Names => Bodies.Keys;
}
