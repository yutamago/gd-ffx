#if TOOLS
using System.Collections.Generic;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The game piece that uses a texture, for the Materials tab's preview (docs/remaster-materials.md,
/// "The preview", Object): every surface of a classic map that draws the texture (its classic
/// material's colour texture, or the lit material's when the map is drawn remastered), with its
/// own UVs and vertex colours, put in one mesh around the origin, about two units across. Its
/// classic materials come along for the left side of the compare.
/// </summary>
public static class PreviewObject
{
    /// <summary>At most this many surfaces (a texture drawn all over a map: the first ones).</summary>
    public const int MaxSurfaces = 48;

    /// <summary>The surfaces of <paramref name="map"/> drawing <paramref name="id"/> as a mesh, with their classic materials (null: none).</summary>
    public static (ArrayMesh Mesh, Material?[] Classic, int Surfaces)? Extract(Node3D map, string id)
    {
        id = id.ToLowerInvariant();
        var found = new List<(Godot.Collections.Array Arrays, Transform3D Place, Material? Classic)>();
        var look = MapLook.Find(map);
        void Walk(Node n, Transform3D parent)
        {
            var place = n is Node3D n3 ? parent * n3.Transform : parent;
            if (n.HasMeta(MapEditRoot.EditorNode))
                return;
            if (n is MeshInstance3D { Mesh: { } mesh } mi && found.Count < MaxSurfaces)
                for (int s = 0; s < mesh.GetSurfaceCount() && found.Count < MaxSurfaces; s++)
                {
                    var m = mi.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s);
                    Material? classic = null;
                    if (RemasterTextures.ColourId(m) == id)
                        classic = m;
                    else if (m != null && PartIds.GetString(m, RemasterLook.LitTextureMeta) == id)
                        classic = look?.ClassicOf(mi, s);
                    else
                        continue;
                    if (mesh is ArrayMesh am && am.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles)
                        continue;
                    found.Add((mesh.SurfaceGetArrays(s), place, classic));
                }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i), place);
        }
        // the map's own scale and place left out: only its inside matters
        Walk(map, map.Transform.AffineInverse());
        if (found.Count == 0)
            return null;
        // the bounds of the placed vertices, then one scale and offset for all
        Aabb? box = null;
        foreach (var (arrays, place, _) in found)
            foreach (var v in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
            {
                var p = place * v;
                box = box is { } b ? b.Expand(p) : new Aabb(p, Vector3.Zero);
            }
        var bb = box!.Value;
        float scale = 2f / Mathf.Max(0.001f, Mathf.Max(bb.Size.X, Mathf.Max(bb.Size.Y, bb.Size.Z)));
        var centre = bb.GetCenter();
        var mesh = new ArrayMesh();
        var classics = new List<Material?>();
        foreach (var (arrays, place, classic) in found)
        {
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            for (int i = 0; i < verts.Length; i++)
                verts[i] = (place * verts[i] - centre) * scale;
            var a = new Godot.Collections.Array();
            a.Resize((int)Mesh.ArrayType.Max);
            a[(int)Mesh.ArrayType.Vertex] = verts;
            if (arrays[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil)
            {
                var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                var nb = place.Basis.Inverse().Transposed();
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = (nb * normals[i]).Normalized();
                a[(int)Mesh.ArrayType.Normal] = normals;
            }
            foreach (var t in new[] { Mesh.ArrayType.Color, Mesh.ArrayType.TexUV, Mesh.ArrayType.TexUV2, Mesh.ArrayType.Index })
                a[(int)t] = arrays[(int)t];
            var st = new SurfaceTool();
            st.CreateFromArrays(a);
            if (a[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil)
                st.GenerateNormals();
            if (a[(int)Mesh.ArrayType.TexUV].VariantType != Variant.Type.Nil)
                st.GenerateTangents();
            st.Commit(mesh);
            classics.Add(classic);
        }
        return (mesh, classics.ToArray(), found.Count);
    }
}
#endif
