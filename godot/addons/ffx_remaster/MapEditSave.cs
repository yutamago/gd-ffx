#if TOOLS
using System.Collections.Generic;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The edit scene's state as the map's override layer (docs/remake.md, "Overrides"): each part
/// compared with its classic placement (a move is the offset on the game's placement, in world
/// axes: <c>B = W'.basis W.basis⁻¹</c>, <c>t = W'.origin − W.origin</c>; a hidden node is
/// <c>Hidden</c>, a part out of the scene <c>Remove</c>, a replacement <c>Replace</c>), each added
/// node with its anchor (the part it hangs under, else the map's root) and its place relative to
/// the anchor's origin and axes. Fields the editor does not show (an earlier layer's undo:
/// <c>Keep</c>, <c>Shown</c>, <c>Game</c>, <c>Original</c>) are kept when the part is classic
/// there; entries for IDs the map lacks are kept as they are.
/// </summary>
public static class MapEditSave
{
    /// <summary>What a save wrote (for the dock and the checks).</summary>
    public sealed record Result(bool Ok, int Parts, int Added, int Removed, int Hidden, int Moved, int Replaced);

    /// <summary>The project's overrides of <paramref name="root"/>'s map now, as entries (sorted by ID by the writer).</summary>
    public static List<PartOverride> Entries(MapEditRoot root, out (int Removed, int Hidden, int Moved, int Replaced) counts)
    {
        counts = default;
        var list = new List<PartOverride>();
        foreach (var info in root.Order)
        {
            // a part under a removed one goes with it
            if (info.Parent != null && root.IsGone(info.Parent))
            {
                if (info.Loaded != null)
                    list.Add(info.Loaded);
                continue;
            }
            var marks = root.MarksOf(info);
            var p = info.Loaded?.Copy() ?? new PartOverride();
            p.Id = info.Short(root.Model);
            p.Note = info.Note;
            p.Remove = marks.HasFlag(PartMarks.Removed) ? RemoveOp.Remove : p.Remove == RemoveOp.Keep ? RemoveOp.Keep : RemoveOp.Unset;
            p.Visibility = marks.HasFlag(PartMarks.Hidden) ? VisibilityOp.Hidden : p.Visibility == VisibilityOp.Shown ? VisibilityOp.Shown : VisibilityOp.Unset;
            if (marks.HasFlag(PartMarks.Moved))
                (p.TransformMode, p.Transform) = Move(root, info);
            else if (marks.HasFlag(PartMarks.Removed) && p.TransformMode is TransformOp.Offset or TransformOp.Absolute)
            {
                // a removed part keeps the move it had: shown again, it is where it was
            }
            else if (p.TransformMode != TransformOp.Game)
            {
                p.TransformMode = TransformOp.Unset;
                p.Transform = Transform3D.Identity;
            }
            if (info.Replacement.Length > 0 && ResourceLoader.Load(info.Replacement) is PackedScene scene)
            {
                p.Replace = ReplaceOp.Scene;
                p.Scene = scene;
            }
            else
            {
                p.Replace = p.Replace == ReplaceOp.Original ? ReplaceOp.Original : ReplaceOp.Unset;
                p.Scene = null;
            }
            if (p.Remove == RemoveOp.Unset && p.Visibility == VisibilityOp.Unset && p.TransformMode == TransformOp.Unset
                && p.Replace == ReplaceOp.Unset && p.Note.Length == 0)
                continue;
            if (p.Remove == RemoveOp.Remove)
                counts.Removed++;
            if (p.Visibility == VisibilityOp.Hidden)
                counts.Hidden++;
            if (p.TransformMode is TransformOp.Offset or TransformOp.Absolute && !marks.HasFlag(PartMarks.Removed))
                counts.Moved++;
            if (p.Replace == ReplaceOp.Scene)
                counts.Replaced++;
            list.Add(p);
        }
        list.AddRange(root.Foreign);
        return list;
    }

