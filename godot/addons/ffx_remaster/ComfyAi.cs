#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// AI estimation of maps (R2b phase 6; docs/remaster-materials.md, "AI estimation"): a ComfyUI
/// workflow of the user's own (an API-format workflow JSON file: Workflow, Export (API) in
/// ComfyUI) run on the user's own server (<see cref="ComfyUpscaler.UrlSetting"/>) on a texture,
/// its image the albedo, height, normal or roughness step's input. gd-ffx ships no model and no
/// workflow of a third party: only an example template of placeholder nodes
/// (<c>addons/ffx_remaster/comfyui/example-map-workflow.json</c>). Per map, three editor settings:
/// <c>ffx_remaster/ai_&lt;map&gt;_workflow</c> (the file), <c>_model</c> (the model's name, for the
/// record) and <c>_licence</c> (the licence of the model or models the workflow runs). A workflow
/// whose licence is empty is not run. The template's placeholders (strings, replaced before it is
/// queued):
/// <list type="bullet">
/// <item><c>%gdffx_input%</c> (required): the uploaded texture's name, e.g. a LoadImage node's <c>image</c>;</item>
/// <item><c>%gdffx_output%</c> (required): the <c>filename_prefix</c> of the SaveImage node whose first image is the map (that node is the output);</item>
/// <item><c>%gdffx_map%</c> (optional): the map asked for: <c>albedo</c>, <c>height</c>, <c>normal</c> or <c>roughness</c>.</item>
/// </list>
/// What the maps are expected to be: albedo (the colour without its painted light, sRGB),
/// height (bright is high; a depth estimator's "near is bright" is the same), normal (OpenGL,
/// green up, as Godot reads it), roughness (bright is rough). Any size: resized to the working size.
/// </summary>
public static class ComfyAi
{
    public const string Input = "%gdffx_input%", Output = "%gdffx_output%", MapName = "%gdffx_map%";

    /// <summary>The shipped example (placeholder nodes: a template to fill in, not a working workflow).</summary>
    public const string Example = "res://addons/ffx_remaster/comfyui/example-map-workflow.json";

    /// <summary>A map step's name in the settings and the template (<c>normal</c>).</summary>
    public static string Kind(MaterialStep s) => s.ToString().ToLowerInvariant();

    public static string WorkflowSetting(MaterialStep s) => $"ffx_remaster/ai_{Kind(s)}_workflow";
    public static string ModelSetting(MaterialStep s) => $"ffx_remaster/ai_{Kind(s)}_model";
    public static string LicenceSetting(MaterialStep s) => $"ffx_remaster/ai_{Kind(s)}_licence";

    /// <summary>The editor check's values instead of the editor settings (by setting name; null: the settings).</summary>
    public static readonly Dictionary<string, string> Overrides = new();

    private static string Setting(string name)
    {
        if (Overrides.TryGetValue(name, out var o))
            return o;
        var es = EditorInterface.Singleton.GetEditorSettings();
        return es.HasSetting(name) ? es.GetSetting(name).AsString().Trim() : "";
    }

    public static string WorkflowFile(MaterialStep s) => Setting(WorkflowSetting(s));
    public static string Model(MaterialStep s) => Setting(ModelSetting(s));
    public static string Licence(MaterialStep s) => Setting(LicenceSetting(s));

    /// <summary>Registers the settings (empty: no workflow), each map's three.</summary>
    public static void RegisterSettings()
    {
        var es = EditorInterface.Singleton.GetEditorSettings();
        foreach (var s in MaterialPipeline.AiSteps)
            foreach (var name in new[] { WorkflowSetting(s), ModelSetting(s), LicenceSetting(s) })
            {
                if (!es.HasSetting(name))
                    es.SetSetting(name, "");
                es.SetInitialValue(name, "", false);
                es.AddPropertyInfo(new Godot.Collections.Dictionary
                {
                    ["name"] = name, ["type"] = (int)Variant.Type.String,
                    ["hint"] = (int)(name.EndsWith("_workflow", StringComparison.Ordinal) ? PropertyHint.GlobalFile : PropertyHint.None),
                    ["hint_string"] = name.EndsWith("_workflow", StringComparison.Ordinal) ? "*.json" : "",
                });
            }
    }

