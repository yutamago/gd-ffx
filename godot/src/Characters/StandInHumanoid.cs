using System;
using System.Collections.Generic;
using Godot;

namespace GdFfx.Characters;

/// <summary>
/// An original stand-in humanoid made in code (docs/remake.md, "The stand-in humanoid"): a test
/// character for R6's checks and a template, no art of the game's. It is built as a user's model
/// would come from a glTF: Godot's conventions (y up, facing +z, metres, 1.8 tall, a T pose) and a
/// skeleton of its own whose names and proportions are not the game's (longer legs, a shorter
/// torso, three spine bones, a weapon socket, a jaw and eyes), skinned meshes with the skin, hair
/// and eye materials (<see cref="CharacterMaterials"/>), a head with the blend shapes
/// <c>mouth_open</c> and <c>blink</c>, and two cloth chains: a hood down the back
/// (<c>Hood_1..3</c>) and a ponytail (<c>Tail_1..3</c>).
/// </summary>
public static class StandInHumanoid
{
    /// <summary>A bone: name, parent (-1 root), head position (model space, the T pose).</summary>
    private readonly record struct BoneDef(string Name, int Parent, Vector3 At);

    private static List<BoneDef> Bones()
    {
        var b = new List<BoneDef>();
        int Add(string name, int parent, float x, float y, float z) { b.Add(new BoneDef(name, parent, new Vector3(x, y, z))); return b.Count - 1; }
        int pelvis = Add("Pelvis", -1, 0, 1.00f, 0);
        int s1 = Add("Spine_Lower", pelvis, 0, 1.10f, -0.01f);
        int s2 = Add("Spine_Upper", s1, 0, 1.22f, -0.01f);
        int chest = Add("Chest", s2, 0, 1.34f, 0);
        int neck = Add("Neck", chest, 0, 1.49f, 0);
        int head = Add("Head", neck, 0, 1.58f, 0.01f);
        Add("HeadTop", head, 0, 1.80f, 0.01f);
        Add("Jaw", head, 0, 1.57f, 0.04f);
        Add("Eye.L", head, 0.034f, 1.665f, 0.085f);
        Add("Eye.R", head, -0.034f, 1.665f, 0.085f);
        int t1 = Add("Tail_1", head, 0, 1.68f, -0.10f);
        int t2 = Add("Tail_2", t1, 0, 1.56f, -0.15f);
        int t3 = Add("Tail_3", t2, 0, 1.44f, -0.18f);
        Add("Tail_End", t3, 0, 1.34f, -0.19f);
        int h1 = Add("Hood_1", chest, 0, 1.47f, -0.11f);
        int h2 = Add("Hood_2", h1, 0, 1.38f, -0.15f);
        int h3 = Add("Hood_3", h2, 0, 1.29f, -0.16f);
        Add("Hood_End", h3, 0, 1.21f, -0.16f);
        foreach (var (side, sx) in new[] { ("L", 1f), ("R", -1f) })
        {
            int clav = Add($"Clavicle.{side}", chest, 0.035f * sx, 1.44f, 0.01f);
            int up = Add($"UpperArm.{side}", clav, 0.17f * sx, 1.44f, -0.01f);
            int low = Add($"LowerArm.{side}", up, 0.45f * sx, 1.44f, -0.02f);
            int hand = Add($"Hand.{side}", low, 0.71f * sx, 1.44f, 0);
            Add($"Fingers.{side}", hand, 0.81f * sx, 1.44f, 0.005f);
            Add($"Weapon.{side}", hand, 0.76f * sx, 1.425f, 0.03f);
            int thigh = Add($"Thigh.{side}", pelvis, 0.10f * sx, 0.96f, 0);
            int calf = Add($"Calf.{side}", thigh, 0.10f * sx, 0.52f, 0.01f);
            int foot = Add($"Foot.{side}", calf, 0.10f * sx, 0.085f, -0.01f);
            int toe = Add($"Toe.{side}", foot, 0.10f * sx, 0.02f, 0.12f);
            Add($"ToeEnd.{side}", toe, 0.10f * sx, 0.02f, 0.20f);
        }
        return b;
    }

