// Hair with hashed alpha (stable strands without sorting; dithered edges that TAA or FSR smooth).
// docs/remake.md, "Character materials". Original work, MIT OR Apache-2.0.
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque, diffuse_burley, specular_schlick_ggx;

#include "res://remake/shaders/hair.gdshaderinc"

uniform float hash_scale : hint_range(0.0, 2.0) = 1.0;

void fragment() {
	vec3 albedo; float alpha; float rough; float spec; float aniso; vec2 flow; float rim_out;
	hair_surface(UV, albedo, alpha, rough, spec, aniso, flow, rim_out);
	ALBEDO = albedo;
	ALPHA = alpha;
	ALPHA_HASH_SCALE = hash_scale;
	ROUGHNESS = rough;
	SPECULAR = spec;
	ANISOTROPY = aniso;
	ANISOTROPY_FLOW = flow;
	RIM = rim_out;
	RIM_TINT = 0.5;
}
