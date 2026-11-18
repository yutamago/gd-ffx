#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// R7b's editor check (docs/remaster-editor.md, "Checks"): run in the editor with
/// <c>-- --remaster-editor-rooms --remaster-rooms=res://remaster/check/rooms --remaster-ui-art=DIR/ui.tres</c>
/// (written there, not the project's; <c>--remaster-captures=DIR</c> with the game's capture of
/// <c>ptkl14</c> for its camera). <b>Rebuild in 3D</b> on the 2D-layer map <c>ptkl14</c>: the map
/// opened (its game data), its rebuilt room written (references only), R5's alignment check on it
/// (the floor on the walk mesh: no issue), the scene opened and built (a projected plane a layer,
/// the floor on the walk mesh, the scene camera against the file's and the game's, the walk mesh,
/// triggers and entrances as guides), a mesh of the user's tagged with a layer and projected, the
/// scene saved (the planes, pictures and materials kept out of the file, the user's mesh in it) and
/// still built. <b>UI art</b>: the movies listed, the save screen's art with thumbnails from the
/// player's copy, a generated image assigned to its cursor for every language, undone, redone, a
/// second to Tidus's portrait, the preview drawing both replaced, and the ids written for the game
/// (<c>expect.txt</c> beside the layer). Exits the editor: 0 on PASS, 1 on FAIL.
/// </summary>
public sealed class RoomsEditorTest
{
    public const string Key = "ptkl14";

    private enum Phase { Archive, OpenMap, Rebuild, Align, OpenRoom, Built, Tag, Save, Saved, UiList, UiAssign, UiUndo, UiRedo, UiSecond, Done }

    private readonly RemasterPlugin _plugin;
    private readonly double _timeout = GameSettings.Number("editor-test-timeout", 300);
    private readonly List<string> _seen = new();
    private Phase _phase = Phase.Archive;
    private double _t, _phaseT;
    private bool _finished, _inStep, _acted;
    private string _level = "";

    public RoomsEditorTest(RemasterPlugin plugin)
    {
        _plugin = plugin;
        GD.Print($"ffx editortest: rooms ({RemasterPlugin.RoomsDir}), UI art ({RemasterPlugin.UiArtFile})");
        if (RemasterPlugin.RoomsDir == "res://remake/map" || RemasterPlugin.UiArtFile == UiArt.ProjectFile)
            Finish(false, "--remaster-rooms=DIR and --remaster-ui-art=FILE are required (the check must not write the project's)");
    }

