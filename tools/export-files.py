#!/usr/bin/env python3
r"""export-files.py: the licence notices and the content checks of an export
(tools/export.sh; docs/exporting.md).

  tools/export-files.py notices OUT TARGET [--allow-gpl] [-- CARGO_ARGS...]
  tools/export-files.py check DIR PLATFORM [--dev]
  tools/export-files.py pkgdir NAME [-- CARGO_ARGS...]

`notices` writes OUT (THIRD-PARTY.txt): every Rust package compiled into the
extension (ffx-gd's normal dependency tree for TARGET, `cargo tree -e
normal`, with CARGO_ARGS: tools/export.sh passes the build's features and
ffx-rust [patch] entries), its licence and repository, the GPL-family ones
called out, then the licence files the packages ship (LICENSE*, COPYING*,
NOTICE*, ...), grouped by text. Exit 1 when the tree has a GPL-family
package (Unicorn, behind dev-unicorn) and --allow-gpl is not given.

`check` fails (exit 1) when the export in DIR (PLATFORM linux or windows)
misses one of its files (the program, the .NET data folder, the extension,
the licences), holds game files by name or imported classic content, or
holds a path or name of this machine (the home folder; the Linux user name
and the Windows one, %USERNAME% from cmd.exe on WSL, as whole words; any
/home/<name>/, /mnt/c/Users/<name>/ or \Users\<name>\ path: in ASCII or
UTF-16, case-insensitive, as binaries keep them), or when the extension
links Unicorn (GPL-2.0) without --dev, the development export.

`pkgdir` prints a package's source folder (Unicorn's COPYING for --dev).
"""
import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
RUST = REPO / "rust"
GPL = re.compile(r"\b(A|L)?GPL")
LICENCE_FILE = re.compile(r"(?i)^(licen[cs]e|copying|notice|copyright|unlicense|third[-_]party|authors|patents)")
# the game's own files, whatever the folder: never in an export
GAME = re.compile(r"(?i)(\.vbf$|^ffx(-2)?\.exe$|^magic_\d+\.dll$|^fmod|^iggy|^steam_api(64)?\.dll$|"
                  r"\.(ftcx|dds|fsb|fev|swf|sav|phyre|bin|dat|webm|pck)$|^gamesetting\.ini$)")
# classic content imported from the player's copy (user://classic; a stale
# res://classic/ is excluded by export_presets.cfg)
CLASSIC = re.compile(rb"classic/[\x21-\x7e]{1,200}\.(glb|scn|res|png|ctex)")
# strings Unicorn's C code leaves in a library that links it
UNICORN_MARKS = (b"UC_IGNORE_REG_BREAK", b"unicorn-engine")
EXPECTED = {
    "linux": ["gd-ffx.x86_64", "libffx_gd.so", "data_gd-ffx_linuxbsd_x86_64/gd-ffx.dll"],
    "windows": ["gd-ffx.exe", "gd-ffx.console.exe", "ffx_gd.dll", "data_gd-ffx_windows_x86_64/gd-ffx.dll"],
}
LIBRARY = {"linux": "libffx_gd.so", "windows": "ffx_gd.dll"}
LICENCES = ["LICENSE-MIT", "LICENSE-APACHE", "THIRD-PARTY.txt", "godot-LICENSE.txt",
            "godot-COPYRIGHT.txt", "dotnet-LICENSE.txt", "dotnet-THIRD-PARTY-NOTICES.txt"]


def _split(expr, op):
    out, depth, start, i = [], 0, 0, 0
    while i < len(expr):
        c = expr[i]
        depth += (c == "(") - (c == ")")
        if depth == 0 and expr.startswith(op, i):
            out.append(expr[start:i])
            i += len(op)
            start = i
            continue
        i += 1
    out.append(expr[start:])
    return [o.strip() for o in out]


def _balanced(s):
    depth = 0
    for c in s:
        depth += (c == "(") - (c == ")")
        if depth < 0:
            return False
    return depth == 0


def gpl_bound(expr):
    """Whether a licence expression leaves no choice but a GPL-family
    licence (AND binds tighter than OR; `MIT/Apache-2.0` is an OR)."""
    expr = expr.strip()
    while expr.startswith("(") and expr.endswith(")") and _balanced(expr[1:-1]):
        expr = expr[1:-1].strip()
    alts = _split(expr, " OR ")
    if len(alts) == 1 and "/" in expr and "(" not in expr:
        alts = [a.strip() for a in expr.split("/")]
    if len(alts) > 1:
        return all(gpl_bound(a) for a in alts)
    parts = _split(expr, " AND ")
    if len(parts) > 1:
        return any(gpl_bound(p) for p in parts)
    return bool(GPL.search(expr))


def cargo(args):
    r = subprocess.run(["cargo", *args], cwd=RUST, capture_output=True, text=True)
    if r.returncode != 0:
        sys.stderr.write(r.stderr)
        return None
    return r.stdout


def metadata(cargo_args):
    out = cargo(["metadata", "-q", "--format-version", "1", *cargo_args])
    return json.loads(out) if out is not None else None


