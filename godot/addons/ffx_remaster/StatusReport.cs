#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// R8's HTML report (tools/remaster-report.sh; docs/remaster-editor.md, "The status dashboard"): the
/// dashboard's rows as one page for sharing, <c>index.html</c> with the summary, a table of every map
/// (status, what the project changes, the renders' thumbnails linking to the full pictures, the
/// colour check, the budgets; filtered and sorted in the page), and the pictures copied beside it
/// (<c>pictures/&lt;map&gt;/</c>). Written outside the project and outside git: the pictures are the
/// player's game. Relative links only, no script or font from elsewhere.
/// </summary>
public static class StatusReport
{
    public const string DefaultDir = "user://remaster/report";

    /// <summary>Writes the report into <paramref name="dir"/> (made; an old report there replaced). The index's path, or an error.</summary>
    public static (string Path, string? Error) Write(RemasterPlugin plugin, string dir)
    {
        if (plugin.Session.Catalogue == null)
            return ("", "the archive is not open");
        var abs = GameSettings.Globalize(dir);
        var rows = RemasterStatus.All(plugin);
        var sum = RemasterStatus.Summarise(rows);
        try
        {
            System.IO.Directory.CreateDirectory(abs);
            var pics = System.IO.Path.Combine(abs, "pictures");
            if (System.IO.Directory.Exists(pics))
                System.IO.Directory.Delete(pics, true);
            var html = Page(rows, sum, rel => Copy(rel, abs));
            var index = System.IO.Path.Combine(abs, "index.html");
            System.IO.File.WriteAllText(index, html);
            GD.Print($"ffx remaster: report: {index}: {rows.Count} maps, {rows.Count(r => r.Render is { State: "done" })} with renders");
            return (index, null);
        }
        catch (System.Exception e)
        {
            return ("", $"cannot write the report: {e.Message}");
        }
    }

    /// <summary>A renders picture copied into the report (its path relative to the index), or null when missing.</summary>
    private static string? Copy(string rel, string dir)
    {
        if (rel.Length == 0)
            return null;
        var src = RenderStore.Abs(rel);
        if (!System.IO.File.Exists(src))
            return null;
        var to = "pictures/" + rel;
        var dest = System.IO.Path.Combine(dir, to);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
        System.IO.File.Copy(src, dest, true);
        return to;
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);

