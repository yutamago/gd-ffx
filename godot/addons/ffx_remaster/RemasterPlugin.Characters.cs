#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Characters;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The character tools (docs/remaster-editor.md, "Characters"; docs/remaster-plan.md R6): the
/// character list, a character opened with its motions, a remade model assigned (its rig built
/// automatically), the mapping fixed by hand, saved; each edit one step of the editor's undo
/// history.
/// </summary>
public partial class RemasterPlugin
{
    private CharacterDock? _characters;
    private CharacterCatalogue? _catalogue;

    public CharacterDock? CharacterDock => _characters;

    /// <summary>The game's characters (read once the archive is open).</summary>
    public CharacterCatalogue? Characters => _catalogue ??= Session.Game is { } g && Session.IsOpen ? CharacterCatalogue.Read(g, "us", "jp", "fr", "de", "it", "es") : null;

    /// <summary>The character edit scene being edited (null: another scene, or none).</summary>
    public CharacterEditRoot? CurrentCharacter => EditorInterface.Singleton.GetEditedSceneRoot() as CharacterEditRoot;

    private void EnterCharacters()
    {
        _characters = new CharacterDock(this);
        AddDock(_characters);
    }

    private void ExitCharacters()
    {
        if (_characters == null)
            return;
        RemoveDock(_characters);
        _characters.QueueFree();
        _characters = null;
    }

