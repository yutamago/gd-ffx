using System;
using System.Collections.Generic;
using GdFfx.Api;
using Godot;

namespace GdFfx;

/// <summary>
/// The game's state as Godot draws it this frame: the newest two snapshots
/// (the game runs 30 updates a second) and their interpolation at Godot's
/// frame rate, in Godot's space. Rendering runs one update behind the game.
///
/// Spaces (ffx-rust's docs/state-api.md): the game's field space has y down;
/// its render space negates x and y, which is right-handed with y up and the
/// camera looking along -z, as Godot. A field point (x, y, z) is
/// (-x, -y, z) * <see cref="Scale"/> here.
/// </summary>
public sealed class GameStateView
{
    /// <summary>Godot units per field unit (to be settled with the classic maps in G2).</summary>
    public const float Scale = 1.0f;

    public Snapshot? Previous { get; private set; }
    public Snapshot? Current { get; private set; }

    /// <summary>Where between Previous (0) and Current (1) this frame is.</summary>
    public float Alpha { get; private set; }

    /// <summary>Snapshots decoded so far.</summary>
    public long Decoded { get; private set; }

    /// <summary>The last decode error, if any.</summary>
    public string Error { get; private set; } = "";

    /// <summary>
    /// The newest update is a cut (a new map, or the camera jumped): it is drawn as
    /// the game has it, nothing interpolated into it. The thresholds are the ones
    /// the user's tested frame-rate mod for FFX.exe uses (ffx-mods' fps-unlock):
    /// a camera move over 100 units, a turn over 45 degrees, a field of view
    /// changed by more than 25 %.
    /// </summary>
    public bool Cut { get; private set; }

    /// <summary>Cuts seen so far.</summary>
    public long Cuts { get; private set; }

    /// <summary>A character's warp: a move over 50 units or a turn over 120 degrees in one update.</summary>
    public const float WarpDistance = 50f, WarpDegrees = 120f;

    private const float CutDistance = 100f, CutDegrees = 45f, CutFov = 0.25f;

    private long _update = -1;

    /// <summary>Takes the newest snapshots from the game (decodes only when they changed).</summary>
    public void Refresh(FfxGame game)
    {
        var update = game.SnapshotUpdate;
        if (update < 0)
            return;
        var d = game.Snapshots;
        if (update != _update)
        {
            try
            {
                var cur = d["current"].AsByteArray();
                var prev = d["previous"].AsByteArray();
                Current = cur.Length > 0 ? StateApi.Decode(cur) : null;
                Previous = prev.Length > 0 ? StateApi.Decode(prev) : Current;
                _update = Current?.Update ?? update;
                Decoded++;
                Error = "";
                Cut = IsCut(Previous, Current);
                if (Cut)
                    Cuts++;
            }
            catch (FormatException e)
            {
                Error = e.Message;
            }
        }
        var age = d["age"].AsDouble();
        var interval = Math.Max(d["interval"].AsDouble(), 1e-3);
        Alpha = (float)Math.Clamp(age / interval, 0.0, 1.0);
    }

    private static bool IsCut(Snapshot? p, Snapshot? c)
    {
        if (p == null || c == null || ReferenceEquals(p, c))
            return false;
        if (p.World.Map != c.World.Map || p.World.Level != c.World.Level)
            return true;
        if (!p.Camera.Valid || !c.Camera.Valid)
            return false;
        var a = CameraTransform(p.Camera.World);
        var b = CameraTransform(c.Camera.World);
        if (a.Origin.DistanceTo(b.Origin) > CutDistance * Scale)
            return true;
        if (Mathf.RadToDeg(Angle(a.Basis, b.Basis)) > CutDegrees)
            return true;
        var fov = Mathf.Max(Mathf.Abs(p.Camera.FovY), 1e-3f);
        return Mathf.Abs(c.Camera.FovY - p.Camera.FovY) / fov > CutFov;
    }

    /// <summary>The angle between two rotations (radians; 0 when either is not a rotation).</summary>
    private static float Angle(Basis a, Basis b)
    {
        var qa = a.Orthonormalized().GetRotationQuaternion();
        var qb = b.Orthonormalized().GetRotationQuaternion();
        return qa.AngleTo(qb);
    }

    /// <summary>Whether a character is interpolated between its two updates (no cut, no warp).</summary>
    public bool Interpolates(Actor previous, Actor current)
    {
        if (Cut || previous.Id != current.Id)
            return false;
        var a = FieldTransform(previous.World);
        var b = FieldTransform(current.World);
        return a.Origin.DistanceTo(b.Origin) <= WarpDistance * Scale
            && Mathf.RadToDeg(Angle(a.Basis, b.Basis)) <= WarpDegrees;
    }

    // ---- spaces ----------------------------------------------------------------

