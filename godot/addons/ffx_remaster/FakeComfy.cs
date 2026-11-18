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
/// it came from here). No model, no network beyond the loopback.
/// </summary>
public sealed class FakeComfy : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (byte[] Png, double Scale, int Polls)> _jobs = new();
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
            lock (_jobs)
            {
                LastModel = prompt["2"]?["inputs"]?["model_name"]?.GetValue<string>() ?? "";
                LastScale = prompt["4"]?["inputs"]?["scale_by"]?.GetValue<double>() ?? 0;
                _jobs[id] = (_upload ?? [], LastScale, 0);
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
            Json(ctx, new JsonObject
            {
                [id] = new JsonObject
                {
                    ["status"] = new JsonObject { ["status_str"] = "success", ["completed"] = true },
                    ["outputs"] = new JsonObject { ["5"] = new JsonObject { ["images"] = images } },
                },
            });
        }
        else if (path == "/view")
        {
            var name = req.QueryString["filename"] ?? "";
            var id = name.StartsWith("up_", StringComparison.Ordinal) ? name[3..^4] : "";
            (byte[] Png, double Scale, int Polls) job;
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
