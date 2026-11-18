#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The project's texture layer as the editor edits it (docs/remaster-editor.md, "Textures"): the
/// index <c>&lt;dir&gt;/textures.tres</c> (a <see cref="RemasterTextureSet"/>, one entry per
/// texture ID, sorted, stable text) and each texture's files in
/// <c>&lt;dir&gt;/&lt;its folder&gt;/</c> (<see cref="RemasterTextures.Folder"/>): the
/// replacement and the material's maps as PNGs named by their content (<c>albedo.&lt;hash&gt;.png</c>:
/// written once, so undo and redo touch no file), the material as <c>material.&lt;hash&gt;.tres</c>.
/// The folder is <c>res://remaster/textures</c>, or <c>--remaster-textures=DIR</c> (the editor
/// check's). Inside the project the PNGs are imported with the entry's preset
/// (<see cref="TexturePreset"/>: lossless with mipmaps; a normal map as one; a cut-out with its
/// alpha border fixed); outside it (a folder elsewhere) the material holds its maps itself.
/// The index's paths are relative to its folder, so a pack can carry the folder as it is.
/// </summary>
public static class TextureStore
{
    private static RemasterTextureSet? _held;

    public static string Dir => GameSettings.Arg("remaster-textures") is { Length: > 0 } d ? d.TrimEnd('/') : RemasterTextures.BaseDir;

    public static string IndexPath => $"{Dir}/{RemasterTextures.FileName}";

    /// <summary>Whether the folder is inside the project (its PNGs imported by Godot).</summary>
    public static bool InProject => Dir.StartsWith("res://", System.StringComparison.Ordinal);

    /// <summary>Points the game's texture layers at this store (the plugin, at start).</summary>
    public static void Attach()
    {
        RemasterTextures.ProjectIndex = IndexPath;
        _held = null;
        RemasterTextures.Invalidate();
    }

    /// <summary>The project's index (read once; a new empty one when there is none).</summary>
    public static RemasterTextureSet Set
    {
        get
        {
            if (_held != null)
                return _held;
            if (FileAccess.FileExists(IndexPath) && ResourceLoader.Load(IndexPath, "", ResourceLoader.CacheMode.Replace) is RemasterTextureSet set)
                return _held = set;
            return _held = new RemasterTextureSet();
        }
    }

    /// <summary><paramref name="id"/>'s entry in the project's layer (null: none).</summary>
    public static RemasterTexture? Find(string id)
    {
        id = id.ToLowerInvariant();
        foreach (var t in Set.Textures)
            if (t != null && t.Id == id)
                return t;
        return null;
    }

    /// <summary>Whether an entry says nothing (no replacement, no material, classic mode unset, no note).</summary>
    public static bool IsEmpty(RemasterTexture t) =>
        t.Mode == TextureMode.Edit && t.Replacement.Length == 0 && t.Material.Length == 0 && string.IsNullOrWhiteSpace(t.Note);

    /// <summary>Sets <paramref name="id"/>'s entry (a copy of <paramref name="entry"/>; an empty one removes it) and writes the index.</summary>
    public static bool Put(string id, RemasterTexture entry)
    {
        id = id.ToLowerInvariant();
        var set = Set;
        for (int i = set.Textures.Count - 1; i >= 0; i--)
            if (set.Textures[i] == null || set.Textures[i].Id == id)
                set.Textures.RemoveAt(i);
        if (!IsEmpty(entry))
        {
            var copy = (RemasterTexture)entry.Duplicate(true);
            copy.Id = id;
            copy.Exceptions = Sorted(entry.Exceptions);
            set.Textures.Add(copy);
        }
        return Write();
    }

    /// <summary>A copy of <paramref name="id"/>'s entry, or a new empty one (for an edit to start from).</summary>
    public static RemasterTexture Copy(string id)
    {
        if (Find(id) is not { } t)
            return new RemasterTexture { Id = id.ToLowerInvariant() };
        var c = (RemasterTexture)t.Duplicate(true);
        c.Exceptions = Sorted(t.Exceptions);
        return c;
    }

