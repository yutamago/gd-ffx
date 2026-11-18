#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GdFfx.Remaster;
using GdFfx.Remaster.Materialize;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// AI estimation of maps in the tab (docs/remaster-materials.md, phase 6): a step (albedo, height,
/// normal, roughness) on <b>AI</b> sends the texture to the user's ComfyUI server with the user's
/// workflow for that map (<see cref="ComfyAi"/>) in the background, one at a time, with progress
/// and Cancel; meanwhile the step generates as before. The answer is cached per texture (its
/// pixels), workflow (its content) and map, in memory and in <c>user://remaster/comfyui-cache/</c>
/// (this machine's), and is the step's input from then on (an import
/// <c>ai:&lt;map&gt;:&lt;texture&gt;|&lt;key&gt;</c>: <see cref="MaterializeGraph"/>), the step's
/// settings on top. Save and Apply to queue record the tool, workflow, model and licence in the
/// texture's Source. A workflow without a licence is refused.
/// </summary>
public partial class MaterialsTab
{
    private Task<byte[]>? _aiTask;
    private CancellationTokenSource? _aiCts;
    private ComfyProgress? _aiProgress;
    private (string Key, string Id, MaterialStep Step, ComfyAi.Workflow Workflow)? _aiJob;
    private readonly HashSet<string> _aiStopped = new();

    /// <summary>Each AI step's state (the step panel; the checks): waiting, running n %, cancelled, refused, cached.</summary>
    public Dictionary<MaterialStep, string> AiState { get; } = new();

    /// <summary>Requests sent for AI estimates by this tab (the checks: a cached one sends none).</summary>
    public int AiRuns { get; private set; }

    /// <summary>The run's progress (0 to 1; -1: none going).</summary>
    public double AiDone => _aiJob != null && _aiProgress != null ? _aiProgress.Now.Done : -1;

    /// <summary>The step whose estimate is being made (null: none).</summary>
    public MaterialStep? AiRunning => _aiJob?.Step;

    private ProgressBar? _aiBar;
    private Label? _aiLabel;

    /// <summary>The cache key of <paramref name="px"/>'s <paramref name="s"/> map by workflow <paramref name="w"/>.</summary>
    public static string AiKey(RgbaImage px, MaterialStep s, ComfyAi.Workflow w) =>
        SigHash.Of($"ai|{ComfyKey(px, 1, "-")}|{ComfyAi.Kind(s)}|{w.Hash}");

    /// <summary>The AI steps of a recipe and its regions'.</summary>
    private static IEnumerable<MaterialStep> AiStepsOf(PbrRecipe r, IEnumerable<RegionInput>? regions) =>
        MaterialPipeline.AiSteps.Where(s => MaterialPipeline.ModeOf(r, s) == StepMode.Ai
            || regions?.Any(x => MaterialPipeline.ModeOf(x.Recipe, s) == StepMode.Ai) == true);

    /// <summary>The AI estimate of <paramref name="id"/>'s <paramref name="s"/> map when cached (an import name and its pixels), or null; the note to record.</summary>
    public static (string Import, RgbaImage Image, string Note)? AiCached(string id, RgbaImage px, MaterialStep s)
    {
        var (w, _) = ComfyAi.Check(s);
        if (w == null)
            return null;
        var key = AiKey(px, s, w);
        return ComfyCached(key) is { } img ? (MaterializeGraph.AiPrefix(s, id) + key, img, ComfyAi.Note(s, w, ComfyAi.Model(s), ComfyAi.Licence(s))) : null;
    }