    private static string Page(List<MapStatusRow> rows, StatusSummary sum, System.Func<string, string?> copy)
    {
        var b = new StringBuilder();
        b.Append("""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Remaster status</title>
<style>
:root{--bg:#fafafa;--fg:#1d1d1f;--mute:#6b6b70;--line:#dcdce0;--warn:#b54708;--ok:#1a7f37;--ip:#9a6700;--rm:#0b5cad;--card:#fff}
@media (prefers-color-scheme: dark){:root{--bg:#16161a;--fg:#e8e8ea;--mute:#9a9aa2;--line:#33333a;--warn:#f0883e;--ok:#56d364;--ip:#e3b341;--rm:#58a6ff;--card:#1e1e23}}
body{margin:0;padding:16px;background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,sans-serif}
h1{font-size:20px;margin:0 0 8px}.sum{color:var(--mute);max-width:1100px}
.counts{display:flex;flex-wrap:wrap;gap:8px;margin:12px 0}.counts div{background:var(--card);border:1px solid var(--line);border-radius:6px;padding:6px 10px}
.counts b{display:block;font-size:18px}
.bar{display:flex;flex-wrap:wrap;gap:8px;align-items:center;margin:12px 0}input,select{font:inherit;padding:4px 6px;background:var(--card);color:var(--fg);border:1px solid var(--line);border-radius:4px}
table{border-collapse:collapse;width:100%}th,td{border-bottom:1px solid var(--line);padding:6px;text-align:left;vertical-align:top}
th{cursor:pointer;position:sticky;top:0;background:var(--bg)}td img{width:160px;height:auto;display:block;border-radius:3px}
.pics{display:flex;gap:4px}.mute{color:var(--mute)}.off{color:var(--warn)}.s-Approved{color:var(--ok)}.s-Remastered{color:var(--rm)}.s-InProgress{color:var(--ip)}.s-Original{color:var(--mute)}
.wrap{overflow-x:auto}
</style></head><body>
<h1>Remaster status</h1>
""");
        b.Append($"<p class=\"sum\">{H(sum.ToString())}</p>\n<div class=\"counts\">");
        foreach (var (s, n) in sum.ByStatus.OrderBy(k => k.Key))
            b.Append($"<div><b>{n}</b>{H(s.ToString())}</div>");
        b.Append($"<div><b>{sum.TexturesWithMaterials}</b>textures with materials</div><div><b>{sum.Rendered}</b>maps rendered</div>"
            + $"<div><b>{sum.ColourOff}</b>colour check off</div><div><b>{sum.BudgetsOver}</b>over budget (of {sum.BudgetsMeasured} measured)</div></div>\n");
        b.Append("""
<div class="bar"><input id="q" placeholder="search" oninput="f()"><select id="st" onchange="f()"><option value="">any status</option><option>Original</option><option>InProgress</option><option>Remastered</option><option>Approved</option></select>
<label><input type="checkbox" id="ch" onchange="f()"> changed</label><label><input type="checkbox" id="rd" onchange="f()"> rendered</label><label><input type="checkbox" id="of" onchange="f()"> off</label></div>
<div class="wrap"><table id="t"><thead><tr><th onclick="s(0)">Map</th><th onclick="s(1)">Area</th><th onclick="s(2)">Status</th><th onclick="s(3)">The project changes</th><th>Before / after</th><th onclick="s(5)">Colour / difference</th><th onclick="s(6)">Budgets</th></tr></thead><tbody>

""");
        foreach (var r in rows)
        {
            var rec = r.Render;
            bool off = rec is { State: "done", ColourOk: false } || r.BudgetVerdict == "over";
            b.Append($"<tr data-st=\"{r.Status}\" data-ch=\"{(r.Changed ? 1 : 0)}\" data-rd=\"{(rec is { State: "done" } ? 1 : 0)}\" data-of=\"{(off ? 1 : 0)}\">");
            b.Append($"<td>{H(r.Entry.Title("us"))}<br><span class=\"mute\">{H(r.Entry.Key)}{(r.Is2D && r.Entry.Kind == "field" ? " · 2D" : "")}</span></td>");
            b.Append($"<td>{H(r.Entry.Area)}</td><td class=\"s-{r.Status}\">{r.Status}</td>");
            b.Append($"<td>{H(r.ChangesText.Length > 0 ? r.ChangesText : "")}{(r.Textures >= 0 ? $"<br><span class=\"mute\">{r.Textures} textures</span>" : "")}</td><td>");
            if (rec is { State: "done" })
            {
                b.Append("<div class=\"pics\">");
                foreach (var (thumb, full, what) in new[] { (rec.ThumbBefore, rec.Before.FirstOrDefault() ?? "", "classic"), (rec.ThumbAfter, rec.After.FirstOrDefault() ?? "", rec.Kind == "3d" ? "remastered" : "rebuilt") })
                {
                    var t = copy(thumb);
                    var f = copy(full);
                    if (t != null)
                        b.Append(f != null ? $"<a href=\"{H(f)}\"><img src=\"{H(t)}\" alt=\"{what}\" title=\"{what}\"></a>" : $"<img src=\"{H(t)}\" alt=\"{what}\">");
                }
                b.Append("</div>");
                var more = rec.Before.Skip(1).Concat(rec.After.Skip(1)).Select(p => (p, copy(p))).Where(x => x.Item2 != null).ToList();
                if (more.Count > 0)
                    b.Append("<span class=\"mute\">more: " + string.Join(" ", more.Select(x => $"<a href=\"{H(x.Item2!)}\">{H(System.IO.Path.GetFileNameWithoutExtension(x.p))}</a>")) + "</span>");
                b.Append($"<br><span class=\"mute\">{H(rec.Views)}, {rec.Ms / 1000:0.0} s, {H(rec.When)}</span>");
            }
            else if (rec != null)
                b.Append($"<span class=\"mute\">{H(rec.State)}: {H(rec.Reason)}</span>");
            b.Append("</td>");
            b.Append($"<td class=\"{(rec is { ColourOk: false } ? "off" : "")}\">{H(r.RenderText)}</td>");
            b.Append($"<td class=\"{(r.BudgetVerdict == "over" ? "off" : "")}\">{string.Join("<br>", r.Budgets.Select(x => H(x.Short)))}</td></tr>\n");
        }
        b.Append("""
</tbody></table></div>
<p class="mute">Pictures of the player's own game: not for git, not for distribution with gd-ffx. Budgets on another GPU than the Steam Deck are an estimate (docs/running.md, "Budgets").</p>
<script>
function f(){const q=document.getElementById('q').value.toLowerCase(),st=document.getElementById('st').value,ch=document.getElementById('ch').checked,rd=document.getElementById('rd').checked,of=document.getElementById('of').checked;
for(const r of document.querySelectorAll('#t tbody tr')){const ok=(!q||r.textContent.toLowerCase().includes(q))&&(!st||r.dataset.st===st)&&(!ch||r.dataset.ch==='1')&&(!rd||r.dataset.rd==='1')&&(!of||r.dataset.of==='1');r.style.display=ok?'':'none';}}
let dir=1;function s(c){const tb=document.querySelector('#t tbody');const rows=[...tb.rows];dir=-dir;rows.sort((a,b)=>a.cells[c].textContent.localeCompare(b.cells[c].textContent,undefined,{numeric:true})*dir);rows.forEach(r=>tb.appendChild(r));}
</script></body></html>
""");
        return b.ToString();
    }
}
#endif
