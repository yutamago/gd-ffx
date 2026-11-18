using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// The remastered render path's materials (docs/remaster-editor.md, "The render path"): a map
/// part drawn under Godot's lit pipeline with its texture's physically based material
/// (<see cref="RemasterTextures.MaterialFor"/>), or, without one, its colour texture (the
/// replacement or the classic one) as a plain rough surface. The lit material keeps what the
/// classic one does the game's way (<see cref="ClassicMaterials.MapShaderMeta"/>): its blend,
/// cull and depth modes, the alpha test, the UV set, the scrolling and morphing UVs and colours.
/// The game's vertex colours hold baked light and tint (128 neutral); <c>ffx_vertex_light</c>
/// (0..1) says how much of it stays under the real lights (a provisional rig until R3:
/// <see cref="RemasterLighting"/>). Water, the 2D layers and the characters keep their classic
/// shaders, which then write linear colours (<c>ffx_linear_out</c>).
/// </summary>
public static class RemasterLook
{
    // The map shader's vertex program (ClassicMaterials.MapBody), the same defines; then the lit
    // fragment: albedo (sRGB texture, linear here) x the PBR material's colour x the game's
    // MaterialColour x the vertex light, normal, ORM (occlusion, roughness, metallic), emission,
    // a simple parallax offset from the height map (as Godot's StandardMaterial3D does).
    private const string Body = @"
global uniform float ffx_time;
// how much of the game's baked vertex light stays under the real lights (0: none, 1: all)
global uniform float ffx_vertex_light;
// 1 where the renderer lights in gamma (Compatibility): the game's colours taken as they are
global uniform float ffx_lit_gamma;
#if defined(UV_MORPH) || defined(COLOR_MORPH)
instance uniform float ffx_morph = 0.0;
#endif
#ifdef UV_MORPH
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
#define FFX_REPEAT repeat_disable
#else
#define FFX_REPEAT repeat_enable
#endif
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, FFX_REPEAT;
uniform vec4 albedo_colour : source_color = vec4(1.0);
uniform vec4 material_colour = vec4(1.0);
uniform float alpha_threshold = 0.0;
uniform vec2 scroll = vec2(0.0);
uniform vec3 uv1_scale = vec3(1.0);
uniform vec3 uv1_offset = vec3(0.0);
uniform float roughness = 0.85;
uniform float metallic = 0.0;
uniform float specular = 0.5;
#ifdef HAS_NORMAL
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic, FFX_REPEAT;
uniform float normal_scale = 1.0;
#endif
#ifdef HAS_ORM
uniform sampler2D orm_tex : hint_default_white, filter_linear_mipmap_anisotropic, FFX_REPEAT;
uniform float ao_light_affect = 0.0;
#endif
#ifdef HAS_EMISSION
uniform sampler2D emission_tex : source_color, hint_default_black, filter_linear_mipmap_anisotropic, FFX_REPEAT;
uniform vec4 emission_colour : source_color = vec4(1.0);
uniform float emission_energy = 1.0;
#endif
#ifdef HAS_HEIGHT
uniform sampler2D height_tex : hint_default_black, filter_linear_mipmap, FFX_REPEAT;
uniform float height_scale = 0.02;
#endif
vec3 ffx_to_linear(vec3 c) {
	c = clamp(c, 0.0, 1.0);
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}
void fragment() {
	vec2 uv = UV;
#ifdef UV_ANIM
	uv += vec2(fract(scroll.x * ffx_time), -fract(scroll.y * ffx_time));
#endif
	uv = uv * uv1_scale.xy + uv1_offset.xy;
#ifdef HAS_HEIGHT
	vec3 view_dir = normalize(normalize(-VERTEX) * mat3(TANGENT, -BINORMAL, NORMAL));
	float depth = 1.0 - texture(height_tex, uv).r;
	uv -= view_dir.xy / max(view_dir.z, 0.1) * depth * height_scale;
#endif
	vec4 t = texture(albedo_tex, uv);
#ifdef ALPHA_TEST
	if (t.a < alpha_threshold) { discard; }
#endif
	// the game's vertex colour: light and tint, 128 neutral (in gamma), as linear light
	vec3 vl = pow(clamp(2.0 * COLOR.rgb, vec3(0.0), vec3(2.0)), vec3(mix(2.2, 1.0, ffx_lit_gamma)));
	vec3 mc = mix(ffx_to_linear(material_colour.rgb), clamp(material_colour.rgb, 0.0, 1.0), ffx_lit_gamma);
	ALBEDO = t.rgb * albedo_colour.rgb * mc * mix(vec3(1.0), vl, ffx_vertex_light);
	ROUGHNESS = roughness;
	METALLIC = metallic;
	SPECULAR = specular;
#ifdef HAS_ORM
	vec3 orm = texture(orm_tex, uv).rgb;
	AO = orm.r;
	AO_LIGHT_AFFECT = ao_light_affect;
	ROUGHNESS = orm.g * roughness;
	METALLIC = orm.b * metallic;
#endif
#ifdef HAS_NORMAL
	NORMAL_MAP = texture(normal_tex, uv).rgb;
	NORMAL_MAP_DEPTH = normal_scale;
#endif
#ifdef HAS_EMISSION
	EMISSION = texture(emission_tex, uv).rgb * emission_colour.rgb * emission_energy;
#endif
#ifdef BLEND
	ALPHA = clamp(t.a * albedo_colour.a * material_colour.a * COLOR.a, 0.0, 1.0);
#endif
}
";

