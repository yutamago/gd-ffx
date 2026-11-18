using System;
using System.Collections.Generic;
using Godot;

namespace GdFfx.Characters;

/// <summary>
/// Drives a remade character's own skeleton from the game's (docs/remake.md, "Retargeting"):
/// the game's element globals in character space (the snapshot's <c>Actor.bones</c> in the game,
/// the classic skeleton's animated pose in the editor) go in, the remade bones' poses come out.
/// Per bone of the remade skeleton, parents first:
/// <list type="bullet">
/// <item>mapped (<see cref="RigBone"/>): its global rotation is the element's times the rest
/// offset, <c>R = K⁻¹ R(G_e) O</c> (<c>K</c> the rig's alignment with the skeleton's place, model
/// to character space), swung so the mapped bone it aims at (<see cref="RigBone.Aim"/>) points
/// where the game's element of it is (the game's motions move joints as well as turn them); its position its own rest offset from its parent
/// (<see cref="BonePositions.Rotation"/>) or the game's joint (<see cref="BonePositions.Game"/>);
/// then its <see cref="RigBone.Offset"/>;</item>
/// <item>not mapped: its rest pose under its parent;</item>
/// <item>a cloth chain bone in <see cref="ClothMode.Game"/>: turned from its skinned pose by how far
/// the game's simulated cloth between its vertex and the next bone's hangs from its own skinned
/// direction (<see cref="ClothPoint"/>, <see cref="ClothChain.RestDirections"/>);</item>
/// <item>a face table row's bone (<see cref="FaceMap"/>): turned about its axis by the row's output;
/// blend shapes set on every mesh that has them.</item>
/// </list>
/// Chains in <see cref="ClothMode.Godot"/> get a <c>SpringBoneSimulator3D</c> under the skeleton,
/// which Godot runs on top of these poses each frame. Nothing is allocated per frame.
/// </summary>
public sealed class Retargeter
{
    public CharacterRig Rig { get; }
    public Skeleton3D Skeleton { get; }
    public RemadeCharacter Root { get; }

    /// <summary>The game's cloth vertex <c>(model, piece, k)</c> in character space this frame (null: none), for <see cref="ClothMode.Game"/> chains.</summary>
    public delegate Vector3? ClothPoint(int model, int piece, int k);

    private readonly int[] _order;
    private readonly int[] _parent;
    private readonly Transform3D[] _restLocal;
    private readonly RigBone?[] _map;
    private readonly Transform3D[] _globals;
    /// <summary>Per bone: its cloth chain (-1 none) and its place in it.</summary>
    private readonly int[] _chain, _chainAt;
    private readonly int[][] _chainBones;
    /// <summary>Per chain bone its skinned pose this frame, per chain the last turn the game's cloth gave.</summary>
    private readonly Transform3D[] _skinned;
    private readonly Basis[] _chainTurn;
    /// <summary>Per bone: the face rows turning it (indices into <see cref="CharacterRig.Face"/>), null none.</summary>
    private readonly List<int>?[] _faceBones;
    private readonly float[] _faceOut;
    private readonly (MeshInstance3D Mesh, int Shape)[][] _faceShapes;
    private readonly int[] _faceBone;
    /// <summary>Per attachment: the remade bone (-1 none).</summary>
    private readonly int[] _attachBone;
    /// <summary>For the error measure: per mapped bone the mapped bone its rest was aimed at (<see cref="RigBone.Aim"/>; -1 none).</summary>
    private readonly int[] _aim;
    private readonly Vector3[] _aimLocal;

    /// <summary>The highest game element the rig reads (the caller's arrays reach it).</summary>
    public int MaxElement { get; }

    /// <summary>Whether a chain follows the game's cloth (the caller fetches the capture then).</summary>
    public bool GameCloth { get; }

    /// <summary>The spring bone simulator made for the <see cref="ClothMode.Godot"/> chains (null none).</summary>
    public SpringBoneSimulator3D? Springs { get; }

