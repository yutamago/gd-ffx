#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using GdFfx.Remaster.Materialize;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// A texture's regions as the editor keeps them (R2b phase 6; <see cref="RemasterTexture.Regions"/>):
/// each a dictionary (its keys below). A region's mask is named by its content: while it is edited
/// <c>mem:&lt;key&gt;</c> (its pixels held here, so an undo step holds a name, not pixels), once
/// saved a store-relative file <c>region-mask.&lt;hash&gt;.png</c> in the texture's folder (8-bit
/// grey, at the texture's own size; gitignored with the maps: made from the game's texture).
/// </summary>
public static class MaterialRegionStore
{
    public const string Name = "name", Colour = "colour", Profile = "profile", ProfileHash = "profile_hash", Exceptions = "exceptions",
        Settings = "settings", Mask = "mask", Feather = "feather", Origin = "origin";

    /// <summary>A new region's feather (classic texels).</summary>
    public const float DefaultFeather = 1.5f;

    /// <summary>The overlay's colours, one per region in turn.</summary>
    public static readonly Color[] Palette =
    [
        new(0.95f, 0.35f, 0.3f), new(0.3f, 0.75f, 0.35f), new(0.3f, 0.55f, 0.95f), new(0.95f, 0.8f, 0.25f),
        new(0.75f, 0.4f, 0.9f), new(0.25f, 0.85f, 0.85f), new(0.95f, 0.55f, 0.15f), new(0.6f, 0.6f, 0.6f),
    ];

    private static readonly Dictionary<string, (byte[] Mask, int W, int H)> Held = new();

    /// <summary>Forgets the masks held in memory (files are read again; the checks).</summary>
    public static void Forget()
    {
        lock (Held)
            Held.Clear();
    }

    /// <summary>A deep copy of a region list (an undo step's, an entry's).</summary>
    public static Godot.Collections.Array Copy(Godot.Collections.Array? regions)
    {
        var r = new Godot.Collections.Array();
        if (regions == null)
            return r;
        foreach (var v in regions)
            if (v.VariantType == Variant.Type.Dictionary)
                r.Add(TextureStore.Sorted(Deep(v.AsGodotDictionary())));
        return r;
    }

    private static Godot.Collections.Dictionary Deep(Godot.Collections.Dictionary d)
    {
        var o = new Godot.Collections.Dictionary();
        foreach (var (k, v) in d)
            o[k] = v.VariantType == Variant.Type.Dictionary ? TextureStore.Sorted(v.AsGodotDictionary()) : v;
        return o;
    }

    /// <summary>Holds <paramref name="mask"/> in memory and returns its name (<c>mem:&lt;key&gt;</c>).</summary>
    public static string Hold(byte[] mask, int w, int h)
    {
        var key = "mem:" + MaterialRegions.Key(mask, w, h);
        lock (Held)
            Held[key] = ((byte[])mask.Clone(), w, h);
        return key;
    }

    /// <summary>A mask by its name (held, or a store-relative file), or null.</summary>
    public static (byte[] Mask, int W, int H)? Load(string name)
    {
        lock (Held)
            if (Held.TryGetValue(name, out var m))
                return ((byte[])m.Mask.Clone(), m.W, m.H);
        if (name.Length == 0 || name.StartsWith("mem:", System.StringComparison.Ordinal))
            return null;
        var path = TextureStore.Absolute(name);
        if (!FileAccess.FileExists(path) || Image.LoadFromFile(ProjectSettings.GlobalizePath(path)) is not { } img || img.IsEmpty())
            return null;
        if (img.IsCompressed())
            img.Decompress();
        img.Convert(Image.Format.L8);
        var r = (img.GetData(), img.GetWidth(), img.GetHeight());
        lock (Held)
            Held[name] = ((byte[])r.Item1.Clone(), r.Item2, r.Item3);
        return r;
    }

    /// <summary>Writes a held mask as the texture's file (content-named, kept when there) and returns its store-relative path; a file's name as it is.</summary>
    public static string Persist(string id, string name)
    {
        if (!name.StartsWith("mem:", System.StringComparison.Ordinal) || Load(name) is not { } m)
            return name;
        var img = Image.CreateFromData(m.W, m.H, false, Image.Format.L8, m.Mask);
        var rel = TextureStore.WriteImage(id, "region-mask", img, TexturePreset.Mask);
        lock (Held)
            Held[rel] = ((byte[])m.Mask.Clone(), m.W, m.H);
        return rel;
    }

    public static string NameOf(Godot.Collections.Dictionary d) => d.TryGetValue(Name, out var v) ? v.AsString() : "";

    public static string ProfileOf(Godot.Collections.Dictionary d) => d.TryGetValue(Profile, out var v) ? v.AsString() : "";

