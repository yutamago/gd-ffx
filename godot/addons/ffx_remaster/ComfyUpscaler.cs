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
/// the result (<c>GET /view</c>); while it waits it asks <c>GET /queue</c> where the prompt is
/// (waiting, running) for the progress. Not tested against a real server yet; the editor check
/// runs it against a fake one of its own that answers the same calls. The model's licence (the
/// editor setting <see cref="LicenceSetting"/>) is recorded in the texture's Source.
/// </summary>
public static class ComfyUpscaler
{
    public const string UrlSetting = "ffx_remaster/comfyui_url";
    public const string ModelSetting = "ffx_remaster/comfyui_upscale_model";
    public const string DefaultModel = "RealESRGAN_x4plus.pth";
    /// <summary>The model's licence, as one writes it (e.g. "BSD-3-Clause"): recorded in the Source of what it upscales.</summary>
    public const string LicenceSetting = "ffx_remaster/comfyui_model_licence";

    /// <summary>How often the server is asked whether the prompt is done (ms; the checks shorten it).</summary>
    public static int PollMs { get; set; } = 1000;

    /// <summary>The model's licence (empty: not given).</summary>
    public static string Licence
    {
        get
        {
            if (LicenceOverride != null)
                return LicenceOverride;
            var es = EditorInterface.Singleton.GetEditorSettings();
            return es.HasSetting(LicenceSetting) ? es.GetSetting(LicenceSetting).AsString().Trim() : "";
        }
    }

    /// <summary>The note a texture's Source gets for an upscale by the server (the tool, the model, its licence).</summary>
    public static string Note(int factor, string model, string licence) =>
        $"upscaled x{factor} by ComfyUI ({model}; licence: {(licence.Length > 0 ? licence : "not given, write it here")})";

    /// <summary>The server, model and licence the editor check uses (its fake server) instead of the editor settings (null: the settings).</summary>
    public static string? UrlOverride, ModelOverride, LicenceOverride;

    /// <summary>The server (empty: none configured, Lanczos is used).</summary>
    public static string Url
    {
        get
        {
            if (UrlOverride != null)
                return UrlOverride;
            var es = EditorInterface.Singleton.GetEditorSettings();
            return es.HasSetting(UrlSetting) ? es.GetSetting(UrlSetting).AsString().Trim().TrimEnd('/') : "";
        }
    }

