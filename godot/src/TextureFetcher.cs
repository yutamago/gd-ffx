using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;


namespace GdFfx;

/// <summary>
/// The game's textures for any thread (G5: classic maps, characters and effect textures load in
/// the background). The extension may only be called from Godot's main thread, so a worker
/// thread's ask is queued and passed on by <see cref="Poll"/> (once a frame, from
/// <see cref="GameHost"/>); the extension reads the archive and decodes the texture on a thread
/// of its own, and the pixels come back the same way. On the main thread an ask is answered at
/// once. Pixels are RGBA8, top row first (no PNG between: the extension no longer encodes one
/// for Godot to decode).
/// </summary>
public static class TextureFetcher
{
    /// <summary>A texture face's pixels: its size, the texture's faces (6 for a cube map), RGBA8 rows.</summary>
    public sealed record Pixels(int Width, int Height, int Faces, byte[] Rgba)
    {
        /// <summary>As a Godot image (no mipmaps).</summary>
        public Image ToImage() => Image.CreateFromData(Width, Height, false, Image.Format.Rgba8, Rgba);
    }

    private static readonly ConcurrentQueue<(string Kind, string Name, int Face, TaskCompletionSource<Pixels?> Done)> Asked = new();
    private static readonly Dictionary<long, TaskCompletionSource<Pixels?>> Waiting = new();
    private static long _next;

    /// <summary>How long a worker waits for an answer (the game may have gone).</summary>
    private const int TimeoutMs = 30000;

    private static bool OnMainThread => OS.GetThreadCallerId() == OS.GetMainThreadId();

    /// <summary>Asks for a texture face (<paramref name="kind"/> <c>face</c> or <c>runtime</c>) from any thread.</summary>
    public static Task<Pixels?> Request(string kind, string name, int face = 0)
    {
        var done = new TaskCompletionSource<Pixels?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Asked.Enqueue((kind, name, face, done));
        return done.Task;
    }

    /// <summary>A texture face now: on the main thread read at once, on another thread waited for (null: none, or no answer).</summary>
    public static Pixels? Fetch(string kind, string name, int face = 0)
    {
        if (OnMainThread)
            return GameHost.Instance?.Game is { } game ? From(game.TextureNow(kind, name, face)) : null;
        var t = Request(kind, name, face);
        return t.Wait(TimeoutMs) ? t.Result : null;
    }

    /// <summary>Several faces of one texture, asked together (see <see cref="Fetch"/>).</summary>
    public static Pixels?[] FetchMany(string kind, string name, int[] faces)
    {
        var r = new Pixels?[faces.Length];
        if (OnMainThread)
        {
            for (int i = 0; i < faces.Length; i++)
                r[i] = Fetch(kind, name, faces[i]);
            return r;
        }
        var tasks = new Task<Pixels?>[faces.Length];
        for (int i = 0; i < faces.Length; i++)
            tasks[i] = Request(kind, name, faces[i]);
        if (!Task.WaitAll(tasks, TimeoutMs))
            return r;
        for (int i = 0; i < faces.Length; i++)
            r[i] = tasks[i].Result;
        return r;
    }

    /// <summary>Passes the queued asks to the extension and hands back what it read (the main thread, once a frame).</summary>
    public static void Poll(FfxGame? game)
    {
        while (Asked.TryDequeue(out var a))
        {
            var id = ++_next;
            if (game != null && game.TextureRequest(id, a.Kind, a.Name, a.Face))
                Waiting[id] = a.Done;
            else
                a.Done.TrySetResult(null);
        }
        if (game == null || Waiting.Count == 0)
            return;
        foreach (var d in game.TexturesFinished())
            if (Waiting.Remove(d["id"].AsInt64(), out var done))
                done.TrySetResult(From(d));
    }

    private static Pixels? From(Godot.Collections.Dictionary d) =>
        d.TryGetValue("found", out var f) && f.AsBool()
            ? new Pixels(d["width"].AsInt32(), d["height"].AsInt32(), d["faces"].AsInt32(), d["rgba"].AsByteArray())
            : null;
}
