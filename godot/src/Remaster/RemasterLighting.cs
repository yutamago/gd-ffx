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
    public static void Set(string? choice) => _toggled = choice;

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
        GD.Print($"ffx: render path {_toggled} (toggled on {PartIds.LevelKey(level)})");
        return _toggled;
    }
}

/// <summary>
/// The remastered path's provisional lighting (R2; R3 brings the game's lights and fog as
/// editable objects, GI and the rest): while the shown map draws remastered, a directional
/// light from the game's own key light (<c>Look.lights[0]</c>, turned as the characters' shader
/// turns it), ambient from the map's ambient, a procedural sky from its background and fog
/// colours, Godot's depth fog from the game's fog, Godot's tone map and glow. The classic
/// environment (the game's colours, <see cref="ClassicPost"/> under Forward+) comes back when the
/// map draws classic again. The game's values are gamma: taken to linear for Godot's lights.
/// </summary>
public sealed partial class RemasterLighting : Node3D
{
    private readonly WorldEnvironment _env;
    private readonly ClassicPost? _post;
    private readonly Node3D? _classicLight;
    private DirectionalLight3D _sun = null!;
    private Sky? _sky;
    private ProceduralSkyMaterial? _skyMaterial;
    private (Environment.BGMode Bg, Environment.AmbientSource Ambient, Color AmbientColour, float AmbientEnergy,
        Environment.ToneMapper Tonemap, bool Glow, bool Fog, Sky? Sky)? _saved;

    /// <summary>The classic shaders' <c>ffx_linear_out</c> as set last (for tests: a headless renderer keeps no globals).</summary>
    public static float LinearOut { get; private set; }

    /// <summary>Whether the rig is on (the map draws remastered).</summary>
    public bool Active { get; private set; }

    /// <summary>The sun (for tests).</summary>
    public DirectionalLight3D Sun => _sun;