def licence_files(pkg, stop):
    """The licence files a package ships: in its folder, else in the
    nearest folder above it that has some (a crate inside a workspace,
    ffx-rust's from its checkout's root); never above `stop`."""
    d = pathlib.Path(pkg["manifest_path"]).parent
    for _ in range(4):
        if d.is_dir():
            files = sorted(f for f in d.iterdir() if f.is_file() and LICENCE_FILE.match(f.name))
            if files:
                return files
        if d.parent == d or d.parent in stop:
            break
        d = d.parent
    return []


def notices(out, target, allow_gpl, cargo_args):
    tree = cargo(["tree", "-p", "ffx-gd", "-e", "normal", "--target", target, "--prefix", "none",
                  "-f", "{p}\t{l}\t{r}", *cargo_args])
    meta = metadata(cargo_args)
    if tree is None or meta is None:
        print("notices: cargo tree/metadata failed", file=sys.stderr)
        return 2
    members = set(meta["workspace_members"])
    by_key = {(p["name"], p["version"]): p for p in meta["packages"]}
    home = pathlib.Path.home()
    stop = {home, home / ".cargo", pathlib.Path("/")}
    seen = {}
    for line in tree.splitlines():
        cols = line.split("\t")
        m = re.match(r"(\S+) v(\S+)", cols[0])
        if m and (m.group(1), m.group(2)) not in seen:
            seen[(m.group(1), m.group(2))] = (cols[1] if len(cols) > 1 else "", cols[2].removesuffix(" (*)") if len(cols) > 2 else "")
    rows, texts, gpl = [], {}, []
    for key in sorted(seen):
        lic, repo = seen[key]
        p = by_key.get(key)
        lic = lic or (p and p.get("license")) or ""
        ours = p is not None and p["id"] in members
        if lic and gpl_bound(lic):
            gpl.append((*key, lic))
        src = "this project" if ours else repo.strip() or (p.get("repository") or "" if p else "")
        rows.append((key[0], key[1], lic or "(none given)", src))
        for f in licence_files(p, stop) if p else []:
            body = f.read_text(errors="replace").strip()
            h = hashlib.sha256(body.encode()).hexdigest()
            texts.setdefault(h, [body, set()])[1].add(f"{key[0]} {key[1]} ({f.name})")
    lines = [
        "gd-ffx: third-party notices for the extension (ffx_gd: ffx-rust in Godot)",
        "",
        "Every Rust package compiled into the extension library, from ffx-gd's",
        f"dependency tree for {target} (cargo tree -e normal; tools/export-files.py).",
        "The packages marked \"this project\" are gd-ffx's own, under MIT OR Apache-2.0",
        "(LICENSE-MIT, LICENSE-APACHE). ffx-rust's crates are under the same licences,",
        "with the notices of the code they carry below. godot-rust (the `godot`",
        "crates) is under the Mozilla Public License 2.0: its source is at",
        "https://github.com/godot-rust/gdext and on crates.io, version as listed.",
        "Where a package ships no licence file of its own, the standard text of",
        "its licence applies (MIT and Apache-2.0: LICENSE-MIT, LICENSE-APACHE).",
        "The Godot engine: godot-LICENSE.txt, godot-COPYRIGHT.txt. The .NET runtime",
        "in the data_ folder: dotnet-LICENSE.txt, dotnet-THIRD-PARTY-NOTICES.txt.",
        "",
    ]
    if gpl:
        lines += ["GPL-family packages (a development export only, never to be distributed):"]
        lines += [f"  {n} {v}: {l}" for n, v, l in gpl] + [""]
    w = max(len(f"{n} {v}") for n, v, _, _ in rows)
    lines += [f"{len(rows)} packages", ""]
    lines += [f"{n + ' ' + v:<{w}}  {l}  {s}".rstrip() for n, v, l, s in rows]
    lines += ["", "", "Licence texts, as the packages ship them", "=" * 40, ""]
    for _, (body, who) in sorted(texts.items(), key=lambda t: sorted(t[1][1])[0]):
        lines += ["-" * 72, "Used by: " + ", ".join(sorted(who)), "-" * 72, "", body, "", ""]
    pathlib.Path(out).write_text("\n".join(lines) + "\n")
    print(f"notices: {len(rows)} packages, {len(texts)} distinct licence texts, "
          f"{len(gpl)} GPL-family -> {out}")
    if gpl and not allow_gpl:
        for n, v, l in gpl:
            print(f"  GPL in the extension: {n} {v}: {l}", file=sys.stderr)
        return 1
    return 0


# user folders of any name, as a build machine leaves them (at the start of a
# path: not OpenXR's /input/home/click). Generic folder names are no one's:
# Public, Default, and runner, the GitHub CI builder's home that Godot's
# official export templates carry (/home/runner/.cargo/...).
USER_DIRS = (b"/home/", b"/mnt/c/users/", b"\\users\\")
NOT_A_USER = {b"public", b"default", b"all users", b"default user", b"runner"}


def _wide(text):
    """A regex for ASCII bytes stored as UTF-16LE."""
    return b"".join(re.escape(bytes([c])) + b"\\x00" for c in text)


