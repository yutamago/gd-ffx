using System.Collections.Generic;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// Which look a map is drawn with (docs/remaster-editor.md, "The render path"): the map's own
/// render path (its overrides' <see cref="MapOverrides.RenderPath"/>), or one for every map, from
/// the options (<see cref="UserSettings.RenderPath"/>: <c>map</c>, <c>classic</c>,
/// <c>remastered</c>), <c>--render-path=</c> on the command line, or the toggle key
/// (<see cref="GameHost.RemasterKey"/>, F7).
/// </summary>
public static class RenderPaths
{
    public static readonly string[] Choices = ["map", "classic", "remastered"];

    /// <summary>The toggle's choice this run (null: none yet).</summary>
    private static string? _toggled;

    /// <summary>The choice in force: the toggle's, else <c>--render-path=</c>, else the setting.</summary>
    public static string Choice => _toggled
        ?? (GameSettings.Arg("render-path") is "classic" or "remastered" or "map" ? GameSettings.Arg("render-path")! : UserSettings.Current.RenderPath);

    /// <summary>Sets the choice for this run (the options do it through the setting; null: back to the setting).</summary>
    public static void Set(string? choice)
    {
        _toggled = choice;
        Version++;
    }

    /// <summary>Bumped whenever the choice may have changed (views cache <see cref="Remastered"/> by it).</summary>
    public static int Version { get; private set; }

    /// <summary>The map's own render path (its overrides' last layer; classic without).</summary>
    public static bool MapSays(string level) => RemasterOverrides.For(level)?.RenderPath == RenderPath.Remastered;

    /// <summary>Whether <paramref name="level"/> is drawn remastered now.</summary>
    public static bool Remastered(string level) => Choice switch
    {
        "classic" => false,
        "remastered" => true,
        _ => level.Length > 0 && MapSays(level),
    };

    /// <summary>
    /// The toggle (F7): from the look <paramref name="level"/> has now to the other one, for every
    /// map, this run (the options' setting is the lasting choice). Returns the new choice.
    /// </summary>
    public static string Toggle(string level)
    {
        _toggled = Remastered(level) ? "classic" : "remastered";
        Version++;
        GD.Print($"ffx: render path {_toggled} (toggled on {PartIds.LevelKey(level)})");
        return _toggled;
    }
}

/// <summary>
/// The values the rig resolved for the shown map (from its <see cref="MapLighting"/> and the
/// game's look; then the volumes the camera is in): what the lights and the environment get.
/// Colours are linear (Godot takes them as sRGB: encoded when set).
/// </summary>
public sealed class ResolvedLighting
{
    public bool KeyOn;
    /// <summary>The way the key light travels (render space).</summary>
    public Vector3 KeyDirection = new(0, -1, 0);
    public Color KeyColour = Colors.White;
    public float KeyEnergy;
    public Color AmbientColour = Colors.White;
    public float AmbientEnergy;
    public bool FogOn;
    public Color FogColour = Colors.White;
    public float FogDensity, FogBegin, FogEnd, FogCurve = 1;
    public float Exposure = 1, Saturation = 1, VolumetricDensity;
    public float VertexLight = 0.5f;
    /// <summary>The GI drawn (after the renderer and the quality).</summary>
    public GiMode Gi;
    /// <summary>The volumes the camera is in, with their weights.</summary>
    public string Volumes = "";

    public ResolvedLighting Copy() => (ResolvedLighting)MemberwiseClone();

    public override string ToString()
    {
        static string I(System.FormattableString f) => System.FormattableString.Invariant(f);
        var key = KeyOn ? I($"{KeyEnergy:0.00} x ({KeyColour.R:0.00}, {KeyColour.G:0.00}, {KeyColour.B:0.00}) towards ({KeyDirection.X:0.00}, {KeyDirection.Y:0.00}, {KeyDirection.Z:0.00})") : "off";
        var fog = FogOn ? I($"{FogDensity:0.00} {FogBegin:0}..{FogEnd:0}") : "off";
        return I($"key {key}, ambient {AmbientEnergy:0.00} x ({AmbientColour.R:0.00}, {AmbientColour.G:0.00}, {AmbientColour.B:0.00}), fog {fog}, ")
            + I($"exposure {Exposure:0.00}, saturation {Saturation:0.00}, vertex light {VertexLight:0.00}, GI {Gi}")
            + (Volumes.Length > 0 ? ", volumes " + Volumes : "");
    }
}

