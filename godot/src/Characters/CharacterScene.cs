using Godot;

namespace GdFfx.Characters;

/// <summary>
/// A remade character's scene and rig as files (docs/remake.md, "Retargeting"): the scene
/// <c>&lt;dir&gt;/&lt;id&gt;.tscn</c> (the convention's <c>res://remake/chr/&lt;id&gt;.tscn</c> in the
/// project), a <see cref="RemadeCharacter"/> root named by the id holding the model (the user's
/// scene as an instance, or nodes made in code inline), and its rig beside it
/// (<c>&lt;id&gt;.rig.tres</c>, text).
/// </summary>
public static class CharacterScene
{
    /// <summary>The project's folder (the remake convention's).</summary>
    public const string ProjectDir = "res://remake/chr";

    public static string ScenePath(string dir, string id) => $"{dir.TrimEnd('/')}/{id}.tscn";

    public static string RigPath(string dir, string id) => $"{dir.TrimEnd('/')}/{id}.rig.tres";

    /// <summary>A remade character's root over <paramref name="model"/> (not in the tree).</summary>
    public static RemadeCharacter Compose(string id, Node3D model, CharacterRig rig)
    {
        var root = new RemadeCharacter { Name = id, Rig = rig, Transform = rig.Align };
        model.Name = "Model";
        root.AddChild(model);
        return root;
    }

    /// <summary>
    /// Writes <paramref name="rig"/> and a scene of <paramref name="model"/> (an instance of
    /// <paramref name="modelScene"/> when given, else <paramref name="model"/>'s nodes inline) into
    /// <paramref name="dir"/>; the scene's path, or null with <paramref name="error"/>.
    /// </summary>
    public static string? Save(string dir, string id, CharacterRig rig, string? modelScene, Node3D? model, out string error)
    {
        error = "";
        DirAccess.MakeDirRecursiveAbsolute(dir);
        var rigPath = RigPath(dir, id);
        rig.Character = id;
        if (modelScene != null)
            rig.Model = modelScene;
        var e = ResourceSaver.Save(rig, rigPath);
        if (e != Error.Ok)
        {
            error = $"save {rigPath}: {e}";
            return null;
        }
        rig.TakeOverPath(rigPath);
        Node3D? inner = model;
        if (modelScene != null)
        {
            if (ResourceLoader.Load(modelScene) is not PackedScene ps || ps.Instantiate() is not Node3D inst)
            {
                error = $"{modelScene} is not a 3D scene";
                return null;
            }
            inner = inst;
        }
        if (inner == null)
        {
            error = "no model";
            return null;
        }
        var root = Compose(id, inner, rig);
        root.SetMeta("ffx_kind", "chr");
        root.SetMeta("ffx_id", id);
        inner.Owner = root;
        // a model made in code: its own nodes saved inline (an instance keeps its file's)
        if (modelScene == null)
            Own(inner, root);
        var packed = new PackedScene();
        var pe = packed.Pack(root);
        root.Free();
        if (pe != Error.Ok)
        {
            error = $"pack: {pe}";
            return null;
        }
        var path = ScenePath(dir, id);
        var se = ResourceSaver.Save(packed, path);
        if (se != Error.Ok)
        {
            error = $"save {path}: {se}";
            return null;
        }
        return path;
    }

    private static void Own(Node n, Node owner)
    {
        for (int i = 0; i < n.GetChildCount(); i++)
        {
            var c = n.GetChild(i);
            c.Owner = owner;
            Own(c, owner);
        }
    }
}
