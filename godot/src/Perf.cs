using System.Diagnostics;

namespace GdFfx;

/// <summary>
/// G5's frame-time probes (<c>--bench</c>, <see cref="BenchTest"/>): the CPU time of each view's
/// <c>_Process</c> and of the parts of the frame that may cost (the snapshots' decoding, the
/// effect frame's decoding and mesh), summed per frame. Off (the default) a probe is one branch.
/// </summary>
public static class Perf
{
    /// <summary>The probed parts of a frame.</summary>
    public enum Section
    {
        /// <summary><see cref="GameHost"/>'s frame, without <see cref="Snapshots"/>.</summary>
        Host,
        /// <summary>The snapshots fetched from the extension and decoded (<see cref="GameStateView.Refresh"/>).</summary>
        Snapshots,
        /// <summary><see cref="StateView3D"/>: camera, look, map.</summary>
        View3D,
        /// <summary><see cref="StateView3D"/>'s map objects.</summary>
        Objects,
        /// <summary><see cref="StateView3D"/>'s characters (interpolation and bone posing).</summary>
        Actors,
        /// <summary><see cref="EffectDrawView"/>: the FXDR frame fetched and decoded.</summary>
        EffectDecode,
        /// <summary><see cref="EffectDrawView"/>: the frame's mesh built.</summary>
        EffectMesh,
        EffectView,
        Distortion,
        Messages,
        BattleMenu,
        BattleHud,
        FieldMenu,
        SphereGrid,
        Filter,
        Debug,
        Count,
    }

    /// <summary>Whether the probes run (<c>--bench</c>).</summary>
    public static bool On { get; set; }

    private static readonly long[] Ticks = new long[(int)Section.Count];
    private static readonly long[] Bytes = new long[(int)Section.Count];

    /// <summary>A probe's start: the time and the main thread's allocations so far (zero when off).</summary>
    public readonly record struct Probe(long Ticks, long Bytes);

    public static Probe Start() =>
        On ? new Probe(Stopwatch.GetTimestamp(), System.GC.GetAllocatedBytesForCurrentThread()) : default;

    /// <summary>A probe's end: the time and allocations since <paramref name="start"/> added to <paramref name="s"/>.</summary>
    public static void End(Section s, Probe start)
    {
        if (!On)
            return;
        Ticks[(int)s] += Stopwatch.GetTimestamp() - start.Ticks;
        Bytes[(int)s] += System.GC.GetAllocatedBytesForCurrentThread() - start.Bytes;
    }

    /// <summary>This frame's milliseconds and bytes allocated per section into <paramref name="ms"/> and <paramref name="bytes"/>, then cleared.</summary>
    public static void Take(double[] ms, long[] bytes)
    {
        for (int i = 0; i < Ticks.Length; i++)
        {
            ms[i] = Ticks[i] * 1000.0 / Stopwatch.Frequency;
            bytes[i] = Bytes[i];
            Ticks[i] = 0;
            Bytes[i] = 0;
        }
    }
}
