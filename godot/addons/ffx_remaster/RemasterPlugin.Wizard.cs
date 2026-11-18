#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The remaster wizard's actions (docs/remaster-editor.md, "The remaster wizard"): a texture's
/// decision in the review, a map's pass accepted (its staged materials, render path, status, notes
/// and lighting written into the project) or discarded, each one step of the editor's global undo
/// history; and the headless batch (<c>--remaster-wizard --maps=...</c>).
/// </summary>
public partial class RemasterPlugin
{
    private Wizard? _wizard;
    private WizardDock? _wizardDock;
    private WizardEditorTest? _wizardTest;
    private bool _wizardHeadless, _wizardStarted;
    private int _wizardMapsAtStart = -1;

    public Wizard? Wizard => _wizard;

    public WizardDock? WizardDock => _wizardDock;

    private void EnterWizard()
    {
        _wizard = new Wizard(this);
        _wizardDock = new WizardDock(this);
        AddDock(_wizardDock);
        _wizard.Changed += () => _wizardDock?.Refresh();
        _wizardHeadless = GameSettings.Flag("remaster-wizard");
        if (GameSettings.Flag("remaster-editor-wizard"))
            _wizardTest = new WizardEditorTest(this);
    }

    private void ExitWizard()
    {
        if (_wizardDock != null)
        {
            RemoveDock(_wizardDock);
            _wizardDock.QueueFree();
            _wizardDock = null;
        }
        _wizard = null;
    }

    private void StepWizard(double delta)
    {
        // the headless batch looks before the wizard steps on (a pause after N maps lands between maps)
        if (_wizardHeadless)
            StepHeadless();
        _wizard?.Tick();
        _wizardDock?.Tick();
        _wizardTest?.Step(delta);
    }

    private EditorUndoRedoManager WizardUndo => GetUndoRedo();

    // ---- the review's actions, each one undo step ------------------------------------------------

    /// <summary>Sets <paramref name="key"/>'s staged pass from <paramref name="before"/> to <paramref name="after"/> (their texts): one undo step.</summary>
    public void WizardSetMap(string key, string before, string after, string action)
    {
        if (before == after)
            return;
        WizardUndo.CreateAction(action, UndoRedo.MergeMode.Disable, this);
        WizardUndo.AddDoMethod(this, MethodName.ApplyWizardMap, key, after);
        WizardUndo.AddUndoMethod(this, MethodName.ApplyWizardMap, key, before);
        WizardUndo.CommitAction();
    }

    /// <summary>Writes <paramref name="key"/>'s staged pass (the undo history calls it; empty: none).</summary>
    public void ApplyWizardMap(string key, string text)
    {
        if (_wizard == null)
            return;
        if (WizardState.MapFrom(text) is { } m)
            _wizard.State.Maps[key] = m;
        else
            _wizard.State.Maps.Remove(key);
        _wizard.State.Save();
        _wizardDock?.Refresh();
    }

    /// <summary>
    /// <paramref name="key"/>'s texture <paramref name="id"/>: "accepted" (its material as made),
    /// "rejected" (classic: no material), "auto" (as chosen): one undo step. Another profile is
    /// <see cref="Wizard.Remake"/>. An error or null.
    /// </summary>
    public string? WizardDecide(string key, string id, string decision)
    {
        if (_wizard?.State.Maps.GetValueOrDefault(key) is not { State: "staged" } m)
            return $"{key} has no staged pass";
        var before = WizardState.MapText(m);
        var copy = WizardState.MapFrom(before)!;
        if (copy.Textures.FirstOrDefault(t => t.Id == id) is not { } t)
            return $"{id} is not one of {key}'s textures";
        if (decision == "accepted" && t.Entry.Length == 0)
            return $"{RemasterTextures.ShortName(id)} has no material to accept (make it with a profile)";
        t.Decision = decision;
        copy.Note = Remaster.Wizard.WizardPass.Note(copy.Textures.Select(x => (x.Profile, x.Applies)), copy.LightingResource(), copy.When[..Mathf.Min(10, copy.When.Length)]);
        WizardSetMap(key, before, WizardState.MapText(copy), $"Wizard: {RemasterTextures.ShortName(id)} {decision}");
        return null;
    }

    /// <summary>Drops <paramref name="key"/>'s staged pass (one undo step; the project unchanged). An error or null.</summary>
    public string? WizardDiscard(string key)
    {
        if (_wizard?.State.Maps.GetValueOrDefault(key) is not { State: "staged" } m)
            return $"{key} has no staged pass";
        var before = WizardState.MapText(m);
        var copy = WizardState.MapFrom(before)!;
        copy.State = "discarded";
        WizardSetMap(key, before, WizardState.MapText(copy), $"Wizard: discard the pass on {key}");
        return null;
    }

