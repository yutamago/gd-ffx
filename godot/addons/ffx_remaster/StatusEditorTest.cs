#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// R8's dashboard check (step 9 of tools/remaster-editor-check.sh; docs/remaster-editor.md,
/// "Checks"): run in the editor after the other steps with their folders
/// (<c>--remaster-maps=DIR/maps --remaster-textures=... --remaster-effect-scenes=... --remaster-rooms=...</c>),
/// the renders batch's folder (<c>--renders-dir=DIR/renders</c>) and a budgets fixture
/// (<c>--remaster-budgets=DIR</c>). The dashboard's rows and counts against the files the check's
/// steps wrote, read here on their own (Besaid beach: its status, its override entries counted in
/// the file, its additions and lighting, its textures with materials and the replaced one by the
/// texture index's text and the where-used index, its remade effect; <c>ptkl14</c>'s rebuilt room;
/// the project's textures with materials; the renders' records), a thumbnail drawn in the list and
/// the selected map's before/after pictures shown, the filters and a sort, the budgets' verdict, and
/// the HTML report written (<c>--status-report=DIR</c>). Exits the editor: 0 on PASS, 1 on FAIL.
/// </summary>
public sealed class StatusEditorTest
{
    private readonly RemasterPlugin _plugin;
    private readonly double _timeout = GameSettings.Number("editor-test-timeout", 300);
    private readonly List<string> _seen = new();
    private double _t;
    private bool _finished, _inStep;

    public StatusEditorTest(RemasterPlugin plugin)
    {
        _plugin = plugin;
        GD.Print($"ffx editortest: the status dashboard (maps {MapStore.Dir}, textures {TextureStore.Dir}, renders {RenderStore.Dir}, budgets {RemasterStatus.BudgetsDir})");
        if (MapStore.Dir == RemasterOverrides.BaseDir)
            Finish(false, "--remaster-maps=DIR is required (the check reads the editor check's files)");
    }

    public void Step(double delta)
    {
        if (_finished || _inStep)
            return;
        _inStep = true;
        try
        {
            _t += delta;
            if (_t > _timeout)
                Finish(false, "timed out waiting for the archive and the where-used index");
            else if (_plugin.Session.IsOpen && _plugin.Uses.Ready && _plugin.StatusDock?.Summary != null && _t > 1)
            {
                var fail = Run();
                Finish(fail == null, fail ?? string.Join("; ", _seen));
            }
        }
        catch (System.Exception e)
        {
            Finish(false, e.ToString());
        }
        finally
        {
            _inStep = false;
        }
    }

