#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Characters;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The character tools' editor check (docs/remaster-editor.md, "Checks"; R6): run in the editor
/// with <c>-- --remaster-editor-character --remaster-characters=DIR</c> (the remade scene and rig
/// go to DIR, not the project). Through the code the Characters dock uses: the character list
/// (Tidus by his name, the search), c001 opened (the classic model with its motions), a motion
/// played and scrubbed, the original stand-in humanoid (<see cref="StandInHumanoid"/>, saved to
/// DIR/standin) assigned as the remake (its rig made automatically; undone and redone), the
/// remake driven by the playing motion (its bones point where the classic skeleton's do), the
/// automatic mapping, one bone mapped by hand (kept by the automatic mapping), saved, undone and
/// redone, saved again, and the character opened again with the hand-set mapping. With
/// <c>--editor-shot=FILE.png</c> under a renderer, a picture of the 3D view. Writes
/// <c>DIR/expect.json</c> for the game's <c>--selftest-character --character-scene=DIR/c001.tscn</c>.
/// Exits the editor: 0 on PASS, 1 on FAIL.
/// </summary>
public sealed class CharacterEditorTest
{
    public const string Id = "c001";
    /// <summary>The bone the check maps by hand, and the element it takes (the game's spine bone under the one the automatic mapping picks).</summary>
    public const string HandBone = "Spine_Upper";
    public const int HandElement = 5;

    private enum Phase { Archive, Open, Motion, Assign, Drive, Hand, Reopen, Shot, Done }

    private readonly RemasterPlugin _plugin;
    private readonly double _timeout = GameSettings.Number("editor-test-timeout", 300);
    private readonly List<string> _seen = new();
    private Phase _phase = Phase.Archive;
    private double _t, _phaseT;
    private bool _finished, _inStep;
    private Transform3D _poseA;
    private float _maxAngle;
    private int _driven;
    private Transform3D? _remadeFirst;
    private float _remadeMoved;
    private int _autoElement = -1;
    private bool _shotAimed;

    public CharacterEditorTest(RemasterPlugin plugin)
    {
        _plugin = plugin;
        GD.Print($"ffx editortest: characters (into {CharacterStore.Dir})");
        if (CharacterStore.Dir == CharacterScene.ProjectDir)
            Finish(false, "--remaster-characters=DIR is required (the check must not write the project's characters)");
    }

    private EditorUndoRedoManager Undo => _plugin.GetUndoRedo();

