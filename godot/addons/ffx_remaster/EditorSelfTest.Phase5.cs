#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>R2b phase 5's part of the editor check (docs/remaster-materials.md, phase 5, "Checks").</summary>
public sealed partial class EditorSelfTest
{
    /// <summary>
    /// R2b phase 5 in the tab: the shortcuts (each sent as an input event: steps, the strip, the
    /// compare, the queue, reset the setting under the mouse, frame, the list, save; none while the
    /// focus is outside the tab or the tab is hidden); colour picks dragged in the 2D map and clicked
    /// on the preview's object (the mean over the radius, one undo step, the sample's mask shown);
    /// a texture picked in the open map with Alt+click and Pick texture (queued; the toast); the In
    /// map view (the open map around the texture, its live lit material, the map's light, a pick;
    /// under a renderer its pixels follow a setting and the wipe shows the classic look; a map loaded
    /// on demand); ComfyUI through a fake server (progress, cancel, run again, the cached answer as
    /// the source, from the cache folder too, the Source note on Save).
    /// </summary>
    private IEnumerable<string?> Phase5(MaterialsTab tab, UndoRedo global, List<string> ids, MapEditRoot root, bool shots, string shotDir, System.Action<string> fail)
    {
        string? f = null;
        IEnumerable<string?> Idle(string what, double limit = 120)
        {
            double t0 = _t;
            while (!tab.Idle)
            {
                if (_t - t0 > limit) { f = $"{what}: the maps were not made in {limit} s"; yield break; }
                yield return null;
            }
        }
        var notes = new List<string>();
        var vp = tab.GetViewport();
        void Press(Godot.Key k, bool shift = false, bool ctrl = false)
        {
            vp.PushInput(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = true, ShiftPressed = shift, CtrlPressed = ctrl });
            vp.PushInput(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = false, ShiftPressed = shift, CtrlPressed = ctrl });
        }
        _plugin.ShowMaterials();
        tab.Open(ids[0]);
        tab.SelectStep(MaterialStep.Albedo);
        foreach (var w in Idle("phase 5's start")) yield return w;
        if (f != null) { fail(f); yield break; }
        yield return null;

        // ---- the shortcuts ----
        tab.Preview.GrabFocus();
        yield return null;
        if (!tab.HasKeyFocus) { fail("the preview took no keyboard focus"); yield break; }
        var strip = tab.StripSteps();
        var keyNotes = new List<string>();
        Press(Godot.Key.Key3);
        if (tab.Step != strip[3]) { fail($"key 3: step {tab.Step}, the strip's 3 is {strip[3]}"); yield break; }
        Press(Godot.Key.Bracketright);
        if (tab.Step != strip[4]) { fail($"key ]: step {tab.Step}"); yield break; }
        Press(Godot.Key.Bracketleft);
        Press(Godot.Key.Key0);
        if (tab.Step != MaterialStep.Source) { fail($"key 0: step {tab.Step}"); yield break; }
        keyNotes.Add($"0-9 and [ ] go to the strip's steps ({strip.Count} in it)");
        float wipe0 = tab.WipeValue;
        Press(Godot.Key.Tab);
        float wipeOff = tab.WipeValue;
        Press(Godot.Key.Tab);
        if (wipeOff > 0.001f || !Mathf.IsEqualApprox(tab.WipeValue, wipe0)) { fail($"Tab: the wipe {wipe0} -> {wipeOff} -> {tab.WipeValue}"); yield break; }
        Press(Godot.Key.Tab, shift: true);
        float swapped = tab.WipeValue;
        Press(Godot.Key.Tab, shift: true);
        if (swapped < 0.999f || tab.WipeValue > 0.001f) { fail($"Shift+Tab: the wipe {swapped}, then {tab.WipeValue}"); yield break; }
        Press(Godot.Key.Tab);
        keyNotes.Add("Tab compare off/on, Shift+Tab swaps the sides");
        var cur = tab.Current;
        Press(Godot.Key.Down);
        var next = tab.Current;
        Press(Godot.Key.Left);
        if (next == cur || tab.Current != cur) { fail($"Down/Left: {cur} -> {next} -> {tab.Current}"); yield break; }
        keyNotes.Add("Down/Up/Left/Right switch texture");
        foreach (var w in Idle("the queue keys")) yield return w;
        if (f != null) { fail(f); yield break; }
        // R: the setting under the mouse back to its default
        tab.SelectStep(MaterialStep.Height);
        tab.SetSetting("HeightContrast", 2.6f);
        foreach (var w in Idle("the contrast")) yield return w;
        if (f != null) { fail(f); yield break; }
        for (int i = 0; i < 3; i++) yield return null;
        if (tab.RowOf("HeightContrast") is not { } row || !row.IsVisibleInTree()) { fail("no HeightContrast row"); yield break; }
        tab.Preview.GrabFocus();
        vp.PushInput(new InputEventMouseMotion { Position = row.GetGlobalRect().GetCenter(), GlobalPosition = row.GetGlobalRect().GetCenter() });
        var vR = global.GetVersion();
        Press(Godot.Key.R);
        if (!Mathf.IsEqualApprox(tab.Recipe.HeightContrast, new PbrRecipe().HeightContrast) || global.GetVersion() != vR + 1)
        { fail($"R over the contrast: {tab.Recipe.HeightContrast} ({tab.LastShortcut}), {global.GetVersion() - vR} undo step(s)"); yield break; }
        keyNotes.Add("R reset the setting under the mouse (one undo step)");
        Press(Godot.Key.F);
        if (tab.LastShortcut != "frame") { fail($"F: {tab.LastShortcut}"); yield break; }
        Press(Godot.Key.Slash, shift: true);
        bool listed = tab.ShortcutsShown;
        if (shots && listed)
        {
            for (int i = 0; i < 8; i++) yield return null;
            Window($"{shotDir}/r2b5-shortcuts.png");
        }
        tab.HideShortcuts();
        tab.Preview.GrabFocus();
        if (!listed) { fail("? did not show the shortcuts"); yield break; }
        keyNotes.Add("F frames, ? lists the shortcuts");
        // Ctrl+S saves the texture (not the scene)
        foreach (var w in Idle("before Ctrl+S")) yield return w;
        if (f != null) { fail(f); yield break; }
        var vS = global.GetVersion();
        Press(Godot.Key.S, ctrl: true);
        double tS = _t;
        while (tab.Saving)
        {
            if (_t - tS > 120) { fail("Ctrl+S: the save did not finish"); yield break; }
            yield return null;
        }
        if (tab.LastShortcut?.StartsWith("save") != true || tab.SaveResult?.Contains("saved") != true || global.GetVersion() != vS + 1)
        { fail($"Ctrl+S: {tab.LastShortcut}; {tab.SaveResult}; {global.GetVersion() - vS} undo step(s)"); yield break; }
        keyNotes.Add("Ctrl+S saved the texture (one undo step)");
        // none of them without the focus: a control outside the tab focused, then the tab hidden
        var outside = new Button { Text = "outside", Position = new Vector2(2, 2) };
        EditorInterface.Singleton.GetBaseControl().AddChild(outside);
        yield return null;
        outside.GrabFocus();
        var stepBefore = tab.Step;
        var lastBefore = tab.LastShortcut;
        Press(Godot.Key.Key5);
        Press(Godot.Key.Down);
        bool quiet = tab.Step == stepBefore && tab.LastShortcut == lastBefore && !tab.HasKeyFocus;
        outside.QueueFree();
        EditorInterface.Singleton.SetMainScreenEditor("3D");
        yield return null;
        Press(Godot.Key.Key2);
        quiet &= tab.Step == stepBefore && tab.LastShortcut == lastBefore;
        _plugin.ShowMaterials();
        yield return null;
        if (!quiet) { fail($"a shortcut fired without the tab's focus (step {tab.Step}, {tab.LastShortcut})"); yield break; }
        keyNotes.Add("none fired with the focus outside the tab, nor with the tab hidden");
        notes.Add("keys: " + string.Join("; ", keyNotes));

        // ---- colour picks ----
        tab.SetMode(MaterialStep.Roughness, StepMode.Generate);
        tab.SelectStep(MaterialStep.Roughness);
        tab.Map2D.Reset();
        foreach (var w in Idle("roughness for the picks")) yield return w;
        if (f != null) { fail(f); yield break; }
        for (int i = 0; i < 3; i++) yield return null;
        tab.PickRadius = 3;
        var albedo = tab.Pixels(MaterialStep.Albedo)!;
        var vP = global.GetVersion();
        tab.StartPick("RoughnessSample2");
        var p1 = tab.Map2D.PointOf(new Vector2(0.25f, 0.3f));
        var p2 = tab.Map2D.PointOf(new Vector2(0.62f, 0.55f));
        tab.Map2D._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = p1 });
        tab.Map2D._GuiInput(new InputEventMouseMotion { Position = p2, ButtonMask = MouseButtonMask.Left });
        tab.Map2D._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = p2 });
        var uv2 = tab.Map2D.UvAt(p2);
        var want = uv2 is { } u2 ? tab.Sample(albedo, u2) : null;
        var got = tab.Recipe.RoughnessSample2;
        if (want is not { } wc || Mathf.Abs(got.R - wc.R) + Mathf.Abs(got.G - wc.G) + Mathf.Abs(got.B - wc.B) > 3.5f / 255 || got.A < 1
            || global.GetVersion() != vP + 1 || tab.Picking != null)
        { fail($"the 2D drag pick: {got} for {want} at {uv2}, {global.GetVersion() - vP} undo step(s), picking {tab.Picking}"); yield break; }
        foreach (var w in Idle("the picked sample")) yield return w;
        if (f != null) { fail(f); yield break; }
        if (!tab.MaskShown) { fail("the picked sample's mask is not shown"); yield break; }
        if (shots)
        {
            for (int i = 0; i < 10; i++) yield return null;
            Window($"{shotDir}/r2b5-pick-mask.png");
        }
        // on the preview's object (a plane)
        tab.SetShapeForCheck(MaterialPreview.Plane);
        for (int i = 0; i < 3; i++) yield return null;
        var vP3 = global.GetVersion();
        tab.StartPick("RoughnessSample3");
        var at = tab.Preview.FocusPoint();
        var uv3 = tab.Preview.UvAt(at);
        tab.Preview._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at });
        tab.Preview._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at });
        var want3 = uv3 is { } u3 ? tab.Sample(albedo, u3) : null;
        var got3 = tab.Recipe.RoughnessSample3;
        if (want3 is not { } w3 || Mathf.Abs(got3.R - w3.R) + Mathf.Abs(got3.G - w3.G) + Mathf.Abs(got3.B - w3.B) > 3.5f / 255 || global.GetVersion() != vP3 + 1)
        { fail($"the preview's pick: {got3} for {want3} at {uv3}, {global.GetVersion() - vP3} undo step(s)"); yield break; }
        notes.Add($"picks: a drag in the 2D map (radius 3: #{got.ToHtml(false)}, the mean there; one undo step; its mask shown), a click on the plane at UV {uv3?.X:0.00},{uv3?.Y:0.00} (#{got3.ToHtml(false)})");
        tab.SetSetting("RoughnessSample2", new Color(0, 0, 0, 0));
        tab.SetSetting("RoughnessSample3", new Color(0, 0, 0, 0));
        tab.SetIsolate(null);
        tab.PickRadius = 1;

        // ---- a texture picked in the open map ----
        var cam = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
        var camWas = cam.GlobalTransform;
        string? picked = null;
        Vector2 pickAt = default;
        var look = MapLook.Find(root.Map!);
        string? IdOf(MeshInstance3D mi, int s) => RemasterTextures.ColourId(look?.ClassicOf(mi, s) ?? mi.GetActiveMaterial(s))?.ToLowerInvariant();
        var meshes = root.Map!.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Where(m => m.IsVisibleInTree() && m.Mesh != null).ToList();
        foreach (var mi in meshes)
        {
            for (int s = 0; s < mi.Mesh!.GetSurfaceCount() && picked == null; s++)
            {
                if (IdOf(mi, s) is not { } id || tab.Queue.Contains(id))
                    continue;
                var arr = mi.Mesh.SurfaceGetArrays(s);
                var v = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var ix = arr[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : arr[(int)Mesh.ArrayType.Index].AsInt32Array();
                if (v.Length < 3)
                    continue;
                var (a, b, c) = ix != null ? (v[ix[0]], v[ix[1]], v[ix[2]]) : (v[0], v[1], v[2]);
                var (wa, wb, wc2) = (mi.GlobalTransform * a, mi.GlobalTransform * b, mi.GlobalTransform * c);
                var centre = (wa + wb + wc2) / 3;
                var n = (wb - wa).Cross(wc2 - wa);
                if (n.Length() < 1e-6f)
                    continue;
                n = n.Normalized();
                foreach (float side in new[] { 1f, -1f })
                {
                    var eye = centre + n * side * 3f;
                    var up = Mathf.Abs(n.Y) > 0.9f ? Vector3.Forward : Vector3.Up;
                    cam.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(centre, up);
                    var pos = cam.UnprojectPosition(centre);
                    if (_plugin.TextureAt(cam, pos) is { } hit && hit.Id == id)
                    {
                        (picked, pickAt) = (id, pos);
                        break;
                    }
                }
            }
            if (picked != null)
                break;
        }
        if (picked == null) { cam.GlobalTransform = camWas; fail("no surface of the map to pick a texture from"); yield break; }
        int queued0 = tab.Queue.Count;
        _plugin._Forward3DGuiInput(cam, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, AltPressed = true, Position = pickAt });
        _plugin._Forward3DGuiInput(cam, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, AltPressed = true, Position = pickAt });
        var altPick = _plugin.LastPick ?? "";
        if (!tab.Queue.Contains(picked) || tab.Queue.Count != queued0 + 1 || !altPick.Contains("queued"))
        { cam.GlobalTransform = camWas; fail($"Alt+click: {altPick}; the queue {queued0} -> {tab.Queue.Count}"); yield break; }
        // Pick texture on, Shift+click: the segment's every texture
        _plugin.PickMode = true;
        int r = _plugin._Forward3DGuiInput(cam, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ShiftPressed = true, Position = pickAt });
        _plugin._Forward3DGuiInput(cam, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, ShiftPressed = true, Position = pickAt });
        var modePick = _plugin.LastPick ?? "";
        _plugin.PickMode = false;
        // a drag with Alt is not a pick (Godot's Alt+drag moves the selection)
        var lastWas = _plugin.LastPick;
        _plugin._Forward3DGuiInput(cam, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, AltPressed = true, Position = pickAt });
        _plugin._Forward3DGuiInput(cam, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, AltPressed = true, Position = pickAt + new Vector2(30, 0) });
        bool dragQuiet = ReferenceEquals(_plugin.LastPick, lastWas);
        if (shots)
        {
            EditorInterface.Singleton.SetMainScreenEditor("3D");
            for (int i = 0; i < 10; i++) yield return null;
            Window($"{shotDir}/r2b5-pick-in-map.png");
            _plugin.ShowMaterials();
        }
        cam.GlobalTransform = camWas;
        if (r != (int)EditorPlugin.AfterGuiInput.Stop || !modePick.StartsWith("Materials:") || !dragQuiet)
        { fail($"Pick texture: {modePick} (consumed {r}); an Alt+drag picked: {!dragQuiet}"); yield break; }
        notes.Add($"picked in the map: Alt+click queued {RemasterTextures.ShortName(picked)} ({altPick}); Pick texture with Shift: {modePick}; an Alt+drag picks nothing");

        // ---- In map ----
        tab.Open(ids[0]);
        tab.SelectStep(MaterialStep.Albedo);
        foreach (var w in Idle("in map's texture")) yield return w;
        if (f != null) { fail(f); yield break; }
        tab.Preview.GrabFocus();
        Press(Godot.Key.M);
        for (int i = 0; i < 3; i++) yield return null;
        if (!tab.InMapShown || tab.Preview.MapScene is not { } scene || scene.Live.Count == 0)
        { fail($"M: in map {tab.InMapShown} ({tab.InMapNote}; {tab.LastShortcut})"); yield break; }
        var (lm, ls, _) = scene.Live[0];
        var liveMat = lm.GetSurfaceOverrideMaterial(ls) as ShaderMaterial;
        bool liveTex = liveMat != null && ReferenceEquals(liveMat.GetShaderParameter("albedo_tex").AsGodotObject(), tab.MapTexture(MaterialStep.Albedo))
            && PartIds.GetString(liveMat, RemasterLook.LitTextureMeta) == ids[0];
        var (cm, cs, cc) = scene.Left[0];
        tab.SetCompareForCheck(1);
        bool leftClassic = ReferenceEquals(cm.GetSurfaceOverrideMaterial(cs), cc);
        var piece = tab.Preview.PiecePoint();
        var pieceUv = piece is { } pp ? tab.Preview.UvAt(pp) : null;
        if (!liveTex || !leftClassic || tab.Preview.Light != PreviewLight.Map || pieceUv == null)
        { fail($"in map: the live lit material {liveTex}, the classic look on the left {leftClassic}, light {tab.Preview.Light}, a pick on the piece {pieceUv} at {piece}"); yield break; }
        var inMapNote = $"in map: {tab.InMapNote}; a pick on the piece at UV {pieceUv?.X:0.00},{pieceUv?.Y:0.00}; the live material (the lit shader over the tab's maps) on the material's side, the classic look on the compare's, the map's light";
        if (shots)
        {
            tab.SetLightForCheck(PreviewLight.Map, 0);
            for (int i = 0; i < 30; i++) yield return null;
            var before = tab.Preview.CaptureMaterial();
            var leftSide = tab.Preview.CaptureCompare();
            Window($"{shotDir}/r2b5-in-map.png");
            tab.Preview.Capture()?.SavePng($"{shotDir}/r2b5-in-map-wipe.png");
            // where the two sides differ is the texture's surfaces (the rest is the same on both)
            var region = before != null && leftSide != null ? Region(leftSide, before) : null;
            int regionPx = region?.Count(x => x) ?? 0;
            double wipe = before != null && leftSide != null ? MeanDiff((Image)leftSide.Duplicate(), (Image)before.Duplicate(), region) : -1;
            tab.Drag("Delight", 1.0);
            tab.Drag("AlbedoMatchClassic", 0.0);
            tab.Drag("NormalStrength", 16.0);
            // metal darkens even where the beach's light clips (the albedo's changes do not show there)
            tab.SetSetting("Metallic", 1f);
            foreach (var w in Idle("in map's change")) yield return w;
            if (f != null) { fail(f); yield break; }
            for (int i = 0; i < 20; i++) yield return null;
            var after = tab.Preview.CaptureMaterial();
            double d = before != null && after != null ? MeanDiff(before, after, region) : -1;
            after?.SavePng($"{shotDir}/r2b5-in-map-changed.png");
            if (d < 2 || wipe < 1 || regionPx < 2000) { fail($"in map's pixels: the texture covers {regionPx} px; a setting changed them by {d:0.###}, classic against live {wipe:0.###}"); yield break; }
            inMapNote += $"; the texture on {regionPx} px of the view: its pixels changed with the de-lighting, the normals' strength and the metal by {d:0.##} of 255, classic against live {wipe:0.##}";
            global.Undo();
            global.Undo();
            global.Undo();
            global.Undo();
        }
        Press(Godot.Key.M);
        if (tab.InMapShown) { fail("M did not leave the In map view"); yield break; }
        // a texture the open map does not draw: its map loaded on demand
        // (a Besaid map's first)
        var other = _plugin.Uses.ByLevel.Where(kv => kv.Key != Key).OrderBy(kv => kv.Key.StartsWith(Key[..2], System.StringComparison.Ordinal) ? 0 : 1).ThenBy(kv => kv.Key, System.StringComparer.Ordinal)
            .SelectMany(kv => kv.Value).FirstOrDefault(id => !_plugin.Uses.Uses(id).Any(u => u.Level == Key));
        if (other != null)
        {
            tab.Add([other]);
            tab.Open(other);
            Press(Godot.Key.M);
            double tm = _t;
            while (tab.Preview.MapScene == null || !tab.InMapShown)
            {
                if (_t - tm > 180) { fail($"in map on demand: {tab.InMapNote}"); yield break; }
                yield return null;
            }
            inMapNote += $"; {RemasterTextures.ShortName(other)} (not on the open map): {tab.InMapNote} in {_t - tm:0.0} s";
            Press(Godot.Key.M);
            tab.RemoveFromQueue(other);
            tab.Open(ids[0]);
        }
        notes.Add(inMapNote);

        // ---- ComfyUI through a fake server ----
        using (var fake = new FakeComfy(delayPolls: 40))
        {
            ComfyUpscaler.UrlOverride = fake.Url;
            ComfyUpscaler.ModelOverride = "check-x4.pth";
            ComfyUpscaler.LicenceOverride = "MIT (the check's fake model)";
            ComfyUpscaler.PollMs = 100;
            // the fake model's answers only: none left from an earlier run
            var made = new List<string>();
            if (tab.ComfyCacheFile(2) is { } own)
            {
                made.Add(own);
                if (FileAccess.FileExists(own))
                    DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(own));
            }
            MaterialsTab.ClearComfyMemory();
            string? cfail = null;
            foreach (var w in Comfy(tab, global, fake, made, x => cfail = x)) yield return w;
            ComfyUpscaler.UrlOverride = ComfyUpscaler.ModelOverride = ComfyUpscaler.LicenceOverride = null;
            ComfyUpscaler.PollMs = 1000;
            foreach (var file in made.Distinct())
                if (FileAccess.FileExists(file))
                    DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(file));
            MaterialsTab.ClearComfyMemory();
            if (cfail != null) { fail(cfail); yield break; }
        }
        notes.Add(_comfyNote);
        foreach (var w in Idle("phase 5's end")) yield return w;
        if (f != null) { fail(f); yield break; }
        GD.Print($"ffx editortest: materials phase 5: {string.Join("; ", notes)}");
        _seen.Add($"materials phase 5: {string.Join("; ", notes)}");
    }

    private string _comfyNote = "";

    /// <summary>The pixels where two pictures of one size differ (by more than 6 of 255 summed).</summary>
    private static bool[]? Region(Image a, Image b)
    {
        if (a.GetSize() != b.GetSize())
            return null;
        a.Convert(Image.Format.Rgba8);
        b.Convert(Image.Format.Rgba8);
        var x = a.GetData();
        var y = b.GetData();
        var r = new bool[x.Length / 4];
        for (int i = 0; i < r.Length; i++)
            r[i] = System.Math.Abs(x[i * 4] - y[i * 4]) + System.Math.Abs(x[i * 4 + 1] - y[i * 4 + 1]) + System.Math.Abs(x[i * 4 + 2] - y[i * 4 + 2]) > 6;
        return r;
    }

    /// <summary>The mean absolute difference of two pictures' RGB inside <paramref name="region"/> (0..255; -1: different sizes or none).</summary>
    private static double MeanDiff(Image a, Image b, bool[]? region)
    {
        if (region == null)
            return MeanDiff(a, b);
        if (a.GetSize() != b.GetSize())
            return -1;
        a.Convert(Image.Format.Rgba8);
        b.Convert(Image.Format.Rgba8);
        var x = a.GetData();
        var y = b.GetData();
        long sum = 0;
        int n = 0;
        for (int i = 0; i < region.Length; i++)
            if (region[i])
            {
                sum += System.Math.Abs(x[i * 4] - y[i * 4]) + System.Math.Abs(x[i * 4 + 1] - y[i * 4 + 1]) + System.Math.Abs(x[i * 4 + 2] - y[i * 4 + 2]);
                n++;
            }
        return n == 0 ? -1 : sum / (n * 3.0);
    }

    private IEnumerable<string?> Comfy(MaterialsTab tab, UndoRedo global, FakeComfy fake, List<string> made, System.Action<string> fail)
    {
        string? f = null;
        IEnumerable<string?> Until(string what, System.Func<bool> ok, double limit = 60)
        {
            double t0 = _t;
            while (!ok())
            {
                if (_t - t0 > limit) { f = $"ComfyUI: {what} ({tab.ComfyState})"; yield break; }
                yield return null;
            }
        }
        var cacheDir = ProjectSettings.GlobalizePath(MaterialsTab.ComfyCacheDir);
        var filesBefore = DirAccess.DirExistsAbsolute(cacheDir) ? DirAccess.GetFilesAt(cacheDir).ToHashSet() : new HashSet<string>();
        tab.SelectStep(MaterialStep.Source);
        if (tab.Recipe.Upscale != 2)
            tab.SetSetting("Upscale", 2);
        foreach (var w in Until("the Lanczos source", () => tab.Idle)) yield return w;
        if (f != null) { fail(f); yield break; }
        var lanczos = tab.Pixels(MaterialStep.Source)!;
        int runs0 = tab.ComfyRuns;
        tab.SetSetting("Upscaler", MaterialsTab.ComfyUi);
        // started, then cancelled while ComfyUI "runs"
        var stages = new HashSet<string>();
        double maxDone = 0;
        foreach (var w in Until("the run did not reach the queue", () =>
        {
            maxDone = System.Math.Max(maxDone, tab.ComfyDone);
            stages.Add(tab.ComfyState.Split(':').Last().Split(',')[0].Trim());
            return tab.ComfyDone >= 0.2;
        })) yield return w;
        if (f != null) { fail(f); yield break; }
        tab.CancelComfy();
        foreach (var w in Until("the cancel did not end the run", () => tab.ComfyDone < 0)) yield return w;
        if (f != null) { fail(f); yield break; }
        if (!tab.ComfyState.Contains("cancelled") || fake.Views != 0 || tab.ComfySourceInUse || tab.ComfyRuns != runs0 + 1)
        { fail($"ComfyUI cancel: {tab.ComfyState}; views {fake.Views}, in use {tab.ComfySourceInUse}, runs {tab.ComfyRuns - runs0}"); yield break; }
        // run again: the answer cached and made the source
        fake.DelayPolls = 6;
        tab.RunComfyAgain();
        foreach (var w in Until("the second run did not finish", () =>
        {
            maxDone = System.Math.Max(maxDone, tab.ComfyDone);
            stages.Add(tab.ComfyState.Split(':').Last().Split(',')[0].Trim());
            return tab.ComfyState.Contains("done") || tab.ComfySourceInUse;
        })) yield return w;
        if (f != null) { fail(f); yield break; }
        foreach (var w in Until("the maps with ComfyUI's source", () => tab.Idle && tab.ComfySourceInUse)) yield return w;
        if (f != null) { fail(f); yield break; }
        var comfy = tab.Pixels(MaterialStep.Source)!;
        double diff = 0;
        for (int i = 0; i < comfy.Rgba.Length && comfy.Rgba.Length == lanczos.Rgba.Length; i++)
            if (i % 4 != 3) diff += System.Math.Abs(comfy.Rgba[i] - lanczos.Rgba[i]);
        diff /= comfy.Rgba.Length * 0.75;
        var newFiles = DirAccess.DirExistsAbsolute(cacheDir) ? DirAccess.GetFilesAt(cacheDir).Where(x => !filesBefore.Contains(x)).ToList() : new List<string>();
        made.AddRange(newFiles.Select(x => $"{MaterialsTab.ComfyCacheDir}/{x}"));
        if (fake.Prompts != 2 || fake.Views != 1 || fake.LastModel != "check-x4.pth" || !Mathf.IsEqualApprox((float)fake.LastScale, 0.5f) || newFiles.Count != 1 || diff < 2)
        { fail($"ComfyUI's answer: prompts {fake.Prompts}, views {fake.Views}, model {fake.LastModel}, scale {fake.LastScale}, cache files {newFiles.Count}, the source changed by {diff:0.##}"); yield break; }
        // cached: Lanczos and back, then from the cache folder alone (memory forgotten): no request
        int prompts = fake.Prompts;
        tab.SetSetting("Upscaler", MaterialsTab.Lanczos3);
        foreach (var w in Until("Lanczos again", () => tab.Idle && !tab.ComfySourceInUse)) yield return w;
        if (f != null) { fail(f); yield break; }
        MaterialsTab.ClearComfyMemory();
        tab.SetSetting("Upscaler", MaterialsTab.ComfyUi);
        foreach (var w in Until("the cached answer", () => tab.Idle && tab.ComfySourceInUse)) yield return w;
        if (f != null) { fail(f); yield break; }
        if (fake.Prompts != prompts || tab.ComfyRuns != runs0 + 2) { fail($"ComfyUI's cache: {fake.Prompts - prompts} more prompt(s), runs {tab.ComfyRuns - runs0}"); yield break; }
        // saved: the Source note names the tool, the model and its licence
        var id = tab.Current!;
        if (tab.Save() is { } err) { fail(err); yield break; }
        foreach (var w in Until("the save", () => !tab.Saving, 120)) yield return w;
        if (f != null) { fail(f); yield break; }
        var source = TextureStore.Find(id)?.Source ?? "";
        if (!source.Contains("upscaled x2 by ComfyUI (check-x4.pth; licence: MIT (the check's fake model))"))
        { fail($"the Source after the save: '{source}'"); yield break; }
        tab.SetSetting("Upscaler", MaterialsTab.Lanczos3);
        foreach (var w in Until("Lanczos at the end", () => tab.Idle)) yield return w;
        if (f != null) { fail(f); yield break; }
        _comfyNote = $"ComfyUI through the fake server ({fake.Url}): progress {string.Join(" > ", stages.Where(x => x.Length > 0))} (to {maxDone * 100:0} %), cancelled (no answer fetched), run again: "
            + $"the answer (x{fake.LastScale * 4:0}, {fake.LastUploadSize.X} px uploaded) cached and the source (it changed the source map by {diff:0.#} of 255), Lanczos and back and from the cache folder with no request, "
            + $"the Source note '{source.Split('\n').Last()}'; {fake.Uploads} uploads, {fake.Prompts} prompts, {fake.Queues} queue and {fake.Histories} history polls, {fake.Views} download";
    }
}
#endif
