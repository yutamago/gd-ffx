using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace GdFfx.Characters;

/// <summary>
/// A skeleton's bones as the rig builder sees them: names, parents, global rest poses in one
/// space (the game's character space for the classic skeleton, the skeleton's own for a remade
/// one) and, for the classic skeleton, each bone's game element (<c>SkeletonNode_i</c>).
/// </summary>
public sealed class SkeletonInfo
{
    public required string[] Names;
    public required int[] Parent;
    public required Transform3D[] Rest;
    public required int[] Element;

    public int Count => Names.Length;

    public Vector3 P(int b) => Rest[b].Origin;

    /// <summary>The bones of <paramref name="skel"/>, their rests taken by <paramref name="place"/> (the skeleton's space to the space wanted).</summary>
    public static SkeletonInfo Of(Skeleton3D skel, Transform3D place)
    {
        int n = skel.GetBoneCount();
        var info = new SkeletonInfo { Names = new string[n], Parent = new int[n], Rest = new Transform3D[n], Element = new int[n] };
        const string prefix = "SkeletonNode_";
        for (int b = 0; b < n; b++)
        {
            info.Names[b] = skel.GetBoneName(b);
            info.Parent[b] = skel.GetBoneParent(b);
            info.Rest[b] = place * skel.GetBoneGlobalRest(b);
            info.Element[b] = info.Names[b].StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(info.Names[b][prefix.Length..], out var e) ? e : -1;
        }
        return info;
    }

    /// <summary>
    /// The game's PS2 skeleton from <see cref="FfxGame.ClassicCharacterSkeleton"/>'s JSON: every
    /// element (<c>SkeletonNode_i</c>), its parent and its bind pose composed into globals
    /// (character space); null when it does not read.
    /// </summary>
    public static SkeletonInfo? FromBind(string json)
    {
        if (json.Length == 0)
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var list = doc.RootElement.EnumerateArray().ToList();
            int n = list.Count;
            var info = new SkeletonInfo { Names = new string[n], Parent = new int[n], Rest = new Transform3D[n], Element = new int[n] };
            var local = new Transform3D[n];
            float F(System.Text.Json.JsonElement e, int i) => e[i].GetSingle();
            for (int i = 0; i < n; i++)
            {
                var b = list[i];
                info.Names[i] = $"SkeletonNode_{i}";
                info.Element[i] = i;
                info.Parent[i] = b.GetProperty("parent").GetInt32();
                var t = b.GetProperty("t");
                var r = b.GetProperty("r");
                var sc = b.GetProperty("s");
                local[i] = new Transform3D(new Basis(new Quaternion(F(r, 0), F(r, 1), F(r, 2), F(r, 3)).Normalized())
                    * Basis.FromScale(new Vector3(F(sc, 0), F(sc, 1), F(sc, 2))), new Vector3(F(t, 0), F(t, 1), F(t, 2)));
            }
            var done = new bool[n];
            Transform3D Global(int i)
            {
                if (done[i])
                    return info.Rest[i];
                int p = info.Parent[i];
                info.Rest[i] = p >= 0 && p < n && p != i ? Global(p) * local[i] : local[i];
                done[i] = true;
                return info.Rest[i];
            }
            for (int i = 0; i < n; i++)
                Global(i);
            return info;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    public int Find(string name) => Array.IndexOf(Names, name);

    /// <summary>The bone of game element <paramref name="e"/> (-1 none).</summary>
    public int OfElement(int e) => Array.IndexOf(Element, e);

    public bool IsUnder(int b, int ancestor)
    {
        for (int p = b; p >= 0; p = Parent[p])
            if (p == ancestor)
                return true;
        return false;
    }

    public int Lca(int a, int b)
    {
        var up = new HashSet<int>();
        for (int p = a; p >= 0; p = Parent[p])
            up.Add(p);
        for (int p = b; p >= 0; p = Parent[p])
            if (up.Contains(p))
                return p;
        return -1;
    }

    /// <summary>The bones from <paramref name="from"/> (excluded) down to <paramref name="to"/> (included).</summary>
    public List<int> Path(int from, int to)
    {
        var list = new List<int>();
        for (int p = to; p >= 0 && p != from; p = Parent[p])
            list.Add(p);
        list.Reverse();
        return list;
    }

    public List<int> Children(int b)
    {
        var list = new List<int>();
        for (int c = 0; c < Count; c++)
            if (Parent[c] == b)
                list.Add(c);
        return list;
    }

    public bool IsLeaf(int b) => Array.IndexOf(Parent, b) < 0;
}

/// <summary>A skeleton's body parts (<see cref="RigBuilder.Label"/>): each label's bone, the spine's and necks' bones, the body's axes and height.</summary>
public sealed class BodyLabels
{
    public readonly Dictionary<string, int> Bones = new();
    public readonly List<int> Spine = new();
    public readonly List<int> Neck = new();
    public Vector3 Up, Forward, Right;
    public float Height;
    public Vector3 Feet;
    /// <summary>How the labels were found (names, the hierarchy and rest positions).</summary>
    public string How = "";

