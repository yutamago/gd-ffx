using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Text;
using Godot;

namespace GdFfx;

/// <summary>
/// G5's benchmark (<c>--bench</c>, with <c>--devsave=N</c> and optionally <c>--battle=F,G,F</c>;
/// docs/running.md "Benchmark"): once the save's map (or battle) is shown with posed models it
/// waits <c>--bench-settle</c> seconds (default 3), then records <c>--bench-seconds</c> (default
/// 20) of frames and prints, as <c>ffx bench:</c> lines, the frame times (mean, p50, p99, max,
/// the frames over 1/60, 1/30 and 0.1 s), the CPU time of each view per frame
/// (<see cref="Perf"/>), the main thread's allocations per frame and the collections and GC pauses
/// in the window, the game's updates (the snapshots' arrivals: mean and longest gap) and, for the
/// load before, its longest frames with where their time went. <c>--bench-walk</c> holds down and
/// left in the window (at Besaid: off the beach to the next map, a load while playing).
/// Headless it measures the CPU side only (Godot's dummy renderer draws nothing).
/// </summary>
public sealed class BenchTest : ITest
{
    private const int Sections = (int)Perf.Section.Count;

    /// <summary>
    /// A frame's record: no references (so the records, kept to the end, are nothing for the
    /// collector to trace; old records pointing at young objects would make every young
    /// collection scan them). Its sections' times and allocations are in the flat arrays at
    /// <see cref="At"/>.
    /// </summary>
    private struct Frame
    {
        public double Ms;
        public long Alloc;
        /// <summary>The highest generation collected in the frame (-1 none).</summary>
        public int Gc;
        public bool Snapshot;
        public bool Loading;
        public int Draws;
        public int Objects;
        /// <summary>The level's index in <see cref="_levels"/>.</summary>
        public int Level;
        /// <summary>The index of the frame's sections in <see cref="_sectionMs"/> and <see cref="_sectionBytes"/> (divided by <see cref="Sections"/>).</summary>
        public int At;
        /// <summary>The frame's number in the load or the window.</summary>
        public int N;
    }

    /// <summary>The load's longest frames kept (the first section slots): the load can be 100,000 frames and more.</summary>
    private const int LoadKept = 8;

    /// <summary>The frames recorded up front (about 50 s at 2,500 fps), so the window allocates no record.</summary>
    private const int Capacity = 1 << 17;

    private readonly double _timeout, _settle, _seconds;
    private readonly bool _battle, _walk;
    private readonly List<Frame> _load = new(LoadKept), _window = new(Capacity);
    private int _loadFrames;
    private float[] _sectionMs = new float[Capacity * Sections];
    private int[] _sectionBytes = new int[Capacity * Sections];
    private int _frames = LoadKept;
    private readonly List<string> _levels = new();
    private readonly List<double> _arrivals = new(4096);
    private StateView3D? _view;
    private readonly double[] _scratch = new double[Sections];
    private readonly long[] _bytes = new long[Sections];
    private long _last, _alloc;
    private readonly int[] _gcSeen = new int[3];
    private double _total, _readyAt = -1, _windowAt = -1;
    private long _update = -1, _skipped;
    private int[] _gc0 = [];
    private TimeSpan _pause0;
    private long _totalAlloc0;
    private long _gcIndex = -1;
    private readonly List<string> _collections = new();
    private readonly List<double> _pauses = new();
    private long _updates0;
    private double _seconds0;
    private readonly GcTrace? _trace;

    public string Note { get; private set; } = "";

