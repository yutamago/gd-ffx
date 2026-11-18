#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The remaster wizard's editor check (docs/remaster-editor.md, "Checks"; R4): run in the editor
/// with <c>-- --remaster-editor-wizard --remaster-wizard-dir=DIR --remaster-maps=DIR/maps
/// --remaster-textures=res://remaster/check/wizard-textures</c> (nothing of the project's layers is
/// written). Through the code the Wizard dock uses:
/// <list type="number">
/// <item>Besaid beach from the map browser's selection: its textures listed with a profile, a score
/// and reasons each, the materials made, the lighting staged (VoxelGI), under a renderer
/// the before/after pictures and the colour check; the project untouched (no entry, no render path,
/// no lighting file) before the accept;</item>
/// <item>the review: a texture rejected (one undo step, undone, redone), another made again with a
/// different profile (one undo step, undone, redone), the map accepted (one undo step: the entries,
/// the render path, the status, the notes, <c>lighting.tres</c>; undone: the project as before;
/// redone);</item>
/// <item>a batch of three maps with a 2D-layer one in the middle, paused after the first (the state
/// saved paused), resumed: two passes staged, the 2D map skipped, the summary with the time per map.</item>
/// </list>
/// Writes <c>DIR/wizard-expect.json</c> for the game's <c>--selftest-wizard --wizard-expect=...</c>.
/// Exits the editor: 0 on PASS, 1 on FAIL.
/// </summary>
public sealed class WizardEditorTest
{
    public const string Key = "bsil00";

    /// <summary>The batch: two small 3D maps around a 2D-layer one.</summary>
    public static readonly string[] Batch = ["slik08", "ptkl14", "mtgz03"];

    private enum Phase { Archive, Making, Review, Remaking, Accept, BatchStart, BatchFirst, Pausing, Resumed, Done }

    private readonly RemasterPlugin _plugin;
    private readonly double _timeout = GameSettings.Number("editor-test-timeout", 900);
    private readonly List<string> _seen = new();
    private Phase _phase = Phase.Archive;
    private double _t, _phaseT;
    private bool _finished, _inStep;
    private string _rejected = "", _remade = "", _remadeFrom = "", _remadeTo = "";
    private ulong _undoBefore;

    public WizardEditorTest(RemasterPlugin plugin)
    {
        _plugin = plugin;
        GD.Print($"ffx editortest: wizard (state into {WizardState.Dir}, overrides into {MapStore.Dir}, textures into {TextureStore.Dir})");
        if (MapStore.Dir == RemasterOverrides.BaseDir || TextureStore.Dir == RemasterTextures.BaseDir || WizardState.Dir == "res://remaster/wizard")
            Finish(false, "--remaster-maps, --remaster-textures and --remaster-wizard-dir are required (the check must not write the project's)");
        // Besaid beach's look (the key light, ambient, fog, background) as the main check writes it, unless the game wrote one
        if (GameSettings.Arg("remaster-looks") is { Length: > 0 } && LookCache.Read(EditorSelfTest.Level) == null)
            EditorSelfTest.WriteStandInLook();
    }

    private UndoRedo Global => _plugin.GetUndoRedo().GetHistoryUndoRedo((int)EditorUndoRedoManager.SpecialHistory.GlobalHistory);

    private Wizard W => _plugin.Wizard!;

    public void Step(double delta)
    {
        if (_finished || _inStep)
            return;
        _inStep = true;
        try
        {
            _t += delta;
            _phaseT += delta;
            if (_t > _timeout)
                Finish(false, $"timed out in {_phase} ({W.Status})");
            else if (Run() is { } fail)
                Finish(false, $"{_phase}: {fail}");
        }
        catch (System.Exception e)
        {
            Finish(false, $"{_phase}: {e}");
        }
        finally
        {
            _inStep = false;
        }
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx editortest: wizard {_phase} -> {p} ({_t:F1} s)");
        _phase = p;
        _phaseT = 0;
    }