    public int Of(string label) => Bones.TryGetValue(label, out var b) ? b : -1;
}

/// <summary>
/// Builds a <see cref="CharacterRig"/> for a remade skeleton against the game's (docs/remake.md,
/// "Retargeting"): the automatic bone mapping (body parts found by name patterns, else by the
/// hierarchy and rest positions: the feet the lowest leaves, the hands the widest pair above the
/// hips, the head the highest leaf between them, the long pairs of each limb its upper and lower
/// bones), the alignment (up, facing, height), the rest offsets (each mapped bone's rest turned to
/// point where the game's does), cloth chains, the face table and the attachments.
/// </summary>
public static class RigBuilder
{
    /// <summary>The body parts mapped, limbs per side (<c>.L</c>, <c>.R</c>: the character's left and right).</summary>
    public static readonly string[] Parts =
    [
        "hips", "neck", "head", "jaw",
        "clavicle.L", "upperarm.L", "forearm.L", "hand.L", "clavicle.R", "upperarm.R", "forearm.R", "hand.R",
        "thigh.L", "shin.L", "foot.L", "toes.L", "thigh.R", "shin.R", "foot.R", "toes.R",
    ];

    // ---- labels by name -----------------------------------------------------------------------

    private static readonly (string Label, Regex Pattern)[] NamePatterns =
    [
        ("hips", new Regex("^(hips?|pelvis|roothips?|cog)$")),
        ("spine", new Regex("^(spine[a-z0-9]*|abdomen[0-9]*|torso[0-9]*|waist[0-9]*)$")),
        ("chest", new Regex("^(chest[a-z0-9]*|upperchest|ribcage)$")),
        ("neck", new Regex("^neck\\d*$")),
        ("head", new Regex("^head$")),
        ("jaw", new Regex("^(jaw|chin|mouth)$")),
        ("clavicle", new Regex("^(clavicle|shoulder|collar(bone)?)$")),
        ("forearm", new Regex("^(forearm|lowerarm|lowarm|elbow|armlower)$")),
        ("upperarm", new Regex("^(upperarm|uparm|arm|armupper)$")),
        ("hand", new Regex("^(hand|wrist)$")),
        ("thigh", new Regex("^(thigh|upleg|upperleg|legupper|hip)$")),
        ("shin", new Regex("^(shin|calf|lowerleg|lowleg|knee|leg|leglower)$")),
        ("foot", new Regex("^(foot|ankle)$")),
        ("toes", new Regex("^(toes?|toebase|ball)$")),
    ];

    /// <summary>A bone name's side (<c>L</c>, <c>R</c> or empty) and its base (letters and digits, lower case, without the side).</summary>
    public static (string Side, string Base) SplitName(string name)
    {
        var s = name.ToLowerInvariant();
        int colon = s.LastIndexOf(':');
        if (colon >= 0)
            s = s[(colon + 1)..];
        string side = "";
        foreach (var (word, sd) in new[] { ("left", "L"), ("right", "R") })
        {
            if (s.StartsWith(word, StringComparison.Ordinal)) { side = sd; s = s[word.Length..]; break; }
            if (s.EndsWith(word, StringComparison.Ordinal)) { side = sd; s = s[..^word.Length]; break; }
        }
        if (side.Length == 0)
        {
            var m = Regex.Match(s, "^(l|r)[._\\- ](.+)$");
            if (m.Success) { side = m.Groups[1].Value.ToUpperInvariant(); s = m.Groups[2].Value; }
            else if ((m = Regex.Match(s, "^(.+)[._\\- ](l|r)$")).Success) { side = m.Groups[2].Value.ToUpperInvariant(); s = m.Groups[1].Value; }
            else if ((m = Regex.Match(s, "^(.+)[._\\- ](l|r)[._\\- ](.+)$")).Success) { side = m.Groups[2].Value.ToUpperInvariant(); s = m.Groups[1].Value + m.Groups[3].Value; }
        }
        return (side, Regex.Replace(s, "[^a-z0-9]", ""));
    }

    /// <summary>The body part a bone's name says (with its side for limbs), or null.</summary>
    public static string? NameLabel(string name)
    {
        var (side, b) = SplitName(name);
        foreach (var (label, re) in NamePatterns)
            if (re.IsMatch(b))
            {
                bool limb = label is "clavicle" or "upperarm" or "forearm" or "hand" or "thigh" or "shin" or "foot" or "toes";
                if (limb)
                    return side.Length > 0 ? $"{label}.{side}" : null;
                // "hip" with a side is a thigh (handled above); without, the hips
                return label;
            }
        return null;
    }

    // ---- labels by the hierarchy and rest positions ----------------------------------------------

    /// <summary>
    /// The body parts of skeleton <paramref name="s"/> whose up is <paramref name="up"/> (in the
    /// rests' space). Names first when they give the main parts (hips, head, both upper arms and
    /// thighs), the hierarchy and rest positions for the rest; <paramref name="hints"/>: labels
    /// known otherwise (the game's attachment points: 9 the head, 13 the jaw). Null when the
    /// skeleton is not a humanoid.
    /// </summary>
    public static BodyLabels? Label(SkeletonInfo s, Vector3 up, IReadOnlyDictionary<string, int>? hints = null)
    {
        var l = new BodyLabels { Up = up.Normalized() };
        var topo = Topology(s, l.Up, l);
        // names
        var named = new Dictionary<string, int>();
        var spineNamed = new List<int>();
        var neckNamed = new List<int>();
        for (int b = 0; b < s.Count; b++)
            if (NameLabel(s.Names[b]) is { } lab)
            {
                if (lab is "spine" or "chest")
                    spineNamed.Add(b);
                else if (lab == "neck")
                    neckNamed.Add(b);
                else if (!named.ContainsKey(lab))
                    named[lab] = b;
            }
        bool byName = new[] { "hips", "head", "upperarm.L", "upperarm.R", "thigh.L", "thigh.R" }.All(named.ContainsKey);
        if (!topo && !byName)
            return null;
        if (byName)
        {
            l.How = topo ? "names (the hierarchy agrees where it can)" : "names";
            foreach (var (k, v) in named)
                l.Bones[k] = v;
            int depth(int b) { int d = 0; for (int p = s.Parent[b]; p >= 0; p = s.Parent[p]) d++; return d; }
            spineNamed.Sort((a, b) => depth(a).CompareTo(depth(b)));
            neckNamed.Sort((a, b) => depth(a).CompareTo(depth(b)));
            l.Spine.Clear();
            l.Spine.AddRange(spineNamed);
            l.Neck.Clear();
            l.Neck.AddRange(neckNamed);
            if (!topo)
                Axes(s, l);
        }
        else
            l.How = "the hierarchy and rest positions";
        if (hints != null)
            foreach (var (k, v) in hints)
                if (v >= 0)
                    l.Bones[k] = v;
        if (l.Neck.Count > 0)
            l.Bones["neck"] = l.Neck[^1];
        return l;
    }