    /// <summary>A copy of <paramref name="d"/> with its keys in order (the index's text stays the same for the same exceptions).</summary>
    public static Godot.Collections.Dictionary Sorted(Godot.Collections.Dictionary? d)
    {
        var r = new Godot.Collections.Dictionary();
        if (d == null)
            return r;
        foreach (var k in d.Keys.Select(k => k.AsString()).OrderBy(k => k, System.StringComparer.Ordinal))
            r[k] = d[k];
        return r;
    }

    /// <summary>
    /// Writes the index: the entries sorted by ID, stable resource IDs (the entry's
    /// <c>t_&lt;hash of its ID&gt;</c>, its recipe's <c>r_...</c>), so a save that changes nothing
    /// changes no byte. An empty index removes the file.
    /// </summary>
    public static bool Write()
    {
        var set = Set;
        var list = new List<RemasterTexture>(set.Textures);
        list.RemoveAll(t => t == null);
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        set.Textures = new Godot.Collections.Array<RemasterTexture>(list);
        var path = IndexPath;
        if (list.Count == 0)
        {
            if (FileAccess.FileExists(path))
                DirAccess.RemoveAbsolute(path);
            Changed(path);
            return true;
        }
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        static void Ext(Resource? r, string path, string id)
        {
            if (r != null && r.GetIdForPath(path).Length == 0)
                r.SetIdForPath(path, id);
        }
        Ext(set.GetScript().As<Script>(), path, "1");
        foreach (var t in list)
        {
            Ext(t.GetScript().As<Script>(), path, "2");
            t.ResourceSceneUniqueId = $"t_{MapStore.Hash(t.Id)}";
            if (t.Recipe != null)
            {
                Ext(t.Recipe.GetScript().As<Script>(), path, "3");
                t.Recipe.ResourceSceneUniqueId = $"r_{MapStore.Hash(t.Id)}";
            }
        }
        set.TakeOverPath(path);
        if (ResourceSaver.Save(set, path) != Error.Ok)
        {
            GD.PrintErr($"ffx remaster: cannot write {path}");
            return false;
        }
        Changed(path);
        return true;
    }