    /// <summary>A moved part's entry: an offset on the game's placement (or the absolute world, when the file had one).</summary>
    public static (TransformOp Mode, Transform3D X) Move(MapEditRoot root, PartInfo info)
    {
        var w = info.Node.GlobalTransform;
        if (info.Mode == TransformOp.Absolute)
        {
            if (info.Loaded is { TransformMode: TransformOp.Absolute } a && a.Transform.IsEqualApprox(w))
                return (TransformOp.Absolute, a.Transform);
            return (TransformOp.Absolute, Snap(w));
        }
        var b = root.BaseWorld(info);
        var x = new Transform3D(w.Basis * b.Basis.Inverse(), w.Origin - b.Origin);
        // what the file said, when the scene still shows it (no float noise in the diff)
        if (info.Loaded is { TransformMode: TransformOp.Offset } o && o.Transform.IsEqualApprox(x))
            return (TransformOp.Offset, o.Transform);
        return (TransformOp.Offset, Snap(x));
    }

    /// <summary>Rounded for the text file: origins to 1e-4 units, the basis to 1e-6.</summary>
    private static Transform3D Snap(Transform3D t)
    {
        static Vector3 S(Vector3 v, float step) => new(Mathf.Snapped(v.X, step), Mathf.Snapped(v.Y, step), Mathf.Snapped(v.Z, step));
        return new Transform3D(new Basis(S(t.Basis.Column0, 1e-6f), S(t.Basis.Column1, 1e-6f), S(t.Basis.Column2, 1e-6f)), S(t.Origin, 1e-4f));
    }

    /// <summary>
    /// The added nodes as an additions scene (null: none): a root <c>Additions</c> with a copy of
    /// each added node, placed relative to its anchor's frame, its anchor in <c>ffx_anchor</c>
    /// (model-relative); names made unique (the IDs are <c>x:level/&lt;key&gt;/+&lt;layer&gt;/&lt;name&gt;</c>).
    /// </summary>
    public static PackedScene? Additions(MapEditRoot root, out int count)
    {
        var added = root.Added();
        count = added.Count;
        if (added.Count == 0)
            return null;
        var scene = new Node3D { Name = "Additions" };
        var names = new HashSet<string>();
        foreach (var (node, anchor) in added)
        {
            // unique names: they make the IDs; the scene's node renamed too, so both agree
            var name = node.Name.ToString();
            for (int i = 2; !names.Add(name); i++)
                name = $"{node.Name}{i}";
            if (name != node.Name.ToString())
                node.Name = name;
            var copy = (Node3D)node.Duplicate();
            copy.Name = name;
            copy.Transform = Snap(root.Frame(anchor).AffineInverse() * node.GlobalTransform);
            if (anchor != null)
                PartIds.SetString(copy, "ffx_anchor", anchor.Short(root.Model));
            scene.AddChild(copy);
            OwnCopy(copy, scene);
        }
        var packed = new PackedScene();
        var err = packed.Pack(scene);
        scene.Free();
        if (err != Error.Ok)
        {
            GD.PrintErr($"ffx remaster: the additions did not pack: {err}");
            return null;
        }
        return packed;
    }

    /// <summary>The copy's nodes owned by the additions' root, except inside an instanced scene (they keep its owner).</summary>
    private static void OwnCopy(Node n, Node scene)
    {
        if (n.Owner == null)
            n.Owner = scene;
        for (int i = 0, k = n.GetChildCount(); i < k; i++)
            OwnCopy(n.GetChild(i), scene);
    }

    /// <summary>Writes <paramref name="root"/>'s edits as its map's override layer (<see cref="MapStore.Write"/>).</summary>
    public static Result Save(MapEditRoot root)
    {
        if (!root.Built || root.Map == null)
            return new Result(false, 0, 0, 0, 0, 0, 0);
        var res = MapStore.GetOrNew(root.Key);
        var entries = Entries(root, out var c);
        res.Parts = new Godot.Collections.Array<PartOverride>(entries);
        var additions = Additions(root, out int added);
        bool ok = MapStore.Write(root.Key, additions);
        if (ok)
        {
            // what was written is now what the scene was opened with
            var byId = new Dictionary<string, PartOverride>();
            foreach (var p in res.Parts)
                byId[PartIds.Full(p.Id, root.Model)] = p;
            foreach (var info in root.Order)
                info.Loaded = byId.GetValueOrDefault(info.Id);
        }
        return new Result(ok, entries.Count, added, c.Removed, c.Hidden, c.Moved, c.Replaced);
    }
}
#endif
