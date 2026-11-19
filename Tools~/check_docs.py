"""Check local file and image links in the repository's Markdown."""

import argparse
from html import unescape
import os
from pathlib import Path
import re
import string
import sys
from urllib.parse import unquote


ROOT = Path(__file__).resolve().parents[1]
GENERATED_DIRS = {
    "build", "builds", "dist", "dist~", "library", "logs", "memorycaptures",
    "node_modules", "obj", "temp", "testresults", "testresults~", "usersettings",
    "__pycache__",
}
ESCAPED_PUNCTUATION = rf"\\([{re.escape(string.punctuation)}])"
PATH_CHAR = rf"(?:{ESCAPED_PUNCTUATION}|[^\\\s<>()])"
DESTINATION = rf"<(?:{ESCAPED_PUNCTUATION}|[^\\<>])*>|(?:{PATH_CHAR}|\({PATH_CHAR}*\))*"
TITLE = r"""(?:"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|\((?:\\.|[^)\\])*\))"""
LABEL = r"(?:\\.|[^\[\]\\]|\[(?:\\.|[^\[\]\\])*\])*"
# Look ahead so a linked image also yields the image's destination.
INLINE_LINK = re.compile(
    rf"(?=(?<!\\)\[{LABEL}\]\([ \t]*(?P<target>{DESTINATION})"
    rf"(?:[ \t]+{TITLE})?[ \t]*\))"
)
REFERENCE = re.compile(
    rf"^ {{0,3}}\[(?!\^){LABEL}\]:[ \t]*(?P<target>{DESTINATION})(?:[ \t]+{TITLE})?[ \t]*$"
)
FENCE = re.compile(r"^ {0,3}(~{3,}|`{3,})(.*)$")
QUOTE_PREFIX = re.compile(r"^(?: {0,3}>[ \t]?)+")
INDENTED_CODE = re.compile(r"^(?: {4}| {0,3}\t)")
INLINE_CODE = re.compile(r"(?<!`)(`+)(?!`).*?(?<!`)\1(?!`)")
URL_SCHEME = re.compile(r"[A-Za-z][A-Za-z0-9+.-]*:")


def markdown_files():
    for folder, dirs, files in os.walk(ROOT):
        dirs[:] = sorted(
            name for name in dirs
            if name.lower() not in GENERATED_DIRS
            and (not name.startswith(".") or name in {".agents", ".github"})
        )
        for name in sorted(files):
            if name.lower().endswith(".md"):
                yield Path(folder) / name


def link_targets(document):
    fence = ""
    for number, line in enumerate(document.splitlines(), 1):
        line = QUOTE_PREFIX.sub("", line)
        marker = FENCE.match(line)
        if fence:
            if (marker and marker[1][0] == fence[0] and len(marker[1]) >= len(fence)
                    and not marker[2].strip()):
                fence = ""
            continue
        if marker and (marker[1][0] == "~" or "`" not in marker[2]):
            fence = marker[1]
            continue
        if INDENTED_CODE.match(line):
            continue

        reference = REFERENCE.match(line)
        if reference:
            yield number, reference["target"]
            continue
        line = INLINE_CODE.sub(" ", line)
        for link in INLINE_LINK.finditer(line):
            yield number, link["target"]


def relative_path(target):
    if target.startswith("<"):
        target = target[1:-1]
    target = unescape(re.sub(ESCAPED_PUNCTUATION, r"\1", target))
    if target.startswith(("/", "\\")) or URL_SCHEME.match(target):
        return None
    path = re.split(r"[?#]", target, maxsplit=1)[0]
    return unquote(path) if path else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("files", type=Path, nargs="*",
                        help="Markdown files to check (default: repository Markdown)")
    args = parser.parse_args()

    failures = 0
    checked = 0
    for document in args.files or markdown_files():
        try:
            contents = document.read_text(encoding="utf-8-sig")
        except (OSError, UnicodeError) as error:
            print(f"{document}: {error}", file=sys.stderr)
            failures += 1
            continue
        checked += 1
        for line, target in link_targets(contents):
            path = relative_path(target)
            if path is not None and not (document.parent / path).exists():
                print(f"{document}:{line}: missing link target {target!r}", file=sys.stderr)
                failures += 1
    if failures:
        return 1
    print(f"Checked {checked} Markdown files.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