    private static void Changed(string path)
    {
        RemasterTextures.Invalidate();
        if (path.StartsWith("res://", System.StringComparison.Ordinal))
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);
    }

    /// <summary><paramref name="t"/>'s fields (its recipe's too) as a dictionary (an undo step's argument).</summary>
    public static Godot.Collections.Dictionary ToDictionary(RemasterTexture t)
    {
        var d = Fields(t);
        if (t.Recipe != null)
            d["Recipe"] = Fields(t.Recipe);
        return d;
    }

    /// <summary>The entry <see cref="ToDictionary"/> made.</summary>
    public static RemasterTexture FromDictionary(string id, Godot.Collections.Dictionary d)
    {
        var t = new RemasterTexture();
        foreach (var (k, v) in d)
            if (k.AsString() != "Recipe")
                t.Set(k.AsString(), v);
        if (d.TryGetValue("Recipe", out var r) && r.VariantType == Variant.Type.Dictionary)
        {
            var recipe = new PbrRecipe();
            foreach (var (k, v) in r.AsGodotDictionary())
                recipe.Set(k.AsString(), v);
            t.Recipe = recipe;
        }
        t.Id = id.ToLowerInvariant();
        return t;
    }

    /// <summary>A resource's script properties (its exported fields) by name.</summary>
    private static Godot.Collections.Dictionary Fields(Resource r)
    {
        var d = new Godot.Collections.Dictionary();
        foreach (var p in r.GetPropertyList())
        {
            var usage = (PropertyUsageFlags)p["usage"].AsInt64();
            var name = p["name"].AsString();
            if ((usage & PropertyUsageFlags.ScriptVariable) == 0 || name == "Recipe")
                continue;
            var v = r.Get(name);
            // a dictionary by value (the exceptions): an undo step must not share the entry's
            d[name] = v.VariantType == Variant.Type.Dictionary ? Sorted(v.AsGodotDictionary()) : v;
        }
        return d;
    }

    // ---- files -------------------------------------------------------------------------------

    /// <summary>
    /// Writes <paramref name="img"/> as <paramref name="id"/>'s <paramref name="role"/> image
    /// (<c>&lt;role&gt;.&lt;hash&gt;.png</c> in its folder; kept when it is there already) and, in
    /// the project, imports it with <paramref name="preset"/>. Returns the path relative to the
    /// store's folder.
    /// </summary>
    public static string WriteImage(string id, string role, Image img, TexturePreset preset)
    {
        var folder = RemasterTextures.Folder(id, Dir);
        DirAccess.MakeDirRecursiveAbsolute(folder);
        // float images (the 16-bit height: half floats) as OpenEXR, single channel; the rest as PNG
        bool exr = img.GetFormat() is Image.Format.Rh or Image.Format.Rf or Image.Format.Rgh or Image.Format.Rgf
            or Image.Format.Rgbh or Image.Format.Rgbf or Image.Format.Rgbah or Image.Format.Rgbaf;
        var file = $"{folder}/{role}.{PbrBuilder.Hash(img)}.{(exr ? "exr" : "png")}";
        if (!FileAccess.FileExists(file))
        {
            var copy = (Image)img.Duplicate();
            if (copy.HasMipmaps())
                copy.ClearMipmaps();
            if (exr)
                copy.SaveExr(file, grayscale: copy.GetFormat() is Image.Format.Rh or Image.Format.Rf);
            else
                copy.SavePng(file);
        }
        if (InProject)
            Import(file, preset);
        return Relative(file);
    }

    /// <summary>The store-relative form of <paramref name="path"/>.</summary>
    public static string Relative(string path) =>
        path.StartsWith(Dir + "/", System.StringComparison.Ordinal) ? path[(Dir.Length + 1)..] : path;

    /// <summary>The absolute form of a store-relative <paramref name="path"/>.</summary>
    public static string Absolute(string path) => RemasterTextures.Resolve(path, Dir);

    /// <summary>
    /// Imports the PNG at <paramref name="file"/> (in the project) with <paramref name="preset"/>:
    /// its <c>.import</c> written first (lossless, mipmaps, no 3D detection; a normal map; a
    /// cut-out's alpha border fixed), then Godot's importer run on it, so it loads at once.
    /// </summary>
    public static void Import(string file, TexturePreset preset)
    {
        var import = file + ".import";
        if (!FileAccess.FileExists(import))
        {
            var text = "[remap]\n\nimporter=\"texture\"\ntype=\"CompressedTexture2D\"\n\n[deps]\n\nsource_file=\"" + file + "\"\n\n[params]\n\n"
                + "compress/mode=0\ncompress/high_quality=false\ncompress/lossy_quality=0.7\n"
                + $"compress/normal_map={(preset == TexturePreset.Normal ? 1 : 2)}\ncompress/channel_pack=0\n"
                + "mipmaps/generate=true\nmipmaps/limit=-1\nroughness/mode=0\nroughness/src_normal=\"\"\n"
                + $"process/fix_alpha_border={(preset == TexturePreset.CutOut || preset == TexturePreset.Colour ? "true" : "false")}\n"
                + "process/premult_alpha=false\nprocess/normal_map_invert_y=false\nprocess/hdr_as_srgb=false\n"
                + "process/hdr_clamp_exposure=false\nprocess/size_limit=0\ndetect_3d/compress_to=0\n";
            using var f = FileAccess.Open(import, FileAccess.ModeFlags.Write);
            f?.StoreString(text);
        }
        var fs = EditorInterface.Singleton.GetResourceFilesystem();
        fs.UpdateFile(file);
        if (fs.GetFileType(file).Length > 0)
            fs.ReimportFiles([file]);
        else
        {
            // in a folder Godot's file system does not know yet: a scan finds the folder (Poll
            // imports what it leaves: a scan skips a folder whose time it saw already), and until
            // then the texture loads from the PNG itself (RemasterTextures.Unimported)
            Pending.Add(file);
            if (!fs.IsScanning())
                fs.Scan();
        }
    }

    /// <summary>PNGs written into folders Godot's file system did not know, not imported yet.</summary>
    private static readonly HashSet<string> Pending = new();

    /// <summary>Whether PNGs are waiting for Godot's import.</summary>
    public static bool Importing => Pending.Count > 0;

    /// <summary>
    /// Once a frame (the plugin): when no scan runs, the pending PNGs whose folders the file system
    /// knows now are imported; those still unknown ask for another scan. True when it imported some.
    /// </summary>
    public static bool Poll()
    {
        var fs = EditorInterface.Singleton.GetResourceFilesystem();
        if (Pending.Count == 0 || fs.IsScanning())
            return false;
        var known = new List<string>();
        bool unknown = false;
        foreach (var f in Pending)
        {
            if (!RemasterTextures.Unimported(f))
                continue;
            fs.UpdateFile(f);
            if (fs.GetFileType(f).Length > 0)
                known.Add(f);
            else
                unknown = true;
        }
        Pending.RemoveWhere(f => !RemasterTextures.Unimported(f) || known.Contains(f));
        if (known.Count > 0)
            fs.ReimportFiles([.. known]);
        if (unknown)
            fs.Scan();
        return known.Count > 0;
    }

    /// <summary>Whether every PNG and material the project's index names is imported (or the store is outside the project).</summary>
    public static bool AllImported(out string? missing)
    {
        missing = null;
        if (!InProject)
            return true;
        foreach (var t in Set.Textures)
        {
            if (t == null)
                continue;
            if (t.Replacement.Length > 0 && ResourceLoader.Load(Absolute(t.Replacement), "", ResourceLoader.CacheMode.Ignore) is not CompressedTexture2D)
            {
                missing = Absolute(t.Replacement);
                return false;
            }
            if (t.Material.Length > 0)
            {
                var m = ResourceLoader.Load(Absolute(t.Material), "", ResourceLoader.CacheMode.Ignore) as BaseMaterial3D;
                var maps = m == null ? [] : new Texture2D?[] { m.AlbedoTexture, m.NormalTexture, m.OrmTexture,
                    m.HeightmapEnabled ? m.HeightmapTexture : null, m.EmissionEnabled ? m.EmissionTexture : null };
                if (m == null || m.AlbedoTexture == null || System.Array.Exists(maps, x => x != null && x is not CompressedTexture2D)
                    || (m.HeightmapEnabled && m.HeightmapTexture == null) || (m.NormalEnabled && m.NormalTexture == null))
                {
                    missing = Absolute(t.Material);
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Writes <paramref name="id"/>'s material over <paramref name="maps"/> (each map's PNG, then
    /// the <see cref="OrmMaterial3D"/>, <c>material.&lt;hash&gt;.tres</c>). In the project the
    /// material refers to the imported PNGs; elsewhere it holds them. Returns the material's path
    /// relative to the store's folder, and the albedo's.
    /// </summary>
    public static (string Material, string Albedo) WriteMaterial(string id, PbrImages maps, PbrRecipe recipe)
    {
        var paths = new Dictionary<string, string>();
        foreach (var (role, img) in PbrBuilder.Files(maps))
            paths[role] = WriteImage(id, role, img, PresetOf(role));
        var folder = RemasterTextures.Folder(id, Dir);
        var key = string.Join("|", paths.Values) + $"|{recipe.HeightScale}|{recipe.Wrap}" + (recipe.EmissionEnergy != 1f ? $"|{recipe.EmissionEnergy}" : "") + ExtrasKey(paths, recipe);
        var file = $"{folder}/material.{MapStore.Hash(key)}.tres";
        if (!FileAccess.FileExists(file))
        {
            if (InProject)
            {
                // written as text, its maps by path: it needs none of them loaded (they may be
                // importing still), and the same maps give the same bytes
                using var f = FileAccess.Open(file, FileAccess.ModeFlags.Write);
                f?.StoreString(MaterialText(paths.ToDictionary(kv => kv.Key, kv => Absolute(kv.Value)), recipe));
            }
            else
            {
                // outside the project nothing is imported: the material holds its maps
                var (a, n, o, h, e) = PbrBuilder.InMemory(maps);
                if (ResourceSaver.Save(PbrBuilder.Material(a, n, o, h, e, recipe, PbrBuilder.ExtrasInMemory(maps)), file) != Error.Ok)
                    GD.PrintErr($"ffx remaster: cannot write {file}");
            }
        }
        if (InProject)
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(file);
        return (Relative(file), paths["albedo"]);
    }

    /// <summary>A map file's import preset by its role: normals, colour (albedo, emission, the detail's albedo), the rest data.</summary>
    public static TexturePreset PresetOf(string role) => role switch
    {
        "normal" or "bent_normal" or "detail_normal" => TexturePreset.Normal,
        "albedo" or "emission" or "detail_albedo" => TexturePreset.Colour,
        _ => TexturePreset.Mask,
    };

    /// <summary>The added maps' material settings, as part of the material's name (empty without added maps: names as before).</summary>
    private static string ExtrasKey(IReadOnlyDictionary<string, string> paths, PbrRecipe r)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        if (paths.ContainsKey("rim")) sb.Append(inv, $"|rim{r.RimAmount},{r.RimTint}");
        if (paths.ContainsKey("clearcoat")) sb.Append(inv, $"|cc{r.ClearcoatAmount},{r.ClearcoatRoughness}");
        if (paths.ContainsKey("flowmap")) sb.Append(inv, $"|an{r.AnisotropyAmount}");
        if (paths.ContainsKey("subsurface")) sb.Append(inv, $"|ss{r.SubsurfaceStrength},{r.SubsurfaceSkin}");
        if (paths.ContainsKey("transmittance")) sb.Append(inv, $"|tr{r.TransmittanceDepth},{r.TransmittanceBoost}");
        if (paths.ContainsKey("refraction")) sb.Append(inv, $"|rf{r.RefractionScale}");
        if (paths.ContainsKey("detail_mask")) sb.Append(inv, $"|dt{r.DetailScale},{(int)r.DetailBlend}");
        return sb.ToString();
    }

    /// <summary>
    /// An <c>ORMMaterial3D</c> as Godot's text format over the maps at <paramref name="maps"/>
    /// (role to <c>res://</c> path), as <see cref="PbrBuilder.Material"/> makes it.
    /// </summary>
    public static string MaterialText(IReadOnlyDictionary<string, string> maps, PbrRecipe r)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var roles = new[] { "albedo", "normal", "orm", "height", "emission" }.Concat(PbrBuilder.ExtraRoles).Where(maps.ContainsKey).ToList();
        var sb = new System.Text.StringBuilder();
        sb.Append($"[gd_resource type=\"ORMMaterial3D\" load_steps={roles.Count + 1} format=3]\n\n");
        for (int i = 0; i < roles.Count; i++)
            sb.Append($"[ext_resource type=\"Texture2D\" path=\"{maps[roles[i]]}\" id=\"{roles[i]}\"]\n");
        sb.Append("\n[resource]\n");
        sb.Append("texture_filter = 5\n");
        sb.Append($"texture_repeat = {(r.Wrap ? "true" : "false")}\n");
        sb.Append("albedo_texture = ExtResource(\"albedo\")\n");
        sb.Append("metallic = 1.0\nmetallic_specular = 0.5\nroughness = 1.0\n");
        sb.Append("orm_texture = ExtResource(\"orm\")\n");
        sb.Append("normal_enabled = true\nnormal_scale = 1.0\nnormal_texture = ExtResource(\"normal\")\n");
        if (maps.ContainsKey("emission"))
            sb.Append($"emission_enabled = true\nemission = Color(1, 1, 1, 1)\nemission_energy_multiplier = {r.EmissionEnergy.ToString("0.0##", inv)}\nemission_texture = ExtResource(\"emission\")\n");
        if (maps.ContainsKey("height"))
            sb.Append($"heightmap_enabled = true\nheightmap_scale = {(r.HeightScale * 100f).ToString("0.###", inv)}\nheightmap_texture = ExtResource(\"height\")\n");
        // the maps added with "+ map" (R2b phase 4), as PbrBuilder.ApplyExtras sets them
        string F(float v) => v.ToString("0.0#####", inv);
        if (maps.ContainsKey("bent_normal"))
            sb.Append("bent_normal_enabled = true\nbent_normal_texture = ExtResource(\"bent_normal\")\n");
        if (maps.ContainsKey("rim"))
            sb.Append($"rim_enabled = true\nrim = {F(r.RimAmount)}\nrim_tint = {F(r.RimTint)}\nrim_texture = ExtResource(\"rim\")\n");
        if (maps.ContainsKey("clearcoat"))
            sb.Append($"clearcoat_enabled = true\nclearcoat = {F(r.ClearcoatAmount)}\nclearcoat_roughness = {F(r.ClearcoatRoughness)}\nclearcoat_texture = ExtResource(\"clearcoat\")\n");
        if (maps.ContainsKey("flowmap"))
            sb.Append($"anisotropy_enabled = true\nanisotropy = {F(r.AnisotropyAmount)}\nanisotropy_flowmap = ExtResource(\"flowmap\")\n");
        if (maps.ContainsKey("subsurface"))
            sb.Append($"subsurf_scatter_enabled = true\nsubsurf_scatter_strength = {F(r.SubsurfaceStrength)}\nsubsurf_scatter_skin_mode = {(r.SubsurfaceSkin ? "true" : "false")}\n"
                + "subsurf_scatter_texture = ExtResource(\"subsurface\")\n");
        if (maps.ContainsKey("transmittance"))
            sb.Append($"subsurf_scatter_transmittance_enabled = true\nsubsurf_scatter_transmittance_color = Color(1, 1, 1, 1)\nsubsurf_scatter_transmittance_texture = ExtResource(\"transmittance\")\n"
                + $"subsurf_scatter_transmittance_depth = {F(r.TransmittanceDepth)}\nsubsurf_scatter_transmittance_boost = {F(r.TransmittanceBoost)}\n");
        if (maps.ContainsKey("backlight"))
            sb.Append("backlight_enabled = true\nbacklight = Color(0, 0, 0, 1)\nbacklight_texture = ExtResource(\"backlight\")\n");
        if (maps.ContainsKey("refraction"))
            sb.Append($"refraction_enabled = true\nrefraction_scale = {F(r.RefractionScale)}\nrefraction_texture = ExtResource(\"refraction\")\nrefraction_texture_channel = 0\n");
        if (maps.ContainsKey("detail_mask"))
        {
            sb.Append($"detail_enabled = true\ndetail_mask = ExtResource(\"detail_mask\")\ndetail_blend_mode = {(int)r.DetailBlend}\ndetail_uv_layer = 1\n");
            if (maps.ContainsKey("detail_albedo"))
                sb.Append("detail_albedo = ExtResource(\"detail_albedo\")\n");
            if (maps.ContainsKey("detail_normal"))
                sb.Append("detail_normal = ExtResource(\"detail_normal\")\n");
            sb.Append($"uv2_scale = Vector3({F(r.DetailScale)}, {F(r.DetailScale)}, 1)\n");
        }
        return sb.ToString();
    }
}
#endif