    private UndoRedo Global => _plugin.GetUndoRedo().GetHistoryUndoRedo((int)EditorUndoRedoManager.SpecialHistory.GlobalHistory);

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
                Finish(false, $"timed out in {_phase}");
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
        GD.Print($"ffx editortest: {_phase} -> {p} ({_t:F1} s)");
        _phase = p;
        _phaseT = 0;
        _acted = false;
    }

    private string Path => RemasterPlugin.RoomScenePath(Key);

    private string? Run()
    {
        switch (_phase)
        {
            case Phase.Archive when _plugin.Session.IsOpen && _phaseT > 0.5:
            {
                if (_plugin.Session.Catalogue?.Find(Key) is not { } entry)
                    return $"{Key} is not in the catalogue";
                _level = entry.Level;
                if (FileAccess.FileExists(Path))
                    DirAccess.RemoveAbsolute(GameSettings.Globalize(Path));
                if (_plugin.OpenMap(entry) is { } err)
                    return err;
                Next(Phase.OpenMap);
                break;
            }
            case Phase.OpenMap when _plugin.Current is { Built: true, Is2D: true, GameData: { } } && _phaseT > 0.5:
                Next(Phase.Rebuild);
                break;
            case Phase.Rebuild:
            {
                var err = _plugin.RebuildRoom(_level, open: false);
                if (err != null && err.StartsWith("importing"))
                    return _phaseT > 60 ? err : null;
                if (err != null)
                    return err;
                var text = FileAccess.GetFileAsString(Path);
                if (text.Contains("ArrayMesh") || text.Contains("Texture") || text.Contains("ShaderMaterial"))
                    return $"{Path} holds more than references";
                _seen.Add($"Rebuild in 3D: {Path} written ({text.Length} bytes, references only)");
                Next(Phase.Align);
                break;
            }
            case Phase.Align:
            {
                if (_plugin.Current?.GameData?.CheckScene(Path) is not { } r)
                    return "the alignment check did not run on the rebuilt scene";
                if (r.Issues.Count != 0 || r.Covered == 0)
                    return $"the alignment check on the rebuilt scene: {r.Summary()}";
                _seen.Add($"R5's alignment check on it: {r.Summary()}");
                if (_plugin.RebuildRoom(_level) is { } err)
                    return err;
                Next(Phase.OpenRoom);
                break;
            }
            case Phase.OpenRoom when _plugin.CurrentRoom is { Built: true } && _phaseT > 0.5:
                Next(Phase.Built);
                break;
            case Phase.OpenRoom when _phaseT > 30:
                return $"the rebuilt room did not build ({_plugin.CurrentRoom?.Summary ?? "not open"})";
            case Phase.Built:
                if (CheckBuilt(_plugin.CurrentRoom!) is { } b)
                    return b;
                Next(Phase.Tag);
                break;
            case Phase.Tag:
            {
                // the user's own mesh with a layer projected on it
                var room = _plugin.CurrentRoom!;
                var model = room.GetNode<Node3D>("Model");
                var deepest = room.Layers.Values.OrderByDescending(l => l.Z).First();
                var cam = room.Cameras[deepest.Scene];
                var box = new MeshInstance3D { Name = "UserBox", Mesh = new BoxMesh { Size = new Vector3(40, 40, 40) } };
                box.SetMeta(RebuiltRoom.LayerMeta, $"{deepest.Scene}:{deepest.Id}");
                model.AddChild(box);
                box.Owner = room;
                box.GlobalPosition = RebuiltRoom.Unproject(cam, room.Fovs[deepest.Scene], new Vector2(256, 300), room.DepthOf(deepest) * 0.5f);
                room.Build(_plugin.RoomSources(_level)!.Value.Layers!, room.Data, 19000f, 0f, true, guides: true);
                if (box.MaterialOverride is not ShaderMaterial)
                    return "the tagged mesh got no projected material";
                _seen.Add($"a mesh of the user's tagged {deepest.Scene}:{deepest.Id}: projected ({room.Summary})");
                Next(Phase.Save);
                break;
            }
            case Phase.Save when !_acted:
                _acted = true;
                EditorInterface.Singleton.SaveScene();
                break;
            case Phase.Save when _phaseT > 0.5:
            {
                var text = FileAccess.GetFileAsString(Path);
                if (text.Contains("ArrayMesh") || text.Contains("ShaderMaterial") || text.Contains("Texture2D") || text.Contains("ImageTexture"))
                    return "the saved scene holds generated meshes, materials or pictures";
                if (!text.Contains("BoxMesh") || !text.Contains("ffx_layer"))
                    return "the user's mesh is not in the saved scene";
                var room = _plugin.CurrentRoom!;
                if (!room.Built || room.Projected.Any(p => p.Mesh.MaterialOverride == null))
                    return "the room lost its build on save";
                _seen.Add($"saved ({text.Length} bytes: the proxies, the user's box and its tag; no plane, picture or material), still built after the save");
                Next(Phase.UiList);
                break;
            }
            case Phase.UiList:
            {
                var dock = _plugin.UiArtDock!;
                dock.Refresh();
                var movies = dock.Movies();
                if (movies.Count < 19 || !movies.Contains("saveload_jp.swf"))
                    return $"{movies.Count} movies listed ({string.Join(", ", movies.Take(8))})";
                dock.ShowMovie("saveload_jp.swf");
                var items = dock.Items;
                var face = UiArt.ArtId(SaveLoadView.FaceClass, "_1");
                if (!items.Any(i => i.Id == SaveLoadView.ArrowClass) || !items.Any(i => i.Id == face))
                    return $"the save screen's art misses the cursor or the portraits ({items.Count} ids)";
                var arrow = items.First(i => i.Id == SaveLoadView.ArrowClass);
                var thumbs = items.Count(i => dock.Thumbnail(_plugin.Session.Game!, "saveload_jp.swf", i) != null);
                if (dock.Thumbnail(_plugin.Session.Game!, "saveload_jp.swf", arrow) == null)
                    return "no thumbnail of the cursor";
                _seen.Add($"UI art: {movies.Count} movies; saveload_jp.swf {items.Count} art ids, {thumbs} with thumbnails from the player's copy");
                Next(Phase.UiAssign);
                break;
            }
            case Phase.UiAssign when !_acted:
            {
                _acted = true;
                var dir = RemasterPlugin.UiArtFile.GetBaseDir();
                DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(dir));
                UiArtTest.TestImage(64, 48, new Color(0.2f, 0.9f, 0.4f), new Color(0.1f, 0.3f, 0.1f)).SavePng($"{dir}/cursor.png");
                UiArtTest.TestImage(128, 128, new Color(0.9f, 0.3f, 0.8f), new Color(0.3f, 0.1f, 0.3f)).SavePng($"{dir}/portrait.png");
                var dock = _plugin.UiArtDock!;
                dock.Select(SaveLoadView.ArrowClass);
                if (dock.Assign(SaveLoadView.ArrowClass, $"{dir}/cursor.png") is { } e)
                    return e;
                break;
            }
            case Phase.UiAssign when _phaseT > 0.3:
                if (UiArt.Find("saveload_kr.swf", SaveLoadView.ArrowClass) is not { } found || !found.Image.EndsWith("cursor.png"))
                    return "the cursor is not replaced for every language's movie";
                if (!_plugin.UiArtDock!.Preview.Replaced.Contains(SaveLoadView.ArrowClass))
                    return "the preview does not draw the replaced cursor";
                _seen.Add($"the cursor assigned a generated image (every language's: saveload): {RemasterPlugin.UiArtFile}");
                Next(Phase.UiUndo);
                break;
            case Phase.UiUndo when !_acted:
                _acted = true;
                Global.Undo();
                break;
            case Phase.UiUndo when _phaseT > 0.3:
                if (UiArt.Find("saveload_jp.swf", SaveLoadView.ArrowClass) != null)
                    return "undo left the replacement";
                Next(Phase.UiRedo);
                break;
            case Phase.UiRedo when !_acted:
                _acted = true;
                Global.Redo();
                break;
            case Phase.UiRedo when _phaseT > 0.3:
                if (UiArt.Find("saveload_jp.swf", SaveLoadView.ArrowClass) == null)
                    return "redo did not bring the replacement back";
                _seen.Add("undo: classic; redo: replaced");
                Next(Phase.UiSecond);
                break;
            case Phase.UiSecond when !_acted:
            {
                _acted = true;
                var dock = _plugin.UiArtDock!;
                var face = UiArt.ArtId(SaveLoadView.FaceClass, "_1");
                dock.Select(face);
                if (dock.Assign(face, $"{RemasterPlugin.UiArtFile.GetBaseDir()}/portrait.png") is { } e)
                    return e;
                break;
            }
            case Phase.UiSecond when _phaseT > 0.3:
            {
                var dock = _plugin.UiArtDock!;
                var face = UiArt.ArtId(SaveLoadView.FaceClass, "_1");
                dock.Select(face);
                var replaced = dock.Preview.Replaced;
                if (!replaced.Contains(face) || !replaced.Contains(SaveLoadView.ArrowClass))
                    return $"the preview draws {string.Join(", ", replaced)} replaced";
                using (var f = FileAccess.Open($"{RemasterPlugin.UiArtFile.GetBaseDir()}/expect.txt", FileAccess.ModeFlags.Write))
                    f.StoreString($"{SaveLoadView.ArrowClass}\n{face}\n");
                _seen.Add($"Tidus's portrait ({face}) assigned; the preview's save row draws {string.Join(", ", replaced)} replaced");
                Next(Phase.Done);
                break;
            }
            case Phase.Done:
                Finish(true, string.Join("; ", _seen));
                break;
        }
        return null;
    }

    private string? CheckBuilt(RebuiltRoom room)
    {
        int layers = room.Layers.Count;
        var planes = room.Projected.Where(p => p.Mesh is RoomProxy { Kind: RoomProxyKind.Layer, Mesh: not null }).ToList();
        var floor = room.Projected.FirstOrDefault(p => p.Mesh is RoomProxy { Kind: RoomProxyKind.Floor, Mesh: not null }).Mesh;
        if (layers == 0 || planes.Count != layers || floor == null)
            return $"{planes.Count} planes for {layers} layers, floor {(floor != null ? "made" : "missing")} ({room.Summary})";
        if (room.Data?.Main is not { } walk || floor.Mesh!.GetFaces().Length / 3 != Enumerable.Range(0, walk.Count).Count(t => WalkMesh.Walkable(walk.Kind(t))))
            return "the floor is not the walk mesh's walkable triangles";
        // the scene camera: the file's, and the game's as the capture has it
        if (room.Guides?.GetNodeOrNull<Camera3D>("Scene0Camera") is not { } cam)
            return "no scene camera guide";
        if (RebuiltRoom.CameraOf(room.Data, 0) is not { } file || cam.GlobalTransform.Origin.DistanceTo(file.Camera.Origin) > 0.001f)
            return "the camera guide is not the file's camera";
        string game = "no capture of the game's camera";
        if (MapCapture.Read(_level) is { CameraValid: true } c)
        {
            float d = cam.GlobalPosition.DistanceTo(c.Camera.Origin);
            float a = Mathf.RadToDeg((-cam.GlobalBasis.Z).AngleTo(-c.Camera.Basis.Z.Normalized()));
            if (d > 0.05f || a > 0.05f)
                return $"the scene camera is {d:F3} units and {a:F3}° off the game's";
            game = $"the game's camera {d:F3} units, {a:F3}° away";
        }
        int triggers = room.Guides.GetNodeOrNull("Triggers")?.GetChildCount() ?? 0;
        int entrances = room.Guides.GetNodeOrNull("Entrances")?.GetChildCount() ?? 0;
        if (room.Guides.GetNodeOrNull("WalkMesh") == null || triggers == 0 || entrances == 0)
            return $"guides: walk mesh {room.Guides.GetNodeOrNull("WalkMesh") != null}, {triggers} triggers, {entrances} entrances";
        // each plane projects its layer from the scene camera: a corner of its rectangle back to its pixel
        foreach (var (mi, l) in planes)
        {
            var v = mi.GlobalTransform * mi.Mesh!.GetFaces()[0];
            var view = file.Camera.AffineInverse() * v;
            float tv = Mathf.Tan(Mathf.DegToRad(file.FovY) / 2f);
            var px = new Vector2(256 + 256 * view.X / (-view.Z * tv * 4f / 3f), 224 - 224 * view.Y / (-view.Z * tv));
            bool corner = new[] { l.Pixels.Position, l.Pixels.End, new Vector2(l.Pixels.End.X, l.Pixels.Position.Y), new Vector2(l.Pixels.Position.X, l.Pixels.End.Y) }
                .Any(p => p.DistanceTo(px) < 0.05f);
            if (!corner || Mathf.Abs(-view.Z - room.DepthOf(l)) > 0.05f)
                return $"layer {l.Id}'s plane: a corner at pixel {px}, depth {-view.Z:F2} (want a corner of {l.Pixels}, depth {room.DepthOf(l):F2})";
        }
        _seen.Add($"opened and built: {room.Summary}; the floor is the walk mesh ({floor.Mesh.GetFaces().Length / 3} triangles); "
            + $"each plane on its layer's pixels at 10 z; the scene camera the file's, {game}; guides: walk mesh, {triggers} trigger(s), {entrances} entrance(s)");
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
