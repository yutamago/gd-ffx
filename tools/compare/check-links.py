"""R8: every local link and picture of an HTML page exists (the report's).

Usage: check-links.py PAGE.html

Collects href and src attributes; a relative one must name a file beside the
page (no '..' out of its folder), an absolute URL or an absolute path is an
error (the report stands alone). Exit 1 on a missing or outside link."""
import html.parser
import pathlib
import sys
import urllib.parse

page = pathlib.Path(sys.argv[1]).resolve()
root = page.parent


class Links(html.parser.HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = []

    def handle_starttag(self, tag, attrs):
        for k, v in attrs:
            if k in ("href", "src") and v:
                self.links.append(v)


p = Links()
p.feed(page.read_text(encoding="utf-8"))
bad = []
for link in p.links:
    u = urllib.parse.urlparse(link)
    if u.scheme or u.netloc or link.startswith("/"):
        bad.append(f"not relative: {link}")
        continue
    if link.startswith("#"):
        continue
    target = (root / urllib.parse.unquote(u.path)).resolve()
    if root not in target.parents and target != root:
        bad.append(f"outside the report: {link}")
    elif not target.is_file():
        bad.append(f"missing: {link}")
for b in bad:
    print(f"check-links: {b}", file=sys.stderr)
print(f"check-links: {page.name}: {len(p.links)} links, {'FAIL: ' + str(len(bad)) if bad else 'all present'}")
sys.exit(1 if bad else 0)