    /// <summary>Finds the body by the hierarchy and rest positions into <paramref name="l"/>; false when it is not a humanoid.</summary>
    private static bool Topology(SkeletonInfo s, Vector3 up, BodyLabels l)
    {
        int n = s.Count;
        if (n < 8)
            return false;
        float H(int b) => s.P(b).Dot(up);
        Vector3 Flat(Vector3 v) => v - up * v.Dot(up);
        float hMin = Enumerable.Range(0, n).Min(H), hMax = Enumerable.Range(0, n).Max(H);
        float body = hMax - hMin;
        if (body <= 0)
            return false;
        // leaves that mark nothing are left out: at the skeleton's origin (the classic models' unused
        // face bones, ground markers) or on their parent
        var leaves = Enumerable.Range(0, n).Where(b => s.IsLeaf(b) && s.P(b).Length() > 0.002f * body
            && (s.Parent[b] < 0 || s.P(b).DistanceTo(s.P(s.Parent[b])) > 0.002f * body)).ToList();
        // the feet: the lowest leaf, and the low leaf farthest from it across
        // (the other foot at the first one's height: helper leaves at the knees stand higher)
        int l1 = leaves.OrderBy(H).First();
        int l2 = -1;
        foreach (var window in new[] { 0.02f, 0.15f })
        {
            l2 = leaves.Where(b => H(b) < H(l1) + window * body && b != l1 && Flat(s.P(b) - s.P(l1)).Length() > 0.02f * body)
                .OrderByDescending(b => Flat(s.P(b) - s.P(l1)).Length()).FirstOrDefault(-1);
            if (l2 >= 0)
                break;
        }
        if (l2 < 0 || Flat(s.P(l2) - s.P(l1)).Length() < 0.02f * body)
            return false;
        int pelvis = s.Lca(l1, l2);
        if (pelvis < 0)
            return false;
        int leg1 = s.Path(pelvis, l1)[0], leg2 = s.Path(pelvis, l2)[0];
        bool InLegs(int b) => s.IsUnder(b, leg1) || s.IsUnder(b, leg2);
        // the hands: the widest pair of leaves above the pelvis, outside the legs
        var arms = leaves.Where(b => !InLegs(b) && H(b) > H(pelvis)).ToList();
        int h1 = -1, h2 = -1;
        float best = 0;
        for (int i = 0; i < arms.Count; i++)
            for (int j = i + 1; j < arms.Count; j++)
            {
                float d = Flat(s.P(arms[i]) - s.P(arms[j])).Length();
                if (d > best) { best = d; h1 = arms[i]; h2 = arms[j]; }
            }
        if (h1 < 0 || best < 0.2f * body)
            return false;
        int chest = s.Lca(h1, h2);
        if (chest < 0 || chest == pelvis)
            return false;
        int arm1 = s.Path(chest, h1)[0], arm2 = s.Path(chest, h2)[0];
        // the head: the highest leaf under the chest outside the arms
        int headLeaf = leaves.Where(b => s.IsUnder(b, chest) && b != chest && !s.IsUnder(b, arm1) && !s.IsUnder(b, arm2))
            .OrderByDescending(H).FirstOrDefault(-1);
        int hips = s.Lca(pelvis, chest);
        if (headLeaf < 0 || hips < 0)
            return false;
        l.Height = body;
        // the joints of each chain: bones at one place grouped, the deepest of each group drives it
        var leg = new[] { Joints(s, s.Path(pelvis, l1), body), Joints(s, s.Path(pelvis, l2), body) };
        var arm = new[] { Joints(s, s.Path(chest, h1), body), Joints(s, s.Path(chest, h2), body) };
        var legEnd = new[] { l1, l2 };
        // facing: the feet's toes ahead of the ankles
        var forward = Vector3.Zero;
        var legLabels = new List<Dictionary<string, int>>();
        for (int k = 0; k < 2; k++)
        {
            var j = leg[k];
            var d = Limb(s, j, legEnd[k], ["thigh", "shin", "foot", "toes"], null);
            legLabels.Add(d);
            if (d.TryGetValue("foot", out var f) && d.TryGetValue("toes", out var t))
                forward += Flat(s.P(t) - s.P(f));
            else if (d.TryGetValue("foot", out f))
                forward += Flat(s.P(legEnd[k]) - s.P(f));
        }
        if (forward.LengthSquared() < 1e-12f)
            return false;
        l.Forward = forward.Normalized();
        l.Right = l.Forward.Cross(l.Up).Normalized();
        l.Feet = (s.P(l1) + s.P(l2)) * 0.5f;
        l.Feet -= up * l.Feet.Dot(up);
        l.Feet += up * hMin;
        // sides by the right axis
        for (int k = 0; k < 2; k++)
        {
            var d = legLabels[k];
            var side = Flat(s.P(leg[k].Count > 0 ? leg[k][0] : pelvis) - s.P(hips)).Dot(l.Right) > 0 ? "R" : "L";
            foreach (var (lab, b) in d)
                l.Bones[$"{lab}.{side}"] = b;
        }
        for (int k = 0; k < 2; k++)
        {
            var d = Limb(s, arm[k], k == 0 ? h1 : h2, ["upperarm", "forearm", "hand"], "clavicle");
            var side = Flat(s.P(k == 0 ? h1 : h2) - s.P(chest)).Dot(l.Right) > 0 ? "R" : "L";
            foreach (var (lab, b) in d)
                l.Bones[$"{lab}.{side}"] = b;
        }
        l.Bones["hips"] = hips;
        // the spine: the joints from the hips (excluded) to the chest (included)
        l.Spine.AddRange(Joints(s, s.Path(hips, chest), body, keepLast: true));
        // the neck and head: the joints from the chest to the highest leaf (the leaf an end)
        var head = Joints(s, s.Path(chest, headLeaf), body);
        if (head.Count >= 2)
        {
            l.Neck.AddRange(head.Take(head.Count - 1));
            l.Bones["head"] = head[^1];
        }
        else if (head.Count == 1)
            l.Bones["head"] = head[0];
        return true;
    }