    private long _lookOf = -1;

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
            RotationDegrees = new Vector3(-55, 30, 0),
        };
        AddChild(_sun);
        ClassicMaterials.EnsureGlobals();
    }

    /// <summary>Switches the rig on or off (main thread); with it on, follows the look once a snapshot.</summary>
    public void Set(bool on, GdFfx.Api.Look? look, long decoded)
    {
        if (on != Active)
        {
            Active = on;
            if (on) Enable(); else Disable();
            _lookOf = -1;
            GD.Print($"ffx: remaster lighting {(on ? "on" : "off")}");
        }
        if (on && look is { Valid: true } && decoded != _lookOf)
        {
            _lookOf = decoded;
            Follow(look);
        }
    }

    private void Enable()
    {
        var e = _env.Environment;
        _saved = (e.BackgroundMode, e.AmbientLightSource, e.AmbientLightColor, e.AmbientLightEnergy, e.TonemapMode, e.GlowEnabled, e.FogEnabled, e.Sky);
        _skyMaterial ??= new ProceduralSkyMaterial();
        _sky ??= new Sky { SkyMaterial = _skyMaterial };
        e.BackgroundMode = Environment.BGMode.Sky;
        e.Sky = _sky;
        e.AmbientLightSource = Environment.AmbientSource.Color;
        // AgX: the classic parts (characters, the sky) come out nearly as they were, the lit ones roll off
        e.TonemapMode = Environment.ToneMapper.Agx;
        e.GlowEnabled = !GameSettings.Flag("no-glow");
        e.FogEnabled = true;
        e.FogMode = Environment.FogModeEnum.Depth;
        if (_post != null)
            _post.Enabled = false;
        if (_classicLight != null)
            _classicLight.Visible = false;
        _sun.Visible = true;
        // the classic shaders still drawing (characters, water, layers, effects): their gamma
        // values written as linear ones, which Forward+ encodes back (ClassicMaterials' ffx_out)
        // (Compatibility writes the values as they are: measured, the classic parts unchanged without it)
        LinearOut = UserSettings.ForwardPlus() ? 1f : 0f;
        RenderingServer.GlobalShaderParameterSet(Variants.Name("ffx_linear_out"), LinearOut);
    }

    private void Disable()
    {
        var e = _env.Environment;
        if (_saved is { } s)
        {
            (e.BackgroundMode, e.AmbientLightSource, e.AmbientLightColor, e.AmbientLightEnergy, e.TonemapMode, e.GlowEnabled, e.FogEnabled, e.Sky) =
                (s.Bg, s.Ambient, s.AmbientColour, s.AmbientEnergy, s.Tonemap, s.Glow, s.Fog, s.Sky);
            _saved = null;
        }
        if (_post != null)
            _post.Enabled = true;
        if (_classicLight != null)
            _classicLight.Visible = true;
        _sun.Visible = false;
        LinearOut = 0f;
        RenderingServer.GlobalShaderParameterSet(Variants.Name("ffx_linear_out"), 0f);
    }

    private static Color Raw(Vector3 c) => new(Mathf.Clamp(c.X, 0, 1), Mathf.Clamp(c.Y, 0, 1), Mathf.Clamp(c.Z, 0, 1));

    private static Color Lin(Vector3 c) => new Color(Mathf.Clamp(c.X, 0, 1), Mathf.Clamp(c.Y, 0, 1), Mathf.Clamp(c.Z, 0, 1)).SrgbToLinear();

    /// <summary>The rig from the game's look: the key light, ambient, sky and fog colours, the fog's range.</summary>
    private void Follow(GdFfx.Api.Look look)
    {
        var e = _env.Environment;
        if (look.Lights.Count > 0 && look.Lights[0].Direction.LengthSquared() > 1e-8f)
        {
            var l = look.Lights[0];
            // the characters' shader lights a surface by dot(sign x L, N): the light comes from sign x L
            var toLight = ClassicMaterials.LightDirection(l) * ClassicMaterials.LightSign;
            if (toLight.LengthSquared() > 1e-8f)
            {
                var d = -toLight.Normalized();
                var up = Mathf.Abs(d.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
                _sun.GlobalBasis = Basis.LookingAt(d, up);
            }
            var c = l.Colour * Mathf.Max(l.Level, 0.0001f);
            float peak = Mathf.Max(c.X, Mathf.Max(c.Y, c.Z));
            _sun.LightColor = peak > 0 ? Lin(c / peak) : Colors.White;
            _sun.LightEnergy = Mathf.Clamp(peak * 0.8f, 0.2f, 1.2f);
        }
        // the game's ambient (the map's and the characters') as the fill light, never black:
        // taken as it is (a gamma decode makes Besaid's 0.34 a 0.09, the shade black)
        var ambient = look.Ambient + look.CharacterAmbient;
        e.AmbientLightColor = new Color(Mathf.Clamp(ambient.X, 0.22f, 1f), Mathf.Clamp(ambient.Y, 0.22f, 1f), Mathf.Clamp(ambient.Z, 0.22f, 1f));
        e.AmbientLightEnergy = 1f;
        // the sky from the background (zenith) and fog (horizon) colours, as they are
        var bg = Raw(look.Background);
        var fog = Raw(look.FogColour);
        _skyMaterial!.SkyTopColor = bg;
        _skyMaterial.SkyHorizonColor = fog.Lerp(bg, 0.3f);
        _skyMaterial.GroundHorizonColor = fog;
        _skyMaterial.GroundBottomColor = fog.Darkened(0.5f);
        e.FogLightColor = fog;
        e.FogDepthBegin = Mathf.Max(look.FogNear, 0f);
        e.FogDepthEnd = Mathf.Max(look.FogFar, e.FogDepthBegin + 1f);
        e.FogDepthCurve = Mathf.Max(look.FogCurve, 0.01f);
        e.FogDensity = Mathf.Clamp(look.FogLimit, 0f, 1f);
        e.FogSkyAffect = 0f;
        if (_logged++ == 0)
            GD.Print(System.FormattableString.Invariant($"ffx: remaster lighting: key light {(look.Lights.Count > 0 ? look.Lights[0].Colour * look.Lights[0].Level : Vector3.Zero)}, ambient {look.Ambient} + {look.CharacterAmbient}, background {look.Background}, fog {look.FogColour} {look.FogNear}..{look.FogFar} (curve {look.FogCurve}, limit {look.FogLimit}); sun energy {_sun.LightEnergy:0.00}"));
    }

    private int _logged;
}