    /// <summary>The stand-in's model: a <see cref="Node3D"/> holding its skeleton and skinned meshes (no rig: as a user's glTF comes).</summary>
    public static Node3D Build()
    {
        var defs = Bones();
        var root = new Node3D { Name = "StandInHumanoid" };
        var skel = new Skeleton3D { Name = "Skeleton3D" };
        root.AddChild(skel);
        for (int i = 0; i < defs.Count; i++)
        {
            var d = defs[i];
            skel.AddBone(d.Name);
            if (d.Parent >= 0)
                skel.SetBoneParent(i, d.Parent);
            var local = d.Parent >= 0 ? d.At - defs[d.Parent].At : d.At;
            skel.SetBoneRest(i, new Transform3D(Basis.Identity, local));
        }
        skel.ResetBonePoses();
        var skin = skel.CreateSkinFromRestTransforms();
        int B(string name) => skel.FindBone(name);
        Vector3 P(string name) => defs[B(name)].At;

        // the body: tubes along the bones, blended at the joints
        var body = new Parts();
        body.Tube(P("Pelvis") + new Vector3(0, -0.06f, 0), P("Spine_Lower"), 0.13f, 0.125f, B("Pelvis"), B("Spine_Lower"));
        body.Tube(P("Spine_Lower"), P("Spine_Upper"), 0.125f, 0.13f, B("Spine_Lower"), B("Spine_Upper"));
        body.Tube(P("Spine_Upper"), P("Chest"), 0.13f, 0.14f, B("Spine_Upper"), B("Chest"));
        body.Tube(P("Chest"), P("Neck") + new Vector3(0, -0.03f, 0), 0.14f, 0.07f, B("Chest"), B("Chest"));
        body.Tube(P("Neck") + new Vector3(0, -0.03f, 0), P("Head"), 0.05f, 0.05f, B("Neck"), B("Neck"));
        foreach (var s in new[] { "L", "R" })
        {
            body.Tube(P($"Clavicle.{s}"), P($"UpperArm.{s}"), 0.05f, 0.055f, B($"Clavicle.{s}"), B($"Clavicle.{s}"));
            body.Tube(P($"UpperArm.{s}"), P($"LowerArm.{s}"), 0.05f, 0.042f, B($"UpperArm.{s}"), B($"LowerArm.{s}"));
            body.Tube(P($"LowerArm.{s}"), P($"Hand.{s}"), 0.042f, 0.032f, B($"LowerArm.{s}"), B($"Hand.{s}"));
            body.Tube(P($"Hand.{s}"), P($"Fingers.{s}"), 0.034f, 0.022f, B($"Hand.{s}"), B($"Hand.{s}"));
            body.Tube(P($"Thigh.{s}") + new Vector3(0, 0.04f, 0), P($"Calf.{s}"), 0.075f, 0.055f, B($"Thigh.{s}"), B($"Calf.{s}"));
            body.Tube(P($"Calf.{s}"), P($"Foot.{s}"), 0.055f, 0.04f, B($"Calf.{s}"), B($"Foot.{s}"));
            body.Tube(P($"Foot.{s}") + new Vector3(0, -0.03f, -0.03f), P($"ToeEnd.{s}"), 0.045f, 0.03f, B($"Foot.{s}"), B($"Toe.{s}"));
        }
        AddMesh(skel, skin, "Body", body.Mesh(CharacterMaterials.Skin()));

        // the head with its blend shapes: the jaw's lower front opens, the eyelids' band closes
        var head = new Parts();
        int jaw = B("Jaw"), hb = B("Head");
        head.Sphere(P("Head") + new Vector3(0, 0.085f, 0.01f), new Vector3(0.095f, 0.115f, 0.105f), hb, 18, 12,
            v => v.Y < P("Head").Y + 0.04f && v.Z > P("Head").Z ? jaw : hb);
        var headMesh = head.MeshWithShapes(CharacterMaterials.Skin(), ["mouth_open", "blink"], (shape, v, n) =>
        {
            var c = P("Head") + new Vector3(0, 0.085f, 0.01f);
            if (shape == 0 && v.Y < c.Y - 0.03f && v.Z > c.Z + 0.02f)
                return v + new Vector3(0, -0.035f * Mathf.Clamp((c.Y - 0.03f - v.Y) / 0.08f + 0.3f, 0, 1), 0.01f);
            if (shape == 1 && Mathf.Abs(v.Y - (P("Eye.L").Y)) < 0.025f && v.Z > c.Z + 0.06f)
                return v + new Vector3(0, P("Eye.L").Y - v.Y, 0.006f);
            return v;
        });
        AddMesh(skel, skin, "Head", headMesh);

        // the eyes
        var eyes = new Parts();
        foreach (var s in new[] { "L", "R" })
            eyes.Sphere(P($"Eye.{s}"), Vector3.One * 0.016f, B($"Eye.{s}"), 14, 10, null);
        AddMesh(skel, skin, "Eyes", eyes.Mesh(CharacterMaterials.Eye()));

        // the hair: a cap and a ponytail down its chain (strands as alpha)
        var hair = new Parts();
        hair.Sphere(P("Head") + new Vector3(0, 0.105f, -0.005f), new Vector3(0.102f, 0.11f, 0.112f), hb, 18, 10, null, upperOnly: true);
        hair.Tube(P("Tail_1"), P("Tail_2"), 0.035f, 0.032f, B("Tail_1"), B("Tail_2"));
        hair.Tube(P("Tail_2"), P("Tail_3"), 0.032f, 0.025f, B("Tail_2"), B("Tail_3"));
        hair.Tube(P("Tail_3"), P("Tail_End"), 0.025f, 0.008f, B("Tail_3"), B("Tail_3"));
        AddMesh(skel, skin, "Hair", hair.Mesh(CharacterMaterials.Hair(), tangents: true));

        // the hood: a flat cloth down the back
        var hood = new Parts();
        hood.Tube(P("Hood_1") + new Vector3(0, 0.02f, 0), P("Hood_2"), 0.09f, 0.075f, B("Hood_1"), B("Hood_2"), flat: 0.35f);
        hood.Tube(P("Hood_2"), P("Hood_3"), 0.075f, 0.06f, B("Hood_2"), B("Hood_3"), flat: 0.35f);
        hood.Tube(P("Hood_3"), P("Hood_End"), 0.06f, 0.03f, B("Hood_3"), B("Hood_3"), flat: 0.35f);
        AddMesh(skel, skin, "Hood", hood.Mesh(CharacterMaterials.Cloth()));
        return root;
    }

