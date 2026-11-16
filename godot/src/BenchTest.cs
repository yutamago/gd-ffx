using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    private sealed class Frame
    {
        public double Ms;
        public readonly double[] Section = new double[Sections];
        public long Alloc;
        public bool Snapshot;
        public bool Loading;
        public int Draws;
        public int Objects;
        public string Level = "";
    }

    private readonly double _timeout, _settle, _seconds;
    private readonly bool _battle, _walk;
    private readonly List<Frame> _load = new(), _window = new();
    private readonly List<double> _arrivals = new();
    private readonly double[] _scratch = new double[Sections];
    private long _last, _alloc;
    private double _total, _readyAt = -1, _windowAt = -1;
    private long _update = -1, _skipped;
    private int[] _gc0 = [];
    private TimeSpan _pause0;
    private long _totalAlloc0;
    private long _updates0;
    private double _seconds0;

    public string Note { get; private set; } = "";

    public BenchTest(double timeout)
    {
        _timeout = timeout;
        _settle = GameSettings.Number("bench-settle", 3);
        _seconds = GameSettings.Number("bench-seconds", 20);
        _battle = !string.IsNullOrEmpty(GameSettings.Arg("battle"));
        _walk = GameSettings.Flag("bench-walk");
        Perf.On = true;
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
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
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
            if (_walk)
                Keys(true);
            return null;
        }
        if (_total - _windowAt < _seconds)
            return null;
        if (_walk)
            Keys(false);
        Report(s, v, view);
        return Done(true, $"{_window.Count} frames in {_total - _windowAt:F1} s at {c?.World.Level}");
    }

    private void Record(long now, long alloc, GameStateView v)
    {
        var f = new Frame
        {
            Ms = (now - _last) * 1000.0 / Stopwatch.Frequency,
            Alloc = alloc - _alloc,
            Draws = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            Objects = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
            Level = v.Current?.World.Level ?? "",
        };
        Perf.Take(_scratch);
        Array.Copy(_scratch, f.Section, Sections);
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
            f.Loading = _readyAt < 0;
            _load.Add(f);
        }
        else
            _window.Add(f);
    }

    private static double Pct(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    private void Report(GameState s, GameStateView v, StateView3D? view)
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
            var xs = w.Select(f => f.Section[i]).OrderBy(x => x).ToList();
            var m = xs.Average();
            sum += m;
            if (xs[^1] < 0.001)
                continue;
            b.Append($" {(Perf.Section)i} {m:F3}/{Pct(xs, 0.99):F3}/{xs[^1]:F2}");
        }
        Print(b.ToString());
        var snapFrames = w.Where(f => f.Snapshot).Select(f => f.Section.Sum()).DefaultIfEmpty(0).Average();
        var otherFrames = w.Where(f => !f.Snapshot).Select(f => f.Section.Sum()).DefaultIfEmpty(0).Average();
        Print($"cpu ms/frame, all views: {sum:F3} (frames with a new snapshot {snapFrames:F3}, others {otherFrames:F3}); "
            + $"the rest of the frame (Godot's own work) {mean - sum:F3}");
        var allocs = w.Select(f => (double)f.Alloc).OrderBy(x => x).ToList();
        int[] gc = [GC.CollectionCount(0) - _gc0[0], GC.CollectionCount(1) - _gc0[1], GC.CollectionCount(2) - _gc0[2]];
        var pause = GC.GetTotalPauseDuration() - _pause0;
        Print($"gc: main thread allocates {allocs.Average() / 1024:F1} KiB/frame (p99 {Pct(allocs, 0.99) / 1024:F1}, "
            + $"max {allocs[^1] / 1024:F1}), all threads {(GC.GetTotalAllocatedBytes() - _totalAlloc0) / seconds / 1048576:F2} MiB/s; "
            + $"collections gen0 {gc[0]} gen1 {gc[1]} gen2 {gc[2]}, paused {pause.TotalMilliseconds:F1} ms in all");
        var gaps = new List<double>();
        for (int i = 1; i < _arrivals.Count; i++)
            gaps.Add(_arrivals[i] - _arrivals[i - 1]);
        gaps.Sort();
        var updates = s.Updates - _updates0;
        Print($"game: {updates} updates in {s.Seconds - _seconds0:F1} s ({updates / Math.Max(s.Seconds - _seconds0, 1e-3):F1}/s); "
            + $"snapshots seen {_arrivals.Count}, {_skipped} skipped; gap ms mean {(gaps.Count > 0 ? gaps.Average() : 0):F1}, "
            + $"p99 {Pct(gaps, 0.99):F1}, max {(gaps.Count > 0 ? gaps[^1] : 0):F1}, over 50 ms {gaps.Count(g => g > 50)}");
        Print($"render (Godot's monitors, mean per frame): {w.Average(f => f.Draws):F0} draw calls, {w.Average(f => f.Objects):F0} objects");
        // the longest frames: the load before the window, then the window's
        Longest("load", _load, 5);
        Longest("window", _window, 5);
    }

    private static void Longest(string what, List<Frame> frames, int n)
    {
        if (frames.Count == 0)
            return;
        var top = frames.Select((f, i) => (f, i)).OrderByDescending(x => x.f.Ms).Take(n);
        foreach (var (f, i) in top)
        {
            var parts = Enumerable.Range(0, Sections).Where(k => f.Section[k] >= 0.5)
                .OrderByDescending(k => f.Section[k]).Select(k => $"{(Perf.Section)k} {f.Section[k]:F1}");
            var views = f.Section.Sum();
            Print($"longest {what} frame #{i}: {f.Ms:F1} ms (views {views:F1}: {string.Join(", ", parts)}; other {f.Ms - views:F1})"
                + $"{(f.Snapshot ? ", new snapshot" : "")}, alloc {f.Alloc / 1024} KiB, {f.Level}");
        }
    }

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