    /// <summary>Frames applied, chain bones the game's cloth turned in the last one, and the face rows' last outputs.</summary>
    public int Applied { get; private set; }
    public int ClothTurned { get; private set; }

    /// <summary>The largest turn the game's cloth gave a chain bone in the last frame (degrees from its skinned pose).</summary>
    public float ClothAngle { get; private set; }
    public IReadOnlyList<float> FaceOutputs => _faceOut;

    /// <summary>The skeleton's space to character space (the rig's alignment with the skeleton's place under the root).</summary>
    public Transform3D K { get; private set; }

    public Retargeter(RemadeCharacter root, CharacterRig rig, Skeleton3D skel, bool springs = true)
    {
        Root = root;
        Rig = rig;
        Skeleton = skel;
        Scale = BakeScale(root, rig, skel);
        root.Transform = Unscaled(rig.Align);
        int n = skel.GetBoneCount();
        _parent = new int[n];
        _restLocal = new Transform3D[n];
        _map = new RigBone?[n];
        _globals = new Transform3D[n];
        _chain = new int[n];
        _chainAt = new int[n];
        _faceBones = new List<int>?[n];
        _aim = new int[n];
        var depth = new int[n];
        var byName = new Dictionary<string, int>();
        for (int b = 0; b < n; b++)
        {
            _parent[b] = skel.GetBoneParent(b);
            _restLocal[b] = skel.GetBoneRest(b);
            byName[skel.GetBoneName(b)] = b;
            for (int p = _parent[b]; p >= 0; p = skel.GetBoneParent(p))
                depth[b]++;
            _chain[b] = -1;
            _aim[b] = -1;
        }
        var order = new List<int>(n);
        for (int b = 0; b < n; b++)
            order.Add(b);
        order.Sort((a, b) => depth[a] != depth[b] ? depth[a].CompareTo(depth[b]) : a.CompareTo(b));
        _order = order.ToArray();
        int max = -1;
        foreach (var m in rig.Bones)
            if (byName.TryGetValue(m.Bone, out var b) && m.Element >= 0)
            {
                _map[b] = m;
                max = Math.Max(max, m.Element);
            }
        // the chains
        _chainBones = new int[rig.Cloth.Count][];
        _chainTurn = new Basis[rig.Cloth.Count];
        _skinned = new Transform3D[n];
        for (int c = 0; c < rig.Cloth.Count; c++)
        {
            var ch = rig.Cloth[c];
            var bones = new List<int>();
            foreach (var name in ch.Bones)
                if (byName.TryGetValue(name, out var b))
                    bones.Add(b);
            _chainBones[c] = bones.ToArray();
            max = Math.Max(max, ch.Anchor);
            if (ch.Mode == ClothMode.Game)
                for (int i = 0; i < bones.Count; i++)
                {
                    _chain[bones[i]] = c;
                    _chainAt[bones[i]] = i;
                    GameCloth = true;
                }
        }
        // the face table
        _faceOut = new float[rig.Face.Count];
        _faceShapes = new (MeshInstance3D, int)[rig.Face.Count][];
        _faceBone = new int[rig.Face.Count];
        var meshes = new List<MeshInstance3D>();
        Meshes(root, meshes);
        for (int f = 0; f < rig.Face.Count; f++)
        {
            var row = rig.Face[f];
            max = Math.Max(max, Math.Max(row.Element, row.Relative));
            _faceBone[f] = -1;
            var shapes = new List<(MeshInstance3D, int)>();
            if (row.Target == FaceTarget.BlendShape)
            {
                foreach (var mi in meshes)
                    if (mi.FindBlendShapeByName(row.TargetName) is var k && k >= 0)
                        shapes.Add((mi, k));
            }
            else if (byName.TryGetValue(row.TargetName, out var fb))
            {
                _faceBone[f] = fb;
                (_faceBones[fb] ??= new()).Add(f);
            }
            _faceShapes[f] = shapes.ToArray();
        }
        _attachBone = new int[rig.Attach.Count];
        for (int a = 0; a < rig.Attach.Count; a++)
        {
            _attachBone[a] = byName.TryGetValue(rig.Attach[a].Bone, out var ab) ? ab : -1;
            max = Math.Max(max, rig.Attach[a].Element);
        }
        MaxElement = max;
        // the aims: the bone each mapped bone's rest was aimed at, and where it lies in the bone's rest frame
        _aimLocal = new Vector3[n];
        for (int b = 0; b < n; b++)
            if (_map[b] is { Aim.Length: > 0 } am && byName.TryGetValue(am.Aim, out var ab))
            {
                _aim[b] = ab;
                _aimLocal[b] = (skel.GetBoneGlobalRest(b).AffineInverse() * skel.GetBoneGlobalRest(ab)).Origin;
            }
        if (springs)
            Springs = MakeSprings(skel, rig);
        Refresh();
    }