    /// <summary>
    /// Before a build: each AI step's estimate in <see cref="_imports"/> when it is cached; else the
    /// first one missing asked for (once per key) while the step generates.
    /// </summary>
    private void AiPrepare(PbrRecipe r, IReadOnlyList<RegionInput> regions)
    {
        if (Current == null || _original == null)
            return;
        var want = new HashSet<string>();
        foreach (var s in AiStepsOf(r, regions))
        {
            var (w, why) = ComfyAi.Check(s);
            if (w == null)
            {
                AiState[s] = why! + " (generated meanwhile)";
                continue;
            }
            var key = AiKey(_original, s, w);
            var import = MaterializeGraph.AiPrefix(s, Current) + key;
            want.Add(import);
            if (_imports.ContainsKey(import) || ComfyCached(key) is { } px && (_imports[import] = px) != null)
            {
                AiState[s] = $"AI {ComfyAi.Kind(s)}: cached ({System.IO.Path.GetFileName(w.File)})";
                continue;
            }
            if (_aiStopped.Contains(key))
                continue;
            if (_aiJob == null)
                StartAi(key, s, w);
            else if (_aiJob.Value.Key != key)
                AiState[s] = $"AI {ComfyAi.Kind(s)}: waiting (generated meanwhile)";
        }
        // only the asked-for estimates of this texture among the imports
        foreach (var k in _imports.Keys.Where(k => k.StartsWith("ai:", System.StringComparison.Ordinal) && k.Contains($":{Current}|") && !want.Contains(k)).ToList())
            _imports.Remove(k);
        RefreshAi();
    }

    private void StartAi(string key, MaterialStep s, ComfyAi.Workflow w)
    {
        var url = ComfyUpscaler.Url;
        var png = PbrBuilder.ToImage(_original!).SavePngToBuffer();
        _aiCts = new CancellationTokenSource();
        _aiProgress = new ComfyProgress();
        _aiJob = (key, Current!, s, w);
        var (token, progress) = (_aiCts.Token, _aiProgress);
        _aiTask = Task.Run(() => ComfyAi.Estimate(url, w, s, png, token, progress), token);
        AiRuns++;
        AiState[s] = $"AI {ComfyAi.Kind(s)}: starting (generated meanwhile)";
        Say(AiState[s]);
    }

