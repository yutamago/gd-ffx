#!/usr/bin/env python3
r"""check-privacy.py: no path, name or e-mail address of a machine or a person
in the files git tracks (docs/building.md, "Privacy"; R8).

  tools/check-privacy.py [--root DIR] [--files FILE...] [-v]

Scans every tracked file (`git ls-files` in the repository, or DIR: the
checkout given; --files: those files only), text and binary alike, its
contents and its path, for:

- the home folder of this machine and the user names of this machine, read
  from the environment at run time ($USER or $LOGNAME, and on WSL the
  Windows one: cmd.exe's %USERNAME%), as whole words, case-insensitive;
- user folders of any name: /home/<name>/, /mnt/c/Users/<name>/,
  C:\Users\<name>\ and C:/Users/<name>/ (generic ones such as Public, Default
  and runner, the CI builder's, pass), in ASCII and UTF-16LE as binaries keep
  them (a Python byte-code file once held one: R8);
- e-mail addresses other than the commit identity (CLAUDE.md) and the
  allowed ones (example domains, GitHub's SSH remote).

The patterns themselves are tools/export-files.py's (the export's check),
so the two agree; nothing here names anyone. tools/privacy-allow.txt lists
what is fine where: `GLOB TEXT` (a match whose text is TEXT, in a file whose
path matches GLOB), for the docs that describe the rule.

Exit 0 when clean, 1 with a line per finding (file, offset, what), 2 on a
usage error.
"""
import argparse
import fnmatch
import importlib.util
import pathlib
import re
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parent
# the commit identity (CLAUDE.md) and addresses that are no one's
IDENTITY = b"12561505+yutamago@users.noreply.github.com"
NO_ONE = re.compile(rb"(?i)@(example\.(com|org|net)|localhost)$|^git@github\.com$|^noreply@")
EMAIL = re.compile(rb"(?i)(?<![a-z0-9._%+-])[a-z0-9._%+-]{1,64}@[a-z0-9-]{1,63}(?:\.[a-z0-9-]{1,63})*\.[a-z]{2,24}(?![a-z0-9-])")


BINARY_EMAIL = re.compile(rb"[a-z0-9][a-z0-9._%+-]{0,63}@[a-z][a-z0-9-]{0,62}(?:\.[a-z][a-z0-9-]{0,62})*"
                          rb"\.(com|org|net|io|dev|de|eu|uk|fr|jp|ch|at|nl|it|es|us|info|me|co|edu|gov)")


def is_binary(data):
    """A NUL in the first 8 KiB, or not UTF-8: a binary file."""
    if b"\0" in data[:8192]:
        return True
    try:
        data.decode("utf-8")
    except UnicodeDecodeError:
        return True
    return False


def load_export_patterns():
    """tools/export-files.py's private_patterns and private_hit (its name has a hyphen: loaded by path)."""
    spec = importlib.util.spec_from_file_location("export_files", HERE / "export-files.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod.private_patterns, mod.private_hit, mod._wide, mod.NOT_A_USER


def tracked(root):
    r = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], capture_output=True, check=True)
    return [p for p in r.stdout.decode("utf-8", "surrogateescape").split("\0") if p]


def read_allow(root):
    allow = []
    f = root / "tools" / "privacy-allow.txt"
    if not f.is_file():
        f = HERE / "privacy-allow.txt"
    if f.is_file():
        for line in f.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            glob, _, text = line.partition(" ")
            allow.append((glob, text.strip().encode()))
    return allow


def allowed(allow, rel, text):
    return any(fnmatch.fnmatch(rel, g) and text.lower() == t.lower() for g, t in allow)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=str(REPO), help="the checkout to scan (default: this one)")
    ap.add_argument("--files", nargs="*", help="these files only (relative to the root)")
    ap.add_argument("-v", action="store_true", help="print the patterns' kinds and the files' count")
    a = ap.parse_args()
    root = pathlib.Path(a.root).resolve()
    private_patterns, private_hit, wide, _ = load_export_patterns()
    patterns = private_patterns()
    # C:/Users/<name>/ with forward slashes too (export-files.py looks for the back-slash form)
    d = b"c:/users/"
    for p in (rb"(?<![a-z0-9_.])" + re.escape(d) + rb"([a-z0-9._ -]{1,40})",
              rb"(?<![a-z0-9_.]\x00)" + wide(d) + rb"((?:[a-z0-9._ -]\x00){1,40})"):
        patterns.append(("a user folder (C:/Users/<name>)", re.compile(p, re.I)))
    allow = read_allow(root)
    files = a.files if a.files else tracked(root)
    if a.v:
        kinds = sorted({w if "user name" not in w else "a user name of this machine" for w, _ in patterns})
        print(f"check-privacy: {len(files)} files; looking for: {', '.join(kinds)}; e-mail addresses")
    problems = []
    for rel in files:
        path = root / rel
        # the path itself (a file named after someone)
        name = rel.encode("utf-8", "surrogateescape")
        for what, pat in patterns:
            m = private_hit(pat, name)
            if m and not allowed(allow, rel, m.group().replace(b"\x00", b"")):
                problems.append(f"{rel}: {what} in the file's path")
                break
        if not path.is_file() or path.is_symlink():
            continue
        data = path.read_bytes()
        for what, pat in patterns:
            for m in pat.finditer(data):
                if m.groups():
                    if private_hit(pat, m.group()) is None:
                        continue
                text = m.group().replace(b"\x00", b"")
                if allowed(allow, rel, text):
                    continue
                line = data.count(b"\n", 0, m.start()) + 1
                problems.append(f"{rel}:{line} (offset {m.start():#x}): {what}: {text[:60].decode(errors='replace')!r}")
                break
        binary = is_binary(data)
        for m in EMAIL.finditer(data):
            addr = m.group()
            if addr.lower() == IDENTITY or NO_ONE.search(addr) or allowed(allow, rel, addr):
                continue
            # in a binary (an image's pixels) only what reads as an address: lower case, a common top-level domain
            if binary and not BINARY_EMAIL.fullmatch(addr):
                continue
            line = data.count(b"\n", 0, m.start()) + 1
            problems.append(f"{rel}:{line}: an e-mail address other than the commit identity: {addr.decode(errors='replace')!r}")
    for p in problems:
        print(f"check-privacy: {p}", file=sys.stderr)
    print(f"check-privacy: {len(files)} tracked files, {'FAIL: ' + str(len(problems)) + ' finding(s)' if problems else 'ok'}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