    /// <summary>Up, facing and height from the labels found by name (no topology).</summary>
    private static void Axes(SkeletonInfo s, BodyLabels l)
    {
        float H(int b) => s.P(b).Dot(l.Up);
        var hb = Enumerable.Range(0, s.Count).Select(H).ToList();
        l.Height = hb.Max() - hb.Min();
        var fwd = Vector3.Zero;
        foreach (var side in new[] { "L", "R" })
            if (l.Of($"foot.{side}") is var f && f >= 0 && l.Of($"toes.{side}") is var t && t >= 0)
                fwd += s.P(t) - s.P(f);
        fwd -= l.Up * fwd.Dot(l.Up);
        if (fwd.LengthSquared() < 1e-12f)
        {
            // the right hand's side and up give the facing
            if (l.Of("upperarm.R") is var r && r >= 0 && l.Of("upperarm.L") is var lf && lf >= 0)
                fwd = l.Up.Cross(s.P(r) - s.P(lf));
            else
                fwd = Vector3.Back;
        }
        l.Forward = fwd.Normalized();
        l.Right = l.Forward.Cross(l.Up).Normalized();
        float low = hb.Min();
        l.Feet = Vector3.Zero - l.Up * Vector3.Zero.Dot(l.Up) + l.Up * low;
    }

    /// <summary>
    /// A chain's joints, root first: consecutive bones at one place (within 0.2 % of the body)
    /// grouped, each group's deepest bone; the last group (the chain's end) left out unless
    /// <paramref name="keepLast"/>.
    /// </summary>
    private static List<int> Joints(SkeletonInfo s, List<int> path, float body, bool keepLast = false)
    {
        var groups = new List<List<int>>();
        foreach (var b in path)
        {
            if (groups.Count > 0 && s.P(groups[^1][^1]).DistanceTo(s.P(b)) < 0.002f * body)
                groups[^1].Add(b);
            else
                groups.Add(new List<int> { b });
        }
        var joints = groups.Select(g => g[^1]).ToList();
        if (!keepLast && joints.Count > 0)
            joints.RemoveAt(joints.Count - 1);
        return joints;
    }

    /// <summary>
    /// A limb's labels from its joints (<paramref name="end"/>: the chain's last bone, where its
    /// last segment ends): the consecutive pair of the longest segments are the upper and lower
    /// parts (<paramref name="names"/>[0], [1]), the joints after them the next names; the joint
    /// before the pair is <paramref name="before"/> (the clavicle) when given.
    /// </summary>
    private static Dictionary<string, int> Limb(SkeletonInfo s, List<int> joints, int end, string[] names, string? before)
    {
        var d = new Dictionary<string, int>();
        if (joints.Count < 2)
            return d;
        var len = new float[joints.Count];
        for (int i = 0; i < joints.Count; i++)
            len[i] = s.P(i + 1 < joints.Count ? joints[i + 1] : end).DistanceTo(s.P(joints[i]));
        int pair = 0;
        float best = -1;
        for (int i = 0; i + 1 < joints.Count; i++)
            if (len[i] + len[i + 1] > best)
            {
                best = len[i] + len[i + 1];
                pair = i;
            }
        for (int k = 0; k < names.Length && pair + k < joints.Count; k++)
            d[names[k]] = joints[pair + k];
        if (before != null && pair > 0)
            d[before] = joints[pair - 1];
        return d;
    }

    // ---- the rig ------------------------------------------------------------------------------

    /// <summary>The game's character space's up (y down: the head towards -y).</summary>
    public static readonly Vector3 GameUp = Vector3.Down;

    /// <summary>A classic cloth vertex: its segment (PS2 model, piece), its number in it, its place at the bind pose (character space).</summary>
    public readonly record struct ClothVertex(int Model, int Piece, int K, Vector3 Position);