    private string? Run()
    {
        switch (_phase)
        {
            case Phase.Archive when _plugin.Session.IsOpen && _plugin.Session.Catalogue != null && _phaseT > 0.5:
            {
                var entry = _plugin.Session.Catalogue.Find(Key)!;
                _plugin.Dock!.Select(entry);
                // from the map browser: the selected map (an area, the changed maps and all maps are the same call)
                if (W.Scope("area", Key, entry.Area).Count < 5 || !W.Scope("all", null, null).Contains(Key))
                    return $"the scopes: area {W.Scope("area", Key, entry.Area).Count}, all {W.Scope("all", null, null).Count}";
                if (_plugin.WizardDock!.RunScope("map") is { } err)
                    return err;
                _undoBefore = Global.GetVersion();
                Next(Phase.Making);
                break;
            }
            case Phase.Making when !W.Busy && W.State.Maps.GetValueOrDefault(Key) is { State: "staged" or "failed" } m:
            {
                if (m.State == "failed")
                    return $"the pass failed: {m.Reason}";
                if (Staged(m) is { } e)
                    return e;
                Next(Phase.Review);
                break;
            }
            case Phase.Review:
            {
                if (Review() is { } e)
                    return e;
                Next(Phase.Remaking);
                break;
            }
            case Phase.Remaking when !W.Busy && _phaseT > 0.2:
            {
                if (Remade() is { } e)
                    return e;
                Next(Phase.Accept);
                break;
            }
            case Phase.Accept when !W.Busy:
            {
                if (Accept() is { } e)
                    return e;
                Next(Phase.BatchStart);
                break;
            }
            case Phase.BatchStart:
            {
                if (W.Start(Batch) is { } err)
                    return err;
                Next(Phase.BatchFirst);
                break;
            }
            case Phase.BatchFirst when W.Making == Batch[0]:
                // paused while the first map is made: it finishes, the batch stops after it
                W.Pause();
                Next(Phase.Pausing);
                break;
            case Phase.Pausing when !W.Busy && _phaseT > 0.5:
            {
                var b = W.State.Batch!;
                if (!b.Paused || b.Next != 1)
                    return $"paused at {b.Next} (paused {b.Paused}), not after the first map";
                var saved = WizardState.Load();
                if (saved.Batch is not { Paused: true, Next: 1 } || saved.Maps.GetValueOrDefault(Batch[0])?.State != "staged")
                    return "the state file does not say the batch paused after the first map";
                _seen.Add($"batch of {Batch.Length} paused after {Batch[0]} (saved: paused at 1 of {b.Maps.Count})");
                if (W.Resume() is { } err)
                    return err;
                Next(Phase.Resumed);
                break;
            }
            case Phase.Resumed when !W.Busy && W.State.Batch is { Finished: true } && _phaseT > 0.5:
            {
                var maps = W.State.Maps;
                if (maps.GetValueOrDefault(Batch[1])?.State != "skipped")
                    return $"the 2D-layer map {Batch[1]}: {maps.GetValueOrDefault(Batch[1])?.State}";
                foreach (var k in new[] { Batch[0], Batch[2] })
                    if (maps.GetValueOrDefault(k) is not { State: "staged", Ms: > 0 } mm)
                        return $"{k}: {maps.GetValueOrDefault(k)?.State} ({maps.GetValueOrDefault(k)?.Reason})";
                var summary = W.State.Summary();
                if (!summary.Contains("1 skipped") || !summary.Contains(" s a map"))
                    return $"the summary: {summary}";
                _seen.Add($"resumed and finished: {summary}");
                Next(Phase.Done);
                break;
            }
            case Phase.Done:
                Finish(true, string.Join("; ", _seen));
                break;
        }
        return null;
    }