    private static readonly Dictionary<string, Shader> Shaders = new();

    private static Shader LitShader(string modes, IEnumerable<string> defines)
    {
        var d = string.Concat(defines.Select(x => $"#define {x}\n"));
        var key = modes + "|" + d;
        lock (Shaders)
        {
            if (!Shaders.TryGetValue(key, out var sh))
                Shaders[key] = sh = new Shader { Code = "shader_type spatial;\nrender_mode " + modes + ";\n" + d + Body };
            return sh;
        }
    }

    /// <summary>The lit materials made, by classic material (made once each while the texture's edit stays).</summary>
    private static readonly ConditionalWeakTable<Material, Made> Cache = new();

    private sealed record Made(int Version, Material Lit);

    /// <summary>Lit materials made so far (for tests and the log): with a PBR material, from a colour texture only.</summary>
    public static (int Pbr, int Plain) Count;

    /// <summary>
    /// The lit material for classic map material <paramref name="classic"/> (null: not a map
    /// material: water, a 2D layer, a character, which keep theirs). Main thread.
    /// </summary>
    public static Material? Lit(Material? classic)
    {
        if (classic is not ShaderMaterial sm || PartIds.GetString(sm, ClassicMaterials.MapShaderMeta) is not { } spec)
            return null;
        if (Cache.TryGetValue(sm, out var made) && made.Version == RemasterTextures.Version)
            return made.Lit;
        var lit = Make(sm, spec);
        Cache.AddOrUpdate(sm, new Made(RemasterTextures.Version, lit));
        return lit;
    }

    private static Material Make(ShaderMaterial classic, string spec)
    {
        var bar = spec.IndexOf('|');
        var modes = spec[..bar].Split(',', System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries)
            .Where(m => m != "unshaded").ToList();
        var defines = spec[(bar + 1)..].Split(',', System.StringSplitOptions.RemoveEmptyEntries).ToList();
        defines.Remove("FOG"); // Godot's own fog (the rig's environment)
        var id = RemasterTextures.ColourId(classic);
        var pbr = RemasterTextures.MaterialFor(id);
        // a ShaderMaterial of one's own is used as it is (none of the game's ways kept)
        if (pbr is ShaderMaterial own)
        {
            var copy = (ShaderMaterial)own.Duplicate();
            copy.RenderPriority = classic.RenderPriority;
            Count.Pbr++;
            return copy;
        }
        var std = pbr as BaseMaterial3D;
        Texture2D? albedo = std?.AlbedoTexture;
        if (albedo == null)
        {
            using var v = classic.GetShaderParameter(Variants.Name("tex"));
            albedo = v.AsGodotObject() as Texture2D;
        }
        var orm = std is OrmMaterial3D o ? o.OrmTexture : null;
        var normal = std is { NormalEnabled: true } ? std.NormalTexture : null;
        var emission = std is { EmissionEnabled: true } ? std.EmissionTexture : null;
        var height = std is { HeightmapEnabled: true } ? std.HeightmapTexture : null;
        if (orm != null) defines.Add("HAS_ORM");
        if (normal != null) defines.Add("HAS_NORMAL");
        if (emission != null) defines.Add("HAS_EMISSION");
        if (height != null) defines.Add("HAS_HEIGHT");
        var m = new ShaderMaterial { Shader = LitShader(string.Join(", ", modes), defines), RenderPriority = classic.RenderPriority };
        foreach (var p in new[] { "material_colour", "alpha_threshold", "scroll", "uv_morph_set" })
        {
            using var v = classic.GetShaderParameter(Variants.Name(p));
            if (v.VariantType != Variant.Type.Nil)
                m.SetShaderParameter(Variants.Name(p), v);
        }
        Variants.SetParameter(m, "albedo_tex", albedo);
        if (std != null)
        {
            m.SetShaderParameter(Variants.Name("albedo_colour"), std.AlbedoColor);
            m.SetShaderParameter(Variants.Name("roughness"), std.Roughness);
            m.SetShaderParameter(Variants.Name("metallic"), std.Metallic);
            m.SetShaderParameter(Variants.Name("specular"), std.MetallicSpecular);
            m.SetShaderParameter(Variants.Name("uv1_scale"), std.Uv1Scale);
            m.SetShaderParameter(Variants.Name("uv1_offset"), std.Uv1Offset);
            m.SetShaderParameter(Variants.Name("normal_scale"), std.NormalScale);
            m.SetShaderParameter(Variants.Name("ao_light_affect"), std.AOLightAffect);
            m.SetShaderParameter(Variants.Name("emission_colour"), std.Emission);
            m.SetShaderParameter(Variants.Name("emission_energy"), std.EmissionEnergyMultiplier);
            // Godot's heightmap scale is in hundredths of the UV space
            m.SetShaderParameter(Variants.Name("height_scale"), std.HeightmapScale * 0.01f);
            Variants.SetParameter(m, "orm_tex", orm);
            Variants.SetParameter(m, "normal_tex", normal);
            Variants.SetParameter(m, "emission_tex", emission);
            Variants.SetParameter(m, "height_tex", height);
            Count.Pbr++;
        }
        else
            Count.Plain++;
        PartIds.SetString(m, LitTextureMeta, id ?? "");
        return m;
    }