    private string? Run()
    {
        var dock = _plugin.StatusDock!;
        dock.Fill();
        var sum = dock.Summary!;
        var rows = dock.Listed.ToList();
        var cat = _plugin.Session.Catalogue!;
        if (rows.Count != cat.Levels.Count)
            return $"{rows.Count} rows for {cat.Levels.Count} maps";
        _seen.Add($"{rows.Count} maps listed; {sum}");
        // Besaid beach, as the editor check left it
        var b = rows.First(r => r.Entry.Key == "bsil00");
        var ovText = FileAccess.GetFileAsString(MapStore.PathOf("bsil00"));
        int entries = Regex.Matches(ovText, @"^Id = """, RegexOptions.Multiline).Count;
        if (b.OverrideParts != entries || entries < 4)
            return $"bsil00: {b.OverrideParts} override entries shown, {entries} in {MapStore.PathOf("bsil00")}";
        if (b.Status != MapStatus.InProgress || !b.Additions || !b.Lighting)
            return $"bsil00: status {b.Status}, additions {b.Additions}, lighting {b.Lighting} (the check set in progress, added a box, saved its lighting)";
        // the textures: the index's text against the where-used index, counted here
        var (withMaterial, replaced) = IndexCounts();
        var uses = _plugin.Uses.ByLevel.GetValueOrDefault("bsil00") ?? new();
        int wantMat = uses.Count(id => withMaterial.Contains(id)), wantRep = uses.Count(id => replaced.Contains(id));
        if (b.Materials != wantMat || b.Replaced != wantRep || wantRep < 1 || wantMat < 2 || b.Textures != uses.Count)
            return $"bsil00: textures {b.Textures} ({uses.Count}), materials {b.Materials} ({wantMat}), replaced {b.Replaced} ({wantRep})";
        if (sum.TexturesWithMaterials != withMaterial.Count || sum.TexturesReplaced != replaced.Count)
            return $"the summary's textures with materials {sum.TexturesWithMaterials} (the index: {withMaterial.Count}), replaced {sum.TexturesReplaced} ({replaced.Count})";
        int effects = DirAccess.DirExistsAbsolute(GameSettings.Globalize(MediaStore.EffectScenesDir))
            ? DirAccess.GetFilesAt(MediaStore.EffectScenesDir).Count(f => f.EndsWith(".tscn")) : 0;
        if (sum.RemadeEffects != effects || b.Effects != (FileAccess.FileExists(MediaStore.EffectScenePath("bsil00-fp15")) ? 1 : 0))
            return $"effects: {sum.RemadeEffects} remade ({effects} scenes), bsil00 {b.Effects}";
        _seen.Add($"bsil00: {b.ChangesText} ({b.Textures} textures)");
        var p = rows.First(r => r.Entry.Key == "ptkl14");
        if (!p.Rebuilt || sum.RebuiltRooms < 1)
            return $"ptkl14: rebuilt {p.Rebuilt} ({RemasterPlugin.RoomScenePath("ptkl14")}), rebuilt rooms {sum.RebuiltRooms}";
        int changed = rows.Count(r => r.Changed);
        if (sum.Changed != changed || changed < 2)
            return $"the summary's changed maps {sum.Changed}, rows changed {changed}";
        // the renders, against renders.json read here
        var state = RenderStore.Load();
        int done = state.Maps.Values.Count(m => m.State == "done");
        if (sum.Rendered != done || done < 1)
            return $"rendered {sum.Rendered}, renders.json {done}";
        if (b.Render is not { State: "done" } br || br.Before.Count == 0 || br.After.Count == 0)
            return $"bsil00 has no before/after renders ({b.Render?.State})";
        if (dock.ThumbsInList < done)
            return $"{dock.ThumbsInList} thumbnails in the list for {done} rendered maps";
        dock.Select("bsil00");
        if (dock.BeforeShown == null || dock.AfterShown == null || dock.BeforeShown.GetWidth() < 100)
            return "the selected map's before/after pictures are not shown";
        _seen.Add($"renders: {done} map(s), {dock.ThumbsInList} thumbnail(s) in the list, bsil00's pictures shown {dock.BeforeShown.GetWidth()}x{dock.BeforeShown.GetHeight()}; {b.RenderText}");
        if (p.Render is { State: "done", Kind: "2d-rebuilt" } pr && pr.Diff > 2.5)
            return $"ptkl14's rebuilt room against its classic layers {pr.Diff}/255";
        // the filters and a sort
        dock.SetFilter(changed: true);
        if (dock.Listed.Count != changed || dock.Listed.All(r => r.Entry.Key != "bsil00"))
            return $"Changed: {dock.Listed.Count} listed, {changed} changed";
        dock.SetFilter(changed: false, rendered: true);
        if (dock.Listed.Count != done)
            return $"Rendered: {dock.Listed.Count} listed, {done} rendered";
        dock.SetFilter(rendered: false, search: "bsil00");
        if (dock.Listed.Count != 1)
            return $"search bsil00: {dock.Listed.Count} listed";
        dock.SetFilter(search: "", status: 1 + (int)MapStatus.InProgress);
        if (dock.Listed.Count != sum.ByStatus[MapStatus.InProgress])
            return $"status InProgress: {dock.Listed.Count} listed, the summary {sum.ByStatus[MapStatus.InProgress]}";
        dock.SetFilter(status: 0, sort: System.Array.IndexOf(StatusDock.Sorts, "render time"));
        if (dock.Listed.FirstOrDefault()?.Render?.Ms != state.Maps.Values.Max(m => m.Ms))
            return "sorted by render time: the slowest map is not first";
        dock.SetFilter(sort: 0);
        _seen.Add("filters (changed, rendered, search, status) and the render-time sort as counted");
        // the budgets fixture
        var budgets = RemasterStatus.Budgets();
        if (budgets.TryGetValue("bsil00", out var bb))
        {
            var want = bb.Any(x => !x.Software && !x.Ok) ? "over" : bb.All(x => x.Software) ? "llvmpipe" : "ok";
            if (b.BudgetVerdict != want || b.Budgets.Count != bb.Count)
                return $"bsil00's budgets: {b.BudgetVerdict} ({b.Budgets.Count}), the tables {want} ({bb.Count})";
            _seen.Add($"bsil00's budgets: {b.BudgetVerdict} ({b.Budgets.Count} record(s))");
        }
        // the report
        if (GameSettings.Arg("status-report") is { Length: > 0 } dir)
        {
            var (index, err) = StatusReport.Write(_plugin, dir);
            if (err != null)
                return err;
            var html = System.IO.File.ReadAllText(index);
            if (!html.Contains("bsil00") || !html.Contains("pictures/bsil00/"))
                return $"the report {index} does not show bsil00's pictures";
            _seen.Add($"the report: {index}");
        }
        return null;
    }

    /// <summary>The project's index read as text: the IDs with a material and the replaced ones.</summary>
    private static (HashSet<string> Material, HashSet<string> Replaced) IndexCounts()
    {
        var mat = new HashSet<string>();
        var rep = new HashSet<string>();
        if (!FileAccess.FileExists(TextureStore.IndexPath))
            return (mat, rep);
        foreach (var block in FileAccess.GetFileAsString(TextureStore.IndexPath).Split("[sub_resource"))
        {
            var id = Regex.Match(block, @"^Id = ""([^""]+)""", RegexOptions.Multiline);
            if (!id.Success)
                continue;
            if (Regex.IsMatch(block, @"^Material = ""[^""]+""", RegexOptions.Multiline))
                mat.Add(id.Groups[1].Value);
            if (Regex.IsMatch(block, @"^Replacement = ""[^""]+""", RegexOptions.Multiline))
                rep.Add(id.Groups[1].Value);
        }
        return (mat, rep);
    }

    private void Finish(bool pass, string note)
    {
        if (_finished)
            return;
        _finished = true;
        GD.Print($"ffx editortest: {(pass ? "PASS" : "FAIL")} {note}{(pass ? "" : $" [{string.Join("; ", _seen)}]")}");
        var tree = _plugin.GetTree();
        tree.CreateTimer(0.5).Timeout += () => tree.Quit(pass ? 0 : 1);
    }
}
#endif
