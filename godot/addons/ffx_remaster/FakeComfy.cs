#if TOOLS
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// A tiny stand-in for a ComfyUI server, for the editor check (docs/remaster-materials.md, phase
/// 5): it answers the calls <see cref="ComfyUpscaler"/> makes (<c>POST /upload/image</c>,
/// <c>POST /prompt</c>, <c>GET /queue</c>, <c>GET /history/&lt;id&gt;</c>, <c>GET /view</c>) on
/// 127.0.0.1, holds each prompt "running" for <see cref="DelayPolls"/> history polls, and returns
/// the uploaded image scaled by the workflow's factor (nearest neighbour, tinted, so a check sees
/// it came from here). R2b phase 6: any workflow (the output is its SaveImage node); one whose
/// output's prefix is <c>gdffx/ai-&lt;map&gt;</c> (an AI estimate, <see cref="ComfyAi"/>) answers a
/// map made from the upload by plain image math (a normal map from the brightness's slopes, the
/// brightness as height, its inverse as roughness, a flattened colour as albedo). No model, no
/// network beyond the loopback.
/// </summary>
public sealed class FakeComfy : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (byte[] Png, double Scale, int Polls, string Kind, string Output)> _jobs = new();
    private byte[]? _upload;
    private int _number;

    public string Url { get; } = "";

    /// <summary>History polls a prompt stays running for.</summary>
    public int DelayPolls { get; set; }

    public int Uploads, Prompts, Histories, Queues, Views;

    /// <summary>The last workflow's model and scale (the factor / 4), the last upload's size.</summary>
    public string LastModel { get; private set; } = "";
    public double LastScale { get; private set; }
    public Vector2I LastUploadSize { get; private set; }

    /// <summary>The last workflow as queued (its text), and the AI maps asked for (by name).</summary>
    public string LastPrompt { get; private set; } = "";
    public readonly List<string> AiMaps = new();

    public FakeComfy(int delayPolls)
    {
        DelayPolls = delayPolls;
        var rnd = new Random();
        for (int i = 0; i < 20 && Url.Length == 0; i++)
        {
            int port = 18700 + rnd.Next(0, 1200);
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Url = $"http://127.0.0.1:{port}";
            }
            catch (HttpListenerException)
            {
                // taken: another port
            }
        }
        if (Url.Length == 0)
            throw new InvalidOperationException("no free port for the fake ComfyUI");
        Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }
            try
            {
                Handle(ctx);
            }
            catch (Exception e)
            {
                Answer(ctx, 500, Encoding.UTF8.GetBytes(e.Message), "text/plain");
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url!.AbsolutePath;
        if (req.HttpMethod == "POST" && path == "/upload/image")
        {
            using var ms = new System.IO.MemoryStream();
            req.InputStream.CopyTo(ms);
            var png = Png(ms.ToArray()) ?? throw new InvalidOperationException("no PNG in the upload");
            var img = new Image();
            img.LoadPngFromBuffer(png);
            lock (_jobs)
            {
                _upload = png;
                Uploads++;
                LastUploadSize = img.GetSize();
            }
            Json(ctx, new JsonObject { ["name"] = "upload.png", ["subfolder"] = "gdffx", ["type"] = "input" });
        }
        else if (req.HttpMethod == "POST" && path == "/prompt")
        {
            using var sr = new System.IO.StreamReader(req.InputStream);
            var body = JsonNode.Parse(sr.ReadToEnd())!;
            var prompt = body["prompt"]!;
            string id = Guid.NewGuid().ToString("N");
            // the output: the SaveImage node (an AI estimate's prefix names its map)
            string output = "5", kind = "";
            foreach (var (nid, node) in prompt.AsObject())
                if (node?["class_type"]?.GetValue<string>() == "SaveImage")
                {
                    output = nid;
                    var pre = node["inputs"]?["filename_prefix"]?.GetValue<string>() ?? "";
                    if (pre.StartsWith("gdffx/ai-", StringComparison.Ordinal))
                        kind = pre["gdffx/ai-".Length..];
                }
            lock (_jobs)
            {
                LastPrompt = prompt.ToJsonString();
                if (kind.Length == 0)
                {
                    LastModel = prompt["2"]?["inputs"]?["model_name"]?.GetValue<string>() ?? "";
                    LastScale = prompt["4"]?["inputs"]?["scale_by"]?.GetValue<double>() ?? 0;
                }
                else
                    AiMaps.Add(kind);
                _jobs[id] = (_upload ?? [], kind.Length == 0 ? LastScale : 0.25, 0, kind, output);
                Prompts++;
            }
            Json(ctx, new JsonObject { ["prompt_id"] = id, ["number"] = _number++ });
        }
        else if (path == "/queue")
        {
            var running = new JsonArray();
            lock (_jobs)
            {
                Queues++;
                foreach (var (id, j) in _jobs)
                    if (j.Polls <= DelayPolls)
                        running.Add(new JsonArray(0, id, new JsonObject(), new JsonObject(), new JsonArray("5")));
            }
            Json(ctx, new JsonObject { ["queue_running"] = running, ["queue_pending"] = new JsonArray() });
        }
        else if (path.StartsWith("/history/", StringComparison.Ordinal))
        {
            var id = path["/history/".Length..];
            bool done;
            lock (_jobs)
            {
                Histories++;
                if (!_jobs.TryGetValue(id, out var j))
                {
                    Json(ctx, new JsonObject());
                    return;
                }
                _jobs[id] = j with { Polls = j.Polls + 1 };
                done = j.Polls + 1 > DelayPolls;
            }
            if (!done)
            {
                Json(ctx, new JsonObject());
                return;
            }
            var images = new JsonArray(new JsonObject { ["filename"] = $"up_{id}.png", ["subfolder"] = "gdffx", ["type"] = "output" });
            string outNode;
            lock (_jobs)
                outNode = _jobs[id].Output;
            Json(ctx, new JsonObject
            {
                [id] = new JsonObject
                {
                    ["status"] = new JsonObject { ["status_str"] = "success", ["completed"] = true },
                    ["outputs"] = new JsonObject { [outNode] = new JsonObject { ["images"] = images } },
                },
            });
        }
        else if (path == "/view")
        {
            var name = req.QueryString["filename"] ?? "";
            var id = name.StartsWith("up_", StringComparison.Ordinal) ? name[3..^4] : "";
            (byte[] Png, double Scale, int Polls, string Kind, string Output) job;
            lock (_jobs)
            {
                Views++;
                if (!_jobs.TryGetValue(id, out job))
                {
                    Answer(ctx, 404, [], "text/plain");
                    return;
                }
            }
            var img = new Image();
            img.LoadPngFromBuffer(job.Png);
            img.Convert(Image.Format.Rgba8);
            if (job.Kind.Length > 0)
            {
                Answer(ctx, 200, Map(img, job.Kind).SavePngToBuffer(), "image/png");
                return;
            }
            double factor = job.Scale * 4;
            img.Resize((int)Math.Round(img.GetWidth() * factor), (int)Math.Round(img.GetHeight() * factor), Image.Interpolation.Nearest);
            var data = img.GetData();
            for (int i = 0; i < data.Length; i += 4)
            {
                data[i + 1] = (byte)(data[i + 1] * 0.9);
                data[i + 2] = (byte)(data[i + 2] * 0.8);
            }
            var outImg = Image.CreateFromData(img.GetWidth(), img.GetHeight(), false, Image.Format.Rgba8, data);
            Answer(ctx, 200, outImg.SavePngToBuffer(), "image/png");
        }
        else
            Answer(ctx, 404, [], "text/plain");
    }

    /// <summary>A stand-in estimate of map <paramref name="kind"/> from <paramref name="img"/> (plain image math, the upload's size).</summary>
    private static Image Map(Image img, string kind)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        var d = img.GetData();
        float L(int x, int y)
        {
            int i = ((Math.Clamp(y, 0, h - 1) * w) + Math.Clamp(x, 0, w - 1)) * 4;
            return (0.2126f * d[i] + 0.7152f * d[i + 1] + 0.0722f * d[i + 2]) / 255f;
        }
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float l = L(x, y);
                (float r, float g, float b) = kind switch
                {
                    "normal" => Normal(L(x + 1, y) - L(x - 1, y), L(x, y + 1) - L(x, y - 1)),
                    "height" => (l, l, l),
                    "roughness" => (1 - l, 1 - l, 1 - l),
                    _ => ((d[i] / 255f + l) / 2, (d[i + 1] / 255f + l) / 2, (d[i + 2] / 255f + l) / 2),
                };
                (o[i], o[i + 1], o[i + 2], o[i + 3]) = ((byte)Math.Clamp(r * 255, 0, 255), (byte)Math.Clamp(g * 255, 0, 255), (byte)Math.Clamp(b * 255, 0, 255), 255);
            }
        return Image.CreateFromData(w, h, false, Image.Format.Rgba8, o);
    }

    /// <summary>A normal (OpenGL, green up) from the brightness's slopes, encoded.</summary>
    private static (float, float, float) Normal(float dx, float dy)
    {
        float nx = -dx * 4, ny = dy * 4, nz = 1;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        return (nx / len * 0.5f + 0.5f, ny / len * 0.5f + 0.5f, nz / len * 0.5f + 0.5f);
    }

    /// <summary>The PNG inside a multipart body (its signature to the end of its IEND chunk), or null.</summary>
    private static byte[]? Png(byte[] body)
    {
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        int start = Find(body, sig, 0);
        if (start < 0)
            return null;
        int iend = Find(body, "IEND"u8.ToArray(), start);
        return iend < 0 ? null : body[start..(iend + 8)];
    }

    private static int Find(byte[] hay, byte[] needle, int from)
    {
        for (int i = from; i + needle.Length <= hay.Length; i++)
        {
            int k = 0;
            while (k < needle.Length && hay[i + k] == needle[k])
                k++;
            if (k == needle.Length)
                return i;
        }
        return -1;
    }

    private static void Json(HttpListenerContext ctx, JsonNode node) => Answer(ctx, 200, Encoding.UTF8.GetBytes(node.ToJsonString()), "application/json");

    private static void Answer(HttpListenerContext ctx, int status, byte[] body, string type)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // closed already
        }
    }
}
#endif