    public static string Model
    {
        get
        {
            if (ModelOverride != null)
                return ModelOverride;
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
        if (!es.HasSetting(LicenceSetting))
            es.SetSetting(LicenceSetting, "");
        es.SetInitialValue(LicenceSetting, "", false);
        es.SetInitialValue(UrlSetting, "", false);
        es.SetInitialValue(ModelSetting, DefaultModel, false);
    }

    /// <summary>
    /// <paramref name="png"/> upscaled <paramref name="factor"/> times (2 or 4) by the server's
    /// model (its x4, then scaled by factor / 4). Any thread; throws on failure (with ComfyUI's reason).
    /// </summary>
    public static Task<byte[]> Upscale(string url, string model, byte[] png, int factor, CancellationToken cancel = default,
        IProgress<(double Done, string Stage)>? progress = null) =>
        Run(url, png, name => new JsonObject
        {
            ["1"] = Node("LoadImage", new JsonObject { ["image"] = name }),
            ["2"] = Node("UpscaleModelLoader", new JsonObject { ["model_name"] = model }),
            ["3"] = Node("ImageUpscaleWithModel", new JsonObject { ["upscale_model"] = Link("2"), ["image"] = Link("1") }),
            ["4"] = Node("ImageScaleBy", new JsonObject { ["upscale_method"] = "lanczos", ["scale_by"] = Math.Clamp(factor, 1, 4) / 4.0, ["image"] = Link("3") }),
            ["5"] = Node("SaveImage", new JsonObject { ["filename_prefix"] = "gdffx/upscale", ["images"] = Link("4") }),
        }, "5", cancel, progress);

    /// <summary>
    /// Runs a workflow on <paramref name="png"/> (any thread; throws with ComfyUI's reason): the
    /// image uploaded (<c>POST /upload/image</c>), the workflow made for its uploaded name by
    /// <paramref name="workflow"/> and queued (<c>POST /prompt</c>), its progress from
    /// <c>GET /queue</c> while <c>GET /history/&lt;id&gt;</c> has no answer, then the first image
    /// of node <paramref name="output"/> downloaded (<c>GET /view</c>).
    /// </summary>
    public static async Task<byte[]> Run(string url, byte[] png, Func<string, JsonObject> workflow, string output, CancellationToken cancel = default,
        IProgress<(double Done, string Stage)>? progress = null)
    {
        progress?.Report((0.05, "uploading"));
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
        progress?.Report((0.15, "queueing the workflow"));
        var body = new JsonObject { ["prompt"] = workflow(name), ["client_id"] = $"gdffx-{Guid.NewGuid():N}" };
        var queued = await http.PostAsync($"{url}/prompt", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), cancel);
        var answer = await queued.Content.ReadAsStringAsync(cancel);
        if (!queued.IsSuccessStatusCode)
            throw new InvalidOperationException($"ComfyUI refused the workflow: {answer}");
        var id = JsonNode.Parse(answer)?["prompt_id"]?.GetValue<string>() ?? throw new InvalidOperationException("ComfyUI gave no prompt_id");
        progress?.Report((0.2, "queued"));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int tries = 0; ; tries++)
        {
            cancel.ThrowIfCancellationRequested();
            await Task.Delay(PollMs, cancel);
            var h = JsonNode.Parse(await http.GetStringAsync($"{url}/history/{id}", cancel))?[id];
            if (h == null)
            {
                if (clock.Elapsed.TotalHours > 1)
                    throw new TimeoutException("ComfyUI did not finish within an hour");
                progress?.Report(await Where(http, url, id, clock.Elapsed.TotalSeconds, cancel));
                continue;
            }
            if (h["status"]?["status_str"]?.GetValue<string>() == "error")
                throw new InvalidOperationException($"ComfyUI failed: {h["status"]}");
            var img = h["outputs"]?[output]?["images"]?[0];
            if (img == null)
                throw new InvalidOperationException($"ComfyUI finished without an image from node {output}");
            var q = $"filename={Uri.EscapeDataString(img["filename"]!.GetValue<string>())}"
                + $"&subfolder={Uri.EscapeDataString(img["subfolder"]?.GetValue<string>() ?? "")}&type={img["type"]?.GetValue<string>() ?? "output"}";
            progress?.Report((0.9, "downloading"));
            var bytes = await http.GetByteArrayAsync($"{url}/view?{q}", cancel);
            progress?.Report((1, "done"));
            return bytes;
        }
    }

    /// <summary>Where prompt <paramref name="id"/> is in the server's queue (<c>GET /queue</c>): waiting (how many ahead) or running; its progress.</summary>
    private static async Task<(double, string)> Where(System.Net.Http.HttpClient http, string url, string id, double seconds, CancellationToken cancel)
    {
        try
        {
            var q = JsonNode.Parse(await http.GetStringAsync($"{url}/queue", cancel));
            static bool Has(JsonNode? list, string id, out int at)
            {
                at = 0;
                if (list is not JsonArray a)
                    return false;
                foreach (var e in a)
                {
                    if (e is JsonArray item && item.Count > 1 && item[1]?.GetValue<string>() == id)
                        return true;
                    at++;
                }
                return false;
            }
            if (Has(q?["queue_running"], id, out _))
                return (0.2 + 0.65 * (1 - Math.Exp(-seconds / 20)), $"running ({seconds:0} s)");
            if (Has(q?["queue_pending"], id, out int ahead))
                return (0.2, $"waiting in ComfyUI's queue ({ahead + (q?["queue_running"] as JsonArray)?.Count ?? 0} ahead)");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // an older server without /queue: the time alone
        }
        return (0.2 + 0.65 * (1 - Math.Exp(-seconds / 20)), $"waiting for ComfyUI ({seconds:0} s)");
    }

    private static JsonObject Node(string type, JsonObject inputs) => new() { ["class_type"] = type, ["inputs"] = inputs };

    private static JsonArray Link(string node) => new(node, 0);
}
#endif
