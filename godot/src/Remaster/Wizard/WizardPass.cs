using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx.Remaster.Wizard;

/// <summary>One texture of a map as the wizard sees it: how the map draws it, its features and the classification.</summary>
public sealed record WizardTexture(string Id, TextureUseInfo Use, TextureFeatures Features, Classification Class);

/// <summary>
/// The remaster wizard's first pass over one map (docs/remaster-plan.md, "R4 in detail"), the part
/// the editor and the game share: the map's colour textures with a kind and a starter profile each
/// (<see cref="TextureClassifier"/>), and its remastered lighting from the game's look (R3's
/// defaults, which follow the game's key light, ambient, fog and sky) with GI chosen by the map's
/// size and the exposure matched to the classic picture when there is a renderer. The editor stages
/// it for review (<c>addons/ffx_remaster/Wizard.cs</c>); <c>--selftest-wizard</c> runs it in the game.
/// </summary>
public static class WizardPass
{
    /// <summary>VoxelGI's box at most (render units, <see cref="RemasterLighting.VoxelBox"/>): a map that fits in 1.5 times it takes VoxelGI, a bigger one SDFGI.</summary>
    public static readonly Vector3 VoxelMax = new(1200, 400, 1200);

    /// <summary>A texture smaller than this on its longer side is left classic (a flare, a dot, a strip: no relief to make).</summary>
    public const int MinSide = 32;

    /// <summary>
    /// The colour textures of the classic map <paramref name="map"/> (loaded; render space), each
    /// classified from its pixels (<paramref name="pixels"/>: the texture's colour image, the
    /// replacement or the classic one; null when unreadable: kept classic) and how the map draws it;
    /// sorted by area drawn, largest first. And the map's box without its sky.
    /// </summary>
    public static (List<WizardTexture> Textures, Aabb Box) Analyze(Node map, string level, Func<string, Image?> pixels)
    {
        var (uses, box) = MapTextures.Collect(map);
        var list = new List<WizardTexture>();
        foreach (var (id, use) in uses.OrderByDescending(kv => kv.Value.Area).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (use.Surfaces == 0)
                continue; // only drawn by the water's shaders: not a map material at all
            var img = pixels(id);
            if (img == null || img.IsEmpty())
                continue;
            var f = TextureFeatures.Of(PbrBuilder.From(img));
            var c = TextureClassifier.Classify(id, f, use, level);
            if (Math.Max(f.Width, f.Height) < MinSide && c.Kind != TextureKind.Keep)
                c = c with { Kind = TextureKind.Keep, Reasons = [.. c.Reasons, new ScoreReason(TextureKind.Keep, $"tiny ({f.Width}x{f.Height})", 0, 9)] };
            list.Add(new WizardTexture(id, use, f, c));
        }
        return (list, box);
    }

    /// <summary>The GI for a map of <paramref name="box"/> (no sky): VoxelGI where its box covers most of it, else SDFGI (high quality only; medium and low draw none, as R3's quality says).</summary>
    public static GiMode GiFor(Aabb box) =>
        box.Size.X <= VoxelMax.X * 1.5f && box.Size.Y <= VoxelMax.Y * 1.5f && box.Size.Z <= VoxelMax.Z * 1.5f ? GiMode.VoxelGi : GiMode.Sdfgi;

    /// <summary>
    /// The wizard's lighting for <paramref name="level"/>: R3's defaults (the game's key light as
    /// the sun, its ambient and fog, a sky in the game's colours) with GI for the map's size, the
    /// exposure as given (1: the default).
    /// </summary>
    public static MapLighting Lighting(string level, Aabb box, float exposure = 1f)
    {
        var m = (MapLighting)LightingLayers.Defaults.Duplicate(true);
        m.Level = PartIds.LevelKey(level);
        m.GiMode = GiFor(box);
        m.ToneExposure = exposure;
        return m;
    }

    /// <summary>
    /// The exposure that brings the remastered picture's mean lightness to the classic one's, from
    /// <paramref name="r"/> (OKLab lightness is about the cube root of luminance, so luminance goes
    /// as its cube; a tone map bends it, so this is one step, measured again after): the
    /// <paramref name="current"/> exposure scaled, within 0.6 to 1.6, in steps of 0.05. The same
    /// exposure when the lightness is within 0.02.
    /// </summary>
    public static float TuneExposure(float current, ColourBalance.Result r)
    {
        if (Math.Abs(r.DL) <= 0.02f || r.Remastered.L <= 0.01f)
            return current;
        float ratio = MathF.Pow(r.Classic.L / r.Remastered.L, 3f);
        return Mathf.Snapped(Math.Clamp(current * ratio, 0.6f, 1.6f), 0.05f);
    }

    /// <summary>The recipe <paramref name="profile"/> makes (its settings as they are; null: no such profile).</summary>
    public static PbrRecipe? Recipe(string profile, int maxSide = 0)
    {
        if (MaterialProfiles.Load(profile) is not { } p)
            return null;
        var r = MaterialProfiles.Effective(p, null);
        if (maxSide > 0)
            r.MaxSide = Math.Min(r.MaxSide, maxSide);
        return r;
    }

    /// <summary>What the pass did, for the map's notes: "wizard pass 2026-11-18: 12 materials (rock 4, sand 8), 9 kept classic; lighting from the game's look, VoxelGI, exposure 1.1".</summary>
    public static string Note(IEnumerable<(string Profile, bool Made)> textures, MapLighting lighting, string when)
    {
        textures = textures.ToList();
        var made = textures.Where(t => t.Made && t.Profile.Length > 0).GroupBy(t => t.Profile).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => $"{g.Key} {g.Count()}").ToList();
        int kept = textures.Count(t => !t.Made || t.Profile.Length == 0);
        int count = textures.Count(t => t.Made && t.Profile.Length > 0);
        return $"wizard pass {when}: {count} material(s)"
            + (made.Count > 0 ? $" ({string.Join(", ", made)})" : "") + $", {kept} kept classic; lighting from the game's look, GI {lighting.GiMode}"
            + (Math.Abs(lighting.ToneExposure - 1f) > 1e-4f ? $", exposure {lighting.ToneExposure:0.##}" : "");
    }
}
