#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The remaster editor's check (docs/remaster-editor.md, "Checks"): run in the editor, headless
/// or under Xvfb, with <c>-- --remaster-editor-test --remaster-maps=DIR</c> (the overrides go to
/// DIR, not the project). It drives the plugin through the code the dock and the inspector use:
/// the archive and the catalogue (search, languages, the changed filter), Besaid beach opened
/// (every part a node by ID, segments apart, the classic materials), then a move (Godot's
/// undoable property change, as a gizmo makes), a hidden segment, a removed instance, a part
/// replaced by a scene and a box added at a part, one undo step each (undo and redo checked),
/// the save (overrides.tres and additions.tscn, a second save byte for byte the same), the scene
/// file without classic content, the map opened again with the same edits, the status (undo
/// in the global history), the play plan. It writes <c>DIR/expect.json</c> for the game's
/// <c>--selftest-saved-overrides</c> and, with <c>--editor-shot=FILE.png</c> under a renderer, a
/// picture of the editor's 3D view. Exits the editor: 0 on PASS, 1 on FAIL.
/// </summary>
public sealed class EditorSelfTest
{
    public const string Level = "/map/bsil/bsil00/";
    public const string Key = "bsil00";
    public const string Moved = "#FBXASC0492_0/*";
    public const string Hidden = "#polySurface32/0";
    public const string Removed = "#FBXASC0494_1/*";
    public const string Replaced = "#polySurface4/0";
    public const string Anchor = "#polySurface34/*";
    public const string Replacement = "res://remaster/examples/bsil00/replacement.tscn";
    public static readonly Vector3 MoveBy = new(0, 20, 0);
    public static readonly Vector3 BoxAbove = new(0, 30, 0);

    private enum Phase { Archive, Open, Edit, Save, Reopen, Status, Uses, Textures, Batch, Imported, Lighting, LightingReopen, Materials, MaterialsImported, GameData, Shot, Map2D, Shot2D, Done }

    /// <summary>R5's 2D-layer map: Kilika's inn (developer save 14).</summary>
    public const string Key2D = "ptkl14";

    /// <summary>The bird the capture places (the game flies it; the file has it at the origin).</summary>
    public const string Bird = "#FBXASC0498_0Shape/*";

    /// <summary>The game's key light as the check's stand-in look says it travels (Besaid beach's, as the game shows it).</summary>
    public static readonly Vector3 LookKey = new Vector3(0.04f, -0.54f, -0.84f).Normalized();
    /// <summary>Where the check turns the key light to.</summary>
    public static readonly Vector3 TurnedKey = new Vector3(0.5f, -0.6f, -0.62f).Normalized();

    private readonly RemasterPlugin _plugin;
    private readonly double _timeout = GameSettings.Number("editor-test-timeout", 240);
    private readonly List<string> _seen = new();
    private Phase _phase = Phase.Archive;
    private double _t, _phaseT;
    private int _frames;
    private bool _finished;
    private readonly Dictionary<string, Transform3D> _before = new();

    /// <summary>
    /// <c>--remaster-editor-roundtrip</c>: DIR already holds an override set for Besaid beach
    /// (e.g. the R1a example's files): opened, its edits must show, and saved without an edit it
    /// must say the same.
    /// </summary>
    private readonly bool _roundTrip = GameSettings.Flag("remaster-editor-roundtrip");
    private Transform3D _boxWorld;

    public EditorSelfTest(RemasterPlugin plugin)
    {
        _plugin = plugin;
        GD.Print($"ffx editortest: start (overrides into {MapStore.Dir})");
        if (MapStore.Dir == RemasterOverrides.BaseDir)
            Finish(false, "--remaster-maps=DIR is required (the check must not write the project's overrides)");
        // the game's look of Besaid beach as a stand-in (the editor runs no game): with --remaster-looks=DIR only
        if (GameSettings.Arg("remaster-looks") is { Length: > 0 })
        {
            var look = new GdFfx.Api.Look
            {
                Valid = true,
                CharacterAmbient = new Vector3(0.34f, 0.34f, 0.45f),
                Background = new Vector3(0.306f, 0.357f, 0.537f),
                FogColour = new Vector3(0.453f, 0.58f, 0.776f),
                FogNear = 62, FogFar = 9022, FogCurve = 0.8f, FogLimit = 0.149f,
            };
            // the characters' shader lights by sign x L: the light travels -sign x L
            look.Lights.Add(new GdFfx.Api.Light { Direction = -LookKey * ClassicMaterials.LightSign, Colour = new Vector3(0.93f, 0.99f, 1.05f), Level = 1.3f });
            LookCache.Write(Level, look);
        }
    }

    private string Full(string id) => PartIds.Full(id, PartIds.ModelIdOf(Level));

    /// <summary>Inside <see cref="Step"/>: a save's progress dialog runs the editor's frames (and so this) while it works.</summary>
    private bool _inStep;

    public void Step(double delta)
    {
        if (_finished || _inStep)
            return;
        _inStep = true;
        try
        {
            StepOnce(delta);
        }
        finally
        {
            _inStep = false;
        }
    }

    private void StepOnce(double delta)
    {
        _t += delta;
        _phaseT += delta;
        _frames++;
        if (_t > _timeout)
        {
            Finish(false, $"timed out in {_phase}");
            return;
        }
        try
        {
            if (Run() is { } fail)
                Finish(false, $"{_phase}: {fail}");
        }
        catch (System.Exception e)
        {
            Finish(false, $"{_phase}: {e}");
        }
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx editortest: {_phase} -> {p} ({_t:F1} s)");
        _phase = p;
        _phaseT = 0;
        _frames = 0;
    }

    private string? Run()
    {
        var s = _plugin.Session;
        var dock = _plugin.Dock!;
        switch (_phase)
        {
            case Phase.Archive:
            {
                if (!s.IsOpen)
                    return _phaseT > 20 ? $"the archive did not open: {s.Error}" : null;
                var cat = s.Catalogue;
                if (cat == null || cat.Levels.Count < 300 || cat.Areas.Count < 20 || s.DevSaves.Length < 100)
                    return $"catalogue {cat?.Levels.Count} levels, {cat?.Areas.Count} areas, {s.DevSaves.Length} developer saves";
                if (Search(dock, "bsil", "us") is { } e1)
                    return e1;
                if (!dock.Listed.Any(e => e.Key == Key))
                    return "search 'bsil' does not list bsil00";
                if (Search(dock, "Besaid", "us") is { } e2 || dock.Listed.All(e => e.Area != cat.Find(Key)!.Area))
                    return "search 'Besaid' lists no Besaid map";
                // the names in Japanese; the event's name finds the level too
                if (Search(dock, "bsmm0000", "jp") is { } e3 || dock.Listed.Count != 1 || dock.Listed[0].Key != Key)
                    return $"search 'bsmm0000': {string.Join(", ", dock.Listed.Select(e => e.Key))}";
                var jp = cat.Find(Key)!.Title("jp");
                SetChanged(dock, true);
                Search(dock, "", "us");
                var changed = dock.Listed.Count;
                if (dock.Listed.Any(e => !DockState.Of(e).Changed))
                    return "the changed filter lists an unchanged map";
                SetChanged(dock, false);
                Search(dock, "", "us");
                _seen.Add($"catalogue {cat.Levels.Count} maps in {cat.Areas.Count} areas ({dock.Listed.Count} listed, {changed} changed), "
                    + $"{s.DevSaves.Length} developer saves; bsil00 \"{cat.Find(Key)!.Title("us")}\" / \"{jp}\"");
                dock.Select(cat.Find(Key)!);
                if (dock.OpenSelected() is { } err)
                    return err;
                Next(Phase.Open);
                return null;
            }
            case Phase.Open:
            {
                if (_plugin.Current is not { Built: true } root)
                    return _phaseT > 150 ? $"the map did not open ({_plugin.Current?.Status ?? "no edit scene"})" : null;
                if (root.Map == null)
                    return root.Status;
                if (_roundTrip)
                {
                    if (RoundTrip(root) is { } rt)
                        return rt;
                    // R5: the alignment check on the example's additions and replacement, and a misplaced test box
                    if (AlignmentStep(root, "R1a's example") is { } al)
                        return al;
                    Next(Phase.Done);
                    return null;
                }
                if (CheckBuilt(root) is { } bad)
                    return bad;
                Next(Phase.Edit);
                return null;
            }
            case Phase.Edit:
                if (_frames < 3)
                    return null;
                if (Edit(_plugin.Current!) is { } editFail)
                    return editFail;
                Next(Phase.Save);
                return null;
            case Phase.Save:
                if (Save(_plugin.Current!) is { } saveFail)
                    return saveFail;
                Next(Phase.Reopen);
                return null;
            case Phase.Reopen:
            {
                if (_frames == 1)
                {
                    EditorInterface.Singleton.ReloadSceneFromPath(MapEditRoot.EditScenePath(Key));
                    return null;
                }
                if (_plugin.Current is not { Built: true } root)
                    return _phaseT > 60 ? "the map did not open again" : null;
                if (Reopened(root) is { } bad)
                    return bad;
                Next(Phase.Status);
                return null;
            }
            case Phase.Status:
                if (Status() is { } statusFail)
                    return statusFail;
                if (Play() is { } playFail)
                    return playFail;
                Next(Phase.Uses);
                return null;
            case Phase.Uses:
                if (!_plugin.Uses.Ready)
                    return _phaseT > 200 ? $"the where-used index is not ready ({_plugin.Uses.Progress.Done} / {_plugin.Uses.Progress.Total} maps)" : null;
                if (CheckUses() is { } usesFail)
                    return usesFail;
                Next(Phase.Textures);
                return null;
            case Phase.Textures:
                if (_frames < 2)
                    return null;
                if (Textures(_plugin.Current!) is { } texFail)
                    return texFail;
                Next(Phase.Batch);
                return null;
            case Phase.Batch:
                if (Batch(_plugin.Current!) is { } batchFail)
                    return batchFail;
                if (_batchDone)
                    Next(Phase.Imported);
                return null;
            case Phase.Imported:
            {
                // Godot's own scan and import of the new PNGs (a new folder each texture)
                if (EditorInterface.Singleton.GetResourceFilesystem().IsScanning() || TextureStore.Importing || _frames < 3)
                    return _phaseT > 120 ? "the import did not finish" : null;
                if (!TextureStore.AllImported(out var missing))
                    return _phaseT > 120 ? $"not imported: {missing}" : null;
                if (CheckImported(_plugin.Current!) is { } impFail)
                    return impFail;
                _seen.Add($"every PNG and material of the index imported ({_phaseT:0.0} s after the last edit)");
                Next(Phase.Lighting);
                return null;
            }
            case Phase.Lighting:
                if (_frames < 3)
                    return null;
                if (Lighting(_plugin.Current!) is { } lightFail)
                    return lightFail;
                Next(Phase.LightingReopen);
                return null;
            case Phase.LightingReopen:
            {
                if (_frames == 1)
                {
                    EditorInterface.Singleton.ReloadSceneFromPath(MapEditRoot.EditScenePath(Key));
                    return null;
                }
                if (_plugin.Current is not { Built: true, Lighting: not null } root || _frames < 4)
                    return _phaseT > 60 ? "the map did not open again" : null;
                // the map's own frame switches the preview on: a map built in this frame has not had one yet
                if (!root.Lighting.Rig.Active && _builtWait++ < 600)
                    return null;
                if (LightingReopened(root) is { } bad)
                    return bad;
                Next(Phase.Materials);
                return null;
            }
            case Phase.Materials:
            {
                _matFlow ??= MaterialsFlow(_plugin.Current!);
                if (_matFlow.MoveNext())
                    return _matFlow.Current;
                Next(Phase.MaterialsImported);
                return null;
            }
            case Phase.MaterialsImported:
            {
                if (EditorInterface.Singleton.GetResourceFilesystem().IsScanning() || TextureStore.Importing || _frames < 3)
                    return _phaseT > 120 ? "the import did not finish" : null;
                if (!TextureStore.AllImported(out var missing))
                    return _phaseT > 120 ? $"not imported: {missing}" : null;
                Next(Phase.GameData);
                return null;
            }
            case Phase.GameData:
            {
                if (_frames < 3)
                    return null;
                var root = _plugin.Current!;
                if (GameData(root) is { } gd)
                    return gd;
                if (AlignmentStep(root, "the check's edits") is { } al)
                    return al;
                Next(Shots ? Phase.Shot : Phase.Map2D);
                return null;
            }
            case Phase.Shot:
                return Shot();
            case Phase.Map2D:
            {
                if (_frames == 1)
                {
                    var e = s.Catalogue!.Find(Key2D);
                    if (e == null)
                        return $"no {Key2D} in the catalogue";
                    dock.Select(e);
                    return dock.OpenSelected();
                }
                if (_plugin.Current is not { Built: true } root || root.Key != Key2D)
                    return _phaseT > 60 ? $"{Key2D} did not open ({_plugin.Current?.Status ?? "no edit scene"})" : null;
                if (Map2D(root) is { } m2)
                    return m2;
                Next(Shots ? Phase.Shot2D : Phase.Done);
                return null;
            }
            case Phase.Shot2D:
                return Shot2D();
            case Phase.Done:
                Finish(true, string.Join("; ", _seen));
                return null;
        }
        return null;
    }

