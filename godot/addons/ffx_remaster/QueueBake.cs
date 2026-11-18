#if TOOLS
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GdFfx.Remaster;
using GdFfx.Remaster.Materialize;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// A run of the Materials tab's Apply to queue or Make again (docs/remaster-materials.md,
/// "Several textures", "Profiles"; R2b phase 3): one texture after another at the full size, on
/// the GPU (<see cref="GpuBake"/>: one frame each) when the tab has a renderer, else on the CPU
/// (<see cref="MaterialBaker.Cpu"/> on a worker); each result's files written as it comes
/// (content-named: undo and redo never overwrite one) and its entry kept, the entries set as one
/// undo step when the run ends (by the tab). A cancel stops the run: what was made is kept. Driven
/// by <see cref="Tick"/> on the main thread (the texture's pixels and imported files are read
/// there: the extension answers on the main thread only).
/// </summary>
public sealed class QueueBake
{
    public enum JobState { Waiting, Making, Done, Failed, Cancelled }

    /// <summary>A texture to make: its settings (the profile's or the queue's, its exceptions over them) and the profile they are linked to (empty: none).</summary>
    /// <param name="Regions">The texture's regions (R2b phase 6; null or empty: none), as an entry keeps them.</param>
    public sealed record Job(string Id, PbrRecipe Recipe, string Profile, Godot.Collections.Array? Regions = null);

    private readonly List<Job> _jobs;
    private readonly Dictionary<string, JobState> _states = new();
    private readonly Node _parent;
    private readonly bool _gpu;
    private readonly System.Func<string, RgbaImage?> _loadImport;
    private readonly System.Func<Job, BakedMaps, RemasterTexture> _finish;
    private readonly CancellationTokenSource _cts = new();
    private int _next;
    private Job? _cur;
    private GpuBake? _bake;
    private Task<BakedMaps>? _task;
    /// <summary>The texture being made's material recipe (its regions' maps on).</summary>
    private PbrRecipe? _union;

    /// <summary>The undo step's name.</summary>
    public string Action { get; }

    /// <summary>The entries made so far, in order.</summary>
    public List<(string Id, RemasterTexture After)> Results { get; } = new();

    public bool Cancelling { get; private set; }

    public bool Finished { get; private set; }

    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    /// <summary>Each texture's time to make (ms, the bake only), in order.</summary>
    public List<double> Times { get; } = new();

    private readonly Stopwatch _jobClock = new();

    public int Total => _jobs.Count;

    /// <summary>The textures finished (made, failed or cancelled).</summary>
    public int Finishes => _states.Values.Count(s => s is JobState.Done or JobState.Failed or JobState.Cancelled);

    public int Made => _states.Values.Count(s => s == JobState.Done);

    /// <summary>How far the run is (0 to 1).</summary>
    public double Progress => Total == 0 ? 1 : (double)Finishes / Total;

    /// <summary>The last texture made and its albedo (the strip's result).</summary>
    public string? LastId { get; private set; }

    public Image? LastAlbedo { get; private set; }

    public IReadOnlyList<Job> Jobs => _jobs;

    /// <summary>A texture's cached upscaled source for its recipe (ComfyUI's; null: none), and the textures made from one.</summary>
    public System.Func<string, RgbaImage, PbrRecipe, (string Import, RgbaImage Image)?>? Upscaled { get; init; }

    public HashSet<string> ComfyUsed { get; } = new();

    /// <summary>A texture's cached AI estimate of a step (R2b phase 6; null: none: the step generates), and the notes of the ones used per texture.</summary>
    public System.Func<string, RgbaImage, MaterialStep, (string Import, RgbaImage Image, string Note)?>? Ai { get; init; }

    public Dictionary<string, Dictionary<MaterialStep, string>> AiUsed { get; } = new();

    public QueueBake(Node parent, IEnumerable<Job> jobs, bool gpu, System.Func<string, RgbaImage?> loadImport,
        System.Func<Job, BakedMaps, RemasterTexture> finish, string action)
    {
        (_parent, _gpu, _loadImport, _finish, Action) = (parent, gpu, loadImport, finish, action);
        _jobs = jobs.ToList();
        foreach (var j in _jobs)
            _states[j.Id] = JobState.Waiting;
    }

    public JobState? StateOf(string id) => _states.TryGetValue(id, out var s) ? s : null;

