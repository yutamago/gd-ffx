// An eye for remade characters (docs/remake.md, "Character materials"): a sphere mapped by
// latitude and longitude (u around from +x towards +z, v from the top), looking along +z. The iris
// lies behind the cornea: its pattern is looked up along the view ray bent into the eye (a
// parallax refraction by the cornea's index), so it shifts against the cornea as the view turns;
// the cornea is wet: a clear coat with a sharp highlight over a smooth sclera. Original work,
// MIT OR Apache-2.0.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

uniform vec3 sclera_color : source_color = vec3(0.90, 0.88, 0.85);
uniform vec3 iris_inner : source_color = vec3(0.30, 0.55, 0.62);
uniform vec3 iris_outer : source_color = vec3(0.06, 0.16, 0.26);
uniform vec3 limbus_color : source_color = vec3(0.02, 0.03, 0.05);
// the iris's and pupil's sizes as angles from the eye's axis (radians)
uniform float iris_angle : hint_range(0.05, 1.2) = 0.55;
uniform float pupil_angle : hint_range(0.0, 0.8) = 0.2;
// how deep the iris lies behind the cornea (in the sphere's radii) and the cornea's index
uniform float iris_depth : hint_range(0.0, 0.6) = 0.22;
uniform float ior : hint_range(1.0, 2.0) = 1.336;
uniform float sclera_roughness : hint_range(0.0, 1.0) = 0.3;
uniform float wet : hint_range(0.0, 1.0) = 1.0;
uniform float wet_roughness : hint_range(0.0, 1.0) = 0.04;

// the unit sphere point of a UV (the mesh's mapping)
vec3 eye_point(vec2 uv) {
	float th = uv.x * 6.2831853;
	float ph = uv.y * 3.1415927;
	return vec3(sin(ph) * cos(th), cos(ph), sin(ph) * sin(th));
}

vec2 eye_uv(vec3 p) {
	p = normalize(p);
	float th = atan(p.z, p.x);
	if (th < 0.0) th += 6.2831853;
	return vec2(th / 6.2831853, acos(clamp(p.y, -1.0, 1.0)) / 3.1415927);
}

void fragment() {
	vec3 p = eye_point(UV);
	// the view direction in the eye's frame: the surface frame (tangent along u, binormal along v)
	vec3 v_ts = vec3(dot(VIEW, TANGENT), dot(VIEW, BINORMAL), dot(VIEW, NORMAL));
	float axis_angle = acos(clamp(p.z, -1.0, 1.0));
	float in_iris = 1.0 - smoothstep(iris_angle - 0.03, iris_angle + 0.03, axis_angle);
	// refracted into the cornea and carried down to the iris: the lookup moves against the view
	vec3 r = refract(-normalize(v_ts), vec3(0.0, 0.0, 1.0), 1.0 / ior);
	float depth = iris_depth * in_iris * (1.0 - axis_angle / max(iris_angle, 0.001));
	vec2 shift = r.xy / max(-r.z, 0.2) * depth;
	vec2 uv2 = UV + vec2(shift.x / 6.2831853, -shift.y / 3.1415927);
	vec3 pi = eye_point(uv2);
	float a = acos(clamp(pi.z, -1.0, 1.0));
	float ring = a / max(iris_angle, 0.001);
	float spokes = 0.5 + 0.5 * sin(atan(pi.y, pi.x) * 40.0 + ring * 7.0);
	vec3 iris = mix(iris_inner, iris_outer, smoothstep(0.2, 1.0, ring)) * (0.75 + 0.25 * spokes);
	iris = mix(iris, limbus_color, smoothstep(0.82, 1.0, ring));
	iris = mix(vec3(0.005), iris, smoothstep(pupil_angle - 0.02, pupil_angle + 0.02, a));
	ALBEDO = mix(sclera_color, iris, in_iris);
	ROUGHNESS = mix(sclera_roughness, 0.2, in_iris);
	SPECULAR = 0.5;
	CLEARCOAT = wet;
	CLEARCOAT_ROUGHNESS = wet_roughness;
}
