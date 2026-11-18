using Godot;

namespace GdFfx.Remake;

/// <summary>
/// The root of a remade effect (R7; docs/remake.md, "Effects"): optional, any <see cref="Node3D"/>
/// root works with the defaults. A remade effect is instanced where and while the game plays the
/// classic effect of its id (<c>res://remake/effect/&lt;id&gt;.tscn</c> or a registry entry), in
/// render space (the game's units, y up) at the effect's place; when the classic effect ends it
/// stops: its particles stop emitting and it is freed once they have died out
/// (<see cref="Linger"/>).
/// </summary>
[Tool, GlobalClass]
public partial class RemadeEffect : Node3D
{
    /// <summary>The classic effect's draws are not drawn while this plays (off: both, for comparing).</summary>
    [Export] public bool HideClassic { get; set; } = true;

    /// <summary>Follows the effect's place every frame (off: stays where it started).</summary>
    [Export] public bool Follow { get; set; } = true;

    /// <summary>Added to the effect's place (render units).</summary>
    [Export] public Vector3 Offset { get; set; }

    /// <summary>Seconds it stays after the classic effect ended, its particles no longer emitting (0: the longest particle lifetime under it).</summary>
    [Export] public double Linger { get; set; }

    /// <summary>A note (what it remakes, what next).</summary>
    [Export(PropertyHint.MultilineText)] public string Note { get; set; } = "";

    /// <summary>The options of a remade effect's root (the defaults for a plain root).</summary>
    public static (bool HideClassic, bool Follow, Vector3 Offset, double Linger) Options(Node n)
    {
        for (Node? x = n; x != null; x = x.GetChildCount() > 0 ? x.GetChild(0) : null)
        {
            if (x is RemadeEffect r)
                return (r.HideClassic, r.Follow, r.Offset, r.Linger);
            if (x != n && x.GetChildCount() != 1)
                break;
        }
        return (true, true, Vector3.Zero, 0);
    }

    /// <summary>Stops every particle system under <paramref name="n"/>; the seconds until the last one has died out.</summary>
    public static double StopEmitting(Node n)
    {
        double longest = 0;
        foreach (var c in n.FindChildren("*", "", true, false))
            switch (c)
            {
                case GpuParticles3D g:
                    g.Emitting = false;
                    longest = System.Math.Max(longest, g.Lifetime);
                    break;
                case CpuParticles3D p:
                    p.Emitting = false;
                    longest = System.Math.Max(longest, p.Lifetime);
                    break;
            }
        return longest;
    }
}