def windows_user():
    """The Windows user name on WSL (cmd.exe's %USERNAME%), else ''."""
    if not shutil.which("cmd.exe"):
        return ""
    try:
        r = subprocess.run(["cmd.exe", "/c", "echo %USERNAME%"],
                           cwd="/mnt/c" if os.path.isdir("/mnt/c") else None,
                           capture_output=True, text=True, timeout=20)
    except (OSError, subprocess.SubprocessError):
        return ""
    name = r.stdout.replace("\r", "").strip()
    return "" if not name or "%" in name else name


def private_patterns():
    """What must not appear in an export, as (what, regex) pairs, each in
    ASCII and UTF-16LE, case-insensitive: the home folder, the user names as
    whole words (any length from 2), user folders of any name."""
    names = {os.environ.get("USER") or os.environ.get("LOGNAME") or "", windows_user()}
    home = str(pathlib.Path.home()).encode()
    pats = [("the home folder", re.escape(home), _wide(home))]
    for n in sorted(x for x in names if len(x) >= 2):
        b = n.encode()
        pats.append((f"the user name {n!r}",
                     rb"(?<![a-z0-9])" + re.escape(b) + rb"(?![a-z0-9])",
                     rb"(?<![a-z0-9]\x00)" + _wide(b) + rb"(?![a-z0-9]\x00)"))
    for d in USER_DIRS:
        pats.append((f"a user folder ({d.decode()}<name>)",
                     rb"(?<![a-z0-9_.])" + re.escape(d) + rb"([a-z0-9._ -]{1,40})",
                     rb"(?<![a-z0-9_.]\x00)" + _wide(d) + rb"((?:[a-z0-9._ -]\x00){1,40})"))
    return [(what, re.compile(p, re.I)) for what, a, w in pats for p in (a, w)]


def private_hit(pattern, data):
    """The first match of `pattern` in `data` that names someone (generic
    user folders such as Public pass), else None."""
    for m in pattern.finditer(data):
        if m.groups():
            name = m.group(1).replace(b"\x00", b"").lower().rstrip(b" ")
            if name in NOT_A_USER or not name.strip(b". "):
                continue
        return m
    return None


def check(root, platform, dev):
    root = pathlib.Path(root)
    problems = []
    for rel in EXPECTED[platform] + [f"licences/{n}" for n in LICENCES]:
        if not (root / rel).is_file():
            problems.append(f"missing: {rel}")
    patterns = private_patterns()
    files = sorted(p for p in root.rglob("*") if p.is_file())
    for f in files:
        rel = f.relative_to(root).as_posix()
        if GAME.search(f.name):
            problems.append(f"game file by name: {rel}")
        data = f.read_bytes()
        for what, pat in patterns:
            m = private_hit(pat, data)
            if m:
                at = m.group().replace(b"\x00", b"")[:60].decode(errors="replace")
                problems.append(f"{what} in {rel} at {m.start():#x}: {at!r}")
                break
        if f.suffix not in (".dll", ".so") and f.name.startswith("gd-ffx") and CLASSIC.search(data):
            problems.append(f"imported classic content in {rel}: {CLASSIC.search(data).group().decode()}")
    lib = root / LIBRARY[platform]
    if lib.is_file():
        data = lib.read_bytes()
        unicorn = any(m in data for m in UNICORN_MARKS)
        if unicorn and not dev:
            problems.append(f"{lib.name} links Unicorn (GPL-2.0): a development build, not for others")
        if dev and not unicorn:
            print(f"check: note: --dev, but {lib.name} shows no Unicorn")
    for p in problems:
        print(f"check: {p}", file=sys.stderr)
    size = sum(f.stat().st_size for f in files)
    print(f"check: {root}: {len(files)} files, {size / 1e6:.0f} MB, "
          f"{'FAIL' if problems else 'ok'}{' (development export: not for distribution)' if dev else ''}")
    return 1 if problems else 0


def pkgdir(name, cargo_args):
    meta = metadata(cargo_args)
    if meta is None:
        return 2
    for p in meta["packages"]:
        if p["name"] == name:
            print(pathlib.Path(p["manifest_path"]).parent)
            return 0
    print(f"pkgdir: no package {name}", file=sys.stderr)
    return 1


def main():
    argv = sys.argv[1:]
    cargo_args = []
    if "--" in argv:
        i = argv.index("--")
        argv, cargo_args = argv[:i], argv[i + 1:]
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    n = sub.add_parser("notices")
    n.add_argument("out")
    n.add_argument("target")
    n.add_argument("--allow-gpl", action="store_true")
    c = sub.add_parser("check")
    c.add_argument("dir")
    c.add_argument("platform", choices=sorted(EXPECTED))
    c.add_argument("--dev", action="store_true")
    k = sub.add_parser("pkgdir")
    k.add_argument("name")
    a = ap.parse_args(argv)
    if a.cmd == "notices":
        sys.exit(notices(a.out, a.target, a.allow_gpl, cargo_args))
    if a.cmd == "check":
        sys.exit(check(a.dir, a.platform, a.dev))
    sys.exit(pkgdir(a.name, cargo_args))


if __name__ == "__main__":
    main()
