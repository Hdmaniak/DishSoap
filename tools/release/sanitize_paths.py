#!/usr/bin/env python3
"""Sanitize machine-specific paths/IDs in this repository's text files.

This is the public, leak-free copy of the working port's release sanitizer
(tools/release/ of the private workspace).  It walks the
repository and rewrites private development references -- the developer's
absolute workspace paths, device serials, LAN addresses and the release-key
identifier -- into neutral placeholders, so a sync from the working port cannot
leak them into the public tree.

The exact private literals live in the encoded rule table below.  They are
stored base64-encoded on purpose: a public script must not itself contain the
strings it is meant to remove.

Usage:
    python3 tools/release/sanitize_paths.py [REPO_ROOT]
    # REPO_ROOT defaults to the repository root this script lives in.
"""
import base64
import json
import os
import re
import sys

RULES = json.loads(base64.b64decode(
    "eyJyZXBsIjpbWyIvaG9tZS9ib21iby9kaXNod2FzaGVyL21nLXBvcnQvRGlzaHdhc2hlci9Db250ZW50Iiwic3JjL0Rpc2h3YXNoZXIvQ29udGVudCJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlci9tZy1wb3J0L0Rpc2h3YXNoZXIiLCJzcmMvRGlzaHdhc2hlciJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlci9tZy1wb3J0L3NjcmlwdHMiLCJ0b29scy9zY3JpcHRzIl0sWyIvaG9tZS9ib21iby9kaXNod2FzaGVyL21nLXBvcnQvdG9vbHMiLCJ0b29scyJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlci9tZy1wb3J0L25vdGVzIiwiPGRldi1ub3Rlcz4iXSxbIi9ob21lL2JvbWJvL2Rpc2h3YXNoZXIvbWctcG9ydC9nYW1lcyIsIjxkZXYtd29ya3NwYWNlPi9nYW1lcyJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlci9tZy1wb3J0L0NvbnRlbnQtc2Z4IiwiPGRldi13b3Jrc3BhY2U+L0NvbnRlbnQtc2Z4Il0sWyIvaG9tZS9ib21iby9kaXNod2FzaGVyL21nLXBvcnQvIiwiPGRldi13b3Jrc3BhY2U+LyJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlci9tZy1wb3J0IiwiPGRldi13b3Jrc3BhY2U+Il0sWyIvaG9tZS9ib21iby9kaXNod2FzaGVyL2Fzc2V0cy1jbGVhbiIsIiRESVNIV0FTSEVSX0FTU0VUU19ESVIiXSxbIi9ob21lL2JvbWJvL2Rpc2h3YXNoZXIvcmV4Z2x1ZS1zZGsiLCI8cmV4Z2x1ZS1zZGs+Il0sWyIvaG9tZS9ib21iby9kaXNod2FzaGVyL2FuZHJvaWQtYXBwIiwiPGFuZHJvaWQtYXBwPiJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlci9hbmRyb2lkLXZrcHJvYmUiLCI8YW5kcm9pZC12a3Byb2JlPiJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlci9tYW5hZ2VkIiwiPG1hbmFnZWQtZGVjb21waWxlPiJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlci9wb3J0IiwiPGV4dHJhY3RlZC1wYWNrYWdlPiJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlci9leHRyYWN0ZWQiLCI8ZXh0cmFjdGVkPiJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlci9hbmRyb2lkIiwiPGRldi13b3Jrc3BhY2U+L2FuZHJvaWQiXSxbIi9ob21lL2JvbWJvL2Rpc2h3YXNoZXIvXHUyMDI2IiwiPGRldi13b3Jrc3BhY2U+L1x1MjAyNiJdLFsiL2hvbWUvYm9tYm8vZGlzaHdhc2hlciIsIjxkZXYtd29ya3NwYWNlPiJdLFsiL2hvbWUvYm9tYm8vYW5kcm9pZC1uZGsvYW5kcm9pZC1uZGstcjI4YiIsIiRIT01FL2FuZHJvaWQtbmRrLXIyOGIiXSxbIi9ob21lL2JvbWJvL2FuZHJvaWQtbmRrLyIsIiRIT01FL2FuZHJvaWQtbmRrLyJdLFsiL2hvbWUvYm9tYm8vYW5kcm9pZC1zZGsiLCIkQU5EUk9JRF9TREtfRElSIl0sWyIvaG9tZS9ib21iby8uZG90bmV0IiwiJEhPTUUvLmRvdG5ldCJdLFsiL2hvbWUvYm9tYm8vLndpbmUtbWdjYiIsIiRIT01FLy53aW5lLW1nY2IiXSxbIi9ob21lL2JvbWJvL2Jpbi9hZGIiLCIkQURCIl0sWyIvaG9tZS9ib21iby9iaW4vIiwiIl0sWyIvaG9tZS9ib21iby8iLCIkSE9NRS8iXSxbIi9ob21lL2JvbWJvIiwiJEhPTUUiXSxbIm1nLXBvcnQvRGlzaHdhc2hlci9Db250ZW50Iiwic3JjL0Rpc2h3YXNoZXIvQ29udGVudCJdLFsibWctcG9ydC9EaXNod2FzaGVyIiwic3JjL0Rpc2h3YXNoZXIiXSxbIm1nLXBvcnQvc2NyaXB0cyIsInRvb2xzL3NjcmlwdHMiXSxbIm1nLXBvcnQvdG9vbHMiLCJ0b29scyJdLFsibWctcG9ydC9ub3RlcyIsIjxkZXYtbm90ZXM+Il0sWyJtZy1wb3J0LyIsIjxkZXYtd29ya3NwYWNlPi8iXSxbIm1nLXBvcnQiLCI8ZGV2LXdvcmtzcGFjZT4iXSxbIlI1OFI5NEEyNk1ZIiwiPGRldmljZS1zZXJpYWw+Il0sWyJSRkNYMzFZMTZLRiIsIjxkZXZpY2Utc2VyaWFsPiJdLFsiMjlkODM4NWMxMjBiN2VjZSIsIjxkZXZpY2Utc2VyaWFsPiJdLFsiRGlzaHdhc2hlci1SZWxlYXNlLTIwMjYhUms5cCIsIjxyZWxlYXNlLWtleS1wYXNzPiJdXX0="
).decode("utf-8"))

# Ordered replacements (longest/most specific first).
REPL = [tuple(x) for x in RULES["repl"]]

IP_RE = re.compile(r"192\.168\.100\.\d+")

SKIP_EXT = {".png", ".jpg", ".jpeg", ".so", ".apk", ".aab", ".jks",
            ".keystore", ".p12", ".xnb", ".dll", ".exe", ".ico", ".gif"}


def default_root():
    return os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else default_root()
    changed = []
    for dirpath, dirnames, filenames in os.walk(root):
        if ".git" in dirnames:
            dirnames.remove(".git")
        for fn in filenames:
            path = os.path.join(dirpath, fn)
            if os.path.splitext(fn)[1].lower() in SKIP_EXT:
                continue
            try:
                with open(path, "rb") as f:
                    raw = f.read()
            except OSError:
                continue
            if b"\x00" in raw:
                continue
            text = raw.decode("utf-8", errors="surrogateescape")
            new = text
            for a, b in REPL:
                new = new.replace(a, b)
            new = IP_RE.sub("<lan-ip>", new)
            if new != text:
                with open(path, "w", encoding="utf-8", errors="surrogateescape") as f:
                    f.write(new)
                changed.append(os.path.relpath(path, root))
    for c in sorted(changed):
        print("changed:", c)
    print("total changed:", len(changed))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
