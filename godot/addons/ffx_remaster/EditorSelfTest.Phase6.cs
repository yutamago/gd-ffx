#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using GdFfx.Remaster.Materialize;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>R2b phase 6's part of the editor check (docs/remaster-materials.md, phase 6, "Checks").</summary>
public sealed partial class EditorSelfTest
{
    /// <summary>
    /// R2b phase 6 in the tab: regions proposed from the UV islands of a Besaid atlas texture (how
    /// many, and how they match the parts), a region by colour (picked in the 2D map), one drawn by
    /// hand (brush, rectangle, polygon, eraser), two profiles on one texture (metal and grass over
    /// stone), the composite valid (normals unit length, the borders and height levels measured),
    /// each action one undo step, save (the masks as files, the regions on the entry, read back),
    /// undo and redo, Apply to queue with the regioned texture, Make again after a region's profile
    /// changed; AI estimation through the fake server (the licence refusal, progress, cancel, run
    /// again, the cache, the step's settings on top, the Source note).
    /// </summary>
    private IEnumerable<string?> Phase6(MaterialsTab tab, UndoRedo global, List<string> ids, MapEditRoot root, bool shots, string shotDir, System.Action<string> fail)
    {
        string? f = null;
        IEnumerable<string?> Idle(string what, double limit = 180)
        {
            double t0 = _t;
            while (!tab.Idle)
            {
                if (_t - t0 > limit) { f = $"{what}: the maps were not made in {limit} s"; yield break; }
                yield return null;
            }
        }
        IEnumerable<string?> Run(string what)
        {
            double t0 = _t;
            while (tab.BakeRun != null)
            {
                if (_t - t0 > 400) { f = $"{what}: the run did not finish in 400 s"; yield break; }
                yield return null;
            }
        }
        var notes = new List<string>();
        _plugin.ShowMaterials();

        // ---- the atlas: the beach texture whose UV islands propose the most regions ----
        var cands = new List<(string Id, int Regions, int Parts, int Islands)>();
        foreach (var id in TextureActions.OnMap(root).Keys)
        {
            if (TextureActions.Source(id) is not { } img)
                continue;
            var surf = RegionSurfaces.Of(root.Map!, id);
            var isl = MaterialRegions.Islands(surf, img.GetWidth(), img.GetHeight(), true);
            var props = MaterialRegions.Propose(isl, img.GetWidth(), img.GetHeight());
            cands.Add((id, props.Count, surf.Select(x => x.Part).Distinct().Count(), isl.Count));
        }
        var best = cands.OrderByDescending(c => c.Regions).ThenByDescending(c => c.Parts).ThenBy(c => c.Id, System.StringComparer.Ordinal).FirstOrDefault();
        if (best.Id == null || best.Regions < 2)
        {
            fail($"no atlas on {Key}: the most regions proposed {best.Regions} ({cands.Count} textures)");
            yield break;
        }
        int atlases = cands.Count(c => c.Regions >= 2);
        var atlas = best.Id;
        tab.Add([atlas]);
        tab.Open(atlas);
        tab.SelectStep(MaterialStep.Source);
        foreach (var w in Idle("the atlas")) yield return w;
        if (f != null) { fail(f); yield break; }
        var (mw, mh) = tab.MaskSize;

        // ---- (a) from the UV islands ----
        var v = global.GetVersion();
        var proposals = tab.Proposals();
        var msg = tab.AddProposals();
        if (tab.Regions.Count != proposals.Count || global.GetVersion() != v + 1 || proposals.Count < 2)
        { fail($"from the UV islands: {msg}; {tab.Regions.Count} regions, {global.GetVersion() - v} undo step(s)"); yield break; }
        // how they match the parts: each region against the islands of its own parts (IoU), and the regions' overlap
        var islands = tab.Islands()!;
        var match = new List<string>();
        double worstIou = 1, overlap = 0;
        for (int i = 0; i < proposals.Count; i++)
        {
            var own = new byte[mw * mh];
            foreach (var isl in islands.Where(x => proposals[i].Parts.Contains(x.Part) && x.Coverage < 0.97))
                for (int k = 0; k < own.Length; k++)
                    own[k] = System.Math.Max(own[k], isl.Mask[k]);
            long inter = 0, uni = 0;
            for (int k = 0; k < own.Length; k++)
            {
                bool a = own[k] > 127, b = proposals[i].Mask[k] > 127;
                inter += a && b ? 1 : 0;
                uni += a || b ? 1 : 0;
            }
            double iou = uni == 0 ? 0 : (double)inter / uni;
            worstIou = System.Math.Min(worstIou, iou);
            for (int j = i + 1; j < proposals.Count; j++)
                overlap = System.Math.Max(overlap, MaterialRegions.Overlap(proposals[i].Mask, proposals[j].Mask));
            match.Add($"{proposals[i].Name} {proposals[i].Coverage * 100:0.#} % from {proposals[i].Parts.Count} part(s) ({proposals[i].Islands} islands; IoU with its parts' islands {iou:0.00})");
        }
        global.Undo();
        int afterUndo = tab.Regions.Count;
        global.Redo();
        if (afterUndo != 0 || tab.Regions.Count != proposals.Count) { fail($"undo/redo of the proposals: {afterUndo}, then {tab.Regions.Count}"); yield break; }
        notes.Add($"atlas {RemasterTextures.ShortName(atlas)} ({atlases} of {cands.Count} beach textures propose 2 or more regions): {tab.IslandsNote}; {proposals.Count} regions proposed: {string.Join("; ", match)}; "
            + $"overlap between regions {overlap * 100:0.##} %; one undo step, undone, redone");
        foreach (var w in Idle("the proposed regions")) yield return w;
        if (f != null) { fail(f); yield break; }
        if (shots)
        {
            for (int i = 0; i < 12; i++) yield return null;
            Window($"{shotDir}/r2b6-regions-islands.png");
        }

        // ---- (b) by colour, the colour picked in the 2D map ----
        tab.Map2D.Reset();
        for (int i = 0; i < 3; i++) yield return null;
        var m0 = tab.RegionMask(0)!;
        // a point well inside region 0
        long cx = 0, cy = 0, cn = 0;
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
                if (m0[y * mw + x] > 250) { cx += x; cy += y; cn++; }
        var at = new Vector2((cx / (float)System.Math.Max(1, cn) + 0.5f) / mw, (cy / (float)System.Math.Max(1, cn) + 0.5f) / mh);
        tab.StartPick(MaterialsTab.RegionPick);
        var pp = tab.Map2D.PointOf(at);
        tab.Map2D._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pp });
        tab.Map2D._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = pp });
        var picked = tab.RegionColour;
        int before = tab.Regions.Count;
        v = global.GetVersion();
        var cerr = tab.AddRegionByColour(picked);
        if (cerr != null || tab.Regions.Count != before + 1 || global.GetVersion() != v + 1)
        { fail($"by colour: {cerr}"); yield break; }
        var colourMask = tab.RegionMask(tab.Regions.Count - 1)!;
        double colourCov = MaterialRegions.Coverage(colourMask), inRegion0 = 0;
        for (int k = 0; k < colourMask.Length; k++)
            inRegion0 += colourMask[k] > 127 && m0[k] > 127 ? 1 : 0;
        notes.Add($"by colour: #{picked.ToHtml(false)} picked in the 2D map at UV {at.X:0.00},{at.Y:0.00} (region 0's middle): {colourCov * 100:0.#} % of the texture, "
            + $"{inRegion0 / System.Math.Max(1, colourMask.Count(b => b > 127)) * 100:0} % of it inside region 0; one undo step");

        // ---- (c) by hand: brush, rectangle, polygon, eraser (each one undo step) ----
        v = global.GetVersion();
        tab.AddEmptyRegion();
        int hand = tab.Regions.Count - 1;
        if (tab.Region != hand) { fail($"the new region is not edited ({tab.Region})"); yield break; }
        tab.BrushRadius = 10;
        tab.Tool = MapView2D.RegionTool.Brush;
        void Drag(Vector2 a, Vector2 b)
        {
            tab.Map2D._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = tab.Map2D.PointOf(a) });
            tab.Map2D._GuiInput(new InputEventMouseMotion { Position = tab.Map2D.PointOf((a + b) / 2), ButtonMask = MouseButtonMask.Left });
            tab.Map2D._GuiInput(new InputEventMouseMotion { Position = tab.Map2D.PointOf(b), ButtonMask = MouseButtonMask.Left });
            tab.Map2D._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = tab.Map2D.PointOf(b) });
        }
        Drag(new Vector2(0.1f, 0.1f), new Vector2(0.3f, 0.15f));
        double brushCov = MaterialRegions.Coverage(tab.RegionMask(hand)!);
        tab.Tool = MapView2D.RegionTool.Rect;
        Drag(new Vector2(0.6f, 0.7f), new Vector2(0.75f, 0.85f));
        double rectCov = MaterialRegions.Coverage(tab.RegionMask(hand)!);
        tab.Tool = MapView2D.RegionTool.Polygon;
        foreach (var pt in new[] { new Vector2(0.4f, 0.4f), new Vector2(0.5f, 0.42f), new Vector2(0.47f, 0.55f) })
            tab.Map2D._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = tab.Map2D.PointOf(pt) });
        tab.Map2D.ClosePolygon();
        double polyCov = MaterialRegions.Coverage(tab.RegionMask(hand)!);
        tab.Tool = MapView2D.RegionTool.Erase;
        Drag(new Vector2(0.62f, 0.72f), new Vector2(0.72f, 0.82f));
        double eraseCov = MaterialRegions.Coverage(tab.RegionMask(hand)!);
        tab.Tool = MapView2D.RegionTool.None;
        int handSteps = (int)(global.GetVersion() - v);
        if (!(brushCov > 0.001 && rectCov > brushCov && polyCov > rectCov && eraseCov < polyCov) || handSteps != 5)
        { fail($"by hand: coverage brush {brushCov:0.0000}, rectangle {rectCov:0.0000}, polygon {polyCov:0.0000}, erased {eraseCov:0.0000}; {handSteps} undo steps (5: add, brush, rectangle, polygon, eraser)"); yield break; }
        global.Undo();
        double undoneCov = MaterialRegions.Coverage(tab.RegionMask(hand)!);
        global.Redo();
        if (System.Math.Abs(undoneCov - polyCov) > 1e-9 || System.Math.Abs(MaterialRegions.Coverage(tab.RegionMask(hand)!) - eraseCov) > 1e-9)
        { fail($"undo of the eraser: {undoneCov:0.0000} (want {polyCov:0.0000})"); yield break; }
        notes.Add($"by hand: + Region, a brush stroke ({brushCov * 100:0.##} %), a rectangle ({rectCov * 100:0.##} %), a polygon ({polyCov * 100:0.##} %), the eraser ({eraseCov * 100:0.##} %): 5 undo steps; the eraser undone and redone");
        foreach (var w in Idle("the regions by colour and by hand")) yield return w;
        if (f != null) { fail(f); yield break; }
        if (shots)
        {
            for (int i = 0; i < 12; i++) yield return null;
            Window($"{shotDir}/r2b6-regions-all.png");
        }
        // keep two proposed regions and the hand-drawn one for what follows (fewer passes)
        while (tab.Regions.Count > 3)
            tab.RemoveRegion(2);
        foreach (var w in Idle("three regions")) yield return w;
        if (f != null) { fail(f); yield break; }

        // ---- two profiles on one texture: stone and concrete; metal over region 0, grass and foliage over region 1 ----
        tab.EditRegion(-1);
        tab.LoadProfile("stone-and-concrete");
        tab.SetRegionProfile(0, "metal");
        tab.SetRegionProfile(1, "grass-and-foliage");
        tab.EditRegion(0);
        tab.SelectStep(MaterialStep.Metallic);
        foreach (var w in Idle("two profiles")) yield return w;
        if (f != null) { fail(f); yield break; }
        var metal = tab.Pixels(MaterialStep.Metallic)!;
        var back = tab.Pixels(MaterialStep.Backlight);
        (double In, double Out) Means(RgbaImage map, byte[] mask)
        {
            double a = 0, b = 0;
            int na = 0, nb = 0;
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                {
                    byte m = mask[(y * mh / map.Height) * mw + x * mw / map.Width];
                    var i = (y * map.Width + x) * 4;
                    float val = (map.Rgba[i] + map.Rgba[i + 1] + map.Rgba[i + 2]) / (3f * 255);
                    if (m > 250) { a += val; na++; }
                    else if (m < 5 && tab.Regions.Count > 0) { b += val; nb++; }
                }
            return (na > 0 ? a / na : -1, nb > 0 ? b / nb : -1);
        }
        var r0 = tab.RegionMask(0)!;
        var r1 = tab.RegionMask(1)!;
        // away from the borders: every region's mask grown by three feathers (the soft border reaches past the drawn mask)
        int grow = (int)System.Math.Ceiling(3 * MaterialRegionStore.DefaultFeather) + 1;
        byte[] Grown(byte[] m)
        {
            var o = new byte[m.Length];
            for (int y = 0; y < mh; y++)
                for (int x = 0; x < mw; x++)
                {
                    byte best = 0;
                    for (int dy = -grow; dy <= grow && best < 255; dy++)
                        for (int dx = -grow; dx <= grow; dx++)
                        {
                            int xx = (x + dx + mw) % mw, yy = (y + dy + mh) % mh;
                            best = System.Math.Max(best, m[yy * mw + xx]);
                        }
                    o[y * mw + x] = best;
                }
            return o;
        }
        var other = new byte[r0.Length];
        var all = Enumerable.Range(0, tab.Regions.Count).Select(i => tab.RegionMask(i)!).ToList();
        for (int k = 0; k < other.Length; k++)
            other[k] = all.Max(m => m[k]);
        other = Grown(other);
        var r1Far = Grown(r1);
        var (metIn, _) = Means(metal, r0);
        var (_, metOut) = Means(metal, other);
        var (backIn, _) = back != null ? Means(back, r1) : (-1, -1);
        var (_, backOut) = back != null ? Means(back, r1Far) : (-1, -1);
        if (metIn < 0.4 || metOut > 0.05 || back == null || backIn < 0.03 || backOut > backIn * 0.5)
        { fail($"two profiles: metallic in region 0 {metIn:0.00}, outside the regions {metOut:0.00}; backlight in region 1 {backIn:0.000}, outside {backOut:0.000}"); yield break; }
        notes.Add($"two profiles on one texture (stone and concrete; metal on region 0, grass and foliage on region 1): metallic {metIn:0.00} in region 0 against {metOut:0.00} outside the regions (beyond {grow} texels of their borders), "
            + $"backlight (grass's map, not the stone's) {backIn:0.000} in region 1 against {backOut:0.000} away from it");
        // a region's own setting: one undo step on its exceptions, the texture's settings untouched
        v = global.GetVersion();
        var baseRough = tab.BaseRecipe.RoughnessBase;
        tab.SelectStep(MaterialStep.Roughness);
        tab.Drag("RoughnessMetal", 0.2, 0.15);
        var ex0 = MaterialRegionStore.ExceptionsOf(tab.Regions[0].AsGodotDictionary());
        if (global.GetVersion() != v + 1 || !ex0.ContainsKey("RoughnessMetal") || !Mathf.IsEqualApprox(tab.BaseRecipe.RoughnessBase, baseRough) || tab.QueueSettings.RoughnessMetal == 0.15f)
        { fail($"a region's setting: {global.GetVersion() - v} undo step(s), its exceptions {ex0}"); yield break; }
        foreach (var w in Idle("the region's setting")) yield return w;
        if (f != null) { fail(f); yield break; }

        // ---- the composite valid: the normals, the borders, the height levels (the graph on the CPU at the working size) ----
        var ins = tab.CurrentInputs()!;
        var (cw, ch, cs) = MaterialPipeline.Size(ins, ins.Recipe.WorkingSide);
        var cpu = new CpuBackend();
        var co = MaterializeGraph.Build(cpu, ins.Key, ins.Original, ins.Imports, ins.Recipe, new BuildRequest(cw, ch, cs), ins.Regions);
        var (cfail, cline) = MaterializeCheck.CompositeCheck(cpu, co, _ => MaterializeGraph.Build(cpu, ins.Key, ins.Original, ins.Imports, ins.Recipe, new BuildRequest(cw, ch, cs, MatchHeights: false), ins.Regions));
        var shownN = tab.Pixels(MaterialStep.Normal)!;
        var (_, shownP999, shownBad) = MaterialRegions.NormalLength(shownN);
        notes.Add($"composite ({ins.Regions!.Count} regions): {cline}; the tab's own normal map (its {(tab.UsingGpu ? "GPU" : "CPU")}): 99.9th percentile |n| - 1 {shownP999:0.0000}, {shownBad * 100:0.000} % above 0.03");
        if (cfail != null) { fail("composite: " + cfail); yield break; }
        if (shownP999 > 0.02) { fail($"the tab's composited normals are not unit length ({shownP999:0.0000})"); yield break; }
        if (shots)
        {
            tab.SelectStep(MaterialStep.Normal);
            for (int i = 0; i < 12; i++) yield return null;
            Window($"{shotDir}/r2b6-composite-normal.png");
            tab.SetShapeForCheck(MaterialPreview.Object);
            tab.SelectStep(MaterialStep.Metallic);
            for (int i = 0; i < 30; i++) yield return null;
            Window($"{shotDir}/r2b6-composite-object.png");
            tab.SetShapeForCheck(MaterialPreview.Sphere);
        }

        // ---- save: the masks as files, the regions on the entry; undo, redo; read back ----
        v = global.GetVersion();
        if (tab.Save() is { } serr) { fail(serr); yield break; }
        double ts = _t;
        while (tab.Saving)
        {
            if (_t - ts > 180) { fail("the save with regions did not finish"); yield break; }
            yield return null;
        }
        var entry = TextureStore.Find(atlas);
        if (global.GetVersion() != v + 1 || entry is not { Material.Length: > 0 } || entry.Regions.Count != 3)
        { fail($"save: {tab.SaveResult}; {global.GetVersion() - v} undo step(s), {entry?.Regions.Count} regions on the entry"); yield break; }
        var maskFiles = entry.Regions.Select(x => x.AsGodotDictionary()[MaterialRegionStore.Mask].AsString()).ToList();
        if (maskFiles.Any(m => !m.Contains("region-mask.") || m.StartsWith('/') || m.Contains(':') || !FileAccess.FileExists(TextureStore.Absolute(m))))
        { fail($"the saved masks: {string.Join(", ", maskFiles)}"); yield break; }
        var d0 = entry.Regions[0].AsGodotDictionary();
        if (MaterialRegionStore.ProfileOf(d0) != "metal" || d0[MaterialRegionStore.ProfileHash].AsString() != MaterialProfiles.Hash(MaterialProfiles.Load("metal")!)
            || !MaterialRegionStore.ExceptionsOf(d0).ContainsKey("RoughnessMetal"))
        { fail($"the saved region 0: {d0}"); yield break; }
        var matText = FileAccess.GetFileAsString(TextureStore.Absolute(entry.Material));
        if (!matText.Contains("backlight_texture")) { fail("the saved material lacks the grass region's backlight"); yield break; }
        global.Undo();
        bool undone = TextureStore.Find(atlas) is not { Regions.Count: > 0 };
        global.Redo();
        if (!undone || TextureStore.Find(atlas)?.Regions.Count != 3) { fail($"undo/redo of the save: undone {undone}, then {TextureStore.Find(atlas)?.Regions.Count} regions"); yield break; }
        // read back from the entry alone (the session and the masks in memory forgotten)
        tab.ForgetRegions(atlas);
        foreach (var w in Idle("the regions read back")) yield return w;
        if (f != null) { fail(f); yield break; }
        if (tab.Regions.Count != 3 || tab.RegionMask(2) is not { } back2 || System.Math.Abs(MaterialRegions.Coverage(back2) - eraseCov) > 0.002)
        { fail($"read back: {tab.Regions.Count} regions, the hand-drawn one's coverage {(tab.RegionMask(2) is { } bm ? MaterialRegions.Coverage(bm) : -1):0.0000} (saved {eraseCov:0.0000})"); yield break; }
        notes.Add($"saved ({tab.SaveResult}): the masks as {string.Join(", ", maskFiles.Select(x => x.GetFile()))}, each region's profile, hash and exceptions on the entry, "
            + $"the material with the grass region's backlight; one undo step, undone, redone; read back from the entry (the masks from their files)");

        // ---- Apply to queue with the regioned texture ----
        var others = tab.Queue.Where(x => x != atlas).Take(1).ToList();
        foreach (var o in tab.Queue.ToList())
            if (o != atlas && !others.Contains(o))
                tab.RemoveFromQueue(o);
        tab.Open(atlas);
        v = global.GetVersion();
        if (tab.ApplyToQueue() is { } aerr) { fail(aerr); yield break; }
        foreach (var w in Run("Apply to queue with regions")) yield return w;
        if (f != null) { fail(f); yield break; }
        var applied = TextureStore.Find(atlas);
        if (global.GetVersion() != v + 1 || applied?.Regions.Count != 3 || tab.BakeResult?.Contains($"{tab.Queue.Count} made") != true)
        { fail($"Apply to queue: {tab.BakeResult}; {applied?.Regions.Count} regions, {global.GetVersion() - v} undo step(s)"); yield break; }
        notes.Add($"Apply to queue: {tab.BakeResult} (the atlas with its 3 regions; one undo step)");

        // ---- Make again after a region's profile changed ----
        var checkProfile = MaterialProfiles.From(MaterialProfiles.Load("grass-and-foliage")!, "Check region");
        _plugin.SaveProfile("check-region", checkProfile, "Save the profile Check region");
        tab.Open(atlas);
        tab.SetRegionProfile(1, "check-region");
        foreach (var w in Idle("the check profile")) yield return w;
        if (f != null) { fail(f); yield break; }
        if (tab.Save() is { } s2) { fail(s2); yield break; }
        ts = _t;
        while (tab.Saving)
        {
            if (_t - ts > 180) { fail("the save with the check profile did not finish"); yield break; }
            yield return null;
        }
        var hash0 = TextureStore.Find(atlas)!.Regions[1].AsGodotDictionary()[MaterialRegionStore.ProfileHash].AsString();
        var changed = (MaterialProfile)checkProfile.Duplicate();
        changed.RoughnessBase = 0.55f;
        changed.Title = "Check region";
        _plugin.SaveProfile("check-region", changed, "Change the profile Check region");
        var stale = ProfileStore.Stale();
        if (!stale.TryGetValue("check-region", out var staleIds) || !staleIds.Contains(atlas))
        { fail($"the region's changed profile is not seen: {ProfileStore.StaleText(stale)}"); yield break; }
        v = global.GetVersion();
        if (tab.MakeAgainStale("check-region") is { } merr) { fail(merr); yield break; }
        foreach (var w in Run("Make again")) yield return w;
        if (f != null) { fail(f); yield break; }
        var hash1 = TextureStore.Find(atlas)!.Regions[1].AsGodotDictionary()[MaterialRegionStore.ProfileHash].AsString();
        if (hash1 != MaterialProfiles.Hash(changed) || hash1 == hash0 || ProfileStore.Stale().ContainsKey("check-region") || global.GetVersion() != v + 1)
        { fail($"Make again: {tab.BakeResult}; the region's hash {hash0} -> {hash1} (the profile's {MaterialProfiles.Hash(changed)}), still changed: {ProfileStore.StaleText(ProfileStore.Stale())}"); yield break; }
        notes.Add($"Make again after the region's profile changed: '{ProfileStore.StaleText(stale)}' before, {tab.BakeResult}; the region's profile hash {hash0} -> {hash1}, nothing changed after");

        // ---- AI estimation through the fake server ----
        tab.EditRegion(-1);
        string? afail = null;
        foreach (var w in Ai(tab, global, x => afail = x)) yield return w;
        if (afail != null) { fail(afail); yield break; }
        notes.Add(_aiNote);
        foreach (var w in Idle("phase 6's end")) yield return w;
        if (f != null) { fail(f); yield break; }
        GD.Print($"ffx editortest: materials phase 6: {string.Join("; ", notes)}");
        _seen.Add($"materials phase 6: {string.Join("; ", notes)}");
    }

    private string _aiNote = "";

    private IEnumerable<string?> Ai(MaterialsTab tab, UndoRedo global, System.Action<string> fail)
    {
        string? f = null;
        IEnumerable<string?> Until(string what, System.Func<bool> ok, double limit = 90)
        {
            double t0 = _t;
            while (!ok())
            {
                if (_t - t0 > limit) { f = $"AI: {what} ({tab.AiState.GetValueOrDefault(MaterialStep.Normal)})"; yield break; }
                yield return null;
            }
        }
        var made = new List<string>();
        var cacheDir = ProjectSettings.GlobalizePath(MaterialsTab.ComfyCacheDir);
        var filesBefore = DirAccess.DirExistsAbsolute(cacheDir) ? DirAccess.GetFilesAt(cacheDir).ToHashSet() : new HashSet<string>();
        using var fake = new FakeComfy(delayPolls: 40);
        ComfyUpscaler.UrlOverride = fake.Url;
        ComfyUpscaler.PollMs = 100;
        var s = MaterialStep.Normal;
        ComfyAi.Overrides[ComfyAi.WorkflowSetting(s)] = ComfyAi.Example;
        ComfyAi.Overrides[ComfyAi.ModelSetting(s)] = "check-normal-estimator";
        ComfyAi.Overrides[ComfyAi.LicenceSetting(s)] = "";
        try
        {
            tab.SelectStep(s);
            tab.SetMode(s, StepMode.Generate);
            foreach (var w in Until("the generated normal", () => tab.Idle)) yield return w;
            if (f != null) { fail(f); yield break; }
            var generated = tab.Pixels(s)!;
            // the licence empty: refused, nothing sent
            var v = global.GetVersion();
            tab.SetMode(s, StepMode.Ai);
            foreach (var w in Until("the refusal", () => tab.Idle)) yield return w;
            if (f != null) { fail(f); yield break; }
            var refusal = tab.AiState.GetValueOrDefault(s, "");
            if (!refusal.Contains("refused") || !refusal.Contains(ComfyAi.LicenceSetting(s)) || fake.Prompts != 0 || fake.Uploads != 0 || tab.AiInUse(s) || global.GetVersion() != v + 1)
            { fail($"AI with no licence: '{refusal}'; {fake.Uploads} uploads, {fake.Prompts} prompts"); yield break; }
            // a licence: started, the progress, cancelled while it "runs"
            ComfyAi.Overrides[ComfyAi.LicenceSetting(s)] = "CC0 (the check's fake estimator)";
            int runs0 = tab.AiRuns;
            tab.RunAiAgain(s);
            var stages = new HashSet<string>();
            double maxDone = 0;
            foreach (var w in Until("the run did not reach the queue", () =>
            {
                maxDone = System.Math.Max(maxDone, tab.AiDone);
                stages.Add(tab.AiState.GetValueOrDefault(s, "").Split(':').Last().Split(',')[0].Trim());
                return tab.AiDone >= 0.2;
            })) yield return w;
            if (f != null) { fail(f); yield break; }
            tab.CancelAi();
            foreach (var w in Until("the cancel", () => tab.AiDone < 0)) yield return w;
            if (f != null) { fail(f); yield break; }
            if (!tab.AiState.GetValueOrDefault(s, "").Contains("cancelled") || fake.Views != 0 || tab.AiInUse(s))
            { fail($"AI cancel: {tab.AiState.GetValueOrDefault(s)}; views {fake.Views}"); yield break; }
            // run again: the estimate cached and the step's input
            fake.DelayPolls = 5;
            tab.RunAiAgain(s);
            foreach (var w in Until("the second run", () =>
            {
                maxDone = System.Math.Max(maxDone, tab.AiDone);
                stages.Add(tab.AiState.GetValueOrDefault(s, "").Split(':').Last().Split(',')[0].Trim());
                return tab.AiInUse(s) && tab.Idle;
            })) yield return w;
            if (f != null) { fail(f); yield break; }
            var ai = tab.Pixels(s)!;
            double diff = 0;
            for (int i = 0; i < ai.Rgba.Length && ai.Rgba.Length == generated.Rgba.Length; i++)
                if (i % 4 != 3) diff += System.Math.Abs(ai.Rgba[i] - generated.Rgba[i]);
            diff /= ai.Rgba.Length * 0.75;
            var newFiles = DirAccess.DirExistsAbsolute(cacheDir) ? DirAccess.GetFilesAt(cacheDir).Where(x => !filesBefore.Contains(x)).ToList() : new List<string>();
            made.AddRange(newFiles.Select(x => $"{MaterialsTab.ComfyCacheDir}/{x}"));
            var prompt = fake.LastPrompt;
            if (fake.Prompts != 2 || fake.Views != 1 || newFiles.Count != 1 || diff < 1 || prompt.Contains("%gdffx_") || !prompt.Contains("gdffx/ai-normal")
                || !prompt.Contains("\"map\":\"normal\"") || fake.AiMaps.LastOrDefault() != "normal" || tab.AiRuns != runs0 + 2)
            { fail($"AI's answer: prompts {fake.Prompts}, views {fake.Views}, cache files {newFiles.Count}, the normal changed by {diff:0.##}, the prompt {prompt}"); yield break; }
            // the step's settings on top: the strength halved (4: the estimate as it is)
            double str0 = tab.Recipe.NormalStrength;
            tab.Drag("NormalStrength", str0 / 2);
            foreach (var w in Until("the strength on top", () => tab.Idle)) yield return w;
            if (f != null) { fail(f); yield break; }
            var stronger = tab.Pixels(s)!;
            double tiltA = 0, tiltB = 0;
            for (int i = 0; i < ai.Rgba.Length; i += 4)
            {
                tiltA += 1 - (ai.Rgba[i + 2] / 255.0 * 2 - 1);
                tiltB += 1 - (stronger.Rgba[i + 2] / 255.0 * 2 - 1);
            }
            if (tiltB >= tiltA * 0.9) { fail($"the strength did not apply on top of the AI normal (tilt {tiltA:0.##} -> {tiltB:0.##})"); yield break; }
            // the cache: Generate and back, then from the cache folder alone: no request
            int prompts = fake.Prompts;
            tab.SetMode(s, StepMode.Generate);
            foreach (var w in Until("generated again", () => tab.Idle && !tab.AiInUse(s))) yield return w;
            if (f != null) { fail(f); yield break; }
            MaterialsTab.ClearComfyMemory();
            tab.SetMode(s, StepMode.Ai);
            foreach (var w in Until("the cached estimate", () => tab.Idle && tab.AiInUse(s))) yield return w;
            if (f != null) { fail(f); yield break; }
            if (fake.Prompts != prompts) { fail($"AI's cache: {fake.Prompts - prompts} more prompt(s)"); yield break; }
            // saved: the Source note names the tool, the workflow, the model and the licence
            var id = tab.Current!;
            if (tab.Save() is { } err) { fail(err); yield break; }
            foreach (var w in Until("the save", () => !tab.Saving, 180)) yield return w;
            if (f != null) { fail(f); yield break; }
            var source = TextureStore.Find(id)?.Source ?? "";
            var want = "material ai normal: normal estimated by ComfyUI (workflow example-map-workflow.json, model check-normal-estimator; licence: CC0 (the check's fake estimator))";
            if (!source.Contains(want)) { fail($"the Source after the save: '{source}'"); yield break; }
            tab.SetMode(s, StepMode.Generate);
            tab.Drag("NormalStrength", str0);
            foreach (var w in Until("generated at the end", () => tab.Idle)) yield return w;
            if (f != null) { fail(f); yield break; }
            _aiNote = $"AI (the normal step, the shipped example template through the fake server {fake.Url}): refused with no licence ('{refusal.Split('(')[0].Trim()}...'; nothing sent), "
                + $"progress {string.Join(" > ", stages.Where(x => x.Length > 0))} (to {maxDone * 100:0} %), cancelled (nothing fetched), run again: the estimate cached and the step's input "
                + $"(it changed the normal map by {diff:0.#} of 255; the placeholders filled: no %gdffx_ left, the output gdffx/ai-normal), the strength halved on top (the tilt x{tiltB / System.Math.Max(1e-6, tiltA):0.00}), "
                + $"Generate and back and from the cache folder with no request; the Source note '{want}'; {fake.Uploads} uploads, {fake.Prompts} prompts, {fake.Queues} queue and {fake.Histories} history polls, {fake.Views} download";
        }
        finally
        {
            ComfyUpscaler.UrlOverride = null;
            ComfyUpscaler.PollMs = 1000;
            ComfyAi.Overrides.Clear();
            foreach (var file in made.Distinct())
                if (FileAccess.FileExists(file))
                    DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(file));
            MaterialsTab.ClearComfyMemory();
        }
    }
}
#endif