    /// <summary>A field-space point in Godot's space.</summary>
    public static Vector3 FieldPoint(Vector3 p) => new Vector3(-p.X, -p.Y, p.Z) * Scale;

    /// <summary>A row-major 4x4 (row vectors, the game's) applied to a point.</summary>
    private static Vector3 Apply(float[] m, Vector3 p) => new(
        p.X * m[0] + p.Y * m[4] + p.Z * m[8] + m[12],
        p.X * m[1] + p.Y * m[5] + p.Z * m[9] + m[13],
        p.X * m[2] + p.Y * m[6] + p.Z * m[10] + m[14]);

    /// <summary>Row <paramref name="r"/>'s first three of a row-major 4x4.</summary>
    private static Vector3 Row(float[] m, int r) => new(m[4 * r], m[4 * r + 1], m[4 * r + 2]);

    /// <summary>
    /// A field-space transform (row-major 4x4, row vectors; its rotation may be
    /// scaled, as the root's x 0.001) as a Godot transform: the axes
    /// normalised, in Godot's space.
    /// </summary>
    public static Transform3D FieldTransform(float[] m)
    {
        // render map L_r(e_k) = c_k * C * row_k, C = diag(-1, -1, 1)
        Vector3 Axis(int k, float c)
        {
            var r = Row(m, k);
            var v = new Vector3(-r.X, -r.Y, r.Z) * c;
            return v.LengthSquared() > 0 ? v.Normalized() : Vector3.Zero;
        }
        var basis = new Basis(Axis(0, -1), Axis(1, -1), Axis(2, 1));
        if (Mathf.IsZeroApprox(basis.Determinant()))
            basis = Basis.Identity;
        return new Transform3D(basis, FieldPoint(new Vector3(m[12], m[13], m[14])));
    }

    /// <summary>
    /// The game camera's transform: Camera.World is
    /// [Y.x Y.y Y.z X.x | Z.x Z.y Z.z X.y | P.x P.y P.z X.z] in render space.
    /// </summary>
    public static Transform3D CameraTransform(float[] w) => new(
        new Basis(new Vector3(w[3], w[7], w[11]), new Vector3(w[0], w[1], w[2]), new Vector3(w[4], w[5], w[6])),
        new Vector3(w[8], w[9], w[10]) * Scale);

    /// <summary>A bone's position in Godot's space: the bone's matrix, then the root's rotation (x 1000) and position.</summary>
    public static Vector3 BonePoint(float[] root, float[] bone)
    {
        // the root's rotation carries x 0.001 (the game's model scale)
        var r = (float[])root.Clone();
        for (int i = 0; i < 12; i++)
            if (i % 4 != 3)
                r[i] *= 1000f;
        var local = new Vector3(bone[12], bone[13], bone[14]);
        return FieldPoint(Apply(r, local));
    }

    // ---- interpolation --------------------------------------------------------

    /// <summary>The camera this frame, interpolated (null without one).</summary>
    public (Transform3D Transform, float FovY, float Far)? Camera()
    {
        var c = Current?.Camera;
        if (c is not { Valid: true })
            return null;
        var t = CameraTransform(c.World);
        var fov = c.FovY;
        var p = Previous?.Camera;
        if (p is { Valid: true } && !Cut)
        {
            var pt = CameraTransform(p.World);
            t = pt.InterpolateWith(t, Alpha);
            fov = Mathf.Lerp(p.FovY, fov, Alpha);
        }
        return (t, fov, c.Far * Scale);
    }

    /// <summary>One character this frame.</summary>
    public readonly record struct ActorFrame(Actor Actor, Transform3D Root, Vector3[] Bones, bool Shown);

    /// <summary>The characters this frame (by slot), interpolated where the same character was there an update before.</summary>
    public List<ActorFrame> Actors()
    {
        var list = new List<ActorFrame>();
        if (Current == null)
            return list;
        var prevBySlot = new Dictionary<ushort, Actor>();
        if (Previous != null)
            foreach (var a in Previous.Actors)
                prevBySlot[a.Slot] = a;
        foreach (var a in Current.Actors)
        {
            var root = FieldTransform(a.World);
            var bones = new Vector3[a.Bones.Count];
            for (int i = 0; i < bones.Length; i++)
                bones[i] = BonePoint(a.World, a.Bones[i].Matrix);
            if (prevBySlot.TryGetValue(a.Slot, out var p) && Interpolates(p, a))
            {
                root = FieldTransform(p.World).InterpolateWith(root, Alpha);
                if (p.Bones.Count == a.Bones.Count)
                    for (int i = 0; i < bones.Length; i++)
                        bones[i] = BonePoint(p.World, p.Bones[i].Matrix).Lerp(bones[i], Alpha);
            }
            list.Add(new ActorFrame(a, root, bones, a.Hidden == 0 && a.Alpha > 0));
        }
        return list;
    }
}