/// <summary>
/// The remastered path's lighting (R3; docs/remaster-editor.md, "Lighting"): while the shown map
/// draws remastered, its <see cref="MapLighting"/> (<see cref="LightingLayers"/>: the map's file,
/// else the defaults) made into Godot's: the game's key light as a directional light (its
/// direction from <c>Look.lights[0]</c>, turned as the characters' shader turns it; its colour
/// mostly neutral, the game's being made for characters), the ambient from the game's, a sky,
/// the game's depth fog, volumetric fog, the tone map, glow, SSAO/SSIL/SSR, GI (SDFGI, a VoxelGI
/// baked on the player's machine), and the volumes the camera is in, blended. The lighting
/// quality (<see cref="UserSettings.Lighting"/>) scales GI, volumetric fog, the screen-space
/// effects and the shadows; Compatibility draws none of GI, volumetric fog or the screen-space
/// effects. The classic environment (the game's colours, <see cref="ClassicPost"/> under
/// Forward+) comes back when the map draws classic again. The game's look values are gamma
/// (as its shaders take them): decoded where Godot wants linear light.
/// </summary>
public sealed partial class RemasterLighting : Node3D
{
    private readonly WorldEnvironment _env;
    private readonly ClassicPost? _post;
    private readonly Node3D? _classicLight;
    private DirectionalLight3D _sun = null!;
    private Sky? _sky;
    private ProceduralSkyMaterial? _procedural;
    private PhysicalSkyMaterial? _physical;
    private PanoramaSkyMaterial? _panorama;
    private Environment? _saved;
    private VoxelGI? _voxel;
    private string _voxelFor = "";

    /// <summary>The classic shaders' <c>ffx_linear_out</c> as set last (for tests: a headless renderer keeps no globals).</summary>
    public static float LinearOut { get; private set; }

    /// <summary>Whether the rig is on (the map draws remastered).</summary>
    public bool Active { get; private set; }

    /// <summary>The sun (for tests).</summary>
    public DirectionalLight3D Sun => _sun;

    /// <summary>The map's lighting in use and its layer ("" the defaults).</summary>
    public (MapLighting Lighting, string Layer) Lighting { get; private set; } = (LightingLayers.Defaults, "");

    /// <summary>The values as resolved from the file and the look (before the volumes).</summary>
    public ResolvedLighting Base { get; private set; } = new();

    /// <summary>The values in force (after the volumes).</summary>
    public ResolvedLighting Now { get; private set; } = new();

    /// <summary>A lighting used instead of the map's layers (the editor's: the one being edited; null: <see cref="LightingLayers"/>).</summary>
    public MapLighting? Fixed { get; set; }

    /// <summary>The editor's key light node: the sun there (its own values, as edited), the rig's own sun stays off.</summary>
    public DirectionalLight3D? ExternalSun { get; set; }

    /// <summary>Applies the lighting again at the next <see cref="Set"/> (the editor after an edit).</summary>
    public void Refresh() => _layersVersion = -1;

    /// <summary>The VoxelGI's state ("" none; "baked N ms", "cached").</summary>
    public string VoxelState { get; private set; } = "";

    private long _lookOf = -1;
    private int _layersVersion = -1;
    private string _level = "";
    private GdFfx.Api.Look? _look;
    private Node3D? _map;

    public RemasterLighting(WorldEnvironment env, ClassicPost? post, Node3D? classicLight)
    {
        Name = "RemasterLighting";
        _env = env;
        _post = post;
        _classicLight = classicLight;
    }

    public override void _Ready()
    {
        _sun = new DirectionalLight3D
        {
            Name = "Sun",
            Visible = false,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 3000f,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            RotationDegrees = new Vector3(-55, 30, 0),
        };
        AddChild(_sun);
        ClassicMaterials.EnsureGlobals();
    }