    public static Godot.Collections.Dictionary ExceptionsOf(Godot.Collections.Dictionary d) =>
        d.TryGetValue(Exceptions, out var v) && v.VariantType == Variant.Type.Dictionary ? v.AsGodotDictionary() : new Godot.Collections.Dictionary();

    public static float FeatherOf(Godot.Collections.Dictionary d) => d.TryGetValue(Feather, out var v) ? (float)v.AsDouble() : DefaultFeather;

    public static Color ColourOf(Godot.Collections.Dictionary d, int i) => d.TryGetValue(Colour, out var v) && v.VariantType == Variant.Type.Color ? v.AsColor() : Palette[i % Palette.Length];

    /// <summary>
    /// A region's settings: its profile's (the winning layer's) with its exceptions over them; when
    /// the profile is gone (or it has none), the settings as baked (<see cref="Settings"/>: the
    /// differences from the defaults) with the exceptions over them.
    /// </summary>
    public static PbrRecipe Recipe(Godot.Collections.Dictionary d)
    {
        var ex = ExceptionsOf(d);
        if (ProfileOf(d) is { Length: > 0 } p && MaterialProfiles.Load(p) is { } prof)
            return MaterialProfiles.Effective(prof, ex);
        var r = new PbrRecipe { Version = 3 };
        if (d.TryGetValue(Settings, out var s) && s.VariantType == Variant.Type.Dictionary)
            foreach (var (k, v) in s.AsGodotDictionary())
                r.Set(k.AsString(), v);
        foreach (var (k, v) in ex)
            r.Set(k.AsString(), v);
        return r;
    }

    /// <summary>The graph's regions of <paramref name="regions"/> (their masks loaded; one without a mask is left out).</summary>
    public static List<RegionInput> Inputs(Godot.Collections.Array? regions, System.Func<Godot.Collections.Dictionary, PbrRecipe>? recipe = null)
    {
        var r = new List<RegionInput>();
        if (regions == null)
            return r;
        foreach (var v in regions)
        {
            if (v.VariantType != Variant.Type.Dictionary)
                continue;
            var d = v.AsGodotDictionary();
            var maskName = d.TryGetValue(Mask, out var mv) ? mv.AsString() : "";
            if (Load(maskName) is not { } m)
                continue;
            r.Add(new RegionInput(NameOf(d), (recipe ?? Recipe)(d), MaterialRegions.Key(m.Mask, m.W, m.H), MaterialRegions.ToRgba(m.Mask, m.W, m.H), FeatherOf(d)));
        }
        return r;
    }

    /// <summary>
    /// <paramref name="regions"/> as an entry keeps them after a bake: each mask written
    /// (content-named), its profile's hash now and its settings as baked; the exceptions worked out
    /// against the profile as it is (none taken over by it).
    /// </summary>
    public static Godot.Collections.Array Baked(string id, Godot.Collections.Array regions, IReadOnlyList<PbrRecipe> recipes)
    {
        var o = new Godot.Collections.Array();
        int i = 0;
        foreach (var v in regions)
        {
            if (v.VariantType != Variant.Type.Dictionary)
                continue;
            var d = TextureStore.Sorted(Deep(v.AsGodotDictionary()));
            var r = i < recipes.Count ? recipes[i] : Recipe(d);
            i++;
            d[Mask] = Persist(id, d.TryGetValue(Mask, out var mv) ? mv.AsString() : "");
            d[Settings] = MaterialProfiles.Differences(r, new PbrRecipe());
            if (ProfileOf(d) is { Length: > 0 } p && MaterialProfiles.Load(p) is { } prof)
            {
                d[ProfileHash] = MaterialProfiles.Hash(prof);
                d[Exceptions] = Own(MaterialProfiles.Differences(r, prof));
            }
            else
            {
                d[Profile] = "";
                d[ProfileHash] = "";
                d[Exceptions] = new Godot.Collections.Dictionary();
            }
            o.Add(TextureStore.Sorted(d));
        }
        return o;
    }

    /// <summary><paramref name="d"/> without the settings a region takes from the texture (<see cref="MaterialRegions.Shared"/>): a region's own exceptions.</summary>
    public static Godot.Collections.Dictionary Own(Godot.Collections.Dictionary d)
    {
        var o = new Godot.Collections.Dictionary();
        foreach (var (k, v) in d)
            if (System.Array.IndexOf(MaterialRegions.Shared, k.AsString()) < 0)
                o[k] = v;
        return o;
    }

    /// <summary>A region's mask, as the graph's input is made (the checks).</summary>
    public static RgbaImage? MaskImage(Godot.Collections.Dictionary d) =>
        Load(d.TryGetValue(Mask, out var mv) ? mv.AsString() : "") is { } m ? MaterialRegions.ToRgba(m.Mask, m.W, m.H) : null;
}
#endif