    private static string? Search(MapBrowserDock dock, string q, string lang)
    {
        dock.SetFilter(q, lang, null);
        foreach (var e in dock.Listed)
            if (!MapBrowserDock.Matches(e, q))
                return $"'{q}' lists {e.Key}, which does not match";
        return null;
    }

    private static void SetChanged(MapBrowserDock dock, bool on) => dock.SetFilter(null, null, on);

    private string? CheckBuilt(MapEditRoot root)
    {
        int instances = root.Order.Count(p => p.Kind == PartKind.Instance);
        int segments = root.Order.Count(p => p.Kind == PartKind.Segment);
        if (instances < 73 || segments < 20)
            return $"{instances} instances, {segments} segments as nodes";
        foreach (var id in new[] { Moved, Hidden, Removed, Replaced, Anchor })
            if (!root.Parts.ContainsKey(Full(id)))
                return $"{id} is not a node of the scene";
        foreach (var p in root.Order)
            if (PartIds.Of(p.Node) != p.Id || p.Node.Owner != root)
                return $"{p.Id}: not tagged with its ID or not in the scene";
        var seg = root.Parts[Full(Hidden)];
        if (seg.Node is not MeshInstance3D { Mesh: { } m } mi || m.GetSurfaceCount() != 1 || seg.Parent?.Node is not MeshInstance3D { Mesh: null })
            return $"{Hidden}: not a node of its own beside its instance (which keeps no mesh)";
        if (mi.GetSurfaceOverrideMaterial(0) is not ShaderMaterial)
            return $"{Hidden}: no classic material";
        if (root.Order.Any(p => root.MarksOf(p) != PartMarks.None))
            return $"edits on a map without overrides: {root.Order.First(p => root.MarksOf(p) != PartMarks.None).Id}";
        _seen.Add($"bsil00 opened: {root.Order.Count} parts by ID ({instances} instances, {segments} segments as their own nodes)");
        return null;
    }

