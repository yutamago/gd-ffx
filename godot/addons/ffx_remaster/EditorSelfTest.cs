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

    private enum Phase { Archive, Open, Edit, Save, Reopen, Status, Shot, Done }

    private readonly RemasterPlugin _plugin;
    private readonly double _timeout = GameSettings.Number("editor-test-timeout", 240);
    private readonly List<string> _seen = new();
    private Phase _phase = Phase.Archive;
    private double _t, _phaseT;
    private int _frames;
    private bool _finished;
    private readonly Dictionary<string, Transform3D> _before = new();
    private Transform3D _boxWorld;

    public EditorSelfTest(RemasterPlugin plugin)
    {
        _plugin = plugin;
        GD.Print($"ffx editortest: start (overrides into {MapStore.Dir})");
        if (MapStore.Dir == RemasterOverrides.BaseDir)
            Finish(false, "--remaster-maps=DIR is required (the check must not write the project's overrides)");
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
                Next(GameSettings.Arg("editor-shot") is { Length: > 0 } && DisplayServer.GetName() != "headless" ? Phase.Shot : Phase.Done);
                return null;
            case Phase.Shot:
                return Shot();
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
        // then the dock in front (the catalogue, the map's status, its parts with their edits)
        var dockPath = path0.GetBaseDir().PathJoin(path0.GetFile().GetBaseName() + "-dock.png");
        EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture().GetImage().SavePng(dockPath);
        _seen.Add($"pictures {path0}, its -window.png and -dock.png");
        Next(Phase.Done);
        return null;
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
