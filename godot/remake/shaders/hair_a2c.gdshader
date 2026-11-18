// Hair with alpha to coverage (strand edges antialiased by MSAA: set the viewport's MSAA).
// docs/remake.md, "Character materials". Original work, MIT OR Apache-2.0.
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque, diffuse_burley, specular_schlick_ggx, alpha_to_coverage_and_one;

#include "res://remake/shaders/hair.gdshaderinc"

uniform float scissor : hint_range(0.0, 1.0) = 0.5;
uniform float edge : hint_range(0.0, 1.0) = 0.3;

void fragment() {
	vec3 albedo; float alpha; float rough; float spec; float aniso; vec2 flow; float rim_out;
	hair_surface(UV, albedo, alpha, rough, spec, aniso, flow, rim_out);
	ALBEDO = albedo;
	ALPHA = alpha;
	ALPHA_SCISSOR_THRESHOLD = scissor;
	ALPHA_ANTIALIASING_EDGE = edge;
	ROUGHNESS = rough;
	SPECULAR = spec;
	ANISOTROPY = aniso;
	ANISOTROPY_FLOW = flow;
	RIM = rim_out;
	RIM_TINT = 0.5;
}