    private UndoRedo Global => Undo.GetHistoryUndoRedo((int)EditorUndoRedoManager.SpecialHistory.GlobalHistory);

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
                Finish(false, $"timed out in {_phase} ({_plugin.CurrentCharacter?.Status})");
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
    }

    private string? Run()
    {
        switch (_phase)
        {
            case Phase.Archive when _plugin.Session.IsOpen:
            {
                var cat = _plugin.Characters;
                if (cat == null || cat.Entries.Count < 100)
                    return $"the character list has {cat?.Entries.Count ?? 0} entries";
                var e = cat.Find(Id);
                if (e?.Title() != "Tidus" || e.Kind != "party" || !e.Motions)
                    return $"{Id} is {e?.Title()} ({e?.Kind}, motions {e?.Motions})";
                var dock = _plugin.CharacterDock!;
                dock.SetSearch("tidus");
                var listed = dock.Listed();
                if (!listed.Contains(Id))
                    return $"searching \"tidus\" lists {string.Join(", ", listed)}";
                dock.SetSearch("");
                _seen.Add($"{cat.Entries.Count} characters ({cat.Entries.Count(x => x.Kind == "party")} party, {cat.Entries.Count(x => x.Kind == "monster")} monsters, "
                    + $"{cat.Entries.Count(x => x.Names.Count > 0)} named by the game); \"tidus\" finds {string.Join(", ", listed)}");
                if (_plugin.OpenCharacter(Id) is { } err)
                    return err;
                Next(Phase.Open);
                break;
            }
            case Phase.Open when _plugin.CurrentCharacter is { Built: true } root:
            {
                if (root.ClassicSkeleton == null || root.Player == null)
                    return $"the classic {Id} has no skeleton or motions ({root.Status})";
                var motions = root.Motions();
                if (motions.Count < 10)
                    return $"only {motions.Count} motions";
                // a motion that moves: the longest of the first group's
                var motion = motions.Where(m => m.StartsWith("resident", System.StringComparison.Ordinal))
                    .OrderByDescending(m => root.Player.GetAnimation(m).Length).First();
                root.Play(motion);
                root.Scrub(0);
                _poseA = root.ClassicSkeleton.GetBoneGlobalPose(1);
                root.Scrub(root.Length * 0.5);
                var poseB = root.ClassicSkeleton.GetBoneGlobalPose(1);
                if (_poseA.IsEqualApprox(poseB))
                    return $"scrubbing {motion} did not move the hips";
                root.Scrub(0);
                if (!_poseA.IsEqualApprox(root.ClassicSkeleton.GetBoneGlobalPose(1)))
                    return "scrubbing back did not give the same pose";
                root.Resume();
                _seen.Add($"{Id} opened: {motions.Count} motions; {motion} ({root.Length:F2} s) played and scrubbed");
                Next(Phase.Motion);
                break;
            }
            case Phase.Motion when _phaseT > 0.6:
            {
                var root = _plugin.CurrentCharacter!;
                if (!root.Playing || root.Time <= 0)
                    return "the motion does not play";
                // the stand-in, made in code, saved as a user's model would be
                var dir = CharacterStore.Dir.TrimEnd('/') + "/standin";
                var path = dir + "/stand_in_humanoid.tscn";
                if (StandInHumanoid.Save(path) is { } err)
                    return err;
                var v = Global.GetVersion();
                if (_plugin.AssignModel(path) is { } e2)
                    return e2;
                if (Global.GetVersion() != v + 1)
                    return "assigning was not one undo step";
                var rig = CharacterStore.Rig(Id);
                if (rig == null || rig.Bones.Count(b => b.Element >= 0) < 18)
                    return $"the automatic rig mapped {rig?.Bones.Count(b => b.Element >= 0) ?? 0} bones";
                var scene = CharacterScene.ScenePath(CharacterStore.Dir, Id);
                if (!FileAccess.FileExists(scene) || !FileAccess.FileExists(CharacterScene.RigPath(CharacterStore.Dir, Id)))
                    return "the scene or rig was not written";
                Global.Undo();
                if (FileAccess.FileExists(scene) || root.Remade != null)
                    return "undo did not take the assignment back";
                Global.Redo();
                if (!FileAccess.FileExists(scene) || root.Remade == null || root.Driver == null)
                    return "redo did not assign it again";
                _autoElement = CharacterStore.Rig(Id)!.Find(HandBone)?.Element ?? -1;
                _seen.Add($"the stand-in assigned: {CharacterStore.Rig(Id)!.Bones.Count(b => b.Element >= 0)} bones mapped automatically, "
                    + $"{CharacterStore.Rig(Id)!.Cloth.Count} cloth chains, {CharacterStore.Rig(Id)!.Face.Count} face rows, {CharacterStore.Rig(Id)!.Attach.Count} attachments; undone and redone");
                Next(Phase.Drive);
                break;
            }
            case Phase.Drive:
            {
                var root = _plugin.CurrentCharacter!;
                if (root.Driver is not { } d)
                    return "no driver";
                var err = d.Error(root.Game, root.Has);
                _maxAngle = System.Math.Max(_maxAngle, err.MaxAngle);
                _driven++;
                var spine = d.Skeleton.GetBoneGlobalPose(d.Skeleton.FindBone("Spine_Upper"));
                _remadeFirst ??= spine;
                _remadeMoved = System.Math.Max(_remadeMoved, Mathf.RadToDeg(_remadeFirst.Value.Basis.GetRotationQuaternion().AngleTo(spine.Basis.GetRotationQuaternion())));
                if (_phaseT < 1.5)
                    break;
                if (_maxAngle > 3f)
                    return $"the remade bones point {_maxAngle:F2}° away from the classic skeleton's";
                if (_remadeMoved < 0.2f)
                    return $"the remade spine did not move with the motion ({_remadeMoved:F3}°)";
                _seen.Add($"driven by the motion in the editor: {_driven} frames, the directions within {_maxAngle:F2}° of the classic skeleton's, the spine turning {_remadeMoved:F1}°");
                Next(Phase.Hand);
                break;
            }
            case Phase.Hand:
            {
                if (_plugin.AutoMapCharacter() is { } e0)
                    return e0;
                var v = Global.GetVersion();
                if (_plugin.SetBoneElement(HandBone, HandElement) is { } e1)
                    return e1;
                if (Global.GetVersion() != v + 1)
                    return "the hand mapping was not one undo step";
                var m = CharacterStore.Rig(Id)!.Find(HandBone);
                if (m is not { Element: HandElement, ByHand: true })
                    return $"{HandBone} is {m?.Element} by hand {m?.ByHand}";
                if (_plugin.AutoMapCharacter() is { } e2)
                    return e2;
                if (CharacterStore.Rig(Id)!.Find(HandBone)?.Element != HandElement)
                    return "the automatic mapping overwrote the hand-set row";
                if (_plugin.SaveCharacter() is { } e3)
                    return e3;
                var saved = FileAccess.GetFileAsString(CharacterScene.RigPath(CharacterStore.Dir, Id));
                if (!saved.Contains($"Bone = \"{HandBone}\"") || !saved.Contains($"Element = {HandElement}"))
                    return "the saved rig does not have the hand-set row";
                // undo the automatic mapping and the hand row, redo them
                Global.Undo();
                Global.Undo();
                if (CharacterStore.Rig(Id)!.Find(HandBone)?.Element != _autoElement)
                    return $"undo did not take the hand mapping back ({CharacterStore.Rig(Id)!.Find(HandBone)?.Element}, automatic {_autoElement})";
                Global.Redo();
                Global.Redo();
                if (CharacterStore.Rig(Id)!.Find(HandBone) is not { Element: HandElement, ByHand: true })
                    return "redo did not map it by hand again";
                if (_plugin.SaveCharacter() is { } e4)
                    return e4;
                var again = FileAccess.GetFileAsString(CharacterScene.RigPath(CharacterStore.Dir, Id));
                _seen.Add($"{HandBone} mapped by hand to element {HandElement} (automatic: {_autoElement}), kept by the automatic mapping, saved, undone and redone"
                    + (again == saved ? ", saved again the same" : ", saved again (changed)"));
                if (again != saved)
                    return "saving the same rig again changed the file";
                // reopen: the scene closed, the rig read from its file
                CharacterStore.Forget(Id);
                EditorInterface.Singleton.CloseScene();
                if (_plugin.OpenCharacter(Id) is { } e5)
                    return e5;
                Next(Phase.Reopen);
                break;
            }
            case Phase.Reopen when _plugin.CurrentCharacter is { Built: true } root:
            {
                var rig = CharacterStore.Rig(Id);
                if (rig?.Find(HandBone) is not { Element: HandElement, ByHand: true })
                    return "opened again, the hand-set mapping is gone";
                if (root.Remade == null || root.Driver == null)
                    return "opened again, the remake is not shown";
                _seen.Add($"opened again: the remake shown, {rig.Bones.Count(b => b.Element >= 0)} bones mapped, the hand-set row kept");
                // the game's check takes this scene
                var expect = new Godot.Collections.Dictionary { ["scene"] = CharacterScene.ScenePath(CharacterStore.Dir, Id), ["hand_bone"] = HandBone, ["hand_element"] = HandElement };
                using (var f = FileAccess.Open(CharacterStore.Dir.TrimEnd('/') + "/expect.json", FileAccess.ModeFlags.Write))
                    f?.StoreString(Json.Stringify(expect));
                if (GameSettings.Arg("editor-shot") is { Length: > 0 } && DisplayServer.GetName() != "headless")
                {
                    root.Play(root.Motions().First());
                    root.Resume();
                    Next(Phase.Shot);
                }
                else
                    Next(Phase.Done);
                break;
            }
            case Phase.Shot:
            {
                var root = _plugin.CurrentCharacter!;
                var vp = EditorInterface.Singleton.GetEditorViewport3D(0);
                var cam = vp.GetCamera3D();
                // both characters from the front (the game's characters face -z), level with their chests
                var target = root.GlobalTransform.Origin + new Vector3(7, 9, 0);
                if (!_shotAimed)
                {
                    cam.GlobalTransform = new Transform3D(Basis.LookingAt(-new Vector3(0, 0.15f, -1).Normalized(), Vector3.Up), target + new Vector3(0, 0.15f, -1).Normalized() * 40);
                    _shotAimed = true;
                }
                if (_phaseT < 2.0)
                    break;
                var path = GameSettings.Arg("editor-shot")!;
                vp.GetTexture().GetImage().SavePng(path);
                _seen.Add($"picture {path}");
                Next(Phase.Done);
                break;
            }
            case Phase.Done:
                Finish(true, string.Join("; ", _seen));
                break;
        }
        return null;
    }

    private void Finish(bool pass, string note)
    {
        if (_finished)
            return;
        _finished = true;
        GD.Print($"ffx editortest: {(pass ? "PASS" : "FAIL")} {note}{(pass ? "" : $" [{string.Join("; ", _seen)}]")}");
        var scene = CharacterEditRoot.EditScenePath(Id);
        if (FileAccess.FileExists(scene))
            DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(scene));
        var dir = ProjectSettings.GlobalizePath(CharacterEditRoot.EditDir);
        if (DirAccess.DirExistsAbsolute(dir) && DirAccess.GetFilesAt(dir).Length == 0)
            DirAccess.RemoveAbsolute(dir);
        var tree = (Engine.GetMainLoop() as SceneTree)!;
        tree.CreateTimer(0.5).Timeout += () => tree.Quit(pass ? 0 : 1);
    }
}
#endif