    /// <summary>What <see cref="Build"/> found and made.</summary>
    public sealed record Result(CharacterRig Rig, BodyLabels Game, BodyLabels Remade, Transform3D[] Aligned, List<string> Notes);

    /// <summary>
    /// A remade skeleton's up in its own space: the axis it is tallest along (y, or z for a model
    /// kept z up), towards the bone named as the head (else +).
    /// </summary>
    public static Vector3 GuessUp(SkeletonInfo s)
    {
        var min = Vector3.One * float.MaxValue;
        var max = -min;
        for (int b = 0; b < s.Count; b++)
        {
            min = min.Min(s.P(b));
            max = max.Max(s.P(b));
        }
        var ext = max - min;
        var axis = ext.Z > ext.Y * 1.2f && ext.Z > ext.X ? Vector3.Back : Vector3.Up;
        var center = (min + max) * 0.5f;
        for (int b = 0; b < s.Count; b++)
            if (NameLabel(s.Names[b]) == "head")
                return (s.P(b) - center).Dot(axis) < 0 ? -axis : axis;
        return axis;
    }

    /// <summary>The game's labels of the classic skeleton (character space), the attachment points' head (9) and jaw (13) as hints.</summary>
    public static BodyLabels? GameLabels(SkeletonInfo game, IReadOnlyDictionary<int, int>? points)
    {
        var hints = new Dictionary<string, int>();
        if (points != null)
        {
            if (points.TryGetValue(9, out var head) && game.OfElement(head) is var hb && hb >= 0)
                hints["head"] = hb;
            if (points.TryGetValue(13, out var jaw) && game.OfElement(jaw) is var jb && jb >= 0)
                hints["jaw"] = jb;
        }
        return Label(game, GameUp, hints);
    }

    /// <summary>
    /// The alignment (the remade root's transform) that stands the remade skeleton where the
    /// game's stands: its up and facing turned to the game's, its head height scaled to the game's,
    /// its feet on the game's.
    /// </summary>
    public static Transform3D AutoAlign(BodyLabels game, SkeletonInfo gameInfo, BodyLabels remade, SkeletonInfo remadeInfo, Transform3D skelInRoot)
    {
        static Basis Frame(BodyLabels l)
        {
            var up = l.Up;
            var fwd = (l.Forward - up * l.Forward.Dot(up)).Normalized();
            var right = fwd.Cross(up).Normalized();
            return new Basis(right, up, fwd);
        }
        var r = Frame(game) * Frame(remade).Transposed();
        float HeadHeight(BodyLabels l, SkeletonInfo s) =>
            l.Of("head") is var h && h >= 0 ? (s.P(h) - l.Feet).Dot(l.Up) : l.Height;
        float hs = HeadHeight(remade, remadeInfo), hg = HeadHeight(game, gameInfo);
        float scale = hs > 1e-6f ? hg / hs : 1f;
        var basis = r * Basis.FromScale(Vector3.One * scale);
        var k = new Transform3D(basis, game.Feet - basis * remade.Feet);
        return k * skelInRoot.AffineInverse();
    }

    /// <summary>
    /// Maps the remade bones to the game's elements by their body parts (rows set by hand are
    /// kept): the limbs, hips, neck (all neck bones to the game's neck), head and jaw; the spines
    /// by their order (first to first, last to last, the middle by their place along them). The
    /// hips carry the game's position (<see cref="BonePositions.Game"/>).
    /// </summary>
    public static int AutoMap(CharacterRig rig, BodyLabels game, SkeletonInfo gameInfo, BodyLabels remade, SkeletonInfo remadeInfo)
    {
        var kept = rig.Bones.Where(b => b.ByHand).ToList();
        var keptNames = kept.Select(b => b.Bone).ToHashSet();
        var rows = new List<RigBone>(kept);
        void Add(int rb, int gb, string label)
        {
            if (rb < 0 || gb < 0 || gameInfo.Element[gb] < 0 || keptNames.Contains(remadeInfo.Names[rb]) || rows.Any(r => r.Bone == remadeInfo.Names[rb]))
                return;
            rows.Add(new RigBone
            {
                Bone = remadeInfo.Names[rb], Element = gameInfo.Element[gb], Label = label,
                Positions = label == "hips" ? BonePositions.Game : BonePositions.Rotation,
            });
        }
        foreach (var part in Parts)
            Add(remade.Of(part), game.Of(part), part);
        // the spines by order (the chest last), the necks all to the game's last neck joint
        if (remade.Spine.Count > 0 && game.Spine.Count > 0)
            for (int i = 0; i < remade.Spine.Count; i++)
            {
                int gi = remade.Spine.Count == 1 ? game.Spine.Count - 1
                    : (int)Mathf.Round(i * (game.Spine.Count - 1) / (float)(remade.Spine.Count - 1));
                Add(remade.Spine[i], game.Spine[gi], i == remade.Spine.Count - 1 ? "chest" : $"spine{i}");
            }
        if (game.Neck.Count > 0)
            foreach (var nb in remade.Neck)
                Add(nb, game.Neck[^1], "neck");
        // in the remade skeleton's order
        rows.Sort((a, b) => remadeInfo.Find(a.Bone).CompareTo(remadeInfo.Find(b.Bone)));
        rig.Bones.Clear();
        foreach (var r in rows)
            rig.Bones.Add(r);
        return rows.Count(r => !r.ByHand);
    }