    public BenchTest(double timeout)
    {
        _timeout = timeout;
        _settle = GameSettings.Number("bench-settle", 3);
        _seconds = GameSettings.Number("bench-seconds", 20);
        _battle = !string.IsNullOrEmpty(GameSettings.Arg("battle"));
        _walk = GameSettings.Flag("bench-walk");
        Perf.On = true;
        if (GameSettings.Flag("bench-gc-trace"))
            _trace = new GcTrace();
        // headless, Godot sleeps up to this long a frame when it cannot draw (6.9 ms by default:
        // a cap of 145 fps); the benchmark measures the frame's work instead
        OS.LowProcessorUsageModeSleepUsec = 0;
    }

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        var now = Stopwatch.GetTimestamp();
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        if (_last != 0)
            Record(now, alloc, v);
        _last = now;
        _alloc = alloc;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > _timeout)
            return Done(false, $"timed out: level {v.Current?.World.Level} ({DebugView.SnapshotLine(v)})");
        if (_view == null || !GodotObject.IsInstanceValid(_view))
            _view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        var view = _view;
        var c = v.Current;
        if (_readyAt < 0)
        {
            if (c == null || view == null || c.World.Level.Length == 0 || c.World.Level.Contains("/titl/")
                || view.MapLevel != c.World.Level || view.Posed.Models == 0 || _battle != c.World.Battle || _total < 5)
                return null;
            _readyAt = _total;
            GD.Print($"ffx bench: ready at {_total:F1} s: {c.World.Level} ({view.Posed.Models} models, {view.ObjectsPlaced} objects)");
            return null;
        }
        if (_windowAt < 0)
        {
            if (_total - _readyAt < _settle)
                return null;
            _windowAt = _total;
            _gc0 = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            _pause0 = GC.GetTotalPauseDuration();
            _totalAlloc0 = GC.GetTotalAllocatedBytes();
            _updates0 = s.Updates;
            _seconds0 = s.Seconds;
            _arrivals.Clear();
            _skipped = 0;
            _trace?.Begin();
            if (_walk)
                Keys(true);
            return null;
        }
        // the census: the Godot disposables each probed section makes, over frames 1000 to 1100 of the window
        if (_trace != null && _window.Count == 1000)
        {
            _trace.Census("before");
            Perf.AfterSection = sec => _trace.Census(sec.ToString());
        }
        else if (_trace != null && _window.Count is > 1000 and <= 1100)
            _trace.Census("between the probes");
        if (_trace != null && _window.Count == 1100)
            Perf.AfterSection = null;
        if (_total - _windowAt < _seconds)
            return null;
        if (_walk)
            Keys(false);
        Report(game, s, v, view);
        return Done(true, $"{_window.Count} frames in {_total - _windowAt:F1} s at {c?.World.Level}");
    }

    private void Record(long now, long alloc, GameStateView v)
    {
        if ((_frames + 1) * Sections > _sectionMs.Length)
        {
            System.Array.Resize(ref _sectionMs, _sectionMs.Length * 2);
            System.Array.Resize(ref _sectionBytes, _sectionBytes.Length * 2);
        }
        var level = v.Current?.World.Level ?? "";
        if (_levels.Count == 0 || _levels[^1] != level)
            _levels.Add(level);
        var f = new Frame
        {
            Ms = (now - _last) * 1000.0 / Stopwatch.Frequency,
            Alloc = alloc - _alloc,
            Gc = -1,
            Draws = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            Objects = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
            Level = _levels.Count - 1,
        };
        var u = v.Current?.Update ?? 0;
        if (v.Current != null && u != _update)
        {
            f.Snapshot = true;
            if (_update >= 0 && u > _update + 1)
                _skipped += u - _update - 1;
            _update = u;
            _arrivals.Add(now * 1000.0 / Stopwatch.Frequency);
        }
        if (_windowAt < 0)
        {
            // the load: only its longest frames are kept, in the first slots
            f.N = _loadFrames++;
            if (_load.Count < LoadKept)
                f.At = _load.Count;
            else
            {
                int min = 0;
                for (int i = 1; i < _load.Count; i++)
                    if (_load[i].Ms < _load[min].Ms)
                        min = i;
                if (f.Ms <= _load[min].Ms)
                {
                    Perf.Take(_scratch, _bytes);
                    NoteCollections(f);
                    return;
                }
                f.At = _load[min].At;
                _load.RemoveAt(min);
            }
        }
        else
        {
            f.N = _window.Count;
            f.At = _frames++;
        }
        Perf.Take(_scratch, _bytes);
        for (int i = 0; i < Sections; i++)
        {
            _sectionMs[f.At * Sections + i] = (float)_scratch[i];
            _sectionBytes[f.At * Sections + i] = (int)Math.Min(_bytes[i], int.MaxValue);
        }
        f.Gc = NoteCollections(f);
        if (_windowAt < 0)
        {
            f.Loading = _readyAt < 0;
            _load.Add(f);
        }
        else
            _window.Add(f);
    }

    /// <summary>The highest generation collected since the last frame (-1 none); in the window, its details noted.</summary>
    private int NoteCollections(Frame f)
    {
        int gc = -1;
        for (int g = 2; g >= 0; g--)
            if (GC.CollectionCount(g) != _gcSeen[g])
            {
                gc = g;
                break;
            }
        for (int g = 0; g < 3; g++)
            _gcSeen[g] = GC.CollectionCount(g);
        if (gc >= 0 && _windowAt >= 0)
            Collection(f);
        return gc;
    }

    private double Sec(Frame f, int i) => _sectionMs[f.At * Sections + i];

    private double SecSum(Frame f)
    {
        double sum = 0;
        for (int i = 0; i < Sections; i++)
            sum += _sectionMs[f.At * Sections + i];
        return sum;
    }

    /// <summary>The last collection's details (the runtime keeps only the last one of each kind).</summary>
    private void Collection(Frame f)
    {
        var i = GC.GetGCMemoryInfo(GCKind.Any);
        if (i.Index == _gcIndex)
            return;
        var missed = _gcIndex >= 0 ? i.Index - _gcIndex - 1 : 0;
        _gcIndex = i.Index;
        double pause = 0;
        foreach (var d in i.PauseDurations)
            pause += d.TotalMilliseconds;
        _pauses.Add(pause);
        var gens = i.GenerationInfo;
        var sizes = new StringBuilder();
        for (int g = 0; g < gens.Length; g++)
            sizes.Append($"{(g == 0 ? "" : " ")}{(g < 3 ? $"g{g}" : g == 3 ? "loh" : "poh")} {gens[g].SizeBeforeBytes / 1024}>{gens[g].SizeAfterBytes / 1024}");
        _collections.Add($"#{i.Index} gen{i.Generation}{(i.Concurrent ? " background" : "")}{(i.Compacted ? " compacting" : "")}"
            + $"{(missed > 0 ? $" (+{missed} not seen)" : "")}: pause {pause:F2} ms, frame {f.Ms:F1} ms, promoted {i.PromotedBytes / 1024} KiB, "
            + $"pinned {i.PinnedObjectsCount}, finalizable pending {i.FinalizationPendingCount}, heap {i.HeapSizeBytes / 1048576} MiB, "
            + $"fragmented {i.FragmentedBytes / 1024} KiB; KiB before>after: {sizes}");
    }

    private static double Pct(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    private void Report(FfxGame game, GameState s, GameStateView v, StateView3D? view)
    {
        var w = _window;
        var ms = w.Select(f => f.Ms).OrderBy(x => x).ToList();
        double mean = ms.Average();
        var seconds = _total - _windowAt;
        var c = v.Current;
        Print($"scene {c?.World.Level} (map {c?.World.Map:x}{(c?.World.Battle == true ? ", battle" : "")}), "
            + $"{view?.Posed.Models} models ({view?.Posed.PosedBones} bones), {view?.ObjectsPlaced} objects, "
            + $"renderer {RenderingServer.GetCurrentRenderingDriverName()}/{DisplayServer.GetName()}, max fps {Engine.MaxFps}, low-processor sleep {OS.LowProcessorUsageModeSleepUsec} us, "
            + $"vsync {DisplayServer.WindowGetVsyncMode()}{(_walk ? ", walking" : "")}");
        Print($"frames {w.Count} in {seconds:F1} s: {w.Count / seconds:F0} fps; frame ms mean {mean:F2}, p50 {Pct(ms, 0.5):F2}, "
            + $"p99 {Pct(ms, 0.99):F2}, max {ms[^1]:F2}; over 16.7 ms {ms.Count(x => x > 16.7)}, "
            + $"over 33.3 ms {ms.Count(x => x > 33.3)}, over 100 ms {ms.Count(x => x > 100)}");
        // the views' CPU time per frame
        var b = new StringBuilder("cpu ms/frame (mean/p99/max):");
        double sum = 0;
        for (int i = 0; i < Sections; i++)
        {
            var xs = w.Select(f => Sec(f, i)).OrderBy(x => x).ToList();
            var m = xs.Average();
            sum += m;
            if (xs[^1] < 0.001)
                continue;
            b.Append($" {(Perf.Section)i} {m:F3}/{Pct(xs, 0.99):F3}/{xs[^1]:F2}");
        }
        Print(b.ToString());
        b.Clear().Append("alloc KiB/frame (mean):");
        for (int i = 0; i < Sections; i++)
        {
            var m = w.Average(f => (double)_sectionBytes[f.At * Sections + i]) / 1024;
            if (m >= 0.05)
                b.Append($" {(Perf.Section)i} {m:F1}");
        }
        Print(b.ToString());
        var snapFrames = w.Where(f => f.Snapshot).Select(SecSum).DefaultIfEmpty(0).Average();
        var otherFrames = w.Where(f => !f.Snapshot).Select(SecSum).DefaultIfEmpty(0).Average();
        Print($"cpu ms/frame, all views: {sum:F3} (frames with a new snapshot {snapFrames:F3}, others {otherFrames:F3}); "
            + $"the rest of the frame (Godot's own work) {mean - sum:F3}");
        var allocs = w.Select(f => (double)f.Alloc).OrderBy(x => x).ToList();
        int[] gc = [GC.CollectionCount(0) - _gc0[0], GC.CollectionCount(1) - _gc0[1], GC.CollectionCount(2) - _gc0[2]];
        var pause = GC.GetTotalPauseDuration() - _pause0;
        Print($"gc: main thread allocates {allocs.Average() / 1024:F1} KiB/frame (p99 {Pct(allocs, 0.99) / 1024:F1}, "
            + $"max {allocs[^1] / 1024:F1}), all threads {(GC.GetTotalAllocatedBytes() - _totalAlloc0) / seconds / 1048576:F2} MiB/s; "
            + $"collections gen0 {gc[0]} gen1 {gc[1]} gen2 {gc[2]}, paused {pause.TotalMilliseconds:F1} ms in all; "
            + $"frames with a collection: mean {w.Where(f => f.Gc >= 0).Select(f => f.Ms).DefaultIfEmpty(0).Average():F2} ms, "
            + $"max {w.Where(f => f.Gc >= 0).Select(f => f.Ms).DefaultIfEmpty(0).Max():F2} ms; "
            + $"last full: {GC.GetGCMemoryInfo(GCKind.FullBlocking).Index}, background {GC.GetGCMemoryInfo(GCKind.Background).Index}, "
            + $"LOH threshold {AppContext.GetData("GCLOHThreshold") ?? AppContext.GetData("System.GC.LOHThreshold")}");
        var pauses = _pauses.OrderBy(x => x).ToList();
        Print($"gc pauses: {pauses.Count} seen, mean {(pauses.Count > 0 ? pauses.Average() : 0):F2} ms, max {(pauses.Count > 0 ? pauses[^1] : 0):F2} ms, "
            + $"over 4 ms {pauses.Count(x => x > 4)}; collected in lost frames (loads) {GameHost.LostFrameCollections}; {(GCSettings.IsServerGC ? "server" : "workstation")} GC, latency {GCSettings.LatencyMode}, "
            + $"concurrent {AppContext.GetData("System.GC.Concurrent") ?? "(default)"}, .NET {System.Environment.Version}, "
            + $"{(Debugger.IsAttached ? "debugger, " : "")}{(IsDebugBuild() ? "Debug" : "Release")} build, "
            + $"Godot objects {Performance.GetMonitor(Performance.Monitor.ObjectCount)}, nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)}, "
            + $"resources {Performance.GetMonitor(Performance.Monitor.ObjectResourceCount)}");
        foreach (var line in _collections)
            Print($"gc {line}");
        if (_trace != null)
        {
            foreach (var line in _trace.Lines(25))
                Print(line);
            _trace.Dispose();
        }
        var gaps = new List<double>();
        for (int i = 1; i < _arrivals.Count; i++)
            gaps.Add(_arrivals[i] - _arrivals[i - 1]);
        gaps.Sort();
        var updates = s.Updates - _updates0;
        Print($"game: {updates} updates in {s.Seconds - _seconds0:F1} s ({updates / Math.Max(s.Seconds - _seconds0, 1e-3):F1}/s); "
            + $"snapshots seen {_arrivals.Count}, {_skipped} skipped; gap ms mean {(gaps.Count > 0 ? gaps.Average() : 0):F1}, "
            + $"p99 {Pct(gaps, 0.99):F1}, max {(gaps.Count > 0 ? gaps[^1] : 0):F1}, over 50 ms {gaps.Count(g => g > 50)}");
        if (c != null)
        {
            var snaps = game.Snapshots;
            var fx = game.EffectDraws(c.Update);
            var frame = EffectDrawView.Decode(fx);
            Print($"sizes: snapshot {snaps["current"].AsByteArray().Length / 1024} KiB, effect frame (FXDR) {fx.Length / 1024} KiB "
                + $"({frame?.Draws.Count ?? 0} draws, {frame?.Draws.Count(d => EffectDrawView.Drawn(d.Pass)) ?? 0} in drawn passes, "
                + $"{frame?.Draws.Sum(d => d.Vertices) ?? 0} vertices)");
        }
        Print($"render (Godot's monitors, mean per frame): {w.Average(f => f.Draws):F0} draw calls, {w.Average(f => f.Objects):F0} objects");
        // the longest frames: the load before the window, then the window's
        Longest("load", _load, 5);
        Longest("window", _window, 5);
    }

    private void Longest(string what, List<Frame> frames, int n)
    {
        if (frames.Count == 0)
            return;
        foreach (var f in frames.OrderByDescending(x => x.Ms).Take(n))
        {
            var parts = Enumerable.Range(0, Sections).Where(k => Sec(f, k) >= 0.5)
                .OrderByDescending(k => Sec(f, k)).Select(k => $"{(Perf.Section)k} {Sec(f, k):F1}");
            var views = SecSum(f);
            Print($"longest {what} frame #{f.N}: {f.Ms:F1} ms (views {views:F1}: {string.Join(", ", parts)}; other {f.Ms - views:F1})"
                + $"{(f.Snapshot ? ", new snapshot" : "")}{(f.Gc >= 0 ? $", gen{f.Gc} collection" : "")}, alloc {f.Alloc / 1024} KiB, {_levels[f.Level]}");
        }
    }

    private static bool IsDebugBuild() =>
        typeof(BenchTest).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().Any(a => a.IsJITOptimizerDisabled);

    private static void Print(string line) => GD.Print($"ffx bench: {line}");

    private static void Keys(bool down)
    {
        foreach (var k in new[] { Key.Down, Key.Left })
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = down });
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }
}