    private static void AddMesh(Skeleton3D skel, Skin skin, string name, Mesh mesh)
    {
        var mi = new MeshInstance3D { Name = name, Mesh = mesh, Skin = skin };
        skel.AddChild(mi);
        mi.Skeleton = new NodePath("..");
    }

    /// <summary>Saves the stand-in as a scene (meshes inline) at <paramref name="path"/>; an error or null.</summary>
    public static string? Save(string path)
    {
        var root = Build();
        Own(root, root);
        var packed = new PackedScene();
        var err = packed.Pack(root);
        root.Free();
        if (err != Error.Ok)
            return $"pack: {err}";
        var dir = path.GetBaseDir();
        if (dir.Length > 0)
            DirAccess.MakeDirRecursiveAbsolute(dir);
        var e2 = ResourceSaver.Save(packed, path);
        return e2 == Error.Ok ? null : $"save {path}: {e2}";
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

    /// <summary>Skinned geometry being made: positions, normals, UVs, bones and weights (two a vertex), triangles.</summary>
    private sealed class Parts
    {
        private readonly List<Vector3> _v = new(), _n = new();
        private readonly List<Vector2> _uv = new();
        private readonly List<int> _bones = new();
        private readonly List<float> _weights = new();
        private readonly List<int> _idx = new();

        private void Vertex(Vector3 v, Vector3 n, Vector2 uv, int b0, int b1, float w1)
        {
            _v.Add(v);
            _n.Add(n.Normalized());
            _uv.Add(uv);
            _bones.AddRange([b0, b1, 0, 0]);
            _weights.AddRange([1 - w1, w1, 0, 0]);
        }

        /// <summary>A tube from <paramref name="a"/> to <paramref name="b"/>, its weight going from bone <paramref name="ba"/> to <paramref name="bb"/> over the last third; <paramref name="flat"/> squashes it front to back.</summary>
        public void Tube(Vector3 a, Vector3 b, float ra, float rb, int ba, int bb, float flat = 1f, int sides = 12, int rings = 6)
        {
            var axis = (b - a).Normalized();
            var side = Mathf.Abs(axis.Dot(Vector3.Back)) > 0.9f ? Vector3.Right : Vector3.Back;
            var u = axis.Cross(side).Normalized();
            var w = axis.Cross(u).Normalized();
            int start = _v.Count;
            for (int r = 0; r <= rings; r++)
            {
                float t = r / (float)rings;
                var c = a.Lerp(b, t);
                float rad = Mathf.Lerp(ra, rb, t);
                float wb = ba == bb ? 0 : Mathf.Clamp((t - 0.66f) / 0.34f, 0, 1) * 0.5f;
                for (int s = 0; s <= sides; s++)
                {
                    float ang = s / (float)sides * Mathf.Tau;
                    var dir = u * Mathf.Cos(ang) + w * Mathf.Sin(ang);
                    var off = u * Mathf.Cos(ang) * rad + w * Mathf.Sin(ang) * rad;
                    if (flat != 1f)
                        off -= Vector3.Back * off.Dot(Vector3.Back) * (1 - flat);
                    Vertex(c + off, dir, new Vector2(s / (float)sides, t), ba, bb, wb);
                }
            }
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < sides; s++)
                {
                    int i0 = start + r * (sides + 1) + s, i1 = i0 + 1, i2 = i0 + sides + 1, i3 = i2 + 1;
                    _idx.AddRange([i0, i2, i1, i1, i2, i3]);
                }
        }

        /// <summary>An ellipsoid around <paramref name="c"/>, each vertex on <paramref name="bone"/> or the bone <paramref name="pick"/> names.</summary>
        public void Sphere(Vector3 c, Vector3 r, int bone, int sides, int rings, Func<Vector3, int>? pick, bool upperOnly = false)
        {
            int start = _v.Count;
            int r0 = 0, r1 = rings;
            for (int i = r0; i <= r1; i++)
            {
                float phi = (upperOnly ? i / (float)rings * 0.55f : i / (float)rings) * Mathf.Pi;
                for (int s = 0; s <= sides; s++)
                {
                    float th = s / (float)sides * Mathf.Tau;
                    var n = new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th));
                    var v = c + n * r;
                    Vertex(v, n, new Vector2(s / (float)sides, i / (float)rings), pick?.Invoke(v) ?? bone, bone, 0);
                }
            }
            for (int i = 0; i < r1 - r0; i++)
                for (int s = 0; s < sides; s++)
                {
                    int i0 = start + i * (sides + 1) + s, i1 = i0 + 1, i2 = i0 + sides + 1, i3 = i2 + 1;
                    _idx.AddRange([i0, i2, i1, i1, i2, i3]);
                }
        }

        private Godot.Collections.Array Arrays()
        {
            var a = new Godot.Collections.Array();
            a.Resize((int)Godot.Mesh.ArrayType.Max);
            a[(int)Godot.Mesh.ArrayType.Vertex] = _v.ToArray();
            a[(int)Godot.Mesh.ArrayType.Normal] = _n.ToArray();
            a[(int)Godot.Mesh.ArrayType.TexUV] = _uv.ToArray();
            a[(int)Godot.Mesh.ArrayType.Bones] = _bones.ToArray();
            a[(int)Godot.Mesh.ArrayType.Weights] = _weights.ToArray();
            a[(int)Godot.Mesh.ArrayType.Index] = _idx.ToArray();
            return a;
        }

        public ArrayMesh Mesh(Material material, bool tangents = false)
        {
            var mesh = new ArrayMesh();
            if (tangents)
            {
                var st = new SurfaceTool();
                st.CreateFromArrays(Arrays());
                st.GenerateTangents();
                mesh = st.Commit();
            }
            else
                mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, Arrays());
            mesh.SurfaceSetMaterial(0, material);
            return mesh;
        }

        /// <summary>A mesh with blend shapes: shape <c>k</c> moves each vertex by <paramref name="shape"/>(k, position, normal).</summary>
        public ArrayMesh MeshWithShapes(Material material, string[] names, Func<int, Vector3, Vector3, Vector3> shape)
        {
            var mesh = new ArrayMesh();
            foreach (var n in names)
                mesh.AddBlendShape(n);
            var shapes = new Godot.Collections.Array<Godot.Collections.Array>();
            for (int k = 0; k < names.Length; k++)
            {
                var a = new Godot.Collections.Array();
                a.Resize((int)Godot.Mesh.ArrayType.Max);
                var v = new Vector3[_v.Count];
                for (int i = 0; i < v.Length; i++)
                    v[i] = shape(k, _v[i], _n[i]);
                a[(int)Godot.Mesh.ArrayType.Vertex] = v;
                a[(int)Godot.Mesh.ArrayType.Normal] = _n.ToArray();
                shapes.Add(a);
            }
            mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, Arrays(), shapes);
            mesh.SurfaceSetMaterial(0, material);
            return mesh;
        }
    }
}
