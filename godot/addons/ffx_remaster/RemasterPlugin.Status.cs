#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// R8's tools (docs/remaster-editor.md, "Before/after renders", "The status dashboard"): the
/// dashboard (<see cref="StatusDock"/>, a bottom panel), the renders batch (<see cref="RenderBatch"/>:
/// from the dashboard, or headless-capable under a renderer with
/// <c>--remaster-renders --maps=a,b|changed|all [--renders-again] [--renders-resume] [--renders-dir=DIR]</c>,
/// which quits when done), and the HTML report (<c>--remaster-report=DIR</c>: written, then quits;
/// tools/remaster-report.sh).
/// </summary>
public partial class RemasterPlugin
{
    private StatusDock? _status;
    private RenderBatch? _renders;
    private StatusEditorTest? _statusTest;
    private bool _rendersHeadless, _reportHeadless, _statusFilled;
    private double _rendersClock;

    public StatusDock? StatusDock => _status;

    public RenderBatch? Renders => _renders;

    private void EnterStatus()
    {
        _status = new StatusDock(this);
        AddDock(_status);
        _rendersHeadless = GameSettings.Flag("remaster-renders");
        _reportHeadless = GameSettings.Arg("remaster-report") is { Length: > 0 };
        if (GameSettings.Flag("remaster-editor-status"))
            _statusTest = new StatusEditorTest(this);
    }

    private void ExitStatus()
    {
        if (_status == null)
            return;
        RemoveDock(_status);
        _status.QueueFree();
        _status = null;
    }

    /// <summary>Starts a renders batch over <paramref name="keys"/> (<paramref name="again"/>: maps already drawn are drawn again). An error or null.</summary>
    public string? StartRenders(IEnumerable<string>? keys, bool again)
    {
        if (!Session.IsOpen)
            return "the archive is not open";
        if (_renders is { Finished: false })
            return "a renders batch is running";
        if (!RenderBatch.Available)
            return "no renderer (headless): the renders need one (Xvfb)";
        _renders = new RenderBatch(this, keys, again);
        _rendersClock = 0;
        return null;
    }

    private void StepStatus(double delta)
    {
        if (!_statusFilled && Session.IsOpen && Uses.Ready)
        {
            _statusFilled = true;
            _status?.Fill();
        }
        if (_renders != null)
        {
            _rendersClock += delta;
            bool was = _renders.Finished;
            _renders.Tick(delta);
            if (!was && _renders.Finished)
            {
                var r = _renders;
                GD.Print($"ffx remaster: renders: batch done: {r.Done} drawn, {r.Kept} kept, {r.Skipped} skipped, {r.Failed} failed in {_rendersClock:0.0} s");
                _status?.Fill();
                if (_rendersHeadless)
                    EndRenders(r.Failed == 0, $"{r.Done} drawn, {r.Kept} kept (already drawn), {r.Skipped} skipped, {r.Failed} failed in {_rendersClock:0.0} s into {RenderStore.Abs()}");
            }
        }
        else if (_rendersHeadless && Session.IsOpen && Uses.Ready)
        {
            List<string>? keys;
            if (GameSettings.Flag("renders-resume"))
                keys = null;
            else
            {
                var arg = (GameSettings.Arg("maps") ?? "").Trim();
                keys = arg switch
                {
                    "all" => Session.Catalogue!.Levels.Select(e => e.Key).ToList(),
                    "changed" => RemasterStatus.All(this).Where(r => r.Changed).Select(r => r.Entry.Key).ToList(),
                    _ => arg.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries).ToList(),
                };
                if (keys.Count == 0)
                {
                    EndRenders(false, "no maps (--maps=a,b,c|changed|all)");
                    return;
                }
            }
            if (StartRenders(keys, GameSettings.Flag("renders-again")) is { } err)
                EndRenders(false, err);
        }
        if (_reportHeadless && Session.IsOpen && Uses.Ready)
        {
            _reportHeadless = false;
            var (path, err) = StatusReport.Write(this, GameSettings.Arg("remaster-report")!);
            GD.Print(err == null ? $"ffx report: DONE {path}" : $"ffx report: FAIL {err}");
            var tree = GetTree();
            tree.CreateTimer(0.5).Timeout += () => tree.Quit(err == null ? 0 : 1);
        }
        _statusTest?.Step(delta);
    }

    private void EndRenders(bool ok, string note)
    {
        _rendersHeadless = false;
        GD.Print($"ffx renders: {(ok ? "DONE" : "FAIL")} {note}");
        var tree = GetTree();
        tree.CreateTimer(0.5).Timeout += () => tree.Quit(ok ? 0 : 1);
    }
}
#endif