    /// <summary>
    /// The rest offsets (<see cref="RigBone.Rest"/>, <see cref="AttachMap.Rest"/>) for the rig's
    /// mapping and alignment: the remade rest, parents first, each mapped bone with one branch of
    /// mapped bones under it turned so the next mapped bone (at a place of its own in the game) lies
    /// where the game's next element does (the rest-pose alignment), then its rotation taken
    /// relative to its element's rest. Returns the aligned rest of every remade bone in character
    /// space.
    /// </summary>
    public static Transform3D[] ComputeRest(CharacterRig rig, SkeletonInfo game, SkeletonInfo remade, Transform3D skelInRoot, SkeletonInfo? bind = null)
    {
        var k = rig.Align * skelInRoot;
        var rk = k.Basis.Orthonormalized();
        var rkT = rk.Transposed();
        int n = remade.Count;
        var map = new RigBone?[n];
        var gameBone = new int[n];
        var jaws = new List<(int Bone, RigBone Map)>();
        foreach (var m in rig.Bones)
        {
            int b = remade.Find(m.Bone);
            if (b < 0)
                continue;
            // the jaw (and the face) is no body chain: no bone's rest is aimed at it
            map[b] = m.Element >= 0 && m.Label != "jaw" ? m : null;
            if (m.Label == "jaw" && m.Element >= 0)
                jaws.Add((b, m));
            gameBone[b] = m.Element >= 0 ? game.OfElement(m.Element) : -1;
            m.Aim = "";
        }
        float height = 1f;
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (int b = 0; b < game.Count; b++)
            {
                float h = game.P(b).Dot(GameUp);
                lo = Math.Min(lo, h);
                hi = Math.Max(hi, h);
            }
            if (hi > lo)
                height = hi - lo;
        }
        // a bone's child branches holding mapped bones
        bool HasMapped(int b)
        {
            if (map[b] != null)
                return true;
            foreach (var c in remade.Children(b))
                if (HasMapped(c))
                    return true;
            return false;
        }
        var depth = new int[n];
        for (int b = 0; b < n; b++)
            for (int p = remade.Parent[b]; p >= 0; p = remade.Parent[p])
                depth[b]++;
        var order = Enumerable.Range(0, n).OrderBy(b => depth[b]).ThenBy(b => b).ToArray();
        var aligned = new Transform3D[n];
        foreach (var b in order)
        {
            int p = remade.Parent[b];
            var restLocal = p >= 0 ? remade.Rest[p].AffineInverse() * remade.Rest[b] : remade.Rest[b];
            var w = p >= 0 ? aligned[p] * restLocal : restLocal;
            if (map[b] is { } m && gameBone[b] >= 0)
            {
                var branches = remade.Children(b).Count(HasMapped);
                if (branches == 1)
                {
                    // the first mapped bone down the branch whose element stands apart from this one's
                    int aim = -1;
                    var queue = new Queue<int>(remade.Children(b));
                    while (queue.Count > 0 && aim < 0)
                    {
                        int c = queue.Dequeue();
                        if (map[c] != null && gameBone[c] >= 0 && game.P(gameBone[c]).DistanceTo(game.P(gameBone[b])) > 1e-3f * height)
                        {
                            aim = c;
                            break;
                        }
                        foreach (var d in remade.Children(c))
                            queue.Enqueue(d);
                    }
                    if (aim >= 0)
                    {
                        var o = (remade.Rest[b].AffineInverse() * remade.Rest[aim]).Origin;
                        var have = rk * (w.Basis * o);
                        var want = game.P(gameBone[aim]) - game.P(gameBone[b]);
                        if (have.LengthSquared() > 1e-12f && want.LengthSquared() > 1e-12f)
                        {
                            var q = new Basis(Retargeter.Arc(have.Normalized(), want.Normalized()));
                            w = new Transform3D(rkT * q * rk * w.Basis, w.Origin);
                        }
                        m.Aim = remade.Names[aim];
                    }
                }
                var rc = rk * w.Basis.Orthonormalized();
                m.Rest = (game.Rest[gameBone[b]].Basis.Orthonormalized().Inverse() * rc).GetRotationQuaternion();
            }
            aligned[b] = w;
        }
        foreach (var (jb, jm) in jaws)
            if (game.OfElement(jm.Element) is var gj && gj >= 0)
                jm.Rest = (game.Rest[gj].Basis.Orthonormalized().Inverse() * (rk * aligned[jb].Basis.Orthonormalized())).GetRotationQuaternion();
        var alignedC = new Transform3D[n];
        for (int b = 0; b < n; b++)
            alignedC[b] = k * aligned[b];
        // the attachments: the element's rest (the classic skeleton's, else the PS2 skeleton's: the
        // hands' points are ends the HD model has no node for) in the bone's unscaled aligned frame;
        // a socket bone (not mapped) takes the element's turn at its own place
        foreach (var a in rig.Attach)
        {
            int ab = remade.Find(a.Bone);
            Transform3D? rest = game.OfElement(a.Element) is var ge && ge >= 0 ? game.Rest[ge]
                : bind != null && a.Element >= 0 && a.Element < bind.Count ? bind.Rest[a.Element] : null;
            if (ab < 0 || rest is not { } r)
                continue;
            var frame = new Transform3D(alignedC[ab].Basis.Orthonormalized(), alignedC[ab].Origin);
            var rel = frame.AffineInverse() * r;
            a.Rest = rig.Find(a.Bone) is { Element: >= 0 } ? rel : new Transform3D(rel.Basis.Orthonormalized(), Vector3.Zero);
        }
        return alignedC;
    }

