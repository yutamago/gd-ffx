using Godot;

namespace GdFfx.Remaster;

/// <summary>Where a value of the remastered lighting comes from: the game's look (as the map's look data says, live), this file's own value, or nothing.</summary>
public enum LightSource
{
    /// <summary>From the game's look, converted (docs/remaster-editor.md, "Lighting").</summary>
    Game,
    /// <summary>This file's value.</summary>
    Custom,
    /// <summary>None (no key light, no fog).</summary>
    Off,
}

/// <summary>The sky of a remastered map.</summary>
public enum SkyKind
{
    /// <summary>A procedural sky in the game's background (zenith) and fog (horizon) colours; the classic sky parts stay drawn.</summary>
    Game,
    /// <summary>A procedural sky in this file's colours.</summary>
    Procedural,
    /// <summary>Godot's physical sky (Rayleigh and Mie scattering, the sun from the key light).</summary>
    Physical,
    /// <summary>A panorama (an HDRI image of the project's, <see cref="MapLighting.SkyPanorama"/>).</summary>
    Panorama,
}

/// <summary>The global illumination of a remastered map (Forward+ only; Compatibility draws none).</summary>
public enum GiMode
{
    None,
    /// <summary>Godot's signed distance field GI: real time, no bake.</summary>
    Sdfgi,
    /// <summary>A VoxelGI over the map, baked on the player's machine at the first visit and kept in <c>user://remaster/bake/</c>.</summary>
    VoxelGi,
    /// <summary>Baked lightmaps (not yet: the maps have no lightmap UVs; drawn as <see cref="None"/>).</summary>
    LightmapGi,
}

/// <summary>How a remastered map draws its blended parts (the game's painted layers: wet sand, shallow water, shading over the ground).</summary>
public enum BlendedLook
{
    /// <summary>In their classic shaders (their gamma colours, as the water and the characters): the shore keeps its colours.</summary>
    Classic,
    /// <summary>Lit like the rest (their colour textures or materials under the real lights).</summary>
    Lit,
}

/// <summary>Godot's tone mappers, as a remastered map's environment names them.</summary>
public enum ToneMap
{
    Linear,
    Reinhard,
    Filmic,
    Aces,
    Agx,
}

/// <summary>
/// One map's remastered lighting and atmosphere (R3; docs/remaster-editor.md, "Lighting"), a
/// Godot resource: <c>res://remaster/maps/&lt;level&gt;/lighting.tres</c> beside the map's
/// overrides (a pack's <c>res://mods/&lt;pack&gt;/remaster/maps/&lt;level&gt;/lighting.tres</c>;
/// the last layer's file wins). Without one a map draws with the defaults below, which take the
/// key light, the ambient, the sky's and the fog's colours from the game's look. Used only while
/// the map draws remastered; the classic path never reads it.
/// The game's lights and fog are objects with stable IDs (<see cref="KeyId"/> and the others):
/// the editor shows them as nodes.
/// </summary>
[GlobalClass]
[Tool]
public partial class MapLighting : Resource
{
    /// <summary>The level key (<c>bsil00</c>); informational, the file's folder decides.</summary>
    [Export] public string Level { get; set; } = "";

    // ---- the game's key light (Look.lights[0]) as the sun -----------------------------------

    [ExportGroup("Key light", "Key")]
    /// <summary>The direction: the game's (turned as the characters' shader turns it), this file's (<see cref="KeyDirection"/>), or no key light.</summary>
    [Export] public LightSource KeyMode { get; set; } = LightSource.Game;
    /// <summary>The way the light travels (render space; with <see cref="LightSource.Custom"/>).</summary>
    [Export] public Vector3 KeyDirection { get; set; } = new(-0.4f, -0.75f, -0.5f);
    /// <summary>The colour: the game's, kept by <see cref="KeyGameTint"/> and warmed by <see cref="KeyWarmth"/>, or <see cref="KeyColour"/>.</summary>
    [Export] public LightSource KeyColourMode { get; set; } = LightSource.Game;
    [Export] public Color KeyColour { get; set; } = new(1f, 0.95f, 0.86f);
    /// <summary>How much of the game's key light colour stays (0: white; the game's is made for characters and leans blue).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float KeyGameTint { get; set; } = 0.2f;
    /// <summary>A sunlight warmth over the game's colour (0: none, 1: about 4,500 K).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float KeyWarmth { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0,16,0.01")] public float KeyEnergy { get; set; } = 2.0f;
    [Export] public bool KeyShadows { get; set; } = true;
    /// <summary>
    /// The sun's apparent size in degrees: above 0, soft shadows by Godot's PCSS (costly: 0 by
    /// default; the GPU check lost the D3D12 device on an Intel Arc with 1.5 and the other R3 costs).
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.1")] public float KeyAngularSize { get; set; }

