using System.Collections.Generic;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// Classic effects by stable id (R7; docs/remaster-editor.md, "Effects"). The extension tells
/// who made each effect draw (<c>rust/ffx-gd/src/fxowner.rs</c>): a map's field program (entry
/// <c>n</c> of the map's effect table: the save sphere's glow, the sea's foam) or a spell
/// overlay. Their ids:
/// <list type="bullet">
/// <item><c>&lt;level&gt;-fp&lt;n&gt;</c>: field program entry <c>n</c> of the map (<c>bsil00-fp12</c>);</item>
/// <item><c>magic-&lt;id&gt;</c>: the spell overlay <c>magic_&lt;id&gt;.dll</c>, id in four hex digits (<c>magic-004c</c>, Fire).</item>
/// </list>
/// Both are stable across runs (the map's table, the spell's file). A remade effect is a scene
/// registered for the id (kind <c>effect</c>, docs/remake.md).
/// </summary>
public static class EffectOwners
{
    public const uint KindField = 1, KindMagic = 2;

    /// <summary>One frame's owners: one word per draw, and the field programs' places (render space) and stuck flags.</summary>
    public sealed class Frame
    {
        public uint[] Draws = [];
        public int Count;
        public readonly Dictionary<uint, (Vector3 Place, bool Stuck)> Places = new();
    }

    /// <summary>Decodes the extension's array into <paramref name="into"/>; false when it is empty.</summary>
    public static bool Decode(int[] v, Frame into)
    {
        into.Places.Clear();
        into.Count = 0;
        if (v.Length == 0)
            return false;
        int n = v[0];
        if (n < 0 || 1 + n > v.Length)
            return false;
        if (into.Draws.Length < n)
            into.Draws = new uint[System.Math.Max(n, into.Draws.Length * 2)];
        for (int i = 0; i < n; i++)
            into.Draws[i] = (uint)v[1 + i];
        into.Count = n;
        for (int at = 1 + n; at + 4 < v.Length; at += 5)
        {
            static float F(int bits) => System.BitConverter.Int32BitsToSingle(bits);
            into.Places[(uint)v[at]] = (new Vector3(F(v[at + 1]), F(v[at + 2]), F(v[at + 3])), v[at + 4] != 0);
        }
        return true;
    }

    private static readonly Dictionary<(uint, string), string> Ids = new();

    /// <summary>The id of owner word <paramref name="owner"/> on map <paramref name="level"/> (any form: <c>/map/bsil/bsil00/</c>); null for none.</summary>
    public static string? IdOf(uint owner, string level)
    {
        if (owner == 0)
            return null;
        if (Ids.TryGetValue((owner, level), out var id))
            return id;
        uint kind = owner >> 24, n = owner & 0xffffff;
        id = kind switch
        {
            KindField => $"{PartIds.LevelKey(level)}-fp{n}",
            KindMagic => $"magic-{n:x4}",
            _ => null,
        };
        if (id != null)
            Ids[(owner, level)] = id;
        return id;
    }

    /// <summary>What an id names, for lists: "Besaid beach's field program 12", "spell 004c".</summary>
    public static string Describe(string id)
    {
        if (id.StartsWith("magic-"))
            return $"spell overlay magic_{id[6..]}";
        int fp = id.LastIndexOf("-fp", System.StringComparison.Ordinal);
        if (fp > 0)
            return $"{id[..fp]}: field program {id[(fp + 3)..]}";
        if (id.Length == 8)
            return "battle effect record (MsGetEffID)";
        return id;
    }
}