    /// <summary>How near (of the body's height) a chain's bones must be to a classic cloth segment's vertices to follow the game's cloth.</summary>
    public const float ClothReach = 0.12f;

    private static readonly Regex ClothName = new("(hair|tail|pony|hood|cape|cloak|skirt|coat|cloth|scarf|ribbon|strand|bang|tassel|sash)");

    /// <summary>
    /// The remade cloth chains: bones named as cloth (hair, tail, hood, cape, skirt...), not
    /// mapped, each root with its single cloth children down. A chain whose every bone has a
    /// classic cloth vertex of one segment near it (within <see cref="ClothReach"/> of the body, at the aligned rest)
    /// follows the game's cloth, else Godot's spring bones. Chains named as before keep their
    /// settings.
    /// </summary>
    public static int AutoCloth(CharacterRig rig, SkeletonInfo remade, Transform3D[] alignedC, IReadOnlyList<ClothVertex>? cloth, float bodyHeight, List<string>? notes = null, SkeletonInfo? game = null)
    {
        var mapped = rig.Bones.Where(b => b.Element >= 0).Select(b => b.Bone).ToHashSet();
        bool IsCloth(int b) => !mapped.Contains(remade.Names[b]) && ClothName.IsMatch(remade.Names[b].ToLowerInvariant());
        int made = 0;
        for (int b = 0; b < remade.Count; b++)
        {
            if (!IsCloth(b) || (remade.Parent[b] >= 0 && IsCloth(remade.Parent[b])))
                continue;
            var bones = new List<int> { b };
            while (remade.Children(bones[^1]).Where(IsCloth).ToList() is { Count: 1 } next)
                bones.Add(next[0]);
            if (bones.Count < 2)
                continue;
            var name = remade.Names[b];
            if (rig.Cloth.Any(c => c.Name == name))
                continue;
            // the spring bones' gravity and radius in the game's units: about 3 m/s² and 2 cm in the model's
            var sv = rig.Align.Basis.Scale;
            float scale = (sv.X + sv.Y + sv.Z) / 3f;
            var chain = new ClothChain
            {
                Name = name, Bones = bones.Select(x => remade.Names[x]).ToArray(), Mode = ClothMode.Godot,
                Gravity = 3f * scale, Radius = 0.02f * scale,
            };
            if (cloth is { Count: > 0 })
            {
                var root = alignedC[b].Origin;
                var near = cloth.OrderBy(v => v.Position.DistanceSquaredTo(root)).First();
                var seg = cloth.Where(v => v.Model == near.Model && v.Piece == near.Piece).ToList();
                // each bone its nearest vertex of the segment not taken by an earlier bone of the chain
                var points = new int[bones.Count];
                var taken = new HashSet<int>();
                float worst = 0;
                Vector3? last = null;
                for (int i = 0; i < bones.Count; i++)
                {
                    var at = alignedC[bones[i]].Origin;
                    // at least half the bone's length from the last point: a pair of neighbours would point anywhere
                    float gap = i > 0 ? 0.5f * at.DistanceTo(alignedC[bones[i - 1]].Origin) : 0f;
                    var v = seg.Where(x => !taken.Contains(x.K) && (last == null || x.Position.DistanceTo(last.Value) >= gap))
                        .OrderBy(x => x.Position.DistanceSquaredTo(at)).FirstOrDefault(seg[0]);
                    last = v.Position;
                    taken.Add(v.K);
                    points[i] = v.K;
                    worst = Math.Max(worst, v.Position.DistanceTo(at));
                }
                notes?.Add($"cloth chain {name}: the game's segment (model {near.Model}, piece {near.Piece}) within {worst:F2} units "
                    + $"(limit {ClothReach * bodyHeight:F2})");
                if (worst < ClothReach * bodyHeight && points.Distinct().Count() == points.Length)
                {
                    chain.Mode = ClothMode.Game;
                    chain.Model = near.Model;
                    chain.Piece = near.Piece;
                    chain.Points = points;
                    var at = points.Select(k => seg.First(v => v.K == k).Position).ToArray();
                    chain.RestDirections = Enumerable.Range(0, at.Length - 1).Select(i => at[i + 1] - at[i]).ToArray();
                    // the element it hangs from: the chain's nearest mapped ancestor's
                    for (int p = remade.Parent[b]; p >= 0; p = remade.Parent[p])
                        if (rig.Find(remade.Names[p]) is { Element: >= 0 } m && game?.OfElement(m.Element) is { } gb && gb >= 0)
                        {
                            chain.Anchor = m.Element;
                            chain.AnchorRest = game.Rest[gb].Basis.Orthonormalized().GetRotationQuaternion();
                            break;
                        }
                }
            }
            rig.Cloth.Add(chain);
            made++;
        }
        return made;
    }

    private static readonly Regex MouthShape = new("(mouth.?open|jaw.?open|open.?mouth|^mouth$|^aa$)");