    // ---- the ambient (the sky's fill) -------------------------------------------------------

    [ExportGroup("Ambient", "Ambient")]
    /// <summary>The colour: the game's (its map and character ambient, kept by <see cref="AmbientGameTint"/>), or <see cref="AmbientColour"/>.</summary>
    [Export] public LightSource AmbientMode { get; set; } = LightSource.Game;
    [Export] public Color AmbientColour { get; set; } = new(0.8f, 0.85f, 0.95f);
    [Export(PropertyHint.Range, "0,1,0.05")] public float AmbientGameTint { get; set; }
    [Export(PropertyHint.Range, "0,16,0.01")] public float AmbientEnergy { get; set; } = 0.65f;
    /// <summary>How much of the ambient comes from the sky (0: all from the colour).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float AmbientSkyContribution { get; set; }
    /// <summary>
    /// How much of the game's baked vertex light stays under the real lights (0..1). All of it by
    /// default: it holds the map's colour and shading as the game's artists lit it (the colour check
    /// measured it: without it the sand loses its hue); lower it where GI or added lights take over.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float VertexLight { get; set; } = 1f;
    /// <summary>
    /// The game's blended map layers: classic (by default; lit, the game's gamma blends of them come
    /// out paler in linear light: Besaid's lagoon lost its cyan) or lit.
    /// </summary>
    [Export] public BlendedLook BlendedParts { get; set; } = BlendedLook.Classic;

    // ---- fog ------------------------------------------------------------------------------

    [ExportGroup("Fog", "Fog")]
    /// <summary>The game's depth fog (colour, range, curve, limit), this file's, or none.</summary>
    [Export] public LightSource FogMode { get; set; } = LightSource.Game;
    [Export] public Color FogColour { get; set; } = new(0.6f, 0.7f, 0.85f);
    [Export(PropertyHint.Range, "0,1,0.01")] public float FogDensity { get; set; } = 0.15f;
    [Export] public float FogBegin { get; set; } = 60f;
    [Export] public float FogEnd { get; set; } = 9000f;
    [Export(PropertyHint.ExpEasing)] public float FogCurve { get; set; } = 1f;

