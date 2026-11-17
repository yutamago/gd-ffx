#if TOOLS
using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// Optional AI upscaling through a ComfyUI server of one's own (docs/remaster-editor.md,
/// "Upscaling"; ported in spirit from ffx-remix's Comfy client and its <c>upscale-4x</c>
/// template, MIT, the same author). Off unless the editor setting <see cref="UrlSetting"/> names
/// a server (e.g. <c>http://127.0.0.1:8188</c>); gd-ffx ships no model and downloads nothing. A
/// run uploads the texture (<c>POST /upload/image</c>), queues the workflow (LoadImage,
/// UpscaleModelLoader with <see cref="ModelSetting"/>, ImageUpscaleWithModel, ImageScaleBy to the
/// factor asked, SaveImage: core nodes only), polls <c>GET /history/&lt;id&gt;</c> and downloads
/// the result (<c>GET /view</c>). Not tested against a real server yet (as ffx-remix's own).
/// Record the model and its licence in the texture's Source.
/// </summary>
public static class ComfyUpscaler
{
    public const string UrlSetting = "ffx_remaster/comfyui_url";
    public const string ModelSetting = "ffx_remaster/comfyui_upscale_model";
    public const string DefaultModel = "RealESRGAN_x4plus.pth";

    /// <summary>The server (empty: none configured, Lanczos is used).</summary>
    public static string Url
    {
        get
        {
            var es = EditorInterface.Singleton.GetEditorSettings();
            return es.HasSetting(UrlSetting) ? es.GetSetting(UrlSetting).AsString().Trim().TrimEnd('/') : "";
        }
    }

    public static string Model
    {
        get
        {
            var es = EditorInterface.Singleton.GetEditorSettings();
            return es.HasSetting(ModelSetting) && es.GetSetting(ModelSetting).AsString() is { Length: > 0 } m ? m : DefaultModel;
        }
    }

    public static bool Configured => Url.Length > 0;

    /// <summary>Registers the two editor settings (empty URL: off).</summary>
    public static void RegisterSettings()
    {
        var es = EditorInterface.Singleton.GetEditorSettings();
        if (!es.HasSetting(UrlSetting))
            es.SetSetting(UrlSetting, "");
        if (!es.HasSetting(ModelSetting))
            es.SetSetting(ModelSetting, DefaultModel);
        es.SetInitialValue(UrlSetting, "", false);
        es.SetInitialValue(ModelSetting, DefaultModel, false);
    }

    /// <summary>
    /// <paramref name="png"/> upscaled <paramref name="factor"/> times (2 or 4) by the server's
    /// model (its x4, then scaled by factor / 4). Any thread; throws on failure (with ComfyUI's reason).
    /// </summary>
    public static async Task<byte[]> Upscale(string url, string model, byte[] png, int factor, CancellationToken cancel = default)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var name = $"gdffx-{Guid.NewGuid():N}.png";
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(png), "image", name);
            form.Add(new StringContent("gdffx"), "subfolder");
            form.Add(new StringContent("input"), "type");
            var up = await http.PostAsync($"{url}/upload/image", form, cancel);
            up.EnsureSuccessStatusCode();
            var uploaded = JsonNode.Parse(await up.Content.ReadAsStringAsync(cancel));
            name = (uploaded?["subfolder"]?.GetValue<string>() is { Length: > 0 } sub ? sub + "/" : "") + (uploaded?["name"]?.GetValue<string>() ?? name);
        }
        var workflow = new JsonObject
        {
            ["1"] = Node("LoadImage", new JsonObject { ["image"] = name }),
            ["2"] = Node("UpscaleModelLoader", new JsonObject { ["model_name"] = model }),
            ["3"] = Node("ImageUpscaleWithModel", new JsonObject { ["upscale_model"] = Link("2"), ["image"] = Link("1") }),
            ["4"] = Node("ImageScaleBy", new JsonObject { ["upscale_method"] = "lanczos", ["scale_by"] = Math.Clamp(factor, 1, 4) / 4.0, ["image"] = Link("3") }),
            ["5"] = Node("SaveImage", new JsonObject { ["filename_prefix"] = "gdffx/upscale", ["images"] = Link("4") }),
        };
        var body = new JsonObject { ["prompt"] = workflow, ["client_id"] = $"gdffx-{Guid.NewGuid():N}" };
        var queued = await http.PostAsync($"{url}/prompt", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), cancel);
        var answer = await queued.Content.ReadAsStringAsync(cancel);
        if (!queued.IsSuccessStatusCode)
            throw new InvalidOperationException($"ComfyUI refused the workflow: {answer}");
        var id = JsonNode.Parse(answer)?["prompt_id"]?.GetValue<string>() ?? throw new InvalidOperationException("ComfyUI gave no prompt_id");
        for (int tries = 0; ; tries++)
        {
            cancel.ThrowIfCancellationRequested();
            await Task.Delay(1000, cancel);
            var h = JsonNode.Parse(await http.GetStringAsync($"{url}/history/{id}", cancel))?[id];
            if (h == null)
            {
                if (tries > 3600)
                    throw new TimeoutException("ComfyUI did not finish within an hour");
                continue;
            }
            if (h["status"]?["status_str"]?.GetValue<string>() == "error")
                throw new InvalidOperationException($"ComfyUI failed: {h["status"]}");
            var img = h["outputs"]?["5"]?["images"]?[0];
            if (img == null)
                throw new InvalidOperationException("ComfyUI finished without an image");
            var q = $"filename={Uri.EscapeDataString(img["filename"]!.GetValue<string>())}"
                + $"&subfolder={Uri.EscapeDataString(img["subfolder"]?.GetValue<string>() ?? "")}&type={img["type"]?.GetValue<string>() ?? "output"}";
            return await http.GetByteArrayAsync($"{url}/view?{q}", cancel);
        }
    }

    private static JsonObject Node(string type, JsonObject inputs) => new() { ["class_type"] = type, ["inputs"] = inputs };

    private static JsonArray Link(string node) => new(node, 0);
}
#endif