    /// <summary>Whether <paramref name="classic"/> is a blended map material (the game's painted layers: wet sand, shallow water, shading).</summary>
    public static bool IsBlended(Material? classic) =>
        classic is ShaderMaterial sm && PartIds.GetString(sm, ClassicMaterials.MapShaderMeta) is { } spec
        && spec[(spec.IndexOf('|') + 1)..].Split(',').Contains("BLEND");

    /// <summary>Metadata on a lit material: the texture ID it draws (for tests and the editor).</summary>
    public const string LitTextureMeta = "ffx_lit_texture";
}

/// <summary>
/// One map's two looks (docs/remaster-editor.md, "The render path"): its classic materials
/// and, made on first use, the lit ones, swapped per surface. Attached to the map's node
/// (<see cref="Of"/>), so a map kept for coming back keeps its state.
/// </summary>
public sealed class MapLook
{
    private readonly List<(MeshInstance3D Mesh, int Surface, Material? Classic)> _surfaces = new();
    private static readonly ConditionalWeakTable<Node, MapLook> Looks = new();

    /// <summary>Whether it draws remastered now.</summary>
    public bool Remastered { get; private set; }

    /// <summary>The <see cref="RemasterTextures.Version"/> its lit materials were made at.</summary>
    private int _version = -1;

    /// <summary>The map surfaces it swaps (classic map materials: not water, layers or characters).</summary>
    public int Surfaces => _surfaces.Count;

    /// <summary><paramref name="map"/>'s look (made on first use: its map surfaces found).</summary>
    public static MapLook Of(Node map)
    {
        if (Looks.TryGetValue(map, out var look))
            return look;
        look = new MapLook();
        look.Collect(map, map is Node3D m3 ? m3.Transform : Transform3D.Identity);
        Looks.Add(map, look);
        if (look.Sky.Count > 0)
            GD.Print($"ffx: remaster: {look.Sky.Count} sky part(s) keep the classic look: {string.Join(", ", look.Sky)}");
        return look;
    }

    /// <summary><paramref name="map"/>'s look if it has one (null: never switched).</summary>
    public static MapLook? Find(Node map) => Looks.TryGetValue(map, out var look) ? look : null;

    /// <summary>The classic materials of the surfaces it swaps.</summary>
    public IEnumerable<Material> Classics
    {
        get
        {
            foreach (var (_, _, m) in _surfaces)
                if (m != null)
                    yield return m;
        }
    }

    /// <summary>The classic material of <paramref name="mesh"/>'s surface <paramref name="surface"/> when this look swaps it (null: not one of its surfaces).</summary>
    public Material? ClassicOf(MeshInstance3D mesh, int surface)
    {
        foreach (var (m, s, c) in _surfaces)
            if (m == mesh && s == surface)
                return c;
        return null;
    }

    /// <summary>The meshes taken for the sky (<see cref="FindSky"/>), by name: they keep the classic look.</summary>
    public readonly List<string> Sky = new();