    /// <summary>The staged pass: profiles with scores and reasons, materials made, the lighting, pictures; the project untouched.</summary>
    private string? Staged(WizardMapState m)
    {
        if (Global.GetVersion() != _undoBefore)
            return "making the pass changed the undo history (it should stage only)";
        if (m.Textures.Count < 20)
            return $"only {m.Textures.Count} textures";
        var made = m.Textures.Where(t => t.Applies).ToList();
        var kept = m.Textures.Where(t => t.Profile.Length == 0).ToList();
        if (made.Count < 10)
            return $"only {made.Count} materials made";
        if (m.Textures.Any(t => t.Why.Length == 0 || t.Kind.Length == 0))
            return "a texture without a kind or reasons";
        if (made.Select(t => t.Profile).Distinct().Count() < 3)
            return $"one or two profiles for every texture ({string.Join(", ", made.Select(t => t.Profile).Distinct())})";
        foreach (var t in made)
        {
            var e = TextureStore.FromDictionary(t.Id, GD.StrToVar(t.Entry).AsGodotDictionary());
            if (e.Material.Length == 0 || !FileAccess.FileExists(TextureStore.Absolute(e.Material)) || e.Profile != t.Profile || e.ProfileHash.Length == 0)
                return $"{t.Id}: the staged entry ({e.Material}, {e.Profile})";
            // nothing applied: the project's index does not have it
            if (TextureStore.Find(t.Id) is { Material.Length: > 0 })
                return $"{t.Id} has a material in the project before the accept";
            if (!FileAccess.FileExists(GameSettings.Globalize(t.Albedo)) || !FileAccess.FileExists(GameSettings.Globalize(t.Normal)))
                return $"{t.Id}: no albedo or normal thumbnail";
        }
        if (MapStore.Find(Key) is { RenderPath: RenderPath.Remastered } or { Status: not MapStatus.Original })
            return "the map's render path or status changed before the accept";
        if (FileAccess.FileExists(LightingEdit.PathOf(Key)))
            return "a lighting file before the accept";
        var light = m.LightingResource();
        if (light.GiMode == GiMode.None || !m.Lighting.ContainsKey("GiMode"))
            return $"no GI staged ({string.Join(", ", m.Lighting.Keys)})";
        if (!m.Note.StartsWith("wizard pass", System.StringComparison.Ordinal))
            return $"the note: {m.Note}";
        bool pictures = WizardRender.Available;
        if (pictures && (m.Before.Count == 0 || m.Before.Count != m.After.Count || !m.Colour.Contains(" dL ")))
            return $"pictures {m.Before.Count}/{m.After.Count}, colour {m.Colour}";
        var sample = made.OrderByDescending(t => t.Area).First();
        _seen.Add($"{Key} staged in {m.Ms / 1000:0.0} s (materials {m.BakeMs / 1000:0.0} s, {(_plugin.Materials?.UsingGpu == true ? "GPU" : "CPU")}): {m.Textures.Count} textures, "
            + $"{made.Count} made ({string.Join(", ", made.GroupBy(t => t.Profile).Select(g => $"{g.Key} {g.Count()}"))}), {kept.Count} kept classic; "
            + $"e.g. {RemasterTextures.ShortName(sample.Id)}: {sample.Why}; GI {light.GiMode}, exposure {light.ToneExposure:0.##}; "
            + (pictures ? $"{m.Before.Count} view(s), colour {(m.ColourOk ? "ok" : "WARNING")}: {m.Colour}" : "no renderer: no pictures")
            + "; the project untouched");
        return null;
    }

    private string? Review()
    {
        var m = W.State.Maps[Key];
        var made = m.Textures.Where(t => t.Applies).OrderByDescending(t => t.Area).ToList();
        // reject the second biggest: classic
        _rejected = made[1].Id;
        ulong v = Global.GetVersion();
        if (_plugin.WizardDecide(Key, _rejected, "rejected") is { } err)
            return err;
        if (Global.GetVersion() != v + 1 || W.State.Maps[Key].Textures.First(t => t.Id == _rejected).Applies)
            return "rejecting a texture is not one undo step";
        Global.Undo();
        if (!W.State.Maps[Key].Textures.First(t => t.Id == _rejected).Applies)
            return "undo did not bring the texture back";
        Global.Redo();
        if (W.State.Maps[Key].Textures.First(t => t.Id == _rejected).Applies || WizardState.Load().Maps[Key].Textures.First(t => t.Id == _rejected).Decision != "rejected")
            return "redo did not reject it again (or the state file did not follow)";
        _seen.Add($"{RemasterTextures.ShortName(_rejected)} rejected (classic), undone, redone");
        // another profile for the third: made again
        var t3 = made[2];
        _remade = t3.Id;
        _remadeFrom = t3.Profile;
        _remadeTo = t3.Profile == "rock" ? "stone-and-concrete" : "rock";
        _undoBefore = Global.GetVersion();
        return W.Remake(Key, _remade, _remadeTo);
    }