    /// <summary>
    /// Switches the rig on or off (main thread) for <paramref name="level"/>'s map
    /// <paramref name="map"/>; with it on, follows the look once a snapshot and the camera
    /// (<paramref name="eye"/>) through the volumes every frame.
    /// </summary>
    public void Set(bool on, GdFfx.Api.Look? look, long decoded, string level = "", Node3D? map = null, Vector3? eye = null)
    {
        if (on != Active)
        {
            Active = on;
            if (on) Enable(); else Disable();
            _lookOf = -1;
            GD.Print($"ffx: remaster lighting {(on ? "on" : "off")}");
        }
        if (!on)
            return;
        bool again = level != _level || map != _map || _layersVersion != LightingLayers.Version || UserSettings.Current.Lighting != _quality;
        if (again)
        {
            (_level, _map, _layersVersion, _quality) = (level, map, LightingLayers.Version, UserSettings.Current.Lighting);
            Lighting = Fixed != null ? (Fixed, "editor") : LightingLayers.For(level);
            ApplyStatic(Lighting.Lighting);
            _lookOf = -1;
        }
        if (look is { Valid: true } && (decoded != _lookOf || again))
        {
            _lookOf = decoded;
            _look = look;
            Base = Resolve(Lighting.Lighting, look);
            if (_logged++ == 0 || again)
                GD.Print($"ffx: remaster lighting {PartIds.LevelKey(level)} ({(Lighting.Layer.Length > 0 ? "layer " + Lighting.Layer : "defaults")}, {QualityName}): {Base}");
        }
        var now = Blend(Base, Lighting.Lighting, eye);
        ApplyDynamic(Lighting.Lighting, now);
        Now = now;
    }

    private string _quality = "";
    private int _logged;

    private string QualityName => $"lighting {UserSettings.Current.Lighting}, {(UserSettings.ForwardPlus() ? "Forward+" : "Compatibility")}";

    private void Enable()
    {
        // the classic environment kept as it was, given back when the map draws classic again
        _saved = (Environment)_env.Environment.Duplicate();
        if (_post != null)
            _post.Enabled = false;
        if (_classicLight != null)
            _classicLight.Visible = false;
        _sun.Visible = ExternalSun == null;
        // the classic shaders still drawing (characters, water, layers, effects): their gamma
        // values written as linear ones, which Forward+ encodes back (ClassicMaterials' ffx_out)
        // (Compatibility writes the values as they are: measured, the classic parts unchanged without it)
        LinearOut = UserSettings.ForwardPlus() ? 1f : 0f;
        RenderingServer.GlobalShaderParameterSet(Variants.Name("ffx_linear_out"), LinearOut);
        RenderingServer.GlobalShaderParameterSet(Variants.Name("ffx_lit_gamma"), UserSettings.ForwardPlus() ? 0f : 1f);
    }

    private void Disable()
    {
        if (_saved != null)
        {
            _env.Environment = _saved;
            _saved = null;
        }
        if (_post != null)
            _post.Enabled = true;
        if (_classicLight != null)
            _classicLight.Visible = true;
        _sun.Visible = false;
        if (_voxel != null)
            _voxel.Visible = false;
        if (_map != null)
            SetClassicSky(_map, true);
        (_level, _map, _layersVersion) = ("", null, -1);
        LinearOut = 0f;
        RenderingServer.GlobalShaderParameterSet(Variants.Name("ffx_linear_out"), 0f);
        RenderingServer.GlobalShaderParameterSet(Variants.Name("ffx_vertex_light"), 0.5f);
    }

    private static Color Raw(Vector3 c) => new(Mathf.Clamp(c.X, 0, 1), Mathf.Clamp(c.Y, 0, 1), Mathf.Clamp(c.Z, 0, 1));

