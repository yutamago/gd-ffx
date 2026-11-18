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
/// ComfyUI upscaling in the Source step (docs/remaster-materials.md, phase 5): with the upscaler
/// set to ComfyUI (the editor setting <c>ffx_remaster/comfyui_url</c> names a server of one's own)
/// and an upscale above 1, the texture is sent to the server in the background
/// (<see cref="ComfyUpscaler"/>: progress, cancel); meanwhile the maps are made from the Lanczos
/// upscale. The answer is cached per texture (its pixels), model and factor, in memory and in
/// <c>user://remaster/comfyui-cache/</c> (this machine's, never in git), and is the graph's source
/// from then on (an import named <c>upscaled:&lt;texture&gt;|x&lt;factor&gt;|&lt;key&gt;</c>:
/// <see cref="MaterializeGraph"/>). Save and Apply to queue record the tool, the model and its
/// licence in the texture's Source.
/// </summary>
public partial class MaterialsTab
{
    public const string ComfyCacheDir = "user://remaster/comfyui-cache";

    /// <summary>The upscaler's names in the recipe.</summary>
    public const string Lanczos3 = "lanczos", ComfyUi = "comfyui";

    private static readonly Dictionary<string, RgbaImage> ComfyMemory = new();

    private sealed class ComfyProgress : System.IProgress<(double Done, string Stage)>
    {
        private (double, string) _now = (0, "starting");
        public (double Done, string Stage) Now { get { lock (this) return _now; } }
        public void Report((double Done, string Stage) v) { lock (this) _now = v; }
    }

    private Task<byte[]>? _comfyTask;
    private CancellationTokenSource? _comfyCts;
    private ComfyProgress? _comfyProgress;
    /// <summary>The run going: its cache key, texture and factor.</summary>
    private (string Key, string Id, int Factor, string Model)? _comfyJob;
    /// <summary>Keys not to ask for again by themselves (cancelled or failed; Run again clears it).</summary>
    private readonly HashSet<string> _comfyStopped = new();

    /// <summary>The ComfyUI upscale's state (the step panel; the checks): idle, running n %, cancelled, failed, cached.</summary>
    public string ComfyState { get; private set; } = "";

    /// <summary>The run's progress (0 to 1; -1: none going).</summary>
    public double ComfyDone => _comfyJob != null && _comfyProgress != null ? _comfyProgress.Now.Done : -1;

    /// <summary>Requests sent to ComfyUI by this tab (the checks: a cached result sends none).</summary>
    public int ComfyRuns { get; private set; }

    private ProgressBar? _comfyBar;
    private Label? _comfyLabel;

    /// <summary>The last texture's pixel hash (a texture's pixels are hashed once while it is shown).</summary>
    private static (RgbaImage? Px, string Hash) _lastHash;