    /// <summary>Stops the run: the texture being made is dropped, the rest not made; what was made stays.</summary>
    public void Cancel()
    {
        Cancelling = true;
        _cts.Cancel();
    }

    /// <summary>Once a frame: collects the texture being made, starts the next. True when something changed.</summary>
    public bool Tick()
    {
        if (Finished)
            return false;
        if (_cur is { } cur)
        {
            BakedMaps? got = null;
            if (_bake != null)
            {
                if (!_bake.Done)
                    return false;
                (got, _) = _bake.Finish(recipe: _union);
                BakeBackends.Return(_bake.Backend);
                _bake = null;
            }
            else if (_task != null)
            {
                if (!_task.IsCompleted)
                    return false;
                if (_task.IsCompletedSuccessfully)
                    got = _task.Result;
                else if (!Cancelling)
                    GD.PrintErr($"ffx remaster: materials: {cur.Id}: {_task.Exception?.InnerException?.Message}");
                _task = null;
            }
            Times.Add(_jobClock.Elapsed.TotalMilliseconds);
            if (got != null && !Cancelling)
            {
                try
                {
                    var entry = _finish(cur, got);
                    Results.Add((cur.Id, entry));
                    _states[cur.Id] = JobState.Done;
                    LastId = cur.Id;
                    LastAlbedo = PbrBuilder.ToImage(got.Maps[MaterialStep.Albedo]);
                }
                catch (System.Exception e)
                {
                    GD.PrintErr($"ffx remaster: materials: {cur.Id}: {e}");
                    _states[cur.Id] = JobState.Failed;
                }
            }
            else
                _states[cur.Id] = Cancelling ? JobState.Cancelled : JobState.Failed;
            _cur = null;
        }
        if (Cancelling)
        {
            foreach (var j in _jobs)
                if (_states[j.Id] == JobState.Waiting)
                    _states[j.Id] = JobState.Cancelled;
            Finished = true;
            return true;
        }
        if (_next >= _jobs.Count)
        {
            Finished = true;
            return true;
        }
        var job = _jobs[_next++];
        _states[job.Id] = JobState.Making;
        if (TextureActions.Source(job.Id) is not { } img)
        {
            GD.PrintErr($"ffx remaster: materials: {job.Id}: no pixels");
            _states[job.Id] = JobState.Failed;
            return true;
        }
        var px = PbrBuilder.From(img);
        var imports = new Dictionary<string, RgbaImage>();
        foreach (var f in MaterialPipeline.Files(job.Recipe))
            if (_loadImport(f) is { } ipx)
                imports[f] = ipx;
        if (Upscaled?.Invoke(job.Id, px, job.Recipe) is { } up)
        {
            imports[up.Import] = up.Image;
            ComfyUsed.Add(job.Id);
        }
        // the regions (R2b phase 6) and their files; the AI estimates cached for its steps
        var regions = MaterialRegionStore.Inputs(job.Regions);
        foreach (var f in regions.SelectMany(x => MaterialPipeline.Files(x.Recipe)))
            if (_loadImport(f) is { } rpx)
                imports[f] = rpx;
        _union = MaterialRegions.Union(job.Recipe, regions.Select(x => x.Recipe));
        foreach (var s in MaterialPipeline.AiSteps)
            if ((MaterialPipeline.ModeOf(job.Recipe, s) == StepMode.Ai || regions.Any(x => MaterialPipeline.ModeOf(x.Recipe, s) == StepMode.Ai)) && Ai?.Invoke(job.Id, px, s) is { } ai)
            {
                imports[ai.Import] = ai.Image;
                if (!AiUsed.TryGetValue(job.Id, out var used))
                    AiUsed[job.Id] = used = new Dictionary<MaterialStep, string>();
                used[s] = ai.Note;
            }
        var inputs = new MaterialPipeline.Inputs(job.Id, px, job.Recipe, imports, regions);
        _cur = job;
        _jobClock.Restart();
        if (_gpu)
        {
            var (w, h, scale) = MaterialPipeline.Size(inputs, MaterialPipeline.FullSide);
            // one set of passes for the run (the textures of a size draw into the same targets)
            _bake = new GpuBake(_parent, inputs, w, h, scale, BakeBackends.Take(_parent));
        }
        else
        {
            var token = _cts.Token;
            _task = Task.Run(() => MaterialBaker.Cpu(job.Id, px, job.Recipe, imports, token, regions), token);
        }
        return true;
    }
}
#endif