    [ExportGroup("Volumetric fog", "Volumetric")]
    /// <summary>Godot's volumetric fog (Forward+, the lighting quality high or medium).</summary>
    [Export] public bool VolumetricFog { get; set; }
    [Export(PropertyHint.Range, "0,0.2,0.0005")] public float VolumetricDensity { get; set; } = 0.004f;
    [Export] public Color VolumetricAlbedo { get; set; } = new(0.9f, 0.92f, 1f);
    [Export] public Color VolumetricEmission { get; set; } = Colors.Black;
    [Export(PropertyHint.Range, "-0.9,0.9,0.05")] public float VolumetricAnisotropy { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "16,1024,1")] public float VolumetricLength { get; set; } = 400f;

    // ---- sky ------------------------------------------------------------------------------

    [ExportGroup("Sky", "Sky")]
    [Export] public SkyKind SkyMode { get; set; } = SkyKind.Game;
    [Export] public Color SkyTop { get; set; } = new(0.3f, 0.45f, 0.75f);
    [Export] public Color SkyHorizon { get; set; } = new(0.65f, 0.75f, 0.85f);
    [Export] public Color SkyGround { get; set; } = new(0.3f, 0.28f, 0.25f);
    [Export(PropertyHint.Range, "0,16,0.01")] public float SkyEnergy { get; set; } = 1f;
    /// <summary>The panorama's image (<see cref="SkyKind.Panorama"/>): a <c>res://</c> path of the project's (an HDRI of your own; never one made from the game).</summary>
    [Export(PropertyHint.File, "*.hdr,*.exr,*.png,*.jpg,*.webp")] public string SkyPanorama { get; set; } = "";
    /// <summary>Whether the map's classic sky parts (domes, cloud rings) are hidden (a sky of Godot's in their place).</summary>
    [Export] public bool SkyHideClassic { get; set; }

    // ---- the picture ------------------------------------------------------------------------

    [ExportGroup("Tone and colour", "Tone")]
    [Export] public ToneMap ToneMapper { get; set; } = ToneMap.Filmic;
    [Export(PropertyHint.Range, "0.05,16,0.01")] public float ToneExposure { get; set; } = 1f;
    [Export(PropertyHint.Range, "0.5,16,0.01")] public float ToneWhite { get; set; } = 2.5f;
    /// <summary>Godot's colour adjustment after the tone map (1: none).</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float ToneSaturation { get; set; } = 1.15f;
    [Export(PropertyHint.Range, "0.5,2,0.01")] public float ToneContrast { get; set; } = 1f;

    [ExportGroup("Glow", "Glow")]
    [Export] public bool Glow { get; set; } = true;
    [Export(PropertyHint.Range, "0,4,0.01")] public float GlowIntensity { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0,4,0.01")] public float GlowThreshold { get; set; } = 1f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float GlowBloom { get; set; }

    [ExportGroup("Screen-space", "Screen")]
    /// <summary>SSAO (off by default: unverified on the GPU check, where the D3D12 device was lost with it on).</summary>
    [Export] public bool ScreenSsao { get; set; }
    [Export(PropertyHint.Range, "0.1,16,0.1")] public float ScreenSsaoRadius { get; set; } = 3f;
    [Export(PropertyHint.Range, "0,16,0.1")] public float ScreenSsaoIntensity { get; set; } = 1.5f;
    [Export] public bool ScreenSsil { get; set; }
    [Export(PropertyHint.Range, "0,16,0.1")] public float ScreenSsilIntensity { get; set; } = 1f;
    [Export] public bool ScreenSsr { get; set; }

    // ---- global illumination ------------------------------------------------------------------

    [ExportGroup("Global illumination", "Gi")]
    [Export] public GiMode GiMode { get; set; } = GiMode.None;
    [Export(PropertyHint.Range, "0,8,0.01")] public float GiEnergy { get; set; } = 1f;
    /// <summary>The VoxelGI's box (render units, centred on <see cref="GiCentre"/>); zero: the map's playable middle (its box, at most 1,200 x 400 x 1,200).</summary>
    [Export] public Vector3 GiSize { get; set; } = Vector3.Zero;
    [Export] public Vector3 GiCentre { get; set; } = Vector3.Zero;
    /// <summary>SDFGI's cascades (1..8) and their first cell size (render units).</summary>
    [Export(PropertyHint.Range, "1,8,1")] public int GiCascades { get; set; } = 4;
    [Export(PropertyHint.Range, "0.5,64,0.5")] public float GiCellSize { get; set; } = 2f;

    // ---- volumes ---------------------------------------------------------------------------

    [ExportGroup("")]
    /// <summary>Boxes with settings of their own (<see cref="LightingVolume"/>), blended at their edges by where the camera is.</summary>
    [Export] public Godot.Collections.Array<LightingVolume> Volumes { get; set; } = new();

    /// <summary>The stable IDs of the game's lighting objects (docs/remaster-editor.md, "Lighting").</summary>
    public static string KeyId(string level) => $"{PartIds.LevelPrefix}{PartIds.LevelKey(level)}#light/key";
    public static string AmbientId(string level) => $"{PartIds.LevelPrefix}{PartIds.LevelKey(level)}#ambient";
    public static string FogId(string level) => $"{PartIds.LevelPrefix}{PartIds.LevelKey(level)}#fog";
    public static string EnvironmentId(string level) => $"{PartIds.LevelPrefix}{PartIds.LevelKey(level)}#environment";
    public static string VolumeId(string level, string name) => $"{PartIds.LevelPrefix}{PartIds.LevelKey(level)}#volume/{name}";
}
