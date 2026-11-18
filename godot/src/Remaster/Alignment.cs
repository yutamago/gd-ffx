using System.Collections.Generic;
using System.Text;
using Godot;

namespace GdFfx.Remaster;

/// <summary>What the alignment check found under (or over) a walk-mesh triangle.</summary>
public enum AlignmentKind
{
    /// <summary>A remade surface at the walk mesh (within the tolerance), nothing in the way.</summary>
    Ok,

    /// <summary>The nearest remade surface under it is lower than the tolerance: the characters float above the remade ground.</summary>
    Floating,

    /// <summary>A remade surface over it within the clearance: the characters stand in the remade ground or walk through it.</summary>
    Blocked,
}

/// <summary>A walk-mesh triangle the remade geometry disagrees with: its worst sample, by how much, and which node's surface.</summary>
public sealed record AlignmentIssue(int Triangle, AlignmentKind Kind, float Distance, Vector3 Point, string Source);

/// <summary>A triangle of remade geometry in render space and the node it comes from.</summary>
public readonly record struct Surface(Vector3 A, Vector3 B, Vector3 C, string Source);

/// <summary>The result of <see cref="Alignment.Check"/>.</summary>
public sealed class AlignmentReport
{
    public required int Triangles { get; init; }

    /// <summary>Walkable triangles with a remade surface above or below one of their samples.</summary>
    public required int Covered { get; init; }

    public required int Surfaces { get; init; }
    public required float Tolerance { get; init; }
    public required float Clearance { get; init; }
    public required List<AlignmentIssue> Issues { get; init; }

    public int Count(AlignmentKind k) => Issues.FindAll(i => i.Kind == k).Count;

    /// <summary>The issues by source node: how many triangles each disagrees with.</summary>
    public Dictionary<string, int> BySource()
    {
        var d = new Dictionary<string, int>();
        foreach (var i in Issues)
            d[i.Source] = d.GetValueOrDefault(i.Source) + 1;
        return d;
    }

    public string Summary()
    {
        var sb = new StringBuilder($"{Surfaces} remade surface(s) over {Covered} of {Triangles} walk-mesh triangle(s): "
            + $"{Count(AlignmentKind.Floating)} floating, {Count(AlignmentKind.Blocked)} blocked (tolerance {Tolerance}, clearance {Clearance})");
        foreach (var (source, n) in BySource())
            sb.Append($"; {source}: {n}");
        return sb.ToString();
    }
}

/// <summary>
/// The alignment check (docs/remaster-editor.md, "The alignment check"): where a map's remade
/// geometry (additions, replacements, a remade map's scene) disagrees with the walk mesh the
/// game moves the characters on. Each walkable triangle is sampled at its centre and near its
/// corners; along the vertical through a sample, the remade surfaces' heights decide: one over
/// the walk mesh within <see cref="AlignmentReport.Clearance"/> (the characters' height) is
/// <see cref="AlignmentKind.Blocked"/> (they stand in it or walk through it); else the nearest
/// one under it lower than <see cref="AlignmentReport.Tolerance"/> is
/// <see cref="AlignmentKind.Floating"/> (a gap under their feet). Samples no remade surface
/// covers are not judged: the classic map is there.
/// </summary>
public static class Alignment
{
    /// <summary>How far the remade ground may be from the walk mesh (render units: field units).</summary>
    public const float DefaultTolerance = 2f;

    /// <summary>How much room the characters need over the walk mesh (Tidus is about 17 units tall).</summary>
    public const float DefaultClearance = 12f;

    /// <summary>How far apart a walk-mesh triangle's samples are (render units).</summary>
    public const float DefaultStep = 6f;

    private const float Cell = 25f;

    /// <summary>
    /// A triangle's samples: the centres of the <c>n²</c> triangles of its <c>n</c>-fold split,
    /// <c>n</c> its longest edge over <paramref name="step"/> (at least 1: the centre), so a
    /// piece of remade geometry wider than the step is seen wherever it lies.
    /// </summary>
    public static void Samples(Vector3 a, Vector3 b, Vector3 c, float step, List<Vector3> into)
    {
        into.Clear();
        float longest = Mathf.Max(a.DistanceTo(b), Mathf.Max(b.DistanceTo(c), c.DistanceTo(a)));
        int n = Mathf.Clamp(Mathf.CeilToInt(longest / Mathf.Max(step, 0.5f)), 1, 64);
        Vector3 P(float i, float j) => a + (b - a) * (i / n) + (c - a) * (j / n);
        for (int i = 0; i < n; i++)
            for (int j = 0; i + j < n; j++)
            {
                // the split's upright and (but on the last row) inverted triangles
                into.Add((P(i, j) + P(i + 1, j) + P(i, j + 1)) / 3f);
                if (i + j < n - 1)
                    into.Add((P(i + 1, j) + P(i, j + 1) + P(i + 1, j + 1)) / 3f);
            }
    }