    /// <summary>The cache key of <paramref name="px"/> upscaled <paramref name="factor"/> times by <paramref name="model"/>.</summary>
    public static string ComfyKey(RgbaImage px, int factor, string model)
    {
        var (seen, hash) = _lastHash;
        if (!ReferenceEquals(seen, px))
        {
            hash = System.Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(px.Rgba))[..16].ToLowerInvariant();
            _lastHash = (px, hash);
        }
        return SigHash.Of($"{hash}|{px.Width}x{px.Height}|{model}|x{factor}");
    }

    /// <summary>The cache file of the current texture upscaled <paramref name="factor"/> times by the model now (null: no texture).</summary>
    public string? ComfyCacheFile(int factor) => _original == null ? null : $"{ComfyCacheDir}/{ComfyKey(_original, factor, ComfyUpscaler.Model)}.png";

    /// <summary>The graph's import name of an upscaled source.</summary>
    public static string UpscaledImport(string id, int factor, string key) => $"upscaled:{id}|x{factor}|{key}";

    /// <summary>A cached upscale (memory, else the cache folder), or null.</summary>
    public static RgbaImage? ComfyCached(string key)
    {
        lock (ComfyMemory)
            if (ComfyMemory.TryGetValue(key, out var hit))
                return hit;
        var file = $"{ComfyCacheDir}/{key}.png";
        if (!FileAccess.FileExists(file) || Image.LoadFromFile(ProjectSettings.GlobalizePath(file)) is not { } img || img.IsEmpty())
            return null;
        var px = PbrBuilder.From(img);
        lock (ComfyMemory)
            ComfyMemory[key] = px;
        return px;
    }

    /// <summary>Forgets the upscales kept in memory (the checks: the cache folder is read again).</summary>
    public static void ClearComfyMemory()
    {
        lock (ComfyMemory)
            ComfyMemory.Clear();
    }

    /// <summary>Whether <paramref name="r"/> asks for ComfyUI's upscale.</summary>
    public static bool WantsComfy(PbrRecipe r) => r.Upscaler == ComfyUi && System.Math.Clamp(r.Upscale, 1, 4) > 1
        && MaterialPipeline.ModeOf(r, MaterialStep.Source) != StepMode.Import;

    /// <summary>
    /// Before a build: the current texture's ComfyUI upscale in <see cref="_imports"/> when it is
    /// cached; else asked for (once per key) while the Lanczos upscale stands in.
    /// </summary>
    private void ComfyPrepare(PbrRecipe r)
    {
        if (Current == null || _original == null)
            return;
        // only the asked-for upscale of this texture among the imports (another model's goes)
        var prefix = $"upscaled:{Current}|";
        string? want = null;
        if (WantsComfy(r))
        {
            int f = System.Math.Clamp(r.Upscale, 1, 4);
            var key = ComfyKey(_original, f, ComfyUpscaler.Model);
            want = UpscaledImport(Current, f, key);
            if (!_imports.ContainsKey(want))
            {
                if (ComfyCached(key) is { } px)
                {
                    _imports[want] = px;
                    ComfyState = $"ComfyUI x{f} ({ComfyUpscaler.Model}): cached";
                }
                else if (!_comfyStopped.Contains(key) && _comfyJob?.Key != key)
                    StartComfy(key, f);
            }
            else
                ComfyState = $"ComfyUI x{f} ({ComfyUpscaler.Model}): cached";
        }
        foreach (var k in _imports.Keys.Where(k => k.StartsWith(prefix, System.StringComparison.Ordinal) && k != want).ToList())
            _imports.Remove(k);
        RefreshComfy();
    }

    private void StartComfy(string key, int factor)
    {
        if (!ComfyUpscaler.Configured)
        {
            ComfyState = $"ComfyUI: no server (the editor setting {ComfyUpscaler.UrlSetting}); Lanczos-3 meanwhile";
            return;
        }
        CancelComfy(quiet: true);
        var (url, model) = (ComfyUpscaler.Url, ComfyUpscaler.Model);
        var png = PbrBuilder.ToImage(_original!).SavePngToBuffer();
        _comfyCts = new CancellationTokenSource();
        _comfyProgress = new ComfyProgress();
        _comfyJob = (key, Current!, factor, model);
        var (token, progress) = (_comfyCts.Token, _comfyProgress);
        _comfyTask = Task.Run(() => ComfyUpscaler.Upscale(url, model, png, factor, token, progress), token);
        ComfyRuns++;
        ComfyState = $"ComfyUI x{factor} ({model}): starting (Lanczos-3 meanwhile)";
        Say(ComfyState);
    }

    /// <summary>Once a frame: the run's progress; its answer cached and made the source when it comes.</summary>
    private void TickComfy()
    {
        if (_comfyJob is not { } job || _comfyTask == null)
            return;
        if (!_comfyTask.IsCompleted)
        {
            var (done, stage) = _comfyProgress!.Now;
            ComfyState = $"ComfyUI x{job.Factor} ({job.Model}): {stage}, {done * 100:0} % (Lanczos-3 meanwhile)";
            RefreshComfy();
            return;
        }
        var task = _comfyTask;
        (_comfyTask, _comfyJob) = (null, null);
        if (task.IsCanceled || _comfyCts?.IsCancellationRequested == true)
        {
            ComfyState = $"ComfyUI x{job.Factor}: cancelled (Lanczos-3)";
            _comfyStopped.Add(job.Key);
        }
        else if (!task.IsCompletedSuccessfully)
        {
            ComfyState = $"ComfyUI x{job.Factor}: failed: {task.Exception?.InnerException?.Message ?? "?"} (Lanczos-3)";
            _comfyStopped.Add(job.Key);
        }
        else
        {
            var img = new Image();
            if (img.LoadPngFromBuffer(task.Result) != Error.Ok || img.IsEmpty())
            {
                ComfyState = $"ComfyUI x{job.Factor}: the answer is not a PNG (Lanczos-3)";
                _comfyStopped.Add(job.Key);
            }
            else
            {
                var px = PbrBuilder.From(img);
                // the size the graph expects (the model's x4 scaled by ComfyUI may be off by a pixel)
                if (TextureActions.Source(job.Id) is { } src)
                {
                    int w = src.GetWidth() * job.Factor, h = src.GetHeight() * job.Factor;
                    if (px.Width != w || px.Height != h)
                        px = Lanczos.Resize(px, w, h, ResizeOptions.Colour);
                }
                DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(ComfyCacheDir));
                PbrBuilder.ToImage(px).SavePng(ProjectSettings.GlobalizePath($"{ComfyCacheDir}/{job.Key}.png"));
                lock (ComfyMemory)
                    ComfyMemory[job.Key] = px;
                ComfyState = $"ComfyUI x{job.Factor} ({job.Model}): done, cached";
                if (job.Id == Current)
                    Remake();
            }
        }
        Say(ComfyState);
        RefreshComfy();
    }

    /// <summary>Stops the ComfyUI run going (the Lanczos upscale stays the source; Run again asks once more). An error or null.</summary>
    public string? CancelComfy(bool quiet = false)
    {
        if (_comfyJob == null || _comfyCts == null)
            return "no ComfyUI run going";
        _comfyCts.Cancel();
        if (!quiet)
            Say("ComfyUI: cancelling...");
        return null;
    }

    /// <summary>Asks ComfyUI again for the current texture (after a cancel or a failure). An error or null.</summary>
    public string? RunComfyAgain()
    {
        if (Current == null || _original == null)
            return "no texture";
        if (!WantsComfy(Recipe))
            return "the upscaler is not ComfyUI (or the upscale is 1)";
        var key = ComfyKey(_original, System.Math.Clamp(Recipe.Upscale, 1, 4), ComfyUpscaler.Model);
        _comfyStopped.Remove(key);
        Remake();
        return null;
    }

    /// <summary>Whether the current texture's source is ComfyUI's upscale now (cached and in the graph).</summary>
    public bool ComfySourceInUse => Current != null && _imports.Keys.Any(k => k.StartsWith($"upscaled:{Current}|", System.StringComparison.Ordinal));

    /// <summary>The Source step's upscaler row: Lanczos-3 or ComfyUI, the run's progress, Cancel, Run again.</summary>
    private void BuildComfyRow()
    {
        var row = new HBoxContainer();
        _settingsBox.AddChild(row);
        row.AddChild(new Label { Text = (Pinned("Upscaler") ? "● " : "") + "Upscaler", CustomMinimumSize = new Vector2(130, 0), TooltipText = "Lanczos-3 (built in) or a ComfyUI server of your own (the editor setting " + ComfyUpscaler.UrlSetting + ")" });
        var ob = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ob.AddItem("Lanczos-3 (built in)");
        ob.AddItem($"ComfyUI ({ComfyUpscaler.Model})");
        if (!ComfyUpscaler.Configured)
            ob.SetItemTooltip(1, $"No server: set the editor setting {ComfyUpscaler.UrlSetting} (e.g. http://127.0.0.1:8188) and {ComfyUpscaler.ModelSetting}");
        ob.Selected = Recipe.Upscaler == ComfyUi ? 1 : 0;
        ob.ItemSelected += i => SetSetting("Upscaler", i == 1 ? ComfyUi : Lanczos3);
        row.AddChild(ob);
        AddPin(row, ["Upscaler"], "Upscaler");
        var state = new HBoxContainer();
        _settingsBox.AddChild(state);
        _comfyBar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = true, CustomMinimumSize = new Vector2(90, 16), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        state.AddChild(_comfyBar);
        var cancel = new Button { Text = "Cancel", TooltipText = "Stop the ComfyUI run (the Lanczos upscale stays)" };
        cancel.Pressed += () => Say(CancelComfy() ?? "");
        state.AddChild(cancel);
        var again = new Button { Text = "Run again", TooltipText = "Ask ComfyUI again (after a cancel or a failure)" };
        again.Pressed += () => Say(RunComfyAgain() ?? "");
        state.AddChild(again);
        _comfyLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.75f) };
        _settingsBox.AddChild(_comfyLabel);
        RefreshComfy();
    }

    private void RefreshComfy()
    {
        if (_comfyLabel == null || !IsInstanceValid(_comfyLabel))
            return;
        bool wants = WantsComfy(Recipe);
        _comfyLabel.Text = wants ? ComfyState : Recipe.Upscaler == ComfyUi ? "ComfyUI: no upscale (1)" : "";
        _comfyBar!.Visible = wants && _comfyJob != null;
        _comfyBar.Value = System.Math.Max(0, ComfyDone);
        foreach (var b in _comfyBar.GetParent().GetChildren().OfType<Button>())
            b.Visible = wants && (b.Text == "Cancel" ? _comfyJob != null : _comfyJob == null && !ComfySourceInUse);
    }

    /// <summary>The note of how <paramref name="id"/>'s material source was upscaled by <paramref name="r"/> (ComfyUI when its answer was the source), or null (Lanczos: nothing to record).</summary>
    private static string? UpscaleNote(PbrRecipe r, bool comfyUsed)
    {
        if (!WantsComfy(r))
            return null;
        int f = System.Math.Clamp(r.Upscale, 1, 4);
        return comfyUsed ? ComfyUpscaler.Note(f, ComfyUpscaler.Model, ComfyUpscaler.Licence)
            : $"upscaled x{f} by Lanczos-3 (ComfyUI was asked for, no answer was cached)";
    }

    /// <summary><paramref name="entry"/>'s Source with the material's upscale note (its line replaced).</summary>
    public static void SetMaterialSourceNote(RemasterTexture entry, string? note)
    {
        const string head = "material source: ";
        var lines = entry.Source.Split('\n').Where(l => !l.StartsWith(head, System.StringComparison.Ordinal) && l.Length > 0).ToList();
        if (note != null)
            lines.Add(head + note);
        entry.Source = string.Join("\n", lines);
    }

    /// <summary>The upscaled source of <paramref name="id"/> for <paramref name="r"/> when cached (a run's import), or null.</summary>
    public static (string Import, RgbaImage Image)? UpscaledFor(string id, RgbaImage px, PbrRecipe r)
    {
        if (!WantsComfy(r))
            return null;
        int f = System.Math.Clamp(r.Upscale, 1, 4);
        var key = ComfyKey(px, f, ComfyUpscaler.Model);
        return ComfyCached(key) is { } img ? (UpscaledImport(id, f, key), img) : null;
    }
}
#endif
