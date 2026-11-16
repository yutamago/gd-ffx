using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;

namespace GdFfx;

/// <summary>
/// The benchmark's GC trace (<c>--bench-gc-trace</c>, docs/running.md "Benchmark"): the runtime's
/// own GC events, in process (no dotnet-trace needed): the allocation samples (one about every
/// 100 KB, with the type allocated), each collection's heap statistics (the objects kept alive for
/// their finalizers, GC handles, pinned objects) and the suspension and collection times. It
/// allocates itself (each event's payload), so the allocation figures of a traced run are higher.
/// </summary>
public sealed class GcTrace : EventListener
{
    private const EventKeywords GcKeyword = (EventKeywords)0x1;

    private readonly ConcurrentDictionary<string, long> _sampled = new();
    private readonly ConcurrentQueue<string> _collections = new();
    private long _suspendStart, _gcStart;
    private string _current = "";
    private volatile bool _on;

    /// <summary>Records from now on (until then events are ignored).</summary>
    public void Begin()
    {
        _sampled.Clear();
        while (_collections.TryDequeue(out _)) { }
        _on = true;
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, GcKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_on || e.PayloadNames == null)
            return;
        switch (e.EventName)
        {
            case { } n when n.StartsWith("GCAllocationTick"):
                var type = Get(e, "TypeName") as string ?? "?";
                var amount = Convert.ToInt64(Get(e, "AllocationAmount64") ?? Get(e, "AllocationAmount") ?? 0L);
                _sampled.AddOrUpdate(type, amount, (_, v) => v + amount);
                break;
            case { } n when n.StartsWith("GCSuspendEEBegin"):
                _suspendStart = e.TimeStamp.Ticks;
                break;
            case { } n when n.StartsWith("GCStart"):
                _gcStart = e.TimeStamp.Ticks;
                _current = $"gen{Get(e, "Depth")} reason {Get(e, "Reason")} type {Get(e, "Type")}, suspend {(_gcStart - _suspendStart) / 1e4:F2} ms";
                break;
            case { } n when n.StartsWith("GCEnd"):
                _current += $", collect {(e.TimeStamp.Ticks - _gcStart) / 1e4:F2} ms";
                break;
            case { } n when n.StartsWith("GCMarkWithType"):
                // what the collection found alive from each kind of root
                var root = Convert.ToInt32(Get(e, "Type") ?? -1) switch
                {
                    0 => "stacks",
                    1 => "finalization queue",
                    2 => "handles",
                    3 => "older generations (cards)",
                    4 => "sized refs",
                    5 => "overflow",
                    6 => "dependent handles",
                    7 => "new objects",
                    8 => "cross-gen",
                    _ => $"root {Get(e, "Type")}",
                };
                var marked = Convert.ToInt64(Get(e, "Bytes") ?? 0L);
                if (marked > 0)
                    _current += $", {root} {marked / 1024} KiB";
                break;
            case { } n when n.StartsWith("GCHeapStats"):
                _current += $"; kept for finalizers {Get(e, "FinalizationPromotedCount")} objects "
                    + $"({Convert.ToInt64(Get(e, "FinalizationPromotedSize") ?? 0L) / 1024} KiB), GC handles {Get(e, "GCHandleCount")}, "
                    + $"pinned {Get(e, "PinnedObjectCount")}, sync blocks {Get(e, "SinkBlockCount")}";
                break;
            case { } n when n.StartsWith("GCRestartEEEnd"):
                _current += $", total {(e.TimeStamp.Ticks - _suspendStart) / 1e4:F2} ms";
                _collections.Enqueue(_current);
                _current = "";
                break;
        }
    }

    private static object? Get(EventWrittenEventArgs e, string name)
    {
        var i = e.PayloadNames!.IndexOf(name);
        return i >= 0 ? e.Payload![i] : null;
    }

    // the census of Godot's disposables (wrappers with finalizers: variants, arrays, dictionaries,
    // string and node names) by the tracker Godot registers each one in (internal, read by reflection)
    private static readonly System.Reflection.PropertyInfo? Tracked = typeof(Godot.GodotObject).Assembly
        .GetType("Godot.DisposablesTracker")?.GetProperty("OtherInstances",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    private HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _made = new();
    private readonly ConcurrentQueue<string> _loads = new();

    /// <summary>The trace of this run (null without <c>--bench-gc-trace</c>).</summary>
    public static GcTrace? Current { get; private set; }

    public GcTrace() => Current = this;

    /// <summary>
    /// Runs a background load (<paramref name="load"/>, on its worker thread) and, when tracing,
    /// notes the Godot disposables it left registered when it ended, by kind: those still
    /// alive or waiting for their finalizers (a collection during the load finalizes some,
    /// so it is a lower bound). Other threads' count too; the main thread makes almost none.
    /// </summary>
    public static T Load<T>(string what, Func<T> load)
    {
        if (Current is not { } trace || Tracked?.GetValue(null) is not System.Collections.IDictionary d)
            return load();
        var before = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var key in d.Keys)
            before.Add(key);
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = load();
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var kinds = new Dictionary<string, int>();
        int n = 0;
        foreach (var key in d.Keys)
            if (!before.Contains(key) && key is WeakReference<IDisposable> w && w.TryGetTarget(out var t))
            {
                n++;
                kinds[Kind(t)] = kinds.GetValueOrDefault(Kind(t)) + 1;
            }
        var top = string.Join(", ", kinds.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} {kv.Value}"));
        trace._loads.Enqueue($"{what} ({ms:F0} ms): {n} godot disposables ({top})");
        return result;
    }

    private static string Kind(object t) =>
        t.GetType().GetField("_native", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(t) is { } native
        && native.GetType().GetField("_typeField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(native) is int type
            ? $"Variant {(Godot.Variant.Type)type}"
            : t.GetType().Name;

    /// <summary>Notes the tracked disposables made since the last call, as made by <paramref name="where"/>.</summary>
    public void Census(string where)
    {
        if (Tracked?.GetValue(null) is not System.Collections.IDictionary d)
            return;
        var now = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var key in d.Keys)
        {
            now.Add(key);
            if (_seen.Contains(key) || key is not WeakReference<IDisposable> w || !w.TryGetTarget(out var t))
                continue;
            var k = $"{where}: {Kind(t)}";
            _made[k] = _made.GetValueOrDefault(k) + 1;
        }
        _seen = now;
    }

    /// <summary>The report's lines: each collection, then the most allocated types.</summary>
    public IEnumerable<string> Lines(int top)
    {
        _on = false;
        foreach (var c in _collections)
            yield return $"gc event: {c}";
        foreach (var (k, n) in _made.OrderByDescending(kv => kv.Value))
            yield return $"gc godot disposables made: {n} {k}";
        foreach (var l in _loads)
            yield return $"gc load: {l}";
        var total = Math.Max(1, _sampled.Values.Sum());
        foreach (var (type, bytes) in _sampled.OrderByDescending(kv => kv.Value).Take(top))
            yield return $"gc sampled allocations: {bytes / 1024} KiB ({bytes * 100.0 / total:F1} %) {type}";
    }
}