    /// <summary>
    /// The triangles of every visible mesh under <paramref name="root"/> (itself included), in
    /// render space relative to <paramref name="space"/> (the map's root: identity in the
    /// editor and the game), each named by its node's path from <paramref name="root"/>.
    /// </summary>
    public static void Collect(Node root, Transform3D space, List<Surface> into, string? name = null, System.Func<Node, bool>? skip = null)
    {
        var inv = space.AffineInverse();
        void Walk(Node n)
        {
            if (skip?.Invoke(n) == true)
                return;
            if (n is Node3D { Visible: false })
                return;
            if (n is MeshInstance3D { Mesh: { } mesh } mi && mi.IsInsideTree() && !MapOverrideRuntime.IsNoDraw(mi.MaterialOverride))
            {
                var faces = mesh.GetFaces();
                var w = inv * mi.GlobalTransform;
                var src = name ?? (n == root ? root.Name.ToString() : $"{root.Name}/{root.GetPathTo(n)}");
                for (int i = 0; i + 2 < faces.Length; i += 3)
                    into.Add(new Surface(w * faces[i], w * faces[i + 1], w * faces[i + 2], src));
            }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(root);
    }

    /// <summary>The check of <paramref name="walk"/> against <paramref name="surfaces"/>.</summary>
    public static AlignmentReport Check(WalkMesh walk, IReadOnlyList<Surface> surfaces, float tolerance = DefaultTolerance,
        float clearance = DefaultClearance, float step = DefaultStep)
    {
        // the surfaces by the x-z cells their boxes touch
        var grid = new Dictionary<(int, int), List<int>>();
        var large = new List<int>();
        for (int i = 0; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            int x0 = Cx(Mathf.Min(s.A.X, Mathf.Min(s.B.X, s.C.X))), x1 = Cx(Mathf.Max(s.A.X, Mathf.Max(s.B.X, s.C.X)));
            int z0 = Cx(Mathf.Min(s.A.Z, Mathf.Min(s.B.Z, s.C.Z))), z1 = Cx(Mathf.Max(s.A.Z, Mathf.Max(s.B.Z, s.C.Z)));
            // a big one (a sea plane) is tried for every sample
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 4096)
            {
                large.Add(i);
                continue;
            }
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!grid.TryGetValue((x, z), out var list))
                        grid[(x, z)] = list = new();
                    list.Add(i);
                }
        }
        var issues = new List<AlignmentIssue>();
        int covered = 0;
        var samples = new List<Vector3>();
        for (int t = 0; t < walk.Count; t++)
        {
            if (!WalkMesh.Walkable(walk.Kind(t)))
                continue;
            var (a, b, c) = walk.Corners(t);
            Samples(a, b, c, step, samples);
            AlignmentIssue? worst = null;
            bool any = false;
            foreach (var p in samples)
            {
                var list = grid.GetValueOrDefault((Cx(p.X), Cx(p.Z)));
                if (list == null && large.Count == 0)
                    continue;
                float above = float.MaxValue, below = float.MaxValue;
                string aboveSrc = "", belowSrc = "";
                foreach (var i in list is null ? large : large.Count == 0 ? list : Both(list, large))
                {
                    var s = surfaces[i];
                    if (WalkMesh.HeightIn(s.A, s.B, s.C, p.X, p.Z) is not { } y)
                        continue;
                    var d = y - p.Y;
                    if (d > tolerance)
                    {
                        if (d < above)
                            (above, aboveSrc) = (d, s.Source);
                    }
                    else if (-d < below)
                        (below, belowSrc) = (-d, s.Source);
                }
                if (above == float.MaxValue && below == float.MaxValue)
                    continue;
                any = true;
                AlignmentIssue? issue = null;
                if (above < clearance)
                    issue = new AlignmentIssue(t, AlignmentKind.Blocked, above, p, aboveSrc);
                else if (below != float.MaxValue && below > tolerance)
                    issue = new AlignmentIssue(t, AlignmentKind.Floating, below, p, belowSrc);
                if (issue != null && (worst == null || Rank(issue) > Rank(worst)))
                    worst = issue;
            }
            if (any)
                covered++;
            if (worst != null)
                issues.Add(worst);
        }
        return new AlignmentReport
        {
            Triangles = walk.Count, Covered = covered, Surfaces = surfaces.Count, Tolerance = tolerance, Clearance = clearance, Issues = issues,
        };
    }

    private static IEnumerable<int> Both(List<int> a, List<int> b)
    {
        foreach (var i in a)
            yield return i;
        foreach (var i in b)
            yield return i;
    }

    private static float Rank(AlignmentIssue i) => (i.Kind == AlignmentKind.Blocked ? 1e6f : 0f) + i.Distance;

    private static int Cx(float v) => Mathf.FloorToInt(v / Cell);

    /// <summary>
    /// A patch of <paramref name="walk"/> as a mesh (render space): the walkable triangles whose
    /// centres lie within <paramref name="radius"/> of <paramref name="centre"/> in x-z, raised
    /// by <paramref name="lift"/>: the checks' stand-in for remade ground that lines up (lift 0)
    /// or does not.
    /// </summary>
    public static ArrayMesh? Patch(WalkMesh walk, Vector3 centre, float radius, float lift)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int n = 0;
        for (int t = 0; t < walk.Count; t++)
        {
            var m = walk.Centre(t);
            if (!WalkMesh.Walkable(walk.Kind(t)) || new Vector2(m.X - centre.X, m.Z - centre.Z).Length() > radius)
                continue;
            var (a, b, c) = walk.Corners(t);
            var up = Vector3.Up * lift;
            st.AddVertex(a + up);
            st.AddVertex(b + up);
            st.AddVertex(c + up);
            n++;
        }
        return n == 0 ? null : st.Commit();
    }
}