    /// <summary>The sky meshes themselves (the lighting may hide them: <see cref="MapLighting.SkyHideClassic"/>).</summary>
    public readonly HashSet<MeshInstance3D> SkyMeshes = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every mesh of the map (the VoxelGI's box and bake).</summary>
    public readonly List<MeshInstance3D> Meshes = new();

    private void Collect(Node map, Transform3D world)
    {
        var meshes = new List<(MeshInstance3D Mesh, Aabb Box)>();
        void Walk(Node n, Transform3D t)
        {
            if (n is MeshInstance3D { Mesh: { } mesh } mi)
                meshes.Add((mi, t * mesh.GetAabb()));
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                if (n.GetChild(i) is Node c)
                    Walk(c, c is Node3D c3 ? t * c3.Transform : t);
        }
        Walk(map, world);
        var sky = FindSky(meshes);
        foreach (var (mi, _) in meshes)
        {
            Meshes.Add(mi);
            if (sky.Contains(mi))
            {
                Sky.Add(mi.Name);
                SkyMeshes.Add(mi);
                continue;
            }
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                if (mi.GetSurfaceOverrideMaterial(s) is ShaderMaterial m && m.HasMeta(Variants.Name(ClassicMaterials.MapShaderMeta)))
                    _surfaces.Add((mi, s, m));
        }
    }

    /// <summary>
    /// The map's sky parts (no light falls on a sky: they keep the game's look until R3 brings a
    /// sky of Godot's): sky domes (<c>m_DObjKind</c> 1), and after ffx-remix's R-45 rule (its
    /// docs/offline-scene.md, "The dome by part") the meshes grouped by their colour texture
    /// whose group reaches 1,000 render units from the vertical axis, is centred on it (its
    /// middle within a third of that), rises above the ground by 5 % of its reach, spans 10 % of
    /// it in height, and reaches at least 0.6 of the farthest such group (Besaid's four
    /// scrolling cloud quarters, not marked as a dome).
    /// </summary>
    public static HashSet<MeshInstance3D> FindSky(List<(MeshInstance3D Mesh, Aabb Box)> meshes)
    {
        var sky = new HashSet<MeshInstance3D>(ReferenceEqualityComparer.Instance);
        var groups = new Dictionary<string, (Aabb Box, List<MeshInstance3D> Members)>();
        foreach (var (mi, box) in meshes)
        {
            if (MeshExtras.Of(mi.Mesh).DobjKind == 1)
            {
                sky.Add(mi);
                continue;
            }
            string? key = null;
            for (int s = 0; s < mi.Mesh.GetSurfaceCount() && key == null; s++)
                key = RemasterTextures.ColourId(mi.GetSurfaceOverrideMaterial(s));
            if (key == null)
                continue;
            groups[key] = groups.TryGetValue(key, out var g) ? (g.Box.Merge(box), [.. g.Members, mi]) : (box, [mi]);
        }
        static float Reach(Aabb b)
        {
            float r = 0;
            foreach (var x in new[] { b.Position.X, b.End.X })
                foreach (var z in new[] { b.Position.Z, b.End.Z })
                    r = Mathf.Max(r, new Vector2(x, z).Length());
            return r;
        }
        var candidates = new List<(float Reach, List<MeshInstance3D> Members)>();
        foreach (var (box, members) in groups.Values)
        {
            float reach = Reach(box);
            var c = box.GetCenter();
            if (reach >= 1000f && new Vector2(c.X, c.Z).Length() < reach / 3f && box.End.Y > 0.05f * reach && box.Size.Y >= 0.1f * reach)
                candidates.Add((reach, members));
        }
        float far = candidates.Count > 0 ? candidates.Max(x => x.Reach) : 0;
        foreach (var (reach, members) in candidates)
            if (reach >= 0.6f * far)
                sky.UnionWith(members);
        return sky;
    }

    /// <summary>Whether its blended parts draw lit too (<see cref="MapLighting.BlendedParts"/>).</summary>
    public bool LitBlended { get; private set; }

    /// <summary>
    /// Draws the map remastered or classic (main thread); remastered, its blended parts (the game's
    /// painted layers) lit only with <paramref name="litBlended"/>, else in their classic shaders as
    /// the water is. Returns the surfaces it changed.
    /// </summary>
    public int Set(bool remastered, bool litBlended = false)
    {
        if (remastered == Remastered && (!remastered || _version == RemasterTextures.Version && litBlended == LitBlended))
            return 0;
        Remastered = remastered;
        LitBlended = litBlended;
        _version = RemasterTextures.Version;
        int n = 0;
        foreach (var (mi, s, classic) in _surfaces)
        {
            if (!GodotObject.IsInstanceValid(mi) || s >= mi.GetSurfaceOverrideMaterialCount())
                continue;
            var want = remastered && (litBlended || !RemasterLook.IsBlended(classic)) ? RemasterLook.Lit(classic) ?? classic : classic;
            if (mi.GetSurfaceOverrideMaterial(s) != want)
            {
                mi.SetSurfaceOverrideMaterial(s, want);
                n++;
            }
        }
        return n;
    }
}