    /// <summary>Opens <paramref name="id"/>'s edit scene (made when missing; the classic import with its motions asked for); an error or null.</summary>
    public string? OpenCharacter(string id)
    {
        if (!Session.IsOpen)
            return "the archive is not open";
        if (Characters?.Find(id) == null)
            return $"{id} is not a character of the archive";
        var path = CharacterEditRoot.EditScenePath(id);
        if (!ResourceLoader.Exists(path))
        {
            DirAccess.MakeDirRecursiveAbsolute(CharacterEditRoot.EditDir);
            var root = new CharacterEditRoot { Name = id, Character = id };
            var packed = new PackedScene();
            var err = packed.Pack(root);
            root.Free();
            if (err != Error.Ok || ResourceSaver.Save(packed, path) != Error.Ok)
                return $"cannot write {path}";
            EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);
        }
        Session.Classic?.Path("chr-motions", id);
        Session.Classic?.Path("chr", id);
        EditorInterface.Singleton.OpenSceneFromPath(path);
        return null;
    }

    /// <summary>The edit scene built (the dock shows its motions and mapping).</summary>
    public void CharacterBuilt(CharacterEditRoot root) => _characters?.CharacterChanged();

    /// <summary>The classic skeleton as the rig builder sees it, the attachment points and the PS2 skeleton's parents, from the open character.</summary>
    private (SkeletonInfo Game, IReadOnlyDictionary<int, int> Points, SkeletonInfo? Bind)? GameSide(CharacterEditRoot root)
    {
        if (root.ClassicSkeleton == null || Characters?.Find(root.Character) is not { } entry || Session.Game == null)
            return null;
        var info = SkeletonInfo.Of(root.ClassicSkeleton, root.ClassicPlace);
        var bind = SkeletonInfo.FromBind(Session.Game.ClassicCharacterSkeleton(root.Character));
        return (info, entry.Points, bind);
    }

    /// <summary>The remade model's skeleton (its own rest, unscaled) and meshes, from a fresh instance of <paramref name="scene"/>.</summary>
    private static (SkeletonInfo Info, Skeleton3D Skeleton, Node3D Model, Transform3D Place)? RemadeSide(string scene)
    {
        if (ResourceLoader.Load(scene) is not PackedScene ps || ps.Instantiate() is not Node3D model)
            return null;
        var skel = RemadeCharacter.FindSkeleton(model);
        if (skel == null)
        {
            model.Free();
            return null;
        }
        Transform3D t = Transform3D.Identity;
        for (Node? p = skel; p != null && p != model; p = p.GetParent())
            if (p is Node3D p3)
                t = p3.Transform * t;
        return (SkeletonInfo.Of(skel, Transform3D.Identity), skel, model, t);
    }

    /// <summary>
    /// Assigns the scene <paramref name="modelScene"/> (a <c>.tscn</c>, <c>.scn</c> or <c>.glb</c>:
    /// any skeleton) as the remake of the open character: its rig built (labels, alignment,
    /// mapping, rest offsets, cloth, face, attachments), the scene and rig written into the store's
    /// folder; one undo step (the files as they were). An error or null.
    /// </summary>
    public string? AssignModel(string modelScene)
    {
        if (CurrentCharacter is not { Built: true } root)
            return "no character is open";
        if (GameSide(root) is not var (game, points, bind))
            return "the classic skeleton is not loaded";
        if (RemadeSide(modelScene) is not var (_, skel, model, place))
            return $"{modelScene} is not a 3D scene with a skeleton";
        var rig = new CharacterRig { Character = root.Character, Model = modelScene };
        var meshes = model.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().ToList();
        var cloth = root.Classic != null ? ClothView.Vertices(root.Classic) : null;
        var result = RigBuilder.Build(rig, game, points, skel, place, meshes, cloth, bind);
        model.Free();
        if (result == null)
            return $"the rig builder found no humanoid in {modelScene} or the classic skeleton";
        var id = root.Character;
        var before = CharacterStore.Files(id);
        var path = CharacterScene.Save(CharacterStore.Dir, id, rig, modelScene, null, out var err);
        if (path == null)
            return err;
        var after = CharacterStore.Files(id);
        CharacterStore.Put(id, rig);
        int b = KeepFiles(before), a = KeepFiles(after);
        Undo.CreateAction($"Assign {modelScene.GetFile()} as {id}'s remake", UndoRedo.MergeMode.Disable, this);
        Undo.AddDoMethod(this, MethodName.ApplyFiles, id, a);
        Undo.AddUndoMethod(this, MethodName.ApplyFiles, id, b);
        Undo.CommitAction(false);
        GD.Print($"ffx remaster: {id} remade by {modelScene}: {string.Join("; ", result.Notes)}");
        root.LoadRemade();
        _characters?.CharacterChanged();
        return null;
    }

    private readonly List<(byte[]? Scene, byte[]? Rig)> _files = new();

    private int KeepFiles((byte[]?, byte[]?) f)
    {
        _files.Add(f);
        return _files.Count - 1;
    }

    /// <summary>Writes a character's files as kept (the undo history calls it).</summary>
    public void ApplyFiles(string id, int index)
    {
        var (scene, rig) = _files[index];
        CharacterStore.Restore(id, scene, rig);
        if (CurrentCharacter is { Built: true } root && root.Character == id)
            root.LoadRemade();
        _characters?.CharacterChanged();
    }

    // ---- rig edits: one undo step each (the rig's states kept) -------------------------------

    private readonly List<CharacterRig> _rigStates = new();

    private int KeepRig(CharacterRig rig)
    {
        _rigStates.Add(rig.Copy());
        return _rigStates.Count - 1;
    }

    /// <summary>Changes the open character's rig by <paramref name="edit"/> (the rest offsets made again) as one undo step; an error or null.</summary>
    public string? EditRig(string action, System.Func<CharacterRig, SkeletonInfo, SkeletonInfo, Transform3D, string?> edit, bool recomputeRest = true)
    {
        if (CurrentCharacter is not { Built: true } root)
            return "no character is open";
        var rig = CharacterStore.Rig(root.Character);
        if (rig == null)
            return $"{root.Character} has no remake assigned";
        if (GameSide(root) is not var (game, _, bind))
            return "the classic skeleton is not loaded";
        if (RemadeSide(rig.Model) is not var (remade, _, model, place))
            return $"{rig.Model} does not load";
        model.Free();
        int before = KeepRig(rig);
        var work = rig.Copy();
        if (edit(work, game, remade, place) is { } fail)
            return fail;
        if (recomputeRest)
            RigBuilder.ComputeRest(work, game, remade, place, bind);
        int after = KeepRig(work);
        Undo.CreateAction(action, UndoRedo.MergeMode.Disable, this);
        Undo.AddDoMethod(this, MethodName.ApplyRig, root.Character, after);
        Undo.AddUndoMethod(this, MethodName.ApplyRig, root.Character, before);
        Undo.CommitAction();
        return null;
    }

    /// <summary>Puts a kept rig state into the rig being edited (the undo history calls it) and drives the remade model with it.</summary>
    public void ApplyRig(string id, int index)
    {
        if (CharacterStore.Rig(id) is not { } rig)
            return;
        var s = _rigStates[index];
        rig.Status = s.Status;
        rig.Notes = s.Notes;
        rig.Model = s.Model;
        rig.Align = s.Align;
        rig.Bones = new Godot.Collections.Array<RigBone>(s.Bones.Select(b => b.Copy()));
        rig.Cloth = new Godot.Collections.Array<ClothChain>(s.Cloth.Select(c => c.Copy()));
        rig.Face = new Godot.Collections.Array<FaceMap>(s.Face.Select(f => f.Copy()));
        rig.Attach = new Godot.Collections.Array<AttachMap>(s.Attach.Select(a => a.Copy()));
        if (CurrentCharacter is { Built: true } root && root.Character == id)
            root.LoadRemade();
        _characters?.CharacterChanged();
    }

    /// <summary>The automatic mapping again (rows set by hand kept).</summary>
    public string? AutoMapCharacter() => EditRig("Map the bones automatically", (rig, game, remade, _) =>
    {
        var gl = RigBuilder.GameLabels(game, Characters?.Find(CurrentCharacter!.Character)?.Points);
        var rl = RigBuilder.Label(remade, RigBuilder.GuessUp(remade));
        if (gl == null || rl == null)
            return "no humanoid found";
        RigBuilder.AutoMap(rig, gl, game, rl, remade);
        return null;
    });

    /// <summary>The alignment again (up, facing, height).</summary>
    public string? AutoAlignCharacter() => EditRig("Align the remade model", (rig, game, remade, place) =>
    {
        var gl = RigBuilder.GameLabels(game, Characters?.Find(CurrentCharacter!.Character)?.Points);
        var rl = RigBuilder.Label(remade, RigBuilder.GuessUp(remade));
        if (gl == null || rl == null)
            return "no humanoid found";
        rig.Align = RigBuilder.AutoAlign(gl, game, rl, remade, place);
        return null;
    });

    /// <summary>Maps <paramref name="bone"/> to the game's <paramref name="element"/> by hand (-1: none).</summary>
    public string? SetBoneElement(string bone, int element) => EditRig($"Map {bone} to element {element}", (rig, _, remade, _) =>
    {
        if (remade.Find(bone) < 0)
            return $"the remade skeleton has no bone {bone}";
        var m = rig.Find(bone);
        if (m == null)
            rig.Bones.Add(m = new RigBone { Bone = bone });
        m.Element = element;
        m.ByHand = true;
        return null;
    });

    public string? SetBonePositions(string bone, BonePositions positions) => EditRig($"{bone}'s position: {positions}", (rig, _, _, _) =>
    {
        if (rig.Find(bone) is not { } m)
            return $"{bone} is not mapped";
        m.Positions = positions;
        return null;
    }, recomputeRest: false);

    public string? SetClothMode(string chain, ClothMode mode) => EditRig($"Cloth {chain}: {mode}", (rig, _, _, _) =>
    {
        if (rig.Cloth.FirstOrDefault(c => c.Name == chain) is not { } c)
            return $"no cloth chain {chain}";
        c.Mode = mode;
        return null;
    }, recomputeRest: false);

    public string? SetCharacterStatus(Remaster.MapStatus status, string notes) => EditRig($"Status: {status}", (rig, _, _, _) =>
    {
        rig.Status = status;
        rig.Notes = notes;
        return null;
    }, recomputeRest: false);

    /// <summary>Writes the open character's rig; an error or null.</summary>
    public string? SaveCharacter()
    {
        if (CurrentCharacter is not { } root)
            return "no character is open";
        return CharacterStore.Save(root.Character);
    }
}
#endif