    /// <summary>Once a frame: the run's progress; its answer cached and made the step's input when it comes.</summary>
    private void TickAi()
    {
        if (_aiJob is not { } job || _aiTask == null)
            return;
        if (!_aiTask.IsCompleted)
        {
            var (done, stage) = _aiProgress!.Now;
            AiState[job.Step] = $"AI {ComfyAi.Kind(job.Step)}: {stage}, {done * 100:0} % (generated meanwhile)";
            RefreshAi();
            return;
        }
        var task = _aiTask;
        (_aiTask, _aiJob) = (null, null);
        string kind = ComfyAi.Kind(job.Step);
        if (task.IsCanceled || _aiCts?.IsCancellationRequested == true)
        {
            AiState[job.Step] = $"AI {kind}: cancelled (generated)";
            _aiStopped.Add(job.Key);
        }
        else if (!task.IsCompletedSuccessfully)
        {
            AiState[job.Step] = $"AI {kind}: failed: {task.Exception?.InnerException?.Message ?? "?"} (generated)";
            _aiStopped.Add(job.Key);
        }
        else
        {
            var img = new Image();
            if (img.LoadPngFromBuffer(task.Result) != Error.Ok || img.IsEmpty())
            {
                AiState[job.Step] = $"AI {kind}: the answer is not a PNG (generated)";
                _aiStopped.Add(job.Key);
            }
            else
            {
                var px = PbrBuilder.From(img);
                DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(ComfyCacheDir));
                PbrBuilder.ToImage(px).SavePng(ProjectSettings.GlobalizePath($"{ComfyCacheDir}/{job.Key}.png"));
                lock (ComfyMemory)
                    ComfyMemory[job.Key] = px;
                AiState[job.Step] = $"AI {kind}: done, cached ({px.Width}x{px.Height})";
            }
        }
        Say(AiState[job.Step]);
        // the next AI step's turn, and the maps with this one's answer
        if (job.Id == Current)
            Remake();
        RefreshAi();
    }

    /// <summary>Stops the AI run going (the step keeps generating; Run again asks once more). An error or null.</summary>
    public string? CancelAi()
    {
        if (_aiJob == null || _aiCts == null)
            return "no AI run going";
        _aiCts.Cancel();
        Say("AI: cancelling...");
        return null;
    }

    /// <summary>Asks for step <paramref name="s"/>'s estimate again (after a cancel, a failure or a refusal). An error or null.</summary>
    public string? RunAiAgain(MaterialStep s)
    {
        if (_original == null)
            return "no texture";
        var (w, why) = ComfyAi.Check(s);
        if (w == null)
        {
            AiState[s] = why!;
            RefreshAi();
            return why;
        }
        _aiStopped.Remove(AiKey(_original, s, w));
        Remake();
        return null;
    }

    /// <summary>Whether step <paramref name="s"/>'s input is an AI estimate now (cached and in the graph).</summary>
    public bool AiInUse(MaterialStep s) => Current != null && _imports.Keys.Any(k => k.StartsWith(MaterializeGraph.AiPrefix(s, Current), System.StringComparison.Ordinal));

    /// <summary>The AI row of an AI step's panel: the state, the progress, Cancel, Run again.</summary>
    private void BuildAiRow()
    {
        if (!MaterialPipeline.AiSteps.Contains(Step))
            return;
        var row = new HBoxContainer();
        _settingsBox.AddChild(row);
        _aiBar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = true, CustomMinimumSize = new Vector2(90, 16), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(_aiBar);
        var cancel = new Button { Text = "Cancel", TooltipText = "Stop the AI run (the step keeps generating)" };
        cancel.Pressed += () => Say(CancelAi() ?? "");
        row.AddChild(cancel);
        var again = new Button { Text = "Run again", TooltipText = "Ask the workflow again (after a cancel, a failure or a refusal)" };
        again.Pressed += () => Say(RunAiAgain(Step) ?? "");
        row.AddChild(again);
        _aiLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.75f) };
        _settingsBox.AddChild(_aiLabel);
        RefreshAi();
    }

    private void RefreshAi()
    {
        if (_aiLabel == null || !IsInstanceValid(_aiLabel))
            return;
        bool ai = MaterialPipeline.ModeOf(Recipe, Step) == StepMode.Ai;
        _aiLabel.Text = ai ? AiState.GetValueOrDefault(Step, $"AI {ComfyAi.Kind(Step)}") + "\n" + AiHelp(Step)
            : $"AI: set the mode to AI to estimate this map with a ComfyUI workflow of your own ({ComfyAi.WorkflowSetting(Step)})";
        bool running = _aiJob?.Step == Step;
        _aiBar!.Visible = ai && running;
        _aiBar.Value = System.Math.Max(0, AiDone);
        foreach (var b in _aiBar.GetParent().GetChildren().OfType<Button>())
            b.Visible = ai && (b.Text == "Cancel" ? running : !running && !AiInUse(Step));
    }

    private static string AiHelp(MaterialStep s) => s switch
    {
        MaterialStep.Albedo => "On top: Match classic (the colour check).",
        MaterialStep.Height => "On top: the bands, contrast, bias, S-curve, invert (bright is high).",
        MaterialStep.Normal => "On top: Strength (4: as it is), the bands, angularity, contrast.",
        MaterialStep.Roughness => "On top: invert, the colour samples, the metal's roughness, contrast, bias, fine detail, edge wear.",
        _ => "",
    };

    /// <summary>The Source note lines of the AI estimates <paramref name="used"/> (step: note).</summary>
    public static void SetAiNotes(RemasterTexture entry, IReadOnlyDictionary<MaterialStep, string> used)
    {
        const string head = "material ai ";
        var lines = entry.Source.Split('\n').Where(l => !l.StartsWith(head, System.StringComparison.Ordinal) && l.Length > 0).ToList();
        foreach (var (s, note) in used.OrderBy(kv => kv.Key))
            lines.Add($"{head}{ComfyAi.Kind(s)}: {note}");
        entry.Source = string.Join("\n", lines);
    }

    /// <summary>The notes of the estimates the current texture's maps use now (for Save).</summary>
    private Dictionary<MaterialStep, string> AiUsedNow()
    {
        var used = new Dictionary<MaterialStep, string>();
        if (Current == null || _original == null)
            return used;
        foreach (var s in MaterialPipeline.AiSteps)
            if (AiInUse(s) && AiCached(Current, _original, s) is { } c)
                used[s] = c.Note;
        return used;
    }
}
#endif