    private static void Meshes(Node n, List<MeshInstance3D> into)
    {
        if (n is MeshInstance3D mi)
            into.Add(mi);
        for (int i = 0, k = n.GetChildCount(); i < k; i++)
            Meshes(n.GetChild(i), into);
    }

    /// <summary>The spring bones for the <see cref="ClothMode.Godot"/> chains, under the skeleton (null: none).</summary>
    private static SpringBoneSimulator3D? MakeSprings(Skeleton3D skel, CharacterRig rig)
    {
        var chains = new List<ClothChain>();
        foreach (var c in rig.Cloth)
            if (c.Mode == ClothMode.Godot && c.Bones.Length >= 2 && skel.FindBone(c.Bones[0]) >= 0 && skel.FindBone(c.Bones[^1]) >= 0)
                chains.Add(c);
        if (chains.Count == 0)
            return null;
        // under its skeleton first: it resolves the bone names only then (configured before, it
        // keeps no joints and moves nothing)
        var sim = new SpringBoneSimulator3D { Name = "ClothSprings" };
        skel.AddChild(sim);
        sim.SetSettingCount(chains.Count);
        for (int i = 0; i < chains.Count; i++)
        {
            var c = chains[i];
            sim.SetRootBoneName(i, c.Bones[0]);
            sim.SetEndBoneName(i, c.Bones[^1]);
            sim.SetStiffness(i, c.Stiffness);
            sim.SetDrag(i, c.Drag);
            sim.SetGravity(i, c.Gravity);
            sim.SetRadius(i, c.Radius);
        }
        return sim;
    }

    /// <summary>The alignment's scale, baked into the skeleton's rests and the skins (1: none).</summary>
    public float Scale { get; }

    /// <summary>The alignment without its scale (the root's transform once the scale is baked).</summary>
    private Transform3D Unscaled(Transform3D align) =>
        Mathf.IsEqualApprox(Scale, 1f) ? align : new Transform3D(align.Basis.Orthonormalized(), align.Origin);