    private string? Remade()
    {
        var t = W.State.Maps[Key].Textures.First(x => x.Id == _remade);
        if (Global.GetVersion() != _undoBefore + 1 || t.Profile != _remadeTo || !t.Applies)
            return $"{RemasterTextures.ShortName(_remade)} made with {_remadeTo}: profile {t.Profile}, decision {t.Decision} (undo steps {(long)(Global.GetVersion() - _undoBefore)})";
        if (TextureStore.FromDictionary(t.Id, GD.StrToVar(t.Entry).AsGodotDictionary()).Profile != _remadeTo)
            return "the entry does not name the new profile";
        Global.Undo();
        if (W.State.Maps[Key].Textures.First(x => x.Id == _remade).Profile != _remadeFrom)
            return "undo did not bring the first profile back";
        Global.Redo();
        if (W.State.Maps[Key].Textures.First(x => x.Id == _remade).Profile != _remadeTo)
            return "redo did not make it the new profile again";
        _seen.Add($"{RemasterTextures.ShortName(_remade)} made again with {_remadeTo} (was {_remadeFrom}), undone, redone");
        if (WizardRender.Available && W.Repicture(Key) is { } err)
            return err;
        return null;
    }

    private string? Accept()
    {
        var m = W.State.Maps[Key];
        var applies = m.Textures.Where(t => t.Applies).Select(t => t.Id).ToList();
        ulong v = Global.GetVersion();
        if (_plugin.WizardAccept(Key) is { } err)
            return err;
        if (Global.GetVersion() != v + 1)
            return "the accept is not one undo step";
        if (Applied(applies, true) is { } a)
            return $"accepted: {a}";
        Global.Undo();
        if (Applied(applies, false) is { } u)
            return $"undone: {u}";
        Global.Redo();
        if (Applied(applies, true) is { } r)
            return $"redone: {r}";
        var light = W.State.Maps[Key].LightingResource();
        // the game's check: what the editor applied
        var expect = new JsonObject
        {
            ["level"] = Key,
            ["materials"] = new JsonArray(applies.Select(id => (JsonNode)id).ToArray()),
            ["rejected"] = _rejected,
            ["remade"] = _remade,
            ["remadeProfile"] = _remadeTo,
            ["gi"] = light.GiMode.ToString(),
            ["exposure"] = light.ToneExposure,
            ["textures"] = TextureStore.IndexPath,
            ["overrides"] = MapStore.PathOf(Key),
            ["lighting"] = LightingEdit.PathOf(Key),
        };
        System.IO.File.WriteAllText(GameSettings.Globalize($"{WizardState.Dir}/wizard-expect.json"), expect.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        _seen.Add($"accepted in one undo step ({applies.Count} materials, render path remastered, status in progress, notes, lighting.tres with GI {light.GiMode}), undone (the project as before), redone");
        return null;
    }

    /// <summary>Whether the project shows the accepted pass (<paramref name="on"/>) or none of it; an error or null.</summary>
    private string? Applied(List<string> applies, bool on)
    {
        foreach (var id in applies)
            if ((TextureStore.Find(id) is { Material.Length: > 0 }) != on)
                return $"{RemasterTextures.ShortName(id)}'s material {(on ? "missing" : "still there")}";
        if (on && TextureStore.Find(_rejected) is { Material.Length: > 0 })
            return "the rejected texture got a material";
        if (on && TextureStore.Find(_remade)?.Profile != _remadeTo)
            return "the remade texture's entry does not name its new profile";
        var ov = MapStore.Find(Key);
        if (on != (ov is { RenderPath: RenderPath.Remastered, Status: MapStatus.InProgress } && ov.Notes.Contains("wizard pass")))
            return $"the map's state: {ov?.Status}, {ov?.RenderPath}, notes \"{ov?.Notes}\"";
        var path = LightingEdit.PathOf(Key);
        bool file = FileAccess.FileExists(path);
        if (file != on)
            return $"lighting.tres {(file ? "there" : "missing")}";
        if (on && (ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) as MapLighting)?.GiMode == GiMode.None)
            return "lighting.tres without GI";
        if (W.State.Maps[Key].State != (on ? "accepted" : "staged"))
            return $"the pass is {W.State.Maps[Key].State}";
        return null;
    }

    private void Finish(bool pass, string note)
    {
        if (_finished)
            return;
        _finished = true;
        GD.Print($"ffx editortest: {(pass ? "PASS" : "FAIL")} {note}{(pass ? "" : $" [{string.Join("; ", _seen)}]")}");
        var tree = (Engine.GetMainLoop() as SceneTree)!;
        tree.CreateTimer(0.5).Timeout += () => tree.Quit(pass ? 0 : 1);
    }
}
#endif
