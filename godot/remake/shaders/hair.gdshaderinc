// gd-ffx's hair for remade characters (docs/remake.md, "Character materials"), shared by
// hair.gdshader (hashed alpha) and hair_a2c.gdshader (alpha to coverage). Original work,
// MIT OR Apache-2.0. Strands are drawn by the alpha: stripes along the strand direction (v, the
// mesh's second UV axis, or a flow map), each of its own length; the highlight is anisotropic
// (Godot's ANISOTROPY along the strands) and shifted towards the tip.
uniform vec3 root_color : source_color = vec3(0.10, 0.065, 0.04);
uniform vec3 tip_color : source_color = vec3(0.36, 0.23, 0.12);
uniform sampler2D albedo_texture : source_color, hint_default_white;
uniform float roughness : hint_range(0.0, 1.0) = 0.42;
uniform float specular : hint_range(0.0, 1.0) = 0.55;
// how stretched the highlight is along the strands (Godot's anisotropy, -1..1)
uniform float anisotropy : hint_range(-1.0, 1.0) = 0.75;
// the strands' direction in UV space; a flow map (Godot's anisotropy hint: rg the direction) wins when given
uniform vec2 flow_direction = vec2(0.0, 1.0);
uniform bool use_flow_map = false;
uniform sampler2D flow_map : hint_anisotropy;
uniform float strand_count = 120.0;
// the share of each strand's width drawn, and the soft edge
uniform float coverage : hint_range(0.0, 1.0) = 0.8;
uniform float softness : hint_range(0.001, 1.0) = 0.35;
// the tips: strands end between this and 1 along v
uniform float tip_start : hint_range(0.0, 1.0) = 0.7;
uniform float rim : hint_range(0.0, 1.0) = 0.15;

float hair_hash(float n) { return fract(sin(n * 91.345) * 43758.5453); }

// the strand's alpha at uv (across: u, along: v) and how far along it is (0 root, 1 tip)
vec2 hair_strand(vec2 uv) {
	float across = uv.x * strand_count;
	float id = floor(across);
	float h = hair_hash(id);
	float x = fract(across + 0.12 * sin(uv.y * 9.0 + h * 6.283)) * 2.0 - 1.0;
	float body = 1.0 - smoothstep(coverage - softness * 0.5, coverage + softness * 0.5, abs(x));
	float ends = mix(tip_start, 1.0, h);
	float along = clamp(uv.y / max(ends, 0.001), 0.0, 1.0);
	float tip = 1.0 - smoothstep(ends - 0.08, ends, uv.y);
	return vec2(body * tip, along);
}

void hair_surface(vec2 uv, out vec3 albedo, out float alpha, out float rough, out float spec, out float aniso, out vec2 flow, out float rim_out) {
	vec2 s = hair_strand(uv);
	albedo = mix(root_color, tip_color, s.y) * texture(albedo_texture, uv).rgb;
	alpha = s.x;
	rough = roughness;
	spec = specular;
	aniso = anisotropy;
	flow = use_flow_map ? texture(flow_map, uv).rg * 2.0 - 1.0 : normalize(flow_direction);
	rim_out = rim;
}
