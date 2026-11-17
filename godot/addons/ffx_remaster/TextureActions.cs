#if TOOLS
using System.Collections.Generic;
using System.Threading.Tasks;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// What the texture dock and the batch do to a texture (docs/remaster-editor.md, "Textures",
/// "Materials"): each makes its files (<see cref="TextureStore"/>: content-named, written once)
/// and then one undo step that sets the texture's entry (<see cref="RemasterPlugin.SetTexture"/>).
/// The colour texture an operation starts from is the replacement when there is one, else the
/// classic texture read from the player's archive.
/// </summary>
public static class TextureActions
{
    /// <summary>The colour texture to work from: <paramref name="id"/>'s replacement, else its classic pixels (null: neither readable).</summary>
    public static Image? Source(string id)
    {
        // the project's own file first (read as it is: a headless editor has no texture to read back)
        if (TextureStore.Find(id) is { Replacement.Length: > 0 } own && FileAccess.FileExists(TextureStore.Absolute(own.Replacement)))
            return Image.LoadFromFile(ProjectSettings.GlobalizePath(TextureStore.Absolute(own.Replacement)));
        if (RemasterTextures.For(id) is { Replaced: true } e && RemasterTextures.Texture(e.Replacement) is { } t && t.GetImage() is { } img && !img.IsEmpty())
            return img;
        return RemasterTextures.ClassicImage(id);
    }

    /// <summary>The preset a replacement image suggests: a cut-out when its alpha is all 0 or 255 and some is 0, else colour.</summary>
    public static TexturePreset Suggest(Image img)
    {
        var px = PbrBuilder.From(img);
        return px.HasAlpha && px.HasOnlyBinaryAlpha ? TexturePreset.CutOut : TexturePreset.Colour;
    }

    /// <summary>Replaces <paramref name="id"/> by <paramref name="img"/> (one undo step); <paramref name="source"/> says where it came from (a file name, a tool: no paths).</summary>
    public static void Replace(RemasterPlugin plugin, string id, Image img, string source, TexturePreset? preset = null)
    {
        var p = preset ?? Suggest(img);
        var entry = TextureStore.Copy(id);
        entry.Mode = TextureMode.Edit;
        entry.Replacement = TextureStore.WriteImage(id, "replacement", img, p);
        entry.Preset = p;
        entry.Source = source;
        plugin.SetTexture(id, entry, $"Replace {RemasterTextures.ShortName(id)}");
    }

