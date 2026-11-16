using System;
using System.Threading.Tasks;
using Godot;

namespace GdFfx;

/// <summary>
/// Where the classic content is built: on a worker thread with a real renderer (its resource
/// storage is thread-safe), inline on the main thread under the headless dummy renderer, whose
/// RID storage is not (materials made on two threads at once corrupted it: "Initializing
/// already initialized RID", null materials in the sweep of the developer saves).
/// </summary>
public static class Background
{
    /// <summary>Whether the renderer takes resources made on other threads.</summary>
    public static readonly bool Threaded = DisplayServer.GetName() != "headless";

    /// <summary>Runs <paramref name="work"/> on a worker, or now when <see cref="Threaded"/> is false.</summary>
    public static Task<T> Run<T>(Func<T> work)
    {
        if (Threaded)
            return Task.Run(work);
        try { return Task.FromResult(work()); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }
}