    private string? RoundTrip(MapEditRoot root)
    {
        var path = MapStore.PathOf(Key);
        if (ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) is not MapOverrides before)
            return $"{path}: no overrides to open";
        var addBefore = Additions(MapStore.AdditionsPathOf(Key));
        // the file's edits shown as edits
        foreach (var p in before.Parts)
        {
            if (!root.Parts.TryGetValue(Full(p.Id), out var info))
                return $"{p.Id}: not on the map";
            var want = PartMarks.None;
            if (p.Remove == RemoveOp.Remove)
                want |= PartMarks.Removed;
            if (p.Visibility == VisibilityOp.Hidden)
                want |= PartMarks.Hidden;
            if (p.TransformMode is TransformOp.Offset or TransformOp.Absolute && p.Remove != RemoveOp.Remove)
                want |= PartMarks.Moved;
            if (p.Replace == ReplaceOp.Scene)
                want |= PartMarks.Replaced;
            if (p.Note.Length > 0)
                want |= PartMarks.Noted;
            if (root.MarksOf(info) != want)
                return $"{p.Id}: shown as {root.MarksOf(info)}, the file says {want}";
        }
        var added = root.Added();
        if (added.Count != addBefore.Count)
            return $"{added.Count} added node(s) shown, the file has {addBefore.Count}";
        foreach (var (node, anchor) in added)
            if (!addBefore.TryGetValue(node.Name, out var a) || (anchor?.Short(root.Model) ?? "") != a.Anchor)
                return $"{node.Name}: anchored to {anchor?.Short(root.Model)}";
        int edits = root.Order.Count(p => root.MarksOf(p) != PartMarks.None);
        // saved without an edit: the same entries and additions
        if (_plugin.SaveMap(root) is not { Ok: true })
            return "the save failed";
        if (ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) is not MapOverrides after)
            return "the saved file does not read back";
        static string Say(PartOverride p) =>
            $"{p.Id} {p.Remove} {p.Visibility} {p.TransformMode} {p.Replace} {p.Scene?.ResourcePath} {p.Note}";
        var a0 = before.Parts.OrderBy(p => p.Id, System.StringComparer.Ordinal).ToList();
        var a1 = after.Parts.OrderBy(p => p.Id, System.StringComparer.Ordinal).ToList();
        if (a0.Count != a1.Count)
            return $"{a1.Count} entries saved, the file had {a0.Count}";
        for (int i = 0; i < a0.Count; i++)
            if (Say(a0[i]) != Say(a1[i]) || !a0[i].Transform.IsEqualApprox(a1[i].Transform))
                return $"saved {Say(a1[i])} {a1[i].Transform}, the file had {Say(a0[i])} {a0[i].Transform}";
        if (after.Status != before.Status || after.Notes != before.Notes || after.RenderPath != before.RenderPath)
            return "the status, notes or render path changed";
        var addAfter = Additions(MapStore.AdditionsPathOf(Key));
        foreach (var (name, (anchor, t)) in addBefore)
            if (!addAfter.TryGetValue(name, out var x) || x.Anchor != anchor || !x.Place.IsEqualApprox(t))
                return $"addition {name}: saved {x}, the file had {anchor} {t}";
        _seen.Add($"round trip: {before.Parts.Count} entries ({edits} parts shown edited) and {addBefore.Count} additions opened and saved back the same");
        return null;
    }

    /// <summary>An additions scene's nodes: name, anchor, place.</summary>
    private static Dictionary<string, (string Anchor, Transform3D Place)> Additions(string path)
    {
        var r = new Dictionary<string, (string, Transform3D)>();
        if ((ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) as PackedScene)?.Instantiate() is not { } root)
            return r;
        for (int i = 0; i < root.GetChildCount(); i++)
            if (root.GetChild(i) is Node3D n)
                r[n.Name] = (PartIds.GetString(n, "ffx_anchor") ?? "", n.Transform);
        root.Free();
        return r;
    }

    private EditorUndoRedoManager Undo => _plugin.GetUndoRedo();

    private UndoRedo SceneHistory(Node root) => Undo.GetHistoryUndoRedo(Undo.GetObjectHistoryId(root));

    /// <summary>Runs <paramref name="edit"/> and checks it made exactly one undo step.</summary>
    private string? OneStep(MapEditRoot root, string what, System.Action edit)
    {
        var h = SceneHistory(root);
        var v = h.GetVersion();
        edit();
        if (h.GetVersion() != v + 1)
            return $"{what}: {h.GetVersion() - v} undo step(s), not one";
        return null;
    }

    private string? Edit(MapEditRoot root)
    {
        var moved = root.Parts[Full(Moved)];
        var hidden = root.Parts[Full(Hidden)];
        var removed = root.Parts[Full(Removed)];
        var replaced = root.Parts[Full(Replaced)];
        var anchor = root.Parts[Full(Anchor)];
        // a move as the gizmo makes it: an undoable change of the node's transform
        var to = moved.Node.Position + moved.Node.GetParentNode3D()!.GlobalBasis.Inverse() * MoveBy;
        var fail = OneStep(root, "move", () =>
        {
            Undo.CreateAction("Move", UndoRedo.MergeMode.Disable, root);
            Undo.AddDoProperty(moved.Node, Node3D.PropertyName.Position, to);
            Undo.AddUndoProperty(moved.Node, Node3D.PropertyName.Position, moved.Node.Position);
            Undo.CommitAction();
        });
        // hiding, as the scene dock's eye does
        fail ??= OneStep(root, "hide", () =>
        {
            Undo.CreateAction("Toggle Visible", UndoRedo.MergeMode.Disable, root);
            Undo.AddDoProperty(hidden.Node, Node3D.PropertyName.Visible, false);
            Undo.AddUndoProperty(hidden.Node, Node3D.PropertyName.Visible, true);
            Undo.CommitAction();
        });
        fail ??= OneStep(root, "remove", () => _plugin.RemovePart(root, removed));
        fail ??= OneStep(root, "replace", () => _plugin.ReplacePart(root, replaced, Replacement));
        var box = RemasterPlugin.MakePrimitive("Box");
        box.Name = "EditorBox";
        var at = RemasterPlugin.PlaceAt(root, anchor);
        _boxWorld = new Transform3D(at.Basis, at.Origin + BoxAbove);
        fail ??= OneStep(root, "add", () => _plugin.AddNode(root, box, anchor, _boxWorld));
        if (fail != null)
            return fail;
        // the marks
        var want = new (PartInfo P, PartMarks M)[]
        {
            (moved, PartMarks.Moved), (hidden, PartMarks.Hidden), (removed, PartMarks.Removed), (replaced, PartMarks.Replaced),
        };
        foreach (var (p, m) in want)
            if (root.MarksOf(p) != m)
                return $"{p.Short(root.Model)}: marked {root.MarksOf(p)}, not {m}";
        if (root.Added() is not [{ Node: var added, Anchor: var a }] || added != box || a != anchor)
            return $"added: {string.Join(", ", root.Added().Select(x => x.Node.Name))}";
        if (replaced.Preview is not { } preview || !preview.IsInsideTree() || preview.Owner != null
            || replaced.Node is not MeshInstance3D rmi || !MapOverrideRuntime.IsNoDraw(rmi.GetSurfaceOverrideMaterial(0)))
            return $"{Replaced}: no preview of its replacement, or the part still draws";
        // undo and redo the add, the replacement and the removal
        var h = SceneHistory(root);
        h.Undo();
        if (box.IsInsideTree())
            return "undo did not take the box out";
        h.Undo();
        if (replaced.Replacement.Length > 0 || replaced.Preview != null || MapOverrideRuntime.IsNoDraw(rmi.GetSurfaceOverrideMaterial(0)))
            return "undo did not take the replacement back";
        h.Undo();
        if (root.IsRemoved(removed) || removed.Node.Owner != root)
            return "undo did not bring the removed part back";
        h.Redo();
        h.Redo();
        h.Redo();
        if (!box.IsInsideTree() || box.Owner != root || !root.IsRemoved(removed) || replaced.Replacement != Replacement)
            return "redo did not do the removal, the replacement and the add again";
        foreach (var p in new[] { moved, hidden, replaced })
            _before[p.Id] = p.Node.GlobalTransform;
        _seen.Add("edited: moved, hid, removed, replaced, added at a part (one undo step each; undo and redo)");
        return null;
    }

    private string? Save(MapEditRoot root)
    {
        // the scene's own save (Ctrl+S): the edit scene keeps no classic content, the edits are written
        if (EditorInterface.Singleton.SaveScene() != Error.Ok)
            return "the scene did not save";
        var scenePath = ProjectSettings.GlobalizePath(MapEditRoot.EditScenePath(Key));
        var sceneText = System.IO.File.ReadAllText(scenePath);
        if (sceneText.Length > 2000 || sceneText.Contains("ArrayMesh") || sceneText.Contains("ClassicMap") || !sceneText.Contains("MapEditRoot"))
            return $"the edit scene holds more than its root ({sceneText.Length} bytes)";
        if (!root.Map!.IsInsideTree() || root.Map.Owner != root)
            return "the map left the scene with the save";
        var path = MapStore.PathOf(Key);
        var addPath = MapStore.AdditionsPathOf(Key);
        if (!FileAccess.FileExists(path) || !FileAccess.FileExists(addPath))
            return $"Ctrl+S wrote no {path} / {addPath}";
        var r = _plugin.SaveMap(root);
        if (r is not { Ok: true, Parts: 4, Added: 1, Removed: 1, Hidden: 1, Moved: 1, Replaced: 1 })
            return $"saved {r}";
        var a = FileAccess.GetFileAsString(path);
        var b = FileAccess.GetFileAsString(addPath);
        _plugin.SaveMap(root);
        if (FileAccess.GetFileAsString(path) != a || FileAccess.GetFileAsString(addPath) != b)
        {
            System.IO.File.WriteAllText(GameSettings.Globalize($"{MapStore.Dir}/first-overrides.tres"), a);
            System.IO.File.WriteAllText(GameSettings.Globalize($"{MapStore.Dir}/first-additions.tscn"), b);
            return $"a second save changed the files (the first save's: {MapStore.Dir}/first-*)";
        }
        if (a.Contains("x:/ffx_data") || a.Contains("ArrayMesh") || b.Contains("ArrayMesh") || b.Contains("PackedVector3Array"))
            return "the files hold classic content or absolute IDs";
        // read back as the game reads it
        var loaded = ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore);
        if (loaded is not MapOverrides res || res.Parts.Count != 4)
            return $"the overrides do not read back: {loaded?.GetType().Name} {(loaded as MapOverrides)?.Parts.Count}";
        var move = res.Parts.FirstOrDefault(p => p.Id == Moved);
        if (move is not { TransformMode: TransformOp.Offset } || !move.Transform.Origin.IsEqualApprox(MoveBy) || !move.Transform.Basis.IsEqualApprox(Basis.Identity))
            return $"{Moved}: {move?.TransformMode} {move?.Transform}";
        if (res.Parts.FirstOrDefault(p => p.Id == Hidden)?.Visibility != VisibilityOp.Hidden
            || res.Parts.FirstOrDefault(p => p.Id == Removed)?.Remove != RemoveOp.Remove
            || res.Parts.FirstOrDefault(p => p.Id == Replaced) is not { Replace: ReplaceOp.Scene, Scene.ResourcePath: Replacement })
            return "the hidden, removed or replaced entry is not as edited";
        var add = (ResourceLoader.Load(addPath, "", ResourceLoader.CacheMode.Ignore) as PackedScene)?.Instantiate();
        if (add?.GetChildCount() != 1 || add.GetChild(0) is not MeshInstance3D saved || PartIds.GetString(saved, "ffx_anchor") != Anchor)
            return "the additions scene is not the box at its anchor";
        var anchor = root.Parts[Full(Anchor)];
        var world = root.Frame(anchor) * saved.Transform;
        add.Free();
        if (!world.Origin.IsEqualApprox(_boxWorld.Origin))
            return $"the box saved at {world.Origin}, placed at {_boxWorld.Origin}";
        WriteExpect(root);
        _seen.Add($"saved {path.GetFile()} ({a.Length} bytes) and {addPath.GetFile()} ({b.Length} bytes), the same twice; the edit scene {sceneText.Length} bytes");
        return null;
    }

    private void WriteExpect(MapEditRoot root)
    {
        var expect = new Dictionary<string, object>
        {
            ["level"] = Level,
            ["removed"] = new[] { Removed },
            ["hidden"] = new[] { Hidden },
            ["moved"] = new Dictionary<string, float[]> { [Moved] = [MoveBy.X, MoveBy.Y, MoveBy.Z] },
            ["replaced"] = new[] { Replaced },
            ["added"] = new Dictionary<string, object>
            {
                ["EditorBox"] = new Dictionary<string, object>
                {
                    ["anchor"] = Anchor,
                    ["world"] = new[] { _boxWorld.Origin.X, _boxWorld.Origin.Y, _boxWorld.Origin.Z },
                },
            },
            ["status"] = nameof(MapStatus.InProgress),
        };
        var file = $"{MapStore.Dir}/expect.json";
        System.IO.File.WriteAllText(GameSettings.Globalize(file), JsonSerializer.Serialize(expect, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"ffx editortest: expectations for the game in {file}");
    }

    private string? Reopened(MapEditRoot root)
    {
        foreach (var (id, w) in _before)
            if (root.Parts.TryGetValue(id, out var p) && !p.Node.GlobalTransform.IsEqualApprox(w))
                return $"{id}: at {p.Node.GlobalTransform.Origin} after opening again, was {w.Origin}";
        var marks = new (string Id, PartMarks M)[] { (Moved, PartMarks.Moved), (Hidden, PartMarks.Hidden), (Removed, PartMarks.Removed), (Replaced, PartMarks.Replaced) };
        foreach (var (id, m) in marks)
            if (root.MarksOf(root.Parts[Full(id)]) != m)
                return $"{id}: marked {root.MarksOf(root.Parts[Full(id)])} after opening again, not {m}";
        if (root.Added() is not [{ Node: var box, Anchor: var a }] || box.Name != "EditorBox" || a?.Id != Full(Anchor)
            || !box.GlobalTransform.Origin.IsEqualApprox(_boxWorld.Origin))
            return $"the box is not back at its anchor: {string.Join(", ", root.Added().Select(x => $"{x.Node.Name} at {x.Anchor?.Short(root.Model)} {x.Node.GlobalPosition} (placed {_boxWorld.Origin})"))}";
        if (root.Order.Count(p => root.MarksOf(p) != PartMarks.None) != 4)
            return $"{root.Order.Count(p => root.MarksOf(p) != PartMarks.None)} parts with edits after opening again, not 4";
        _seen.Add("opened again: the same four edits and the box in place");
        return null;
    }

    private string? Status()
    {
        var global = Undo.GetHistoryUndoRedo((int)EditorUndoRedoManager.SpecialHistory.GlobalHistory);
        var v = global.GetVersion();
        _plugin.SetMapState(Key, MapStatus.Approved, RenderPath.Classic, "editor check");
        if (global.GetVersion() != v + 1 || MapStore.Find(Key)?.Status != MapStatus.Approved)
            return "the status is not set in one step";
        if ((ResourceLoader.Load(MapStore.PathOf(Key), "", ResourceLoader.CacheMode.Ignore) as MapOverrides)?.Status != MapStatus.Approved)
            return "the status is not written";
        global.Undo();
        if (MapStore.Find(Key)?.Status != MapStatus.Original)
            return "undo did not take the status back";
        _plugin.SetMapState(Key, MapStatus.InProgress, RenderPath.Classic, "editor check: moved, hidden, removed, replaced, added");
        var res = ResourceLoader.Load(MapStore.PathOf(Key), "", ResourceLoader.CacheMode.Ignore) as MapOverrides;
        if (res is not { Status: MapStatus.InProgress, Parts.Count: 4 })
            return $"the file after the status: {res?.Status}, {res?.Parts.Count} parts";
        var st = DockState.Of(_plugin.Session.Catalogue!.Find(Key)!);
        if (st is not { HasOverrides: true, HasAdditions: true, Effective: MapStatus.InProgress })
            return $"the dock's state: {st}";
        _seen.Add("status set, undone and set again (written, the parts kept)");
        return null;
    }

    private string? Play()
    {
        var s = _plugin.Session;
        var entry = s.Catalogue!.Find(Key)!;
        var plan = s.PlanFor(entry);
        if (!plan.OnMap || !plan.Saves.Contains(7))
            return $"the play plan for bsil00: {plan}";
        var args = _plugin.PlayArguments("", plan.Save);
        if (!args.StartsWith("-- --devsave=", System.StringComparison.Ordinal))
            return $"play arguments {args}";
        // a field map without a developer save: the plan says what is possible
        var other = s.Catalogue.Levels.FirstOrDefault(l => l.Kind == "field" && l.Model3D != null && s.DevSavesOn(l).Count == 0);
        var otherPlan = other != null ? s.PlanFor(other) : null;
        _seen.Add($"play: bsil00 by developer save(s) {string.Join(", ", plan.Saves)} ({args}); "
            + $"{other?.Key}: {otherPlan?.Note}");
        return null;
    }

    // ---- R3: lighting ----------------------------------------------------------------------------

    private string _lightingText = "";

    /// <summary>
    /// The game's lighting as nodes (IDs, the key light from the look), then a turn of the key
    /// light (as the gizmo makes it), the exposure and the fog in the inspector, a volume added,
    /// one undo step each, undone and redone; saved (lighting.tres, the same twice), read back.
    /// </summary>
    private string? Lighting(MapEditRoot root)
    {
        if (root.Lighting is not { } l)
            return "no lighting nodes";
        if (PartIds.Of(l.Key) != MapLighting.KeyId(Key) || PartIds.Of(l.Ambient) != MapLighting.AmbientId(Key)
            || PartIds.Of(l.Fog) != MapLighting.FogId(Key) || PartIds.Of(l.Environment) != MapLighting.EnvironmentId(Key))
            return "the lighting nodes are not tagged with their IDs";
        if (!l.FromGame)
            return $"the look was not read from {LookCache.PathOf(Level)}";
        var dir = (-l.Key.GlobalBasis.Z).Normalized();
        if (dir.AngleTo(LookKey) > 0.002f)
            return $"the key light travels {dir}, the game's look says {LookKey}";
        if (l.Environment.Get("ToneExposure").AsSingle() != 1f || l.Fog.Get("FogMode").AsInt32() != (int)LightSource.Game)
            return "the facets do not show the defaults";
        var turned = new Transform3D(Basis.LookingAt(TurnedKey, Vector3.Up), l.Key.Position);
        var fail = OneStep(root, "turn the key light", () =>
        {
            Undo.CreateAction("Rotate", UndoRedo.MergeMode.Disable, root);
            Undo.AddDoProperty(l.Key, Node3D.PropertyName.Transform, turned);
            Undo.AddUndoProperty(l.Key, Node3D.PropertyName.Transform, l.Key.Transform);
            Undo.CommitAction();
        });
        // the inspector's edits of a facet (as Godot's inspector makes them)
        fail ??= OneStep(root, "exposure", () =>
        {
            Undo.CreateAction("Set ToneExposure", UndoRedo.MergeMode.Disable, root);
            Undo.AddDoProperty(l.Environment, "ToneExposure", 1.3f);
            Undo.AddUndoProperty(l.Environment, "ToneExposure", 1f);
            Undo.CommitAction();
        });
        fail ??= OneStep(root, "fog", () =>
        {
            Undo.CreateAction("Set FogMode", UndoRedo.MergeMode.Disable, root);
            Undo.AddDoProperty(l.Fog, "FogMode", (int)LightSource.Custom);
            Undo.AddUndoProperty(l.Fog, "FogMode", (int)LightSource.Game);
            Undo.CommitAction();
        });
        var vol = l.NewVolume("WholeBeach");
        (vol.Size, vol.Blend, vol.SetExposure, vol.Exposure) = (new Vector3(8000, 3000, 8000), 10f, true, 0.6f);
        fail ??= OneStep(root, "add a volume", () => _plugin.AddVolume(root, vol, Transform3D.Identity));
        if (fail != null)
            return fail;
        if (PartIds.Of(vol) != MapLighting.VolumeId(Key, "WholeBeach") || vol.Owner != root)
            return "the volume is not tagged or not in the scene";
        var h = SceneHistory(root);
        for (int i = 0; i < 4; i++)
            h.Undo();
        if (vol.IsInsideTree() || l.Res.FogMode != LightSource.Game || l.Res.ToneExposure != 1f || (-l.Key.GlobalBasis.Z).AngleTo(LookKey) > 0.002f)
            return "undo did not take the lighting edits back";
        for (int i = 0; i < 4; i++)
            h.Redo();
        if (!vol.IsInsideTree() || l.Res.FogMode != LightSource.Custom || l.Res.ToneExposure != 1.3f || (-l.Key.GlobalBasis.Z).AngleTo(TurnedKey) > 0.002f)
            return "redo did not do the lighting edits again";
        // the preview: the view remastered, the rig on with the edited lighting
        _plugin.SetEditorLook(true);
        if (_plugin.SaveMap(root) is not { Ok: true })
            return "the save failed";
        var path = LightingEdit.PathOf(Key);
        if (!FileAccess.FileExists(path))
            return $"no {path}";
        var a = FileAccess.GetFileAsString(path);
        _plugin.SaveMap(root);
        if (FileAccess.GetFileAsString(path) != a)
        {
            System.IO.File.WriteAllText(GameSettings.Globalize($"{MapStore.Dir}/first-lighting.tres"), a);
            return $"a second save changed {path.GetFile()} (the first save's: {MapStore.Dir}/first-lighting.tres)";
        }
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (a.Contains("x:/ffx_data") || home.Length > 1 && a.Contains(home, System.StringComparison.Ordinal))
            return "the lighting file holds absolute IDs or a path of this machine";
        if (ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Ignore) is not MapLighting m)
            return "the lighting does not read back";
        if (m.KeyMode != LightSource.Custom || m.KeyDirection.AngleTo(TurnedKey) > 0.002f || m.ToneExposure != 1.3f || m.FogMode != LightSource.Custom
            || m.Volumes.Count != 1 || m.Volumes[0] is not { Name: "WholeBeach", SetExposure: true } v || !Mathf.IsEqualApprox(v.Exposure, 0.6f)
            || m.KeyColourMode != LightSource.Game)
            return $"read back: key {m.KeyMode} {m.KeyDirection} (colour {m.KeyColourMode}), exposure {m.ToneExposure}, fog {m.FogMode}, {m.Volumes.Count} volume(s)";
        _lightingText = a;
        var expect = new Dictionary<string, object>
        {
            ["key"] = new[] { TurnedKey.X, TurnedKey.Y, TurnedKey.Z },
            ["exposure"] = 1.3f,
            ["volume"] = "WholeBeach",
            ["volumeExposure"] = 0.6f,
            ["fogMode"] = nameof(LightSource.Custom),
        };
        System.IO.File.WriteAllText(GameSettings.Globalize($"{MapStore.Dir}/lighting-expect.json"), JsonSerializer.Serialize(expect, new JsonSerializerOptions { WriteIndented = true }));
        _seen.Add($"lighting: the key light (from the game's look), ambient, fog and environment as nodes by ID; turned the key light, "
            + $"exposure 1.3, fog custom, a volume added (one undo step each; undo and redo); {path.GetFile()} ({a.Length} bytes) the same twice");
        return null;
    }

    private string? LightingReopened(MapEditRoot root)
    {
        var l = root.Lighting!;
        if ((-l.Key.GlobalBasis.Z).AngleTo(TurnedKey) > 0.002f)
            return $"the key light travels {-l.Key.GlobalBasis.Z} after opening again, was turned to {TurnedKey}";
        if (l.Environment.Get("ToneExposure").AsSingle() != 1.3f || l.Fog.Get("FogMode").AsInt32() != (int)LightSource.Custom)
            return "the facets lost their edits on opening again";
        if (l.Volumes.GetChildren().OfType<VolumeNode>().ToList() is not [{ Name: var vn, Exposure: var ve }] || vn != "WholeBeach" || !Mathf.IsEqualApprox(ve, 0.6f))
            return "the volume is not back";
        // the preview: remastered, the rig with the edited lighting and the camera in the volume
        if (!_plugin.EditorRemastered || !l.Rig.Active || l.Rig.Lighting.Layer != "editor" || !l.Rig.Now.Volumes.StartsWith("WholeBeach", System.StringComparison.Ordinal))
            return $"the preview is not on: remastered {_plugin.EditorRemastered}, rig {l.Rig.Active} ({l.Rig.Lighting.Layer}), volumes '{l.Rig.Now.Volumes}'";
        // opened and saved without an edit: the same bytes
        _plugin.SaveMap(root);
        if (FileAccess.GetFileAsString(LightingEdit.PathOf(Key)) != _lightingText)
        {
            System.IO.File.WriteAllText(GameSettings.Globalize($"{MapStore.Dir}/reopened-lighting.tres"), FileAccess.GetFileAsString(LightingEdit.PathOf(Key)));
            return "saved after opening again, the lighting file changed";
        }
        _seen.Add("lighting opened again: the turned key light, the facets' edits, the volume; the preview remastered with the camera in the volume; saved again the same");
        return null;
    }

    // ---- R2: textures and materials ------------------------------------------------------------

    private string _replaced = "", _material = "";
    private bool _batchStarted, _batchDone;

    /// <summary>The where-used index: Besaid beach's textures, each with its parts; the cache written.</summary>
    private string? CheckUses()
    {
        var u = _plugin.Uses;
        if (!u.ByLevel.TryGetValue(Key, out var ids) || ids.Count < 10)
            return $"where-used: bsil00 has {ids?.Count ?? 0} textures";
        if (!FileAccess.FileExists(WhereUsed.CachePath))
            return $"where-used: no cache {WhereUsed.CachePath}";
        // Besaid's textures by how many of its parts draw them
        var ranked = ids.Select(id => (Id: id, Parts: u.Uses(id).First(x => x.Level == Key).Parts.Count))
            .OrderByDescending(x => x.Parts).ThenBy(x => x.Id, System.StringComparer.Ordinal).ToList();
        if (ranked[0].Parts == 0)
            return "where-used: no parts on Besaid's textures";
        int shared = ids.Count(id => u.Uses(id).Count > 1);
        _seen.Add($"where-used: {u.ByTexture.Count} textures in {u.ByLevel.Count} maps ({(u.BuildMs > 0 ? $"built in {u.BuildMs} ms" : "cached")}); "
            + $"bsil00 {ids.Count} textures, {shared} also on other maps");
        return null;
    }

    /// <summary>The textures the open map's classic materials bind for <paramref name="id"/> (their <c>tex</c>).</summary>
    private static List<Texture?> Bound(MapEditRoot root, string id)
    {
        var r = new List<Texture?>();
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } mesh } mi)
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                    if (mi.GetSurfaceOverrideMaterial(s) is ShaderMaterial sm && RemasterTextures.ColourId(sm) == id
                        && sm.HasMeta(Variants.Name(ClassicMaterials.MapShaderMeta)))
                    {
                        using var v = sm.GetShaderParameter(Variants.Name("tex"));
                        r.Add(v.AsGodotObject() as Texture);
                    }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(root.Map!);
        return r;
    }

    /// <summary>
    /// A texture replaced by a generated test image, a material generated for another, each one
    /// undo step (undone and redone); the map's render path set to remastered and the 3D view's
    /// toggle; the index saved twice the same, without a path of this machine.
    /// </summary>
    private string? Textures(MapEditRoot root)
    {
        var global = Undo.GetHistoryUndoRedo((int)EditorUndoRedoManager.SpecialHistory.GlobalHistory);
        var onMap = TextureActions.OnMap(root);
        var ranked = _plugin.Uses.ByLevel[Key].Where(onMap.ContainsKey)
            .OrderByDescending(id => onMap[id]).ThenBy(id => id, System.StringComparer.Ordinal).ToList();
        if (ranked.Count < 4)
            return $"the open map binds {ranked.Count} of where-used's textures";
        (_replaced, _material) = (ranked[0], ranked[1]);
        _plugin.SetEditorLook(false);
        // 1. replace by a generated test image (a checker: synthetic, nothing of the game)
        var img = Image.CreateEmpty(64, 64, false, Image.Format.Rgba8);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                img.SetPixel(x, y, (x / 8 + y / 8) % 2 == 0 ? new Color(0.95f, 0.5f, 0.1f) : new Color(0.1f, 0.3f, 0.9f));
        var classic = Bound(root, _replaced);
        var v = global.GetVersion();
        TextureActions.Replace(_plugin, _replaced, img, "editor check: a synthetic test pattern");
        if (global.GetVersion() != v + 1)
            return "the replacement is not one undo step";
        if (TextureStore.Find(_replaced) is not { Replacement.Length: > 0 } e)
            return "no entry for the replaced texture";
        var file = TextureStore.Absolute(e.Replacement);
        if (TextureStore.InProject ? !ResourceLoader.Exists(file) : !FileAccess.FileExists(file))
            return $"{file}: not written or not imported";
        var want = RemasterTextures.Texture(file);
        var bound = Bound(root, _replaced);
        if (want == null || bound.Count == 0 || bound.Any(t => t != want))
            return $"the open map does not draw the replacement ({bound.Count} surface(s))";
        global.Undo();
        if (TextureStore.Find(_replaced) != null || !Bound(root, _replaced).SequenceEqual(classic))
            return "undo did not put the classic texture back";
        global.Redo();
        if (Bound(root, _replaced).Any(t => t != RemasterTextures.Texture(file)))
            return "redo did not draw the replacement again";
        _plugin.Textures!.SetFilter(TextureDock.ModeMap, RemasterTextures.ShortName(_replaced));
        if (!_plugin.Textures.Listed.Contains(_replaced))
            return "the texture browser does not list the replaced texture";
        _plugin.Textures.Select(_replaced);
        _seen.Add($"replaced {RemasterTextures.ShortName(_replaced)} ({bound.Count} surface(s), {file.GetFile()}, imported), undone, redone");
        // 2. a material generated from the classic pixels
        v = global.GetVersion();
        var t0 = Time.GetTicksMsec();
        if (TextureActions.GenerateMaterial(_plugin, _material, new PbrRecipe { Upscale = 2, MaxSide = 1024 }) is { } genFail)
            return genFail;
        if (global.GetVersion() != v + 1)
            return "the material is not one undo step";
        // the maps are imported in the background (a scan): the material's own check waits for it (CheckImported)
        if (TextureStore.Find(_material) is not { Material.Length: > 0 } me
            || FileAccess.GetFileAsString(TextureStore.Absolute(me.Material)) is not { } matText
            || !matText.Contains("orm_texture = ExtResource") || !matText.Contains("normal_texture = ExtResource") || !matText.Contains("heightmap_texture"))
            return "the material file does not name its maps";
        global.Undo();
        if (TextureStore.Find(_material) != null)
            return "undo did not take the material back";
        global.Redo();
        _seen.Add($"material of {RemasterTextures.ShortName(_material)} made ({Time.GetTicksMsec() - t0} ms, "
            + $"{TextureStore.Find(_material)!.Material.GetFile()}), undone, redone");
        // 3. the render path: remastered (written), the view follows; the toggle both ways
        _plugin.SetMapState(Key, MapStatus.InProgress, RenderPath.Remastered, "editor check: moved, hidden, removed, replaced, added; textures");
        if ((ResourceLoader.Load(MapStore.PathOf(Key), "", ResourceLoader.CacheMode.Ignore) as MapOverrides)?.RenderPath != RenderPath.Remastered)
            return "the render path is not written";
        if (MapLook.Find(root.Map!) is not { Remastered: true } look || !_plugin.EditorRemastered)
            return "the open map does not draw remastered after its render path";
        _plugin.SetEditorLook(false);
        if (look.Remastered || Bound(root, _replaced).Count == 0)
            return "the view's toggle did not draw it classic";
        _plugin.SetEditorLook(true);
        _seen.Add($"render path remastered: {look.Surfaces} surface(s) lit, {look.Sky.Count} sky part(s) classic; the toggle both ways");
        // 4. the index: stable, no path of this machine
        TextureStore.Write();
        var a = FileAccess.GetFileAsBytes(TextureStore.IndexPath);
        TextureStore.Write();
        var b = FileAccess.GetFileAsBytes(TextureStore.IndexPath);
        var text = System.Text.Encoding.UTF8.GetString(a);
        if (!a.SequenceEqual(b))
            return "the texture index changed on a save without an edit";
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (home.Length > 1 && text.Contains(home, System.StringComparison.Ordinal))
            return "the texture index holds a path of this machine";
        _seen.Add($"index {TextureStore.IndexPath.GetFile()} ({a.Length} bytes), the same twice");
        return null;
    }

    /// <summary>After the import: the material an ORMMaterial3D over its imported maps, drawn by the open map (remastered).</summary>
    private string? CheckImported(MapEditRoot root)
    {
        if (TextureStore.Find(_material) is not { Material.Length: > 0 } me
            || ResourceLoader.Load(TextureStore.Absolute(me.Material), "", ResourceLoader.CacheMode.Ignore) is not OrmMaterial3D orm
            || orm.OrmTexture is not CompressedTexture2D || orm.NormalTexture is not CompressedTexture2D
            || orm.AlbedoTexture is not CompressedTexture2D || !orm.HeightmapEnabled)
            return "the material is not an ORMMaterial3D over its imported maps";
        int lit = 0;
        void Count(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } mesh } mi)
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                    if (mi.GetSurfaceOverrideMaterial(s) is ShaderMaterial sm && PartIds.GetString(sm, RemasterLook.LitTextureMeta) == _material
                        && sm.Shader.Code.Contains("#define HAS_ORM"))
                        lit++;
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Count(n.GetChild(i));
        }
        Count(root.Map!);
        if (lit == 0)
            return "no surface draws the generated material after the import";
        _seen.Add($"the material imported ({orm.AlbedoTexture.GetWidth()} px maps), {lit} surface(s) draw it");
        return null;
    }

    /// <summary>The batch over the open map: two materials generated for review, one unticked, the other accepted as one undo step.</summary>
    private string? Batch(MapEditRoot root)
    {
        var batch = _plugin.Textures!.Batch;
        if (!_batchStarted)
        {
            _batchStarted = true;
            return batch.Start(BatchPanel.ScopeMap, 2);
        }
        if (batch.Running)
            return _phaseT > 120 ? "the batch did not finish" : null;
        if (batch.Made.Count != 2)
            return $"the batch made {batch.Made.Count} material(s) for review, not 2";
        var (keep, drop) = (batch.Made[0].Id, batch.Made[1].Id);
        if (TextureStore.Find(keep) != null || TextureStore.Find(drop) != null)
            return "the batch wrote before the review";
        batch.Untick(drop);
        var global = Undo.GetHistoryUndoRedo((int)EditorUndoRedoManager.SpecialHistory.GlobalHistory);
        var v = global.GetVersion();
        batch.Accept();
        if (global.GetVersion() != v + 1 || TextureStore.Find(keep)?.Material is not { Length: > 0 } || TextureStore.Find(drop) != null)
            return "the batch's accept: not one step, or the unticked one kept";
        global.Undo();
        if (TextureStore.Find(keep) != null)
            return "undo did not take the batch back";
        global.Redo();
        WriteTexturesExpect();
        _seen.Add($"batch: 2 made for review, {RemasterTextures.ShortName(keep)} kept, {RemasterTextures.ShortName(drop)} unticked; undone, redone");
        _batchDone = true;
        return null;
    }

    /// <summary>What the game's <c>--selftest-materials --textures-file=</c> checks: <c>DIR/textures-expect.json</c>.</summary>
    private void WriteTexturesExpect()
    {
        var expect = new Dictionary<string, object>
        {
            ["index"] = TextureStore.IndexPath,
            ["replaced"] = _replaced,
            ["material"] = _material,
        };
        var file = $"{MapStore.Dir}/textures-expect.json";
        System.IO.File.WriteAllText(GameSettings.Globalize(file), JsonSerializer.Serialize(expect, new JsonSerializerOptions { WriteIndented = true }));
    }

    private string? Shot()
    {
        var root = _plugin.Current!;
        var vp = EditorInterface.Singleton.GetEditorViewport3D(0);
        var cam = vp.GetCamera3D();
        if (_frames == 1)
        {
            // looking at the box and the moved hut from above the beach
            var target = _boxWorld.Origin;
            cam.GlobalTransform = new Transform3D(Basis.LookingAt(-new Vector3(0, 150, 260).Normalized(), Vector3.Up), target + new Vector3(0, 150, 260));
            RemasterPlugin.Select(root.Parts[Full(Moved)].Node);
            return null;
        }
        if (_frames < 20)
            return null;
        var path0 = GameSettings.Arg("editor-shot")!;
        if (_frames == 20)
        {
            // the 3D view, and the whole window (the scene tree, the inspector's part section)
            vp.GetTexture().GetImage().SavePng(path0);
            EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture().GetImage()
                .SavePng(path0.GetBaseDir().PathJoin(path0.GetFile().GetBaseName() + "-window.png"));
            _plugin.Dock!.MakeVisible();
            return null;
        }
        if (_frames < 40)
            return null;
        if (_frames == 40)
        {
            // then the dock in front (the catalogue, the map's status, its parts with their edits)
            var dockPath = path0.GetBaseDir().PathJoin(path0.GetFile().GetBaseName() + "-dock.png");
            EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture().GetImage().SavePng(dockPath);
            // R5: the game's data from above the beach, the exit off it selected (the inspector's game-data section)
            if (root.GameData is { Data.Main: { } walk } gd)
            {
                var mid = walk.Vertices.Aggregate(Vector3.Zero, (a, x) => a + x) / walk.Vertices.Length;
                cam.GlobalTransform = new Transform3D(Basis.LookingAt(-new Vector3(0, 700, 450).Normalized(), Vector3.Up), mid + new Vector3(0, 700, 450));
                if (gd.Triggers.GetChildren().OfType<Node3D>().FirstOrDefault(n => n.HasMeta(GameDataEdit.TargetMap)) is { } exit)
                    RemasterPlugin.Select(exit);
            }
            return null;
        }
        if (_frames < 60)
            return null;
        vp.GetTexture().GetImage().SavePng(path0.GetBaseDir().PathJoin("editor-gamedata.png"));
        EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture().GetImage()
            .SavePng(path0.GetBaseDir().PathJoin("editor-gamedata-window.png"));
        _seen.Add($"pictures {path0}, its -window.png and -dock.png, editor-gamedata.png and -window.png");
        Next(Phase.Map2D);
        return null;
    }

    private static bool Shots => GameSettings.Arg("editor-shot") is { Length: > 0 } && DisplayServer.GetName() != "headless";

    // ---- R5: the game's data -------------------------------------------------------------------

    private static int Count(Node3D group) => group.GetChildCount();

    /// <summary>Besaid beach's game data: the walk mesh, triggers (the exit to the crossroads named), entrances, actors, cameras, the capture's birds.</summary>
    private string? GameData(MapEditRoot root)
    {
        if (root.GameData is not { Data: { } data } gd)
            return "no game data on the map";
        var walk = data.Main;
        if (walk == null || walk.Count < 500)
            return $"the walk mesh has {walk?.Count ?? 0} triangles (Besaid beach: 839)";
        if (gd.WalkMeshes.GetChildCount() < 1 || gd.WalkMeshes.GetChild(0) is not MeshInstance3D { Mesh: { } wm } || wm.GetFaces().Length / 3 != walk.Count)
            return "the walk mesh is not drawn whole";
        int triggers = Count(gd.Triggers), entrances = Count(gd.Entrances), actors = Count(gd.Actors);
        if (triggers < 20 || entrances < 3 || actors < 10)
            return $"{triggers} triggers, {entrances} entrances, {actors} actor starts (too few)";
        // the exit off the beach: a line trigger named by where it leads
        var exit = gd.Triggers.GetChildren().OfType<Node3D>().FirstOrDefault(n => n.HasMeta(GameDataEdit.TargetMap)
            && n.GetMeta(GameDataEdit.TargetMap).AsInt32() == 0x14);
        if (exit == null)
            return "no trigger leading to map 0x14";
        var to = s_Target(exit);
        if (!to.Contains("bsil02"))
            return $"the exit says '{to}', not the crossroads (bsil02)";
        // every game-data node locked, tagged, not saved
        foreach (var n in gd.Group.FindChildren("*", "", true, false))
            if (!n.HasMeta(MapEditRoot.EditorNode))
                return $"{gd.Group.GetPathTo(n)} is not the editor's";
        if (root.Added().Any(a => gd.Group.IsAncestorOf(a.Node) || a.Node == gd.Group))
            return "game data counted as added nodes";
        // the inspector's section for a game-data node
        if (!new PartInspector(_plugin)._CanHandle(exit))
            return "the inspector does not show a trigger's info";
        // the capture: the birds where the game had them, the game's camera
        var cap = MapCapture.Read(Level);
        var note = "no capture";
        if (cap != null)
        {
            var bird = cap.Parts.FirstOrDefault(p => p.Id == Full(Bird));
            if (bird == null || !root.Parts.TryGetValue(Full(Bird), out var info))
                return $"the capture has no {Bird}";
            if (!info.GamePlaced || info.Node.GlobalTransform.Origin.DistanceTo(bird.World.Origin) > 0.05f)
                return $"{Bird} at {info.Node.GlobalTransform.Origin}, the game had it at {bird.World.Origin}";
            if (root.MarksOf(info) != PartMarks.None)
                return $"{Bird} placed where the game had it shows as an edit ({root.MarksOf(info)})";
            if (gd.Cameras.GetNodeOrNull<Camera3D>("Game") is not { } game || Mathf.Abs(game.Fov - Mathf.RadToDeg(cap.FovY)) > 0.01f)
                return "no game camera from the capture";
            if (gd.Characters.GetChildCount() < 1)
                return "no characters from the capture";
            note = $"the capture: {gd.Placed} parts where the game had them ({Bird} at {bird.World.Origin.Round()}), {gd.Morphed} morph(s), "
                + $"the game's camera, {gd.Characters.GetChildCount()} character(s)";
        }
        else if (GameSettings.Arg("remaster-captures") is { Length: > 0 })
            return $"no capture in {MapCapture.Dir} (the game's --selftest-alignment writes it)";
        _seen.Add($"game data: walk mesh {walk.Count} triangles, {triggers} triggers (the exit '{to}'), {entrances} entrances, {actors} actor starts, "
            + $"{Count(gd.Cameras)} camera(s); {note}");
        return null;
    }

    private static string s_Target(Node3D n) => n.GetNodeOrNull<Label3D>("Label")?.Text ?? "";

    /// <summary>
    /// The alignment check on the open map's remade geometry as it is, then with a test box
    /// added 8 units over a walkable triangle (as the dock adds one: one undo step): the box
    /// flagged as blocking, every flagged triangle marked; undone after.
    /// </summary>
    private string? AlignmentStep(MapEditRoot root, string what)
    {
        if (root.GameData is not { Data.Main: { } walk } gd)
            return "no walk mesh for the alignment check";
        var r0 = gd.Check();
        if (r0 == null)
            return "the alignment check did not run";
        // a walkable triangle away from the map's edges: the one nearest the walk mesh's middle
        var mid = walk.Vertices.Aggregate(Vector3.Zero, (a, v) => a + v) / walk.Vertices.Length;
        int tri = Enumerable.Range(0, walk.Count).Where(t => WalkMesh.Walkable(walk.Kind(t)))
            .OrderBy(t => walk.Centre(t).DistanceSquaredTo(mid)).First();
        var at = walk.Centre(tri) + Vector3.Up * 10f;
        var box = new MeshInstance3D { Name = "MisplacedBox", Mesh = new BoxMesh { Size = new Vector3(20, 4, 20) } };
        var v = Undo.GetHistoryUndoRedo(Undo.GetObjectHistoryId(root)).GetVersion();
        _plugin.AddNode(root, box, null, new Transform3D(Basis.Identity, at));
        var r1 = gd.Check();
        if (r1 == null)
            return "the alignment check did not run with the box";
        var flagged = r1.Issues.Where(i => i.Source == "added MisplacedBox").ToList();
        if (flagged.Count == 0 || flagged.Any(i => i.Kind != AlignmentKind.Blocked) || !flagged.Any(i => i.Triangle == tri))
            return $"the misplaced box is not flagged as blocking triangle {tri}: {r1.Summary()}";
        if (gd.AlignmentMarks.GetNodeOrNull<MeshInstance3D>("Flagged") is not { Mesh: { } marks } || marks.GetFaces().Length / 3 != r1.Issues.Count)
            return "the flagged triangles are not marked";
        // undone: the box gone, the check as before
        var history = Undo.GetHistoryUndoRedo(Undo.GetObjectHistoryId(root));
        if (history.GetVersion() != v + 1)
            return "adding the box was not one undo step";
        history.Undo();
        if (box.IsInsideTree())
            return "undo left the box";
        var r2 = gd.Check()!;
        if (r2.Issues.Count != r0.Issues.Count)
            return $"after undo {r2.Issues.Count} issue(s), before the box {r0.Issues.Count}";
        _seen.Add($"alignment on {what}: {r0.Summary()}; a box 8 units over triangle {tri}: {flagged.Count} triangle(s) blocked, marked; undone");
        return null;
    }

    /// <summary>The 2D-layer map: no 3D map, the walk mesh, triggers, entrances, the scene's fixed camera.</summary>
    private string? Map2D(MapEditRoot root)
    {
        if (!root.Is2D || root.Map != null)
            return $"{Key2D} opened as a 3D map";
        if (root.GameData is not { Data: { } data } gd || data.Main is not { } walk)
            return $"{Key2D}: no game data";
        int triggers = Count(gd.Triggers), entrances = Count(gd.Entrances);
        if (walk.Count < 30 || entrances < 2 || triggers < 1)
            return $"{Key2D}: walk mesh {walk.Count} triangles, {entrances} entrances, {triggers} triggers (too few)";
        if (gd.Cameras.GetNodeOrNull<Camera3D>("Scene0") is not { } cam)
            return $"{Key2D}: no fixed camera of scene 0";
        // the camera looks at the room: the walk mesh's middle in front of it
        var mid = walk.Vertices.Aggregate(Vector3.Zero, (a, x) => a + x) / walk.Vertices.Length;
        var toMid = (mid - cam.GlobalPosition).Normalized();
        var angle = Mathf.RadToDeg((-cam.GlobalBasis.Z).AngleTo(toMid));
        if (angle > cam.Fov)
            return $"{Key2D}: the scene camera looks {angle:F0}° away from the room (its field of view {cam.Fov:F0}°)";
        // the file's camera where the game has its own (the capture of developer save 14, when there is one)
        var vs = "";
        if (gd.Cameras.GetNodeOrNull<Camera3D>("Game") is { } game)
        {
            float off = game.GlobalPosition.DistanceTo(cam.GlobalPosition);
            float turn = Mathf.RadToDeg((-game.GlobalBasis.Z).AngleTo(-cam.GlobalBasis.Z));
            if (off > 0.5f || turn > 0.5f || Mathf.Abs(game.Fov - cam.Fov) > 0.1f)
                return $"{Key2D}: the scene camera is {off:F2} units and {turn:F2}° from the game's (fov {cam.Fov:F2}° against {game.Fov:F2}°)";
            vs = $"; the game's camera {off:F2} units and {turn:F2}° from it";
        }
        else if (GameSettings.Arg("remaster-captures") is { Length: > 0 })
            return $"{Key2D}: no capture with the game's camera in {MapCapture.Dir}";
        if (_plugin.SaveMap(root) is { Ok: true })
            return $"{Key2D}: a 2D-layer map saved edits (it has none to save)";
        _seen.Add($"{Key2D} (2D layers only): walk mesh {walk.Count} triangles, {triggers} trigger(s), {entrances} entrances, "
            + $"the scene camera (fov {cam.Fov:F1}°, the room {angle:F0}° off its axis{vs})");
        return null;
    }

    /// <summary>Pictures of the 2D map's game data: from above, and through its fixed camera.</summary>
    private string? Shot2D()
    {
        var root = _plugin.Current!;
        var vp = EditorInterface.Singleton.GetEditorViewport3D(0);
        var cam = vp.GetCamera3D();
        var gd = root.GameData!;
        var walk = gd.Data!.Main!;
        var mid = walk.Vertices.Aggregate(Vector3.Zero, (a, x) => a + x) / walk.Vertices.Length;
        if (_frames == 1)
        {
            var scene = gd.Cameras.GetNode<Camera3D>("Scene0");
            cam.GlobalTransform = scene.GlobalTransform;
            return null;
        }
        if (_frames < 20)
            return null;
        var path0 = GameSettings.Arg("editor-shot")!;
        var dir = path0.GetBaseDir();
        if (_frames == 20)
        {
            vp.GetTexture().GetImage().SavePng(dir.PathJoin($"editor-{Key2D}-camera.png"));
            cam.GlobalTransform = new Transform3D(Basis.LookingAt(-new Vector3(0, 90, 60).Normalized(), Vector3.Up), mid + new Vector3(0, 90, 60));
            return null;
        }
        if (_frames < 40)
            return null;
        vp.GetTexture().GetImage().SavePng(dir.PathJoin($"editor-{Key2D}.png"));
        _seen.Add($"pictures editor-{Key2D}-camera.png, editor-{Key2D}.png");
        Next(Phase.Done);
        return null;
    }

    // ---- R2b: the Materials tab ---------------------------------------------------------------------

    private IEnumerator<string?>? _matFlow;
    private int _builtWait;
    private double _saveMs;

    /// <summary>
    /// The Materials tab on Besaid (docs/remaster-materials.md, "Checks"; phase 1): three
    /// textures queued (the Textures dock's Materialize and the tab's own add), every step
    /// entered, a setting dragged (one undo step: the height and the maps after it made again,
    /// the albedo not; the preview's pixels changed under a renderer), the latency of several
    /// changes, undo and redo, a map imported and one skipped, the texture switched with the
    /// settings kept, saved into the texture layer (one undo step, undone, redone), the open map's
    /// textures added; pictures under a renderer. Yields null to wait a frame, a text to fail.
    /// </summary>
    private IEnumerator<string?> MaterialsFlow(MapEditRoot root)
    {
        var tab = _plugin.Materials;
        if (tab == null)
        {
            yield return "no Materials tab";
            yield break;
        }
        var global = Undo.GetHistoryUndoRedo((int)EditorUndoRedoManager.SpecialHistory.GlobalHistory);
        bool shots = GameSettings.Arg("editor-shot") is { Length: > 0 } && DisplayServer.GetName() != "headless";
        string shotDir = shots ? GameSettings.Arg("editor-shot")!.GetBaseDir() : "";
        _plugin.ShowMaterials();
        yield return null;
        yield return null;
        if (!tab.IsVisibleInTree())
        {
            yield return "the Materials tab is not shown as a main screen";
            yield break;
        }
        // three of Besaid's textures (not the ones R2's check edited), the most drawn first
        var onMap = TextureActions.OnMap(root);
        var ids = _plugin.Uses.ByLevel[Key].Where(onMap.ContainsKey).Where(id => id != _replaced && id != _material)
            .OrderByDescending(id => onMap[id]).ThenBy(id => id, System.StringComparer.Ordinal).Take(3).ToList();
        if (ids.Count < 3)
        {
            yield return $"only {ids.Count} textures to queue";
            yield break;
        }
        var dock = _plugin.Textures!;
        dock.SetFilter(TextureDock.ModeMap, RemasterTextures.ShortName(ids[0]));
        dock.Select(ids[0]);
        int added = _plugin.Materialize(dock.SelectedIds());
        added += tab.Add(ids.Skip(1));
        if (added != 3 || tab.Queue.Count != 3 || tab.Current != ids[0])
        {
            yield return $"queued {added}, the queue {tab.Queue.Count}, current {tab.Current}";
            yield break;
        }
        string? fail = null;
        IEnumerable<string?> WaitIdle(string what, double limit = 90)
        {
            double t0 = _t;
            while (!tab.Idle)
            {
                if (_t - t0 > limit)
                {
                    fail = $"{what}: the maps were not made in {limit} s";
                    yield break;
                }
                yield return null;
            }
        }
        foreach (var w in WaitIdle("the first texture")) yield return w;
        if (fail != null) { yield return fail; yield break; }
        // small first, then refined
        var arrivals = tab.Arrivals.ToList();
        int small = arrivals.Count > 0 ? arrivals[0].Side : 0;
        if (arrivals.Count < 9 || (small == MaterialPipeline.SmallSide && arrivals.FindIndex(a => a.Side != small) < 9))
        {
            yield return $"the maps arrived as {string.Join(", ", arrivals.Select(a => $"{a.Step}@{a.Side}"))}";
            yield break;
        }
        // every step entered: its map made at the working size
        var dims = new List<string>();
        foreach (var s in MaterialPipeline.Steps)
        {
            tab.SelectStep(s);
            foreach (var w in WaitIdle($"step {s}")) yield return w;
            if (fail != null) { yield return fail; yield break; }
            if (tab.Pixels(s) is not { } px || tab.Step != s)
            {
                yield return $"step {s}: no map";
                yield break;
            }
            dims.Add($"{s} {px.Width}x{px.Height}");
        }
        _seen.Add($"materials tab: 3 queued (the dock's Materialize, the tab's add), every step made ({dims[^1]}); small first ({small} px)");
        // a slider dragged: one undo step, the height and the maps after it made again, the albedo kept
        tab.SelectStep(MaterialStep.Height);
        foreach (var w in WaitIdle("height")) yield return w;
        if (fail != null) { yield return fail; yield break; }
        var a0 = tab.Pixels(MaterialStep.Albedo)!;
        var h0 = tab.Pixels(MaterialStep.Height)!;
        var n0 = tab.Pixels(MaterialStep.Normal)!;
        Image? p0 = null;
        if (shots)
        {
            for (int i = 0; i < 12; i++) yield return null;
            p0 = tab.Preview.CaptureMaterial();
        }
        var v = global.GetVersion();
        tab.Drag("HeightContrast", 1.4, 1.8, 2.2);
        if (global.GetVersion() != v + 1 || !Mathf.IsEqualApprox(tab.Recipe.HeightContrast, 2.2f))
        {
            yield return $"the drag: {global.GetVersion() - v} undo step(s), contrast {tab.Recipe.HeightContrast}";
            yield break;
        }
        foreach (var w in WaitIdle("the drag")) yield return w;
        if (fail != null) { yield return fail; yield break; }
        var lat = new List<string> { $"height contrast {tab.Latency.Small:0}/{tab.Latency.Refined:0}/{tab.Latency.All:0}" };
        var lats = new List<(double S, double R, double A)> { tab.Latency };
        if (tab.Pixels(MaterialStep.Height)!.Rgba.SequenceEqual(h0.Rgba) || tab.Pixels(MaterialStep.Normal)!.Rgba.SequenceEqual(n0.Rgba))
        {
            yield return "the drag did not change the height and the normal map";
            yield break;
        }
        if (!ReferenceEquals(tab.Pixels(MaterialStep.Albedo), a0))
        {
            yield return "the drag made the albedo again (it is upstream of the height)";
            yield break;
        }
        string previewNote = "no renderer: the preview's pixels not checked";
        if (shots)
        {
            for (int i = 0; i < 12; i++) yield return null;
            var p1 = tab.Preview.CaptureMaterial();
            double d = p0 != null && p1 != null ? MeanDiff(p0, p1) : -1;
            if (d < 0.2)
            {
                yield return $"the preview's pixels did not change with the setting (mean difference {d:0.###})";
                yield break;
            }
            previewNote = $"the preview's pixels changed (mean difference {d:0.##} of 255)";
        }
        // more changes, for the latency: a step's own setting, then one upstream of every map
        foreach (var (step, prop, vals) in new (MaterialStep, string, double[])[]
        {
            (MaterialStep.Normal, "NormalStrength", [6.0]), (MaterialStep.Roughness, "RoughnessBase", [0.6]),
            (MaterialStep.Height, "HeightBlur", [2.0]), (MaterialStep.Albedo, "Delight", [0.8]),
        })
        {
            tab.SelectStep(step);
            foreach (var w in WaitIdle($"step {step}")) yield return w;
            if (fail != null) { yield return fail; yield break; }
            tab.Drag(prop, vals);
            foreach (var w in WaitIdle(prop)) yield return w;
            if (fail != null) { yield return fail; yield break; }
            lats.Add(tab.Latency);
            lat.Add($"{prop} {tab.Latency.Small:0}/{tab.Latency.Refined:0}/{tab.Latency.All:0}");
        }
        var a1 = tab.Pixels(MaterialStep.Albedo)!;
        // undo and redo the last (the de-lighting): the albedo back as it was, then again
        global.Undo();
        foreach (var w in WaitIdle("undo")) yield return w;
        if (fail != null) { yield return fail; yield break; }
        if (!Mathf.IsEqualApprox(tab.Recipe.Delight, 0.6f) || !tab.Pixels(MaterialStep.Albedo)!.Rgba.SequenceEqual(a0.Rgba))
        {
            yield return $"undo: de-lighting {tab.Recipe.Delight}, the albedo {(tab.Pixels(MaterialStep.Albedo)!.Rgba.SequenceEqual(a0.Rgba) ? "as before" : "not as before")}";
            yield break;
        }
        global.Redo();
        foreach (var w in WaitIdle("redo")) yield return w;
        if (fail != null) { yield return fail; yield break; }
        if (!Mathf.IsEqualApprox(tab.Recipe.Delight, 0.8f) || !tab.Pixels(MaterialStep.Albedo)!.Rgba.SequenceEqual(a1.Rgba))
        {
            yield return "redo did not make the albedo with the de-lighting again";
            yield break;
        }
        // the same at 1024 px: upscaled x2 (Besaid's textures are 512 px), then a height and a de-lighting change
        tab.SelectStep(MaterialStep.Source);
        tab.Drag("Upscale", 2);
        foreach (var w in WaitIdle("upscale")) yield return w;
        if (fail != null) { yield return fail; yield break; }
        var lats1k = new List<(double S, double R, double A)>();
        var lat1k = new List<string> { $"Upscale {tab.Latency.Small:0}/{tab.Latency.Refined:0}/{tab.Latency.All:0}" };
        int side1k = tab.Pixels(MaterialStep.Albedo)!.Width;
        foreach (var (step, prop, vals) in new (MaterialStep, string, double[])[]
        {
            (MaterialStep.Height, "HeightContrast", [2.0]), (MaterialStep.Normal, "NormalStrength", [5.0]),
            (MaterialStep.Roughness, "RoughnessBase", [0.65]), (MaterialStep.Albedo, "Delight", [0.7]),
            (MaterialStep.Height, "HeightContrast", [2.2]),
        })
        {
            tab.SelectStep(step);
            foreach (var w in WaitIdle($"step {step}")) yield return w;
            if (fail != null) { yield return fail; yield break; }
            tab.Drag(prop, vals);
            foreach (var w in WaitIdle(prop)) yield return w;
            if (fail != null) { yield return fail; yield break; }
            lats1k.Add(tab.Latency);
            lat1k.Add($"{prop} {tab.Latency.Small:0}/{tab.Latency.Refined:0}/{tab.Latency.All:0}");
        }
        // a map imported (a synthetic gradient, outside the project) and one skipped, one undo step each
        var grad = Image.CreateEmpty(64, 64, false, Image.Format.Rgba8);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                grad.SetPixel(x, y, new Color(x / 63f, x / 63f, x / 63f));
        var gradFile = GameSettings.Globalize($"{MapStore.Dir}/check-roughness.png");
        grad.SavePng(gradFile);
        v = global.GetVersion();
        if (tab.Import(MaterialStep.Roughness, gradFile) is { } impErr || global.GetVersion() != v + 1)
        {
            yield return $"import: {global.GetVersion() - v} undo step(s)";
            yield break;
        }
        tab.SetMode(MaterialStep.Occlusion, StepMode.Skip);
        if (global.GetVersion() != v + 2)
        {
            yield return "skip: not one undo step";
            yield break;
        }
        foreach (var w in WaitIdle("import and skip")) yield return w;
        if (fail != null) { yield return fail; yield break; }
        var rough = tab.Pixels(MaterialStep.Roughness)!;
        var ao = tab.Pixels(MaterialStep.Occlusion)!;
        int Px(GdFfx.Remaster.Texturing.RgbaImage im, int x, int y) => im.Rgba[(y * im.Width + x) * 4];
        if (Px(rough, 1, rough.Height / 2) > 40 || Px(rough, rough.Width - 2, rough.Height / 2) < 215 || ao.Rgba.Where((b, i) => i % 4 == 0).Any(b => b != 255)
            || tab.OrmPixels is not { } orm || orm.Rgba[(rough.Width - 2) * 4 + 1] < 215)
        {
            yield return $"import/skip: roughness {Px(rough, 1, rough.Height / 2)}..{Px(rough, rough.Width - 2, rough.Height / 2)}, occlusion not all white or the ORM not packed";
            yield break;
        }
        // the next texture: the settings kept, its own maps made
        var albedoBefore = tab.Pixels(MaterialStep.Albedo)!;
        tab.Next(1);
        if (tab.Current != ids[1] || !Mathf.IsEqualApprox(tab.Recipe.HeightContrast, 2.2f) || tab.Recipe.RoughnessMode != StepMode.Import
            || tab.Recipe.OcclusionMode != StepMode.Skip)
        {
            yield return $"switch: current {tab.Current}, contrast {tab.Recipe.HeightContrast}, roughness {tab.Recipe.RoughnessMode}, occlusion {tab.Recipe.OcclusionMode}";
            yield break;
        }
        foreach (var w in WaitIdle("the second texture")) yield return w;
        if (fail != null) { yield return fail; yield break; }
        if (tab.Pixels(MaterialStep.Albedo) is not { } albedoNext || albedoNext.Rgba.SequenceEqual(albedoBefore.Rgba))
        {
            yield return "switch: the albedo was not made for the next texture";
            yield break;
        }
        // shots of the tab (the height step, the studio; an HDRI; the game piece in the map's light, classic on the left)
        if (shots)
        {
            tab.SelectStep(MaterialStep.Height);
            foreach (var w in WaitIdle("shot")) yield return w;
            for (int i = 0; i < 20; i++) yield return null;
            Window($"{shotDir}/materials-studio.png");
            tab.SetLightForCheck(PreviewLight.Hdri, 0);
            for (int i = 0; i < 30; i++) yield return null;
            Window($"{shotDir}/materials-hdri.png");
            tab.SetShapeForCheck(MaterialPreview.Object);
            tab.SetCompareForCheck(1);
            tab.SetLightForCheck(PreviewLight.Map, 0);
            tab.SelectStep(MaterialStep.Normal);
            for (int i = 0; i < 40; i++) yield return null;
            Window($"{shotDir}/materials-object.png");
        }
        // saved: the maps at the full size, the material, the settings, one undo step
        var saveId = tab.Current!;
        v = global.GetVersion();
        if (tab.Save() is { } saveErr)
        {
            yield return saveErr;
            yield break;
        }
        double ts = _t;
        var saveClock = System.Diagnostics.Stopwatch.StartNew();
        while (tab.Saving)
        {
            if (_t - ts > 120) { yield return "the save did not finish"; yield break; }
            yield return null;
        }
        _saveMs = saveClock.Elapsed.TotalMilliseconds;
        if (global.GetVersion() != v + 1 || TextureStore.Find(saveId) is not { Material.Length: > 0, Recipe: { } saved } entry)
        {
            yield return $"save: {tab.SaveResult}; {global.GetVersion() - v} undo step(s)";
            yield break;
        }
        var matText = FileAccess.GetFileAsString(TextureStore.Absolute(entry.Material));
        if (saved.Version != 2 || !Mathf.IsEqualApprox(saved.HeightContrast, 2.2f) || saved.Upscale != 2 || saved.OcclusionMode != StepMode.Skip || saved.RoughnessMode != StepMode.Import
            || !saved.RoughnessFile.Contains("import-roughness") || saved.RoughnessFile.StartsWith('/') || saved.RoughnessFile.Contains(':')
            || !matText.Contains("orm_texture = ExtResource") || !matText.Contains("albedo_texture = ExtResource"))
        {
            yield return $"saved: version {saved.Version}, contrast {saved.HeightContrast}, occlusion {saved.OcclusionMode}, roughness {saved.RoughnessMode} '{saved.RoughnessFile}'";
            yield break;
        }
        var ormFile = matText.Split('\n').FirstOrDefault(l => l.Contains("id=\"orm\""));
        var ormPath = ormFile?.Split("path=\"")[1].Split('"')[0];
        var ormImg = ormPath != null ? Image.LoadFromFile(ProjectSettings.GlobalizePath(ormPath)) : null;
        if (ormImg == null || ormImg.GetPixel(ormImg.GetWidth() / 2, ormImg.GetHeight() / 2).R < 0.99f)
        {
            yield return $"the saved ORM ({ormPath}) does not hold the skipped occlusion (white)";
            yield break;
        }
        global.Undo();
        if (TextureStore.Find(saveId)?.Material is { Length: > 0 })
        {
            yield return "undo did not take the saved material back";
            yield break;
        }
        global.Redo();
        if (TextureStore.Find(saveId)?.Material is not { Length: > 0 })
        {
            yield return "redo did not save the material again";
            yield break;
        }
        // the open map's textures
        int more = tab.AddOpenMap();
        if (tab.Queue.Count != onMap.Count)
        {
            yield return $"the open map's textures: {tab.Queue.Count} queued, the map draws {onMap.Count}";
            yield break;
        }
        static double Median(IEnumerable<double> x) { var l = x.OrderBy(a => a).ToList(); return l[l.Count / 2]; }
        int side0 = a0.Width;
        var summary = $"latency (ms, small/refined/all, median of {lats.Count}) at {side0} px: {Median(lats.Select(l => l.S)):0}/{Median(lats.Select(l => l.R)):0}/{Median(lats.Select(l => l.A)):0}, "
            + $"at {side1k} px (median of {lats1k.Count}): {Median(lats1k.Select(l => l.S)):0}/{Median(lats1k.Select(l => l.R)):0}/{Median(lats1k.Select(l => l.A)):0}; save {_saveMs:0} ms";
        GD.Print($"ffx editortest: materials {summary}; each at {side0} px: {string.Join("; ", lat)}; at {side1k} px: {string.Join("; ", lat1k)}");
        _seen.Add($"materials: a drag one undo step (height and normal made again, albedo kept; {previewNote}); {summary}; "
            + $"undo/redo; roughness imported, occlusion skipped; switched texture, settings kept; saved {ormImg.GetWidth()} px maps "
            + $"({entry.Material.GetFile()}, one undo step, undone, redone); +{more} from the open map");
        // the 3D view again for the pictures after
        EditorInterface.Singleton.SetMainScreenEditor("3D");
        yield return null;
    }

    private static void Window(string path)
    {
        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"ffx editortest: picture {path}");
    }

    /// <summary>The mean absolute difference of two pictures' RGB (0..255; -1: different sizes).</summary>
    private static double MeanDiff(Image a, Image b)
    {
        if (a.GetSize() != b.GetSize())
            return -1;
        a.Convert(Image.Format.Rgba8);
        b.Convert(Image.Format.Rgba8);
        var x = a.GetData();
        var y = b.GetData();
        long sum = 0;
        for (int i = 0; i < x.Length; i++)
            if (i % 4 != 3)
                sum += System.Math.Abs(x[i] - y[i]);
        return sum / (x.Length * 0.75);
    }

    private void Finish(bool pass, string note)
    {
        if (_finished)
            return;
        _finished = true;
        GD.Print($"ffx editortest: {(pass ? "PASS" : "FAIL")} {note}");
        // the edit scene is made per run (not in git); the overrides stay in DIR for the game's check
        var scene = MapEditRoot.EditScenePath(Key);
        if (FileAccess.FileExists(scene))
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(scene));
        var dir = ProjectSettings.GlobalizePath(MapEditRoot.EditDir);
        if (DirAccess.DirExistsAbsolute(dir) && DirAccess.GetFilesAt(dir).Length == 0)
            DirAccess.RemoveAbsolute(dir);
        var tree = (Engine.GetMainLoop() as SceneTree)!;
        tree.CreateTimer(0.5).Timeout += () => tree.Quit(pass ? 0 : 1);
    }
}
#endif