    /// <summary>
    /// Applies <paramref name="key"/>'s staged pass to the project as one undo step: every texture
    /// it makes (not rejected) gets its material entry, the map's render path becomes remastered,
    /// its status in progress (a higher one set by hand stays), the pass's note is added to its
    /// notes, its <c>lighting.tres</c> is written (only the fields that differ from the defaults),
    /// and the pass is marked accepted. An error or null.
    /// </summary>
    public string? WizardAccept(string key)
    {
        if (_wizard?.State.Maps.GetValueOrDefault(key) is not { State: "staged" } m)
            return $"{key} has no staged pass";
        var before = WizardState.MapText(m);
        var accepted = WizardState.MapFrom(before)!;
        accepted.State = "accepted";
        // the lighting file's text: saved once where it goes (Godot's text, stable IDs), then put back as it was
        var path = LightingEdit.PathOf(key);
        var lightingBefore = FileText(path);
        var res = m.LightingResource();
        string lightingAfter = "";
        if (!LightingEdit.IsDefault(res))
        {
            DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(path.GetBaseDir()));
            res.GetScript().As<Script>().SetIdForPath(path, "1");
            res.TakeOverPath(path);
            if (ResourceSaver.Save(res, path) != Error.Ok)
                return $"cannot write {path}";
            lightingAfter = FileText(path);
            ApplyFileText(path, lightingBefore);
        }
        var ov = MapStore.Find(key);
        var (s0, p0, n0) = ov != null ? (ov.Status, ov.RenderPath, ov.Notes) : (MapStatus.Original, RenderPath.Classic, "");
        var status = s0 is MapStatus.Remastered or MapStatus.Approved ? s0 : MapStatus.InProgress;
        var notes = string.IsNullOrWhiteSpace(n0) ? m.Note : $"{n0.TrimEnd()}\n{m.Note}";
        int made = 0;
        WizardUndo.CreateAction($"Accept the wizard's pass on {key}", UndoRedo.MergeMode.Disable, this);
        foreach (var t in m.Textures)
        {
            if (!t.Applies)
                continue;
            var entry = GD.StrToVar(t.Entry).AsGodotDictionary();
            WizardUndo.AddDoMethod(this, MethodName.ApplyTexture, t.Id, entry);
            WizardUndo.AddUndoMethod(this, MethodName.ApplyTexture, t.Id, TextureStore.ToDictionary(TextureStore.Copy(t.Id)));
            made++;
        }
        WizardUndo.AddDoMethod(this, MethodName.ApplyMapState, key, (int)status, (int)RenderPath.Remastered, notes);
        WizardUndo.AddUndoMethod(this, MethodName.ApplyMapState, key, (int)s0, (int)p0, n0);
        WizardUndo.AddDoMethod(this, MethodName.ApplyFileText, path, lightingAfter);
        WizardUndo.AddUndoMethod(this, MethodName.ApplyFileText, path, lightingBefore);
        WizardUndo.AddDoMethod(this, MethodName.ApplyWizardMap, key, WizardState.MapText(accepted));
        WizardUndo.AddUndoMethod(this, MethodName.ApplyWizardMap, key, before);
        WizardUndo.CommitAction();
        GD.Print($"ffx remaster: wizard: {key} accepted: {made} material(s), render path remastered, status {status}, {(lightingAfter.Length > 0 ? path : "lighting as the defaults")}");
        return null;
    }

    private static string FileText(string path) => FileAccess.FileExists(path) ? FileAccess.GetFileAsString(path) : "";

    /// <summary>Writes a project file's text (the undo history calls it; empty: removes it); the lighting and the open map follow.</summary>
    public void ApplyFileText(string path, string text)
    {
        var abs = GameSettings.Globalize(path);
        if (text.Length == 0)
        {
            if (System.IO.File.Exists(abs))
                System.IO.File.Delete(abs);
        }
        else
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs)!);
            System.IO.File.WriteAllText(abs, text);
        }
        LightingLayers.Invalidate(path.GetBaseDir().GetFile());
        if (path.StartsWith("res://", System.StringComparison.Ordinal))
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);
        // the open map holds its lighting as it opened: opened again so a save does not write the old one back
        if (Current is { Built: true } root && root.Key == path.GetBaseDir().GetFile() && root.SceneFilePath.Length > 0)
            EditorInterface.Singleton.ReloadSceneFromPath(root.SceneFilePath);
    }

    // ---- headless: --remaster-wizard --maps=a,b,c [--wizard-resume] [--wizard-accept] [--wizard-pause-after=N] -------------

    private void StepHeadless()
    {
        if (_wizard == null || !Session.IsOpen)
            return;
        var b = _wizard.State.Batch;
        if (!_wizardStarted)
        {
            _wizardStarted = true;
            string? err;
            if (GameSettings.Flag("wizard-resume"))
                err = _wizard.Resume();
            else
            {
                var maps = (GameSettings.Arg("maps") ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
                err = _wizard.Start(maps, GameSettings.Flag("wizard-2d-classic") ? "classic" : "skip");
            }
            if (err != null)
            {
                EndHeadless(false, err);
                return;
            }
            _wizardMapsAtStart = _wizard.State.Batch?.Next ?? 0;
            return;
        }
        if (b == null || _wizard.Busy)
            return;
        // --wizard-pause-after=N: paused after N maps of this run (the editor closed mid-batch: --wizard-resume goes on)
        if (GameSettings.Number("wizard-pause-after", 0) is var n and > 0 && !b.Paused && !b.Finished && b.Next - _wizardMapsAtStart >= n)
        {
            _wizard.Pause();
            EndHeadless(true, $"paused after {n} map(s) at {b.Next} of {b.Maps.Count}: {_wizard.State.Summary()}");
            return;
        }
        if (!b.Finished)
            return;
        if (GameSettings.Flag("wizard-accept"))
            foreach (var key in b.Maps)
                if (_wizard.State.Maps.GetValueOrDefault(key) is { State: "staged" })
                    WizardAccept(key);
        EndHeadless(_wizard.State.Maps.Values.All(m => m.State != "failed"), _wizard.State.Summary());
    }

    private void EndHeadless(bool ok, string note)
    {
        _wizardHeadless = false;
        GD.Print($"ffx wizard: {(ok ? "DONE" : "FAIL")} {note}");
        var tree = GetTree();
        tree.CreateTimer(0.5).Timeout += () => tree.Quit(ok ? 0 : 1);
    }
}
#endif