    /// <summary>A workflow ready to run: its text, the output node's ID, its content key.</summary>
    public sealed record Workflow(string File, string Text, string OutputNode, string Hash);

    /// <summary>
    /// Whether step <paramref name="s"/>'s AI can run: a server, a workflow file that is an API
    /// workflow with the two placeholders, and a licence written down. The workflow, or the reason
    /// it cannot run (a refusal for an empty licence).
    /// </summary>
    public static (Workflow? Ready, string? Why) Check(MaterialStep s)
    {
        if (!MaterialPipeline.AiSteps.Contains(s))
            return (null, $"no AI for the {Kind(s)} step (albedo, height, normal and roughness only)");
        if (!ComfyUpscaler.Configured)
            return (null, $"AI {Kind(s)}: no ComfyUI server (the editor setting {ComfyUpscaler.UrlSetting})");
        var file = WorkflowFile(s);
        if (file.Length == 0)
            return (null, $"AI {Kind(s)}: no workflow (the editor setting {WorkflowSetting(s)}: an API workflow JSON of your own; see {Example})");
        var path = file.StartsWith("res://", StringComparison.Ordinal) || file.StartsWith("user://", StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(file) : file;
        if (!System.IO.File.Exists(path))
            return (null, $"AI {Kind(s)}: the workflow {System.IO.Path.GetFileName(file)} is not there");
        var text = System.IO.File.ReadAllText(path);
        if (Licence(s).Length == 0)
            return (null, $"AI {Kind(s)}: refused: the workflow's licence is empty. Write the licence of the model(s) {System.IO.Path.GetFileName(file)} runs "
                + $"in the editor setting {LicenceSetting(s)} first (it is recorded with every map it makes; gd-ffx runs no workflow without one)");
        if (!text.Contains(Input, StringComparison.Ordinal) || !text.Contains(Output, StringComparison.Ordinal))
            return (null, $"AI {Kind(s)}: {System.IO.Path.GetFileName(file)} lacks the placeholders {Input} and {Output} (docs/remaster-materials.md, \"AI estimation\")");
        string? output = null;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root)
                return (null, $"AI {Kind(s)}: {System.IO.Path.GetFileName(file)} is not an API workflow (an object of nodes)");
            foreach (var (id, node) in root)
                if (node?["inputs"]?["filename_prefix"] is JsonValue v && v.TryGetValue<string>(out var pre) && pre == Output)
                    output = id;
        }
        catch (JsonException e)
        {
            return (null, $"AI {Kind(s)}: {System.IO.Path.GetFileName(file)} is not JSON ({e.Message})");
        }
        if (output == null)
            return (null, $"AI {Kind(s)}: no node of {System.IO.Path.GetFileName(file)} has filename_prefix {Output} (the output's SaveImage)");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
        return (new Workflow(file, text, output, hash), null);
    }

    /// <summary>The workflow's JSON for an uploaded image (the placeholders replaced, as JSON strings).</summary>
    public static JsonObject Fill(Workflow w, MaterialStep s, string uploaded)
    {
        static string Esc(string v) => JsonSerializer.Serialize(v)[1..^1];
        var text = w.Text.Replace(Input, Esc(uploaded), StringComparison.Ordinal).Replace(Output, Esc($"gdffx/ai-{Kind(s)}"), StringComparison.Ordinal)
            .Replace(MapName, Kind(s), StringComparison.Ordinal);
        return (JsonObject)JsonNode.Parse(text)!;
    }

    /// <summary>Step <paramref name="s"/>'s map of <paramref name="png"/> by workflow <paramref name="w"/> (any thread; throws with ComfyUI's reason).</summary>
    public static Task<byte[]> Estimate(string url, Workflow w, MaterialStep s, byte[] png, CancellationToken cancel, IProgress<(double, string)>? progress) =>
        ComfyUpscaler.Run(url, png, name => Fill(w, s, name), w.OutputNode, cancel, progress);

    /// <summary>The Source note's line for an estimate (the tool, the workflow, the model, its licence).</summary>
    public static string Note(MaterialStep s, Workflow w, string model, string licence) =>
        $"{Kind(s)} estimated by ComfyUI (workflow {System.IO.Path.GetFileName(w.File)}{(model.Length > 0 ? $", model {model}" : "")}; licence: {licence})";
}
#endif
