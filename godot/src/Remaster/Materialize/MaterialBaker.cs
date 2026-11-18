using System.Collections.Generic;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.Remaster.Materialize;

/// <summary>Every map of a bake: the maps (8-bit), the ORM pack, the height as floats (the 16-bit file).</summary>
public sealed record BakedMaps(Dictionary<MaterialStep, RgbaImage> Maps, RgbaImage Orm, Image Height);

/// <summary>
/// A texture's material made at the full size by the workbench's graph (docs/remaster-materials.md,
/// "Speed"): on the CPU (<see cref="Cpu"/>: any thread, the checks, headless editors) or from a
/// GPU bake's read back (<see cref="GpuBake"/>), and the maps as files take them
/// (<see cref="Images"/>: the height as half floats, emission only where something glows, no
/// height without parallax). What the Materials tab's Save and Apply to queue write, and how the
/// game's checks make a material by a profile.
/// </summary>
public static class MaterialBaker
{
    /// <summary>The maps of <paramref name="original"/> by <paramref name="r"/> at the full size, on the CPU (any thread).</summary>
    public static BakedMaps Cpu(string key, RgbaImage original, PbrRecipe r, IReadOnlyDictionary<string, RgbaImage>? imports = null,
        System.Threading.CancellationToken cancel = default)
    {
        var b = new CpuBackend { Cancel = cancel };
        var inputs = new MaterialPipeline.Inputs(key, original, r, imports ?? new Dictionary<string, RgbaImage>());
        var (w, h, scale) = MaterialPipeline.Size(inputs, MaterialPipeline.FullSide);
        var outs = MaterializeGraph.Build(b, key, original, inputs.Imports, r, new BuildRequest(w, h, scale));
        var maps = new Dictionary<MaterialStep, RgbaImage>();
        foreach (var (s, t) in outs.Maps)
            maps[s] = b.Get(t).ToRgba();
        var f = b.Get(outs.Maps[MaterialStep.Height]).Red();
        var bytes = new byte[f.Length * 4];
        System.Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        var height = Image.CreateFromData(maps[MaterialStep.Height].Width, maps[MaterialStep.Height].Height, false, Image.Format.Rf, bytes);
        return new BakedMaps(maps, b.Get(outs.Orm).ToRgba(), height);
    }

    /// <summary>
    /// The files' images of a bake: albedo, normal, ORM; the height as half floats (an OpenEXR:
    /// no 8-bit terraces in parallax) when the recipe has parallax and does not skip it; the
    /// emission when it is not skipped and something glows.
    /// </summary>
    public static PbrImages Images(BakedMaps baked, PbrRecipe r)
    {
        bool height = r.HeightScale > 0 && MaterialPipeline.ModeOf(r, MaterialStep.Height) != StepMode.Skip;
        bool emission = MaterialPipeline.ModeOf(r, MaterialStep.Emission) != StepMode.Skip && MaterialPipeline.AnyLight(baked.Maps[MaterialStep.Emission]);
        Image? h = null;
        if (height)
        {
            h = (Image)baked.Height.Duplicate();
            if (h.GetFormat() != Image.Format.Rh)
                h.Convert(Image.Format.Rh);
        }
        return new PbrImages(PbrBuilder.ToImage(baked.Maps[MaterialStep.Albedo]), PbrBuilder.ToImage(baked.Maps[MaterialStep.Normal]),
            PbrBuilder.ToImage(baked.Orm), h, emission ? PbrBuilder.ToImage(baked.Maps[MaterialStep.Emission]) : null);
    }

    /// <summary>The material of <paramref name="original"/> by profile (or recipe) <paramref name="r"/>, made on the CPU, as files take it.</summary>
    public static PbrImages CpuImages(string key, RgbaImage original, PbrRecipe r) => Images(Cpu(key, original, r), r);
}