    /// <summary>
    /// The face table's mouth: the game's jaw (attachment point 13, which the game opens as the
    /// character talks: up to 7° about the jaw's axis) onto the remade face's mouth-open blend
    /// shape. Its input range starts at the jaw's rest angle.
    /// </summary>
    public static int AutoFace(CharacterRig rig, BodyLabels game, SkeletonInfo gameInfo, IEnumerable<MeshInstance3D> meshes)
    {
        int jaw = game.Of("jaw");
        if (jaw < 0 || rig.Face.Any(f => f.Name == "mouth"))
            return 0;
        string? shape = null;
        foreach (var mi in meshes)
            if (mi.Mesh is ArrayMesh am)
                for (int i = 0; i < am.GetBlendShapeCount() && shape == null; i++)
                    if (MouthShape.IsMatch(am.GetBlendShapeName(i).ToString().ToLowerInvariant()))
                        shape = am.GetBlendShapeName(i);
        if (shape == null)
            return 0;
        int parent = gameInfo.Parent[jaw];
        while (parent >= 0 && gameInfo.Element[parent] < 0)
            parent = gameInfo.Parent[parent];
        var restLocal = parent >= 0 ? gameInfo.Rest[parent].AffineInverse() * gameInfo.Rest[jaw] : gameInfo.Rest[jaw];
        float v0 = Retargeter.FaceInput(restLocal, FaceSource.RotationX);
        rig.Face.Add(new FaceMap
        {
            Name = "mouth", Element = gameInfo.Element[jaw], Relative = parent >= 0 ? gameInfo.Element[parent] : -1,
            Source = FaceSource.RotationX, InMin = v0, InMax = v0 + 7f, Target = FaceTarget.BlendShape, TargetName = shape, OutMin = 0, OutMax = 1,
        });
        return 1;
    }

    private static readonly Regex SocketName = new("(weapon|socket|prop|grip|hold)");

    /// <summary>
    /// The attachments: the game's hand points (4 and 5 on the party: where it binds weapons) onto
    /// the remade bone named as a weapon socket on that side, else the remade hand; carried over by
    /// the nearest mapped element above the point (<paramref name="elementParents"/>: the game's PS2
    /// skeleton's parents, which has the points the HD model lacks).
    /// </summary>
    public static int AutoAttach(CharacterRig rig, IReadOnlyDictionary<int, int>? points, SkeletonInfo? bind, SkeletonInfo remadeInfo)
    {
        if (points == null || bind == null)
            return 0;
        var elementParents = bind.Parent;
        int made = 0;
        foreach (var id in new[] { 4, 5 })
        {
            if (!points.TryGetValue(id, out var e) || rig.Attach.Any(a => a.Element == e))
                continue;
            RigBone? via = null;
            for (int x = e, guard = 0; x >= 0 && x < elementParents.Length && guard < 256; x = elementParents[x] == x ? -1 : elementParents[x], guard++)
                if (rig.Bones.FirstOrDefault(b => b.Element == x) is { } m)
                {
                    via = m;
                    break;
                }
            if (via == null)
                continue;
            string side = via.Label.EndsWith(".L", StringComparison.Ordinal) ? "L" : via.Label.EndsWith(".R", StringComparison.Ordinal) ? "R" : SplitName(via.Bone).Side;
            string bone = via.Bone;
            for (int b = 0; b < remadeInfo.Count; b++)
                if (SplitName(remadeInfo.Names[b]) is var (sd, bs) && sd == side && side.Length > 0 && SocketName.IsMatch(bs))
                {
                    bone = remadeInfo.Names[b];
                    break;
                }
            rig.Attach.Add(new AttachMap { Element = e, Point = id, Bone = bone, Via = via.Element });
            made++;
        }
        return made;
    }

    /// <summary>
    /// Everything for a remade skeleton at once: the labels, the alignment (when
    /// <paramref name="align"/>), the mapping, the rest offsets, cloth, face and attachments.
    /// <paramref name="rig"/> is filled (rows set by hand kept); null when either skeleton is not a
    /// humanoid.
    /// </summary>
    public static Result? Build(CharacterRig rig, SkeletonInfo game, IReadOnlyDictionary<int, int>? points, Skeleton3D remadeSkel,
        Transform3D skelInRoot, IEnumerable<MeshInstance3D> meshes, IReadOnlyList<ClothVertex>? cloth, SkeletonInfo? bind, bool align = true)
    {
        var notes = new List<string>();
        var remade = SkeletonInfo.Of(remadeSkel, Transform3D.Identity);
        var gl = GameLabels(game, points);
        if (gl == null)
        {
            notes.Add("the classic skeleton is not a humanoid the mapping knows");
            return null;
        }
        var rl = Label(remade, GuessUp(remade));
        if (rl == null)
        {
            notes.Add("the remade skeleton is not a humanoid the mapping knows (name its bones: hips, spine, chest, neck, head, upperarm.L...)");
            return null;
        }
        if (align)
            rig.Align = AutoAlign(gl, game, rl, remade, skelInRoot);
        int mapped = AutoMap(rig, gl, game, rl, remade);
        AutoAttach(rig, points, bind, remade);
        var alignedC = ComputeRest(rig, game, remade, skelInRoot, bind);
        int chains = AutoCloth(rig, remade, alignedC, cloth, gl.Height, notes, game);
        notes.Add($"{cloth?.Count ?? 0} classic cloth vertices");
        int face = AutoFace(rig, gl, game, meshes);
        notes.Add($"game: {gl.How}; remade: {rl.How}; {mapped} bone(s) mapped, {rig.Cloth.Count} cloth chain(s) ({chains} new), "
            + $"{rig.Face.Count} face row(s) ({face} new), {rig.Attach.Count} attachment(s)");
        return new Result(rig, gl, rl, alignedC, notes);
    }
}