    /// <summary>Replaces <paramref name="id"/> by the image file at <paramref name="file"/> (an error, or null).</summary>
    public static string? ReplaceFromFile(RemasterPlugin plugin, string id, string file)
    {
        var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(file));
        if (img == null || img.IsEmpty())
            return $"cannot read {file.GetFile()}";
        if (img.IsCompressed())
            img.Decompress();
        Replace(plugin, id, img, $"replaced by hand ({file.GetFile()})");
        return null;
    }

    /// <summary>
    /// Upscales <paramref name="id"/>'s colour texture <paramref name="factor"/> times as its
    /// replacement: by ComfyUI when configured (in the background; <paramref name="done"/> with
    /// the outcome), else Lanczos-3 at once.
    /// </summary>
    public static void Upscale(RemasterPlugin plugin, string id, int factor, System.Action<string>? done = null)
    {
        if (Source(id) is not { } img)
        {
            done?.Invoke($"{id}: no pixels to upscale");
            return;
        }
        bool replaced = RemasterTextures.For(id)?.Replaced == true;
        var what = replaced ? "the replacement" : "the classic texture";
        if (ComfyUpscaler.Configured)
        {
            var (url, model) = (ComfyUpscaler.Url, ComfyUpscaler.Model);
            var png = img.SavePngToBuffer();
            Task.Run(() => ComfyUpscaler.Upscale(url, model, png, factor)).ContinueWith(t => Callable.From(() =>
            {
                if (!t.IsCompletedSuccessfully)
                {
                    done?.Invoke($"ComfyUI: {t.Exception?.InnerException?.Message ?? "failed"}");
                    return;
                }
                var up = new Image();
                if (up.LoadPngFromBuffer(t.Result) != Error.Ok)
                {
                    done?.Invoke("ComfyUI's answer is not a PNG");
                    return;
                }
                Replace(plugin, id, up, $"upscaled x{factor} by ComfyUI ({model}) from {what}; record the model's licence here");
                done?.Invoke($"upscaled x{factor} by ComfyUI");
            }).CallDeferred());
            return;
        }
        var lanczos = PbrBuilder.ToImage(PbrBuilder.Upscale(PbrBuilder.From(img), factor));
        Replace(plugin, id, lanczos, $"upscaled x{factor} (Lanczos-3, built in) from {what}", RemasterTextures.For(id)?.Preset);
        done?.Invoke($"upscaled x{factor} (Lanczos)");
    }

    /// <summary>The maps of <paramref name="id"/> by <paramref name="recipe"/> (no files yet: the batch reviews them first); null when no pixels.</summary>
    public static PbrImages? Make(string id, PbrRecipe recipe) =>
        Source(id) is { } img ? PbrBuilder.Make(PbrBuilder.From(img), recipe) : null;

    /// <summary>The entry that gives <paramref name="id"/> the material over <paramref name="maps"/> (files written now).</summary>
    public static RemasterTexture WithMaterial(string id, PbrImages maps, PbrRecipe recipe)
    {
        var entry = TextureStore.Copy(id);
        entry.Mode = TextureMode.Edit;
        var (material, _) = TextureStore.WriteMaterial(id, maps, recipe);
        entry.Material = material;
        entry.Recipe = (PbrRecipe)recipe.Duplicate();
        return entry;
    }

    /// <summary>Generates <paramref name="id"/>'s material (one undo step); an error or null.</summary>
    public static string? GenerateMaterial(RemasterPlugin plugin, string id, PbrRecipe? recipe = null)
    {
        recipe ??= TextureStore.Find(id)?.Recipe is { } r ? (PbrRecipe)r.Duplicate() : new PbrRecipe();
        if (Make(id, recipe) is not { } maps)
            return $"{id}: no pixels to make a material of";
        plugin.SetTexture(id, WithMaterial(id, maps, recipe), $"Generate the material of {RemasterTextures.ShortName(id)}");
        return null;
    }

    /// <summary>Drops <paramref name="id"/>'s replacement, its material, or both (one undo step).</summary>
    public static void Revert(RemasterPlugin plugin, string id, bool replacement = true, bool material = true)
    {
        var entry = TextureStore.Copy(id);
        if (replacement)
            (entry.Replacement, entry.Source) = ("", "");
        if (material)
            (entry.Material, entry.Recipe) = ("", null);
        plugin.SetTexture(id, entry, $"Revert {RemasterTextures.ShortName(id)}{(replacement && material ? "" : replacement ? "'s replacement" : "'s material")}");
    }

    /// <summary>Opens <paramref name="id"/>'s material in the inspector (false: none).</summary>
    public static bool EditMaterial(string id)
    {
        if (TextureStore.Find(id) is not { Material.Length: > 0 } e)
            return false;
        var path = TextureStore.Absolute(e.Material);
        if (ResourceLoader.Load(path) is not Material m)
            return false;
        EditorInterface.Singleton.EditResource(m);
        return true;
    }

    /// <summary>The textures the open map draws (its classic materials' bound textures), with how many surfaces each.</summary>
    public static SortedDictionary<string, int> OnMap(MapEditRoot? root)
    {
        var r = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
        if (root?.Map is not { } map)
            return r;
        var seen = new HashSet<Material>(ReferenceEqualityComparer.Instance);
        void Count(Material? m)
        {
            foreach (var (_, id) in RemasterTextures.BoundTextures(m))
                r[id] = r.GetValueOrDefault(id) + 1;
        }
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } mesh } mi)
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    var m = mi.GetSurfaceOverrideMaterial(s);
                    // drawn remastered: the classic material is held aside (MapLook)
                    if (m != null && !m.HasMeta(Variants.Name(ClassicMaterials.MapShaderMeta)) && m.HasMeta(Variants.Name(RemasterLook.LitTextureMeta)))
                    {
                        var id = PartIds.GetString(m, RemasterLook.LitTextureMeta);
                        if (!string.IsNullOrEmpty(id))
                            r[id] = r.GetValueOrDefault(id) + 1;
                        continue;
                    }
                    Count(m);
                }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(map);
        return r;
    }
}
#endif