    private static float Luma(Color c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;

    /// <summary><paramref name="c"/> scaled to a luminance of 1 (white when black).</summary>
    private static Color Unit(Color c)
    {
        float l = Luma(c);
        return l > 1e-4f ? new Color(c.R / l, c.G / l, c.B / l) : Colors.White;
    }

    /// <summary>A gamma colour of the game's look as a linear hue of luminance 1, kept by <paramref name="tint"/> (0: white).</summary>
    private static Color GameHue(Vector3 gamma, float tint)
    {
        float peak = Mathf.Max(gamma.X, Mathf.Max(gamma.Y, gamma.Z));
        if (peak <= 1e-4f)
            return Colors.White;
        var hue = Unit(Raw(gamma / peak).SrgbToLinear());
        return Unit(Colors.White.Lerp(hue, Mathf.Clamp(tint, 0, 1)));
    }

    /// <summary>Sunlight's warmth (linear, luminance 1): about 4,500 K at 1.</summary>
    private static readonly Color Warm = Unit(new Color(1f, 0.78f, 0.56f));

    /// <summary>Compatibility lights in gamma (its lit shaders get the values as they are): the energies it gets instead (measured: the colour check).</summary>
    public static float CompatKeyScale = 0.55f, CompatAmbientScale = 1.25f, CompatExposure = 0.9f;

    /// <summary>The file's values with the game's look in the places that follow it.</summary>
    public static ResolvedLighting Resolve(MapLighting m, GdFfx.Api.Look look)
    {
        bool fwd = UserSettings.ForwardPlus();
        var r = new ResolvedLighting { VertexLight = m.VertexLight, Exposure = m.ToneExposure, Saturation = m.ToneSaturation, VolumetricDensity = m.VolumetricDensity };
        // the key light
        var key = look.Lights.Count > 0 ? look.Lights[0] : null;
        r.KeyOn = m.KeyMode != LightSource.Off;
        if (m.KeyMode == LightSource.Game && key != null && key.Direction.LengthSquared() > 1e-8f)
        {
            // the characters' shader lights a surface by dot(sign x L, N): the light comes from sign x L
            var toLight = ClassicMaterials.LightDirection(key) * ClassicMaterials.LightSign;
            r.KeyDirection = toLight.LengthSquared() > 1e-8f ? -toLight.Normalized() : new Vector3(0, -1, 0);
        }
        else
            r.KeyDirection = m.KeyDirection.LengthSquared() > 1e-8f ? m.KeyDirection.Normalized() : new Vector3(0, -1, 0);
        var kc = key != null ? key.Colour * Mathf.Max(key.Level, 0f) : new Vector3(1.2f, 1.2f, 1.2f);
        r.KeyColour = m.KeyColourMode == LightSource.Custom
            ? m.KeyColour.SrgbToLinear()
            : Unit(GameHue(kc, m.KeyGameTint) * Colors.White.Lerp(Warm, Mathf.Clamp(m.KeyWarmth, 0, 1)));
        // the game's level against a daylight one (Besaid beach's 1.29): dim maps stay dimmer
        float level = m.KeyColourMode == LightSource.Custom ? 1f : Mathf.Clamp(Luma(Raw(kc / 1.3f)) / Luma(Raw(new Vector3(1.2f, 1.29f, 1.37f) / 1.3f)), 0.25f, 1.25f);
        r.KeyEnergy = m.KeyEnergy * level * (fwd ? 1f : CompatKeyScale);
        // the ambient: the map's and the characters' (Besaid 0 + (0.34, 0.34, 0.45))
        var a = look.Ambient + look.CharacterAmbient;
        float al = Luma(Raw(a));
        if (m.AmbientMode == LightSource.Custom)
            (r.AmbientColour, r.AmbientEnergy) = (Unit(m.AmbientColour.SrgbToLinear()), m.AmbientEnergy);
        else if (m.AmbientMode == LightSource.Off)
            (r.AmbientColour, r.AmbientEnergy) = (Colors.White, 0f);
        else
            (r.AmbientColour, r.AmbientEnergy) = (GameHue(a, m.AmbientGameTint), m.AmbientEnergy * (al < 0.02f ? 1f : Mathf.Clamp(al / 0.35f, 0.4f, 2f)));
        if (!fwd)
            r.AmbientEnergy *= CompatAmbientScale;
        // the fog
        r.FogOn = m.FogMode != LightSource.Off;
        if (m.FogMode == LightSource.Game)
        {
            r.FogColour = Raw(look.FogColour).SrgbToLinear();
            r.FogBegin = Mathf.Max(look.FogNear, 0f);
            r.FogEnd = Mathf.Max(look.FogFar, r.FogBegin + 1f);
            r.FogCurve = Mathf.Max(look.FogCurve, 0.01f);
            r.FogDensity = Mathf.Clamp(look.FogLimit, 0f, 1f);
            // a game without fog (near 65536): none
            r.FogOn = look.FogNear < 60000f;
        }
        else
        {
            r.FogColour = m.FogColour.SrgbToLinear();
            (r.FogBegin, r.FogEnd, r.FogCurve, r.FogDensity) = (m.FogBegin, Mathf.Max(m.FogEnd, m.FogBegin + 1), m.FogCurve, m.FogDensity);
        }
        if (!fwd)
            r.Exposure *= CompatExposure;
        r.Gi = EffectiveGi(m.GiMode);
        return r;
    }

    /// <summary>The GI drawn for <paramref name="wanted"/>: none under Compatibility or the low quality; SDFGI only at high.</summary>
    public static GiMode EffectiveGi(GiMode wanted)
    {
        if (!UserSettings.ForwardPlus() || wanted == GiMode.LightmapGi)
            return GiMode.None;
        return UserSettings.Current.Lighting switch
        {
            "low" => GiMode.None,
            "medium" when wanted == GiMode.Sdfgi => GiMode.None,
            _ => wanted,
        };
    }

    /// <summary>The volumes the camera is in, each set value blended in by its weight (later volumes over earlier ones).</summary>
    public static ResolvedLighting Blend(ResolvedLighting b, MapLighting m, Vector3? eye)
    {
        if (m.Volumes.Count == 0 || eye is not { } p)
            return b;
        var r = b.Copy();
        var names = new List<string>();
        foreach (var v in m.Volumes)
        {
            if (v == null)
                continue;
            float w = v.Weight(p);
            if (w <= 0)
                continue;
            names.Add(System.FormattableString.Invariant($"{v.Name} {w:0.00}"));
            if (v.SetExposure)
                r.Exposure = Mathf.Lerp(r.Exposure, v.Exposure * (UserSettings.ForwardPlus() ? 1f : CompatExposure), w);
            if (v.SetAmbient)
            {
                r.AmbientColour = r.AmbientColour.Lerp(Unit(v.AmbientColour.SrgbToLinear()), w);
                r.AmbientEnergy = Mathf.Lerp(r.AmbientEnergy, v.AmbientEnergy * (UserSettings.ForwardPlus() ? 1f : CompatAmbientScale), w);
            }
            if (v.SetFog)
            {
                r.FogOn = true;
                r.FogColour = r.FogColour.Lerp(v.FogColour.SrgbToLinear(), w);
                r.FogDensity = Mathf.Lerp(b.FogOn ? r.FogDensity : 0f, v.FogDensity, w);
            }
            if (v.SetKeyEnergy)
                r.KeyEnergy = Mathf.Lerp(r.KeyEnergy, v.KeyEnergy * (UserSettings.ForwardPlus() ? 1f : CompatKeyScale), w);
            if (v.SetSaturation)
                r.Saturation = Mathf.Lerp(r.Saturation, v.Saturation, w);
            if (v.SetVolumetricDensity)
                r.VolumetricDensity = Mathf.Lerp(r.VolumetricDensity, v.VolumetricDensity, w);
        }
        r.Volumes = string.Join(", ", names);
        return r;
    }

    /// <summary>What does not follow the look or the camera: the sky, the tone map's kind, glow, SSAO/SSIL/SSR, GI, the shadows.</summary>
    private void ApplyStatic(MapLighting m)
    {
        var e = _env.Environment;
        bool fwd = UserSettings.ForwardPlus();
        string q = UserSettings.Current.Lighting;
        e.BackgroundMode = Environment.BGMode.Sky;
        _sky ??= new Sky();
        e.Sky = _sky;
        e.SkyRotation = Vector3.Zero;
        switch (m.SkyMode)
        {
            case SkyKind.Physical:
                _sky.SkyMaterial = _physical ??= new PhysicalSkyMaterial();
                _physical.EnergyMultiplier = m.SkyEnergy;
                break;
            case SkyKind.Panorama when m.SkyPanorama.Length > 0 && ResourceLoader.Exists(m.SkyPanorama):
                _sky.SkyMaterial = _panorama ??= new PanoramaSkyMaterial();
                _panorama.Panorama = ResourceLoader.Load<Texture2D>(m.SkyPanorama);
                _panorama.EnergyMultiplier = m.SkyEnergy;
                break;
            default:
                _sky.SkyMaterial = _procedural ??= new ProceduralSkyMaterial();
                _procedural.EnergyMultiplier = m.SkyEnergy;
                _procedural.SunAngleMax = 30f;
                if (m.SkyMode == SkyKind.Procedural || m.SkyMode == SkyKind.Panorama)
                    (_procedural.SkyTopColor, _procedural.SkyHorizonColor, _procedural.GroundHorizonColor, _procedural.GroundBottomColor) =
                        (m.SkyTop, m.SkyHorizon, m.SkyHorizon, m.SkyGround);
                break;
        }
        _sky.ProcessMode = Sky.ProcessModeEnum.Automatic;
        e.AmbientLightSource = Environment.AmbientSource.Color;
        e.ReflectedLightSource = Environment.ReflectionSource.Sky;
        e.TonemapMode = m.ToneMapper switch
        {
            ToneMap.Linear => Environment.ToneMapper.Linear,
            ToneMap.Reinhard => Environment.ToneMapper.Reinhardt,
            ToneMap.Aces => Environment.ToneMapper.Aces,
            ToneMap.Agx => Environment.ToneMapper.Agx,
            _ => Environment.ToneMapper.Filmic,
        };
        e.TonemapWhite = m.ToneWhite;
        e.TonemapAgxWhite = m.ToneWhite;
        e.AdjustmentEnabled = true;
        e.AdjustmentContrast = m.ToneContrast;
        e.AdjustmentBrightness = 1f;
        e.GlowEnabled = m.Glow && !GameSettings.Flag("no-glow");
        e.GlowIntensity = m.GlowIntensity;
        e.GlowHdrThreshold = m.GlowThreshold;
        e.GlowBloom = m.GlowBloom;
        e.GlowBlendMode = Environment.GlowBlendModeEnum.Softlight;
        e.GlowNormalized = false;
        for (int i = 0; i < 7; i++)
            e.SetGlowLevel(i, i is 1 or 2 or 3 ? 1f : 0f);
        e.FogMode = Environment.FogModeEnum.Depth;
        e.FogSkyAffect = 0f;
        e.FogAerialPerspective = 0f;
        // Forward+ only (Compatibility draws none: left off there)
        e.SsaoEnabled = fwd && m.ScreenSsao && q != "low";
        e.SsaoRadius = m.ScreenSsaoRadius;
        e.SsaoIntensity = m.ScreenSsaoIntensity;
        e.SsilEnabled = fwd && m.ScreenSsil && q == "high";
        e.SsilIntensity = m.ScreenSsilIntensity;
        e.SsrEnabled = fwd && m.ScreenSsr && q == "high";
        e.VolumetricFogEnabled = fwd && m.VolumetricFog && q != "low";
        e.VolumetricFogAlbedo = m.VolumetricAlbedo;
        e.VolumetricFogEmission = m.VolumetricEmission;
        e.VolumetricFogAnisotropy = m.VolumetricAnisotropy;
        e.VolumetricFogLength = m.VolumetricLength;
        e.VolumetricFogSkyAffect = 0f;
        if (fwd)
            RenderingServer.EnvironmentSetVolumetricFogVolumeSize(q == "high" ? 128 : 64, q == "high" ? 96 : 48);
        var gi = EffectiveGi(m.GiMode);
        e.SdfgiEnabled = gi == GiMode.Sdfgi;
        if (gi == GiMode.Sdfgi)
        {
            e.SdfgiCascades = Mathf.Clamp(m.GiCascades, 1, 8);
            e.SdfgiMinCellSize = Mathf.Max(m.GiCellSize, 0.1f);
            e.SdfgiEnergy = m.GiEnergy;
            e.SdfgiReadSkyLight = true;
        }
        UpdateVoxel(m, gi == GiMode.VoxelGi);
        // the shadows by the quality
        var sun = ExternalSun ?? _sun;
        if (ExternalSun == null)
            _sun.ShadowEnabled = m.KeyShadows;
        sun.DirectionalShadowMode = q == "high" ? DirectionalLight3D.ShadowMode.Parallel4Splits : DirectionalLight3D.ShadowMode.Parallel2Splits;
        sun.DirectionalShadowMaxDistance = q switch { "low" => 1200f, "medium" => 2000f, _ => 3000f };
        if (ExternalSun == null)
            _sun.LightAngularDistance = m.KeyAngularSize;
        RenderingServer.DirectionalShadowAtlasSetSize(q == "high" ? 4096 : 2048, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(q switch
        {
            "low" => RenderingServer.ShadowQuality.SoftVeryLow,
            "medium" => RenderingServer.ShadowQuality.SoftLow,
            _ => RenderingServer.ShadowQuality.SoftMedium,
        });
        if (_map != null)
            SetClassicSky(_map, !m.SkyHideClassic);
        RenderingServer.GlobalShaderParameterSet(Variants.Name("ffx_vertex_light"), Mathf.Clamp(m.VertexLight, 0, 1));
    }

    private ResolvedLighting? _applied;

    /// <summary>What follows the look and the camera: the sun, the ambient, the fog, the exposure, the sky's game colours.</summary>
    private void ApplyDynamic(MapLighting m, ResolvedLighting r)
    {
        if (_applied is { } a && Same(a, r))
            return;
        _applied = r;
        var e = _env.Environment;
        _sun.Visible = r.KeyOn && ExternalSun == null;
        if (r.KeyOn && ExternalSun == null)
        {
            var up = Mathf.Abs(r.KeyDirection.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
            _sun.GlobalBasis = Basis.LookingAt(r.KeyDirection, up);
            _sun.LightColor = r.KeyColour.LinearToSrgb();
            _sun.LightEnergy = r.KeyEnergy;
        }
        e.AmbientLightColor = r.AmbientColour.LinearToSrgb();
        e.AmbientLightEnergy = r.AmbientEnergy;
        e.AmbientLightSkyContribution = m.AmbientSkyContribution;
        e.TonemapExposure = r.Exposure;
        e.AdjustmentSaturation = r.Saturation;
        e.FogEnabled = r.FogOn;
        e.FogLightColor = r.FogColour.LinearToSrgb();
        e.FogDensity = r.FogDensity;
        e.FogDepthBegin = r.FogBegin;
        e.FogDepthEnd = r.FogEnd;
        e.FogDepthCurve = r.FogCurve;
        e.VolumetricFogDensity = r.VolumetricDensity;
        if (m.SkyMode == SkyKind.Game && _procedural != null && _look is { } look)
        {
            // the sky from the background (zenith) and fog (horizon) colours, as the game shows them
            var bg = Raw(look.Background);
            var fog = Raw(look.FogColour);
            _procedural.SkyTopColor = bg;
            _procedural.SkyHorizonColor = fog.Lerp(bg, 0.3f);
            _procedural.GroundHorizonColor = fog;
            _procedural.GroundBottomColor = fog.Darkened(0.5f);
        }
    }

    private static bool Same(ResolvedLighting a, ResolvedLighting b) =>
        a.KeyOn == b.KeyOn && a.KeyDirection.IsEqualApprox(b.KeyDirection) && a.KeyColour.IsEqualApprox(b.KeyColour) && Mathf.IsEqualApprox(a.KeyEnergy, b.KeyEnergy)
        && a.AmbientColour.IsEqualApprox(b.AmbientColour) && Mathf.IsEqualApprox(a.AmbientEnergy, b.AmbientEnergy)
        && a.FogOn == b.FogOn && a.FogColour.IsEqualApprox(b.FogColour) && Mathf.IsEqualApprox(a.FogDensity, b.FogDensity)
        && Mathf.IsEqualApprox(a.FogBegin, b.FogBegin) && Mathf.IsEqualApprox(a.FogEnd, b.FogEnd) && Mathf.IsEqualApprox(a.FogCurve, b.FogCurve)
        && Mathf.IsEqualApprox(a.Exposure, b.Exposure) && Mathf.IsEqualApprox(a.Saturation, b.Saturation)
        && Mathf.IsEqualApprox(a.VolumetricDensity, b.VolumetricDensity);

    /// <summary>Shows or hides the map's classic sky parts (<see cref="MapLook.Sky"/>).</summary>
    private static void SetClassicSky(Node3D map, bool shown)
    {
        if (MapLook.Find(map) is not { } look)
            return;
        foreach (var mi in look.SkyMeshes)
            if (GodotObject.IsInstanceValid(mi))
                mi.Visible = shown;
    }

    // ---- VoxelGI ------------------------------------------------------------------------------

    /// <summary>Where a map's VoxelGI bake is kept (on the player's machine: it is made from the game's content).</summary>
    public static string BakePath(string level) => $"user://remaster/bake/{PartIds.LevelKey(level)}/voxelgi.res";

    /// <summary>
    /// The map's VoxelGI (<see cref="GiMode.VoxelGi"/>): over <see cref="MapLighting.GiSize"/> (or the
    /// map's middle), its data from the bake kept for the map, else baked now from the map's lit
    /// surfaces (their albedo textures as the voxelizer reads them) and kept.
    /// </summary>
    private void UpdateVoxel(MapLighting m, bool on)
    {
        if (!on || _map == null)
        {
            if (_voxel != null)
                _voxel.Visible = false;
            return;
        }
        var (centre, size) = VoxelBox(m, _map);
        string key = $"{_level}|{centre}|{size}";
        if (_voxel == null)
        {
            _voxel = new VoxelGI { Name = "VoxelGI", Subdiv = VoxelGI.SubdivEnum.Subdiv128 };
            AddChild(_voxel);
        }
        _voxel.Visible = true;
        if (_voxelFor == key && _voxel.Data != null)
            return;
        _voxelFor = key;
        _voxel.GlobalPosition = centre;
        _voxel.Size = size;
        var path = BakePath(_level);
        if (ResourceLoader.Exists(path) && ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace) is VoxelGIData data
            && data.GetBounds().Size.IsEqualApprox(size))
        {
            _voxel.Data = data;
            data.Energy = m.GiEnergy;
            VoxelState = "cached";
            GD.Print($"ffx: remaster lighting: VoxelGI from {path}");
            return;
        }
        var t0 = Time.GetTicksMsec();
        var swapped = BakeMaterials(_map);
        _voxel.Bake(_map, false);
        foreach (var (mi, s, mat) in swapped)
            mi.SetSurfaceOverrideMaterial(s, mat);
        if (_voxel.Data != null)
        {
            _voxel.Data.Energy = m.GiEnergy;
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(path.GetBaseDir()));
            ResourceSaver.Save(_voxel.Data, path);
        }
        VoxelState = $"baked in {Time.GetTicksMsec() - t0} ms";
        GD.Print($"ffx: remaster lighting: VoxelGI {VoxelState} ({size}, {swapped.Count} surface(s)), kept in {path}");
    }

    /// <summary>The VoxelGI's box: the file's, else the map's box around its middle (at most 1,200 x 400 x 1,200).</summary>
    public static (Vector3 Centre, Vector3 Size) VoxelBox(MapLighting m, Node3D map)
    {
        if (m.GiSize.X > 0 && m.GiSize.Y > 0 && m.GiSize.Z > 0)
            return (m.GiCentre, m.GiSize);
        Aabb? box = null;
        if (MapLook.Find(map) is { } look)
            foreach (var mi in look.Meshes)
                if (GodotObject.IsInstanceValid(mi) && !look.SkyMeshes.Contains(mi))
                {
                    var b = mi.GlobalTransform * mi.GetAabb();
                    box = box is { } x ? x.Merge(b) : b;
                }
        var bb = box ?? new Aabb(new Vector3(-600, -200, -600), new Vector3(1200, 400, 1200));
        var size = new Vector3(Mathf.Min(bb.Size.X, 1200), Mathf.Min(bb.Size.Y, 400), Mathf.Min(bb.Size.Z, 1200));
        return (bb.GetCenter(), size);
    }

    /// <summary>
    /// For the bake: each lit map surface given a plain material the voxelizer reads (its albedo
    /// texture; Godot's voxelizer sees only Godot's own materials). Returns the surfaces and the
    /// materials to put back.
    /// </summary>
    private static List<(MeshInstance3D Mesh, int Surface, Material? Was)> BakeMaterials(Node3D map)
    {
        var r = new List<(MeshInstance3D, int, Material?)>();
        if (MapLook.Find(map) is not { } look)
            return r;
        foreach (var mi in look.Meshes)
        {
            if (!GodotObject.IsInstanceValid(mi) || look.SkyMeshes.Contains(mi))
                continue;
            for (int s = 0; s < mi.GetSurfaceOverrideMaterialCount(); s++)
            {
                var was = mi.GetSurfaceOverrideMaterial(s);
                if (was is not ShaderMaterial sm || !sm.HasMeta(Variants.Name(RemasterLook.LitTextureMeta)))
                    continue;
                using var v = sm.GetShaderParameter(Variants.Name("albedo_tex"));
                r.Add((mi, s, was));
                mi.SetSurfaceOverrideMaterial(s, new StandardMaterial3D { AlbedoTexture = v.AsGodotObject() as Texture2D });
            }
        }
        return r;
    }
}