    /// <summary>
    /// Bakes the alignment's (uniform) scale into the remade model: every bone's rest and pose
    /// position, the nodes between the root and the skeleton, the skins' bind poses (copies) and
    /// the meshes that are not skinned; the root then needs no scale. Godot's spring bones do not
    /// work on a scaled skeleton (its docs; they flip half a turn a frame), and a character
    /// authored in metres is scaled about ten times into the game's units.
    /// </summary>
    private static float BakeScale(RemadeCharacter root, CharacterRig rig, Skeleton3D skel)
    {
        var sv = rig.Align.Basis.Scale;
        float s = (sv.X + sv.Y + sv.Z) / 3f;
        if (Mathf.Abs(s - 1f) < 1e-4f || s <= 0f)
            return 1f;
        for (int b = 0; b < skel.GetBoneCount(); b++)
        {
            var rest = skel.GetBoneRest(b);
            skel.SetBoneRest(b, new Transform3D(rest.Basis, rest.Origin * s));
            skel.SetBonePosePosition(b, skel.GetBonePosePosition(b) * s);
        }
        for (Node? p = skel; p != null && p != root; p = p.GetParent())
            if (p is Node3D p3)
                p3.Position *= s;
        var scale = new Transform3D(Basis.FromScale(Vector3.One * s), Vector3.Zero);
        var skins = new Dictionary<Skin, Skin>();
        void Walk(Node n)
        {
            if (n is MeshInstance3D mi)
            {
                if (mi.Skin is { } skin)
                {
                    if (!skins.TryGetValue(skin, out var baked))
                    {
                        baked = (Skin)skin.Duplicate();
                        for (int i = 0; i < baked.GetBindCount(); i++)
                            baked.SetBindPose(i, scale * baked.GetBindPose(i));
                        skins[skin] = baked;
                    }
                    mi.Skin = baked;
                    // its bounds as drawn (culling, the camera's close-up)
                    if (mi.Mesh is { } mesh)
                    {
                        var box = mesh.GetAabb();
                        mi.CustomAabb = new Aabb(box.Position * s, box.Size * s).Grow(0.25f * s * box.Size.Length() / 4f);
                    }
                }
                else if (!skel.IsAncestorOf(mi))
                    mi.Transform = new Transform3D(mi.Transform.Basis.Scaled(Vector3.One * s), mi.Transform.Origin * s);
            }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(root);
        return s;
    }

    /// <summary>Takes up the rig's alignment again (after an edit in the editor).</summary>
    public void Refresh()
    {
        Root.Transform = Unscaled(Rig.Align);
        Transform3D t = Transform3D.Identity;
        for (Node? p = Skeleton; p != null && p != Root; p = p.GetParent())
            if (p is Node3D p3)
                t = p3.Transform * t;
        K = Unscaled(Rig.Align) * t;
        foreach (var m in Rig.Bones)
            if (Skeleton.FindBone(m.Bone) is var b && b >= 0)
                _map[b] = m.Element >= 0 ? m : null;
    }

    /// <summary>
    /// Poses the skeleton from the game's element globals <paramref name="game"/> (character space;
    /// <paramref name="has"/>: the game drives the element), the game's cloth from
    /// <paramref name="cloth"/> (null: none).
    /// </summary>
    public void Apply(Transform3D[] game, bool[] has, ClothPoint? cloth)
    {
        var skel = Skeleton;
        var k = K;
        var kInv = k.AffineInverse();
        var rk = k.Basis.Orthonormalized();
        var rkT = rk.Transposed();
        // the face rows' outputs (they read the game only)
        for (int f = 0; f < _faceOut.Length; f++)
        {
            var row = Rig.Face[f];
            if (row.Element < 0 || row.Element >= has.Length || !has[row.Element])
                continue;
            var local = row.Relative >= 0 && row.Relative < has.Length && has[row.Relative]
                ? Solid(game[row.Relative]).AffineInverse() * game[row.Element] : game[row.Element];
            _faceOut[f] = row.Map(FaceInput(local, row.Source));
            foreach (var (mi, shape) in _faceShapes[f])
                if (GodotObject.IsInstanceValid(mi))
                    mi.SetBlendShapeValue(shape, _faceOut[f]);
        }
        int turned = 0;
        float most = 0;
        foreach (var b in _order)
        {
            int p = _parent[b];
            var pg = p >= 0 ? _globals[p] : Transform3D.Identity;
            Transform3D w;
            if (_map[b] is { } m && m.Element < has.Length && has[m.Element] && Rotation(game[m.Element]) is { } rg)
            {
                var rot = rkT * rg * new Basis(m.Rest);
                // aimed: the bone it aims at where the game's element of it is (the game's motions
                // move joints too: the wrist along the forearm)
                if (_aim[b] >= 0 && _map[_aim[b]] is { } ma && ma.Element < has.Length && has[ma.Element])
                {
                    var want = kInv.Basis * (game[ma.Element].Origin - game[m.Element].Origin);
                    var haveAim = rot * _aimLocal[b];
                    if (want.LengthSquared() > 1e-12f && haveAim.LengthSquared() > 1e-12f)
                        rot = new Basis(Arc(haveAim.Normalized(), want.Normalized())) * rot;
                }
                var origin = m.Positions == BonePositions.Game ? kInv * game[m.Element].Origin : pg * _restLocal[b].Origin;
                w = new Transform3D(rot, origin) * m.Offset;
            }
            else
                w = pg * _restLocal[b];
            // a face row's bone: turned about its own axis
            if (_faceBones[b] is { } rows)
                foreach (var f in rows)
                {
                    var axis = Rig.Face[f].Target switch
                    {
                        FaceTarget.BoneRotationY => Vector3.Up,
                        FaceTarget.BoneRotationZ => Vector3.Back,
                        _ => Vector3.Right,
                    };
                    w = w * new Transform3D(new Basis(axis, Mathf.DegToRad(_faceOut[f])), Vector3.Zero);
                }
            // a chain bone following the game's cloth: turned from where its parent alone puts it
            // (its skinned pose) by how far the game's simulated cloth hangs from its own skinned
            // direction, which the element it hangs from gives
            if (_chain[b] >= 0)
            {
                int c = _chain[b];
                var ch = Rig.Cloth[c];
                var bones = _chainBones[c];
                int i = _chainAt[b];
                var skinned = i == 0 ? pg * _restLocal[b] : _skinned[bones[i - 1]] * _restLocal[b];
                _skinned[b] = skinned;
                var q = i == 0 ? Basis.Identity : _chainTurn[c];
                if (cloth != null && i + 1 < ch.Points.Length && i < ch.RestDirections.Length && ch.Points[i] >= 0 && ch.Points[i + 1] >= 0
                    && cloth(ch.Model, ch.Piece, ch.Points[i]) is { } pa && cloth(ch.Model, ch.Piece, ch.Points[i + 1]) is { } pb)
                {
                    var sim = pb - pa;
                    var skin = ch.RestDirections[i];
                    if (ch.Anchor >= 0 && ch.Anchor < has.Length && has[ch.Anchor] && Rotation(game[ch.Anchor]) is { } ra)
                        skin = ra * new Basis(ch.AnchorRest.Inverse()) * skin;
                    if (sim.LengthSquared() > 1e-12f && skin.LengthSquared() > 1e-12f)
                    {
                        var arc = Arc(skin.Normalized(), sim.Normalized());
                        most = Math.Max(most, Mathf.RadToDeg(arc.GetAngle()));
                        q = rkT * new Basis(arc) * rk;
                        turned++;
                    }
                }
                _chainTurn[c] = q;
                w = new Transform3D(q * skinned.Basis, i == 0 ? skinned.Origin : pg * _restLocal[b].Origin);
            }
            _globals[b] = w;
        }
        // the locals, as Godot holds a pose
        foreach (var b in _order)
        {
            int p = _parent[b];
            var local = p >= 0 ? _globals[p].AffineInverse() * _globals[b] : _globals[b];
            var basis = local.Basis;
            var scale = basis.Scale;
            if (Mathf.Abs(basis.Determinant()) < 1e-12f || !scale.IsFinite())
                continue;
            skel.SetBonePosePosition(b, local.Origin);
            skel.SetBonePoseRotation(b, basis.GetRotationQuaternion());
            skel.SetBonePoseScale(b, scale);
        }
        ClothTurned = turned;
        ClothAngle = most;
        Applied++;
        // the spring bones start from the first placed pose (not from where the model was made)
        if (Applied == 2)
            Springs?.Reset();
    }

    /// <summary>A bone's global pose as the last <see cref="Apply"/> made it, in the skeleton's space.</summary>
    public Transform3D Global(int bone) => _globals[bone];

    /// <summary>
    /// The frame in character space that what the game binds to element <paramref name="element"/>
    /// follows instead of the element's: the remade bone's global (unscaled) times the
    /// attachment's rest and the user's offset; else, for an element a bone is mapped to, that
    /// bone's frame as the element's; null when neither.
    /// </summary>
    public Transform3D? AttachFrame(int element)
    {
        for (int a = 0; a < _attachBone.Length; a++)
            if (Rig.Attach[a].Element == element && _attachBone[a] >= 0)
            {
                var g = K * _globals[_attachBone[a]];
                return new Transform3D(g.Basis.Orthonormalized(), g.Origin) * Rig.Attach[a].Rest * Rig.Attach[a].Offset;
            }
        // an element a bone is mapped to: the bone's frame as the element's (its rest offset taken
        // back), at the remade joint
        for (int b = 0; b < _map.Length; b++)
            if (_map[b] is { } m && m.Element == element)
            {
                var g = K * _globals[b];
                return new Transform3D(g.Basis.Orthonormalized() * new Basis(m.Rest.Inverse()), g.Origin);
            }
        return null;
    }

    /// <summary>
    /// How far the last <see cref="Apply"/> is from the game's skeleton: per mapped bone the
    /// distance of its joint from the element's (character units), and the angle between the
    /// direction to the bone it aims at and the game's direction between the same elements.
    /// </summary>
    public (float MaxPosition, float MeanPosition, float MaxAngle, float MeanAngle, int Bones) Error(Transform3D[] game, bool[] has)
    {
        float maxP = 0, sumP = 0, maxA = 0, sumA = 0;
        int n = 0, na = 0;
        var k = K;
        for (int b = 0; b < _map.Length; b++)
        {
            if (_map[b] is not { } m || m.Element >= has.Length || !has[m.Element])
                continue;
            float d = (k * _globals[b].Origin).DistanceTo(game[m.Element].Origin);
            maxP = Math.Max(maxP, d);
            sumP += d;
            n++;
            int c = _aim[b];
            if (c < 0 || _map[c] is not { } mc || mc.Element >= has.Length || !has[mc.Element])
                continue;
            var dr = k.Basis * (_globals[c].Origin - _globals[b].Origin);
            var dg = game[mc.Element].Origin - game[m.Element].Origin;
            if (dr.LengthSquared() < 1e-10f || dg.LengthSquared() < 1e-10f)
                continue;
            float ang = Mathf.RadToDeg(dr.AngleTo(dg));
            maxA = Math.Max(maxA, ang);
            sumA += ang;
            na++;
        }
        return (maxP, n > 0 ? sumP / n : 0, maxA, na > 0 ? sumA / na : 0, n);
    }

    /// <summary>The value a face row reads of a local pose.</summary>
    public static float FaceInput(Transform3D local, FaceSource source)
    {
        switch (source)
        {
            case FaceSource.PositionX: return local.Origin.X;
            case FaceSource.PositionY: return local.Origin.Y;
            case FaceSource.PositionZ: return local.Origin.Z;
        }
        var e = local.Basis.Orthonormalized().GetEuler(EulerOrder.Zyx);
        return Mathf.RadToDeg(source switch { FaceSource.RotationY => e.Y, FaceSource.RotationZ => e.Z, _ => e.X });
    }

    /// <summary>The rotation of a game matrix (null when it has no size: a hidden part).</summary>
    private static Basis? Rotation(Transform3D t)
    {
        var b = t.Basis;
        if (Mathf.Abs(b.Determinant()) < 1e-9f)
            return null;
        return b.Orthonormalized();
    }

    private static Transform3D Solid(Transform3D t) =>
        Mathf.Abs(t.Basis.Determinant()) < 1e-12f ? new Transform3D(Basis.FromScale(Vector3.One * 1e-4f), t.Origin) : t;

    /// <summary>The shortest turn from unit vector <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static Quaternion Arc(Vector3 from, Vector3 to)
    {
        float d = from.Dot(to);
        if (d > 0.999999f)
            return Quaternion.Identity;
        if (d < -0.999999f)
        {
            var axis = from.Cross(Vector3.Right);
            if (axis.LengthSquared() < 1e-6f)
                axis = from.Cross(Vector3.Up);
            return new Quaternion(axis.Normalized(), Mathf.Pi);
        }
        var c = from.Cross(to);
        return new Quaternion(c.X, c.Y, c.Z, 1 + d).Normalized();
    }
}
