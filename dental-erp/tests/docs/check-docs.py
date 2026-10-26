#!/usr/bin/env python3
"""Sənədlərin sınıq istinadlarını tapır (şəbəkəsiz, asılılıqsız).

Yoxlanır: (1) hər .md faylında nisbi keçid və şəkil real fayla işarə edir; (2) `#anchor` hədəf faylda başlıq kimi var
(GitHub slug qaydası); (3) docs/adr/README.md bütün ADR fayllarını sadalayır; (4) bələdçilərdə `infra|db|tests/...sh|py|mjs`
kimi göstərilən əmr faylları mövcuddur; (5) docs/guides/api-guide.md-dəki hər `curl` əmri tests/docs/api-guide-examples.sh-də
(boşluqlar və sətir davamları nəzərə alınmadan) sözbəsöz var, yəni bələdçidə göstərilən hər nümunə canlı stack-ə qarşı işləyir.  İstifadə:  python3 tests/docs/check-docs.py   (çıxış kodu 1 = sınıq istinad var)
"""
import re
import sys
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SKIP = {"node_modules", ".next", "bin", "obj", ".git", "test-results", "coverage"}
LINK = re.compile(r"!?\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
HEAD = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")
CMD = re.compile(r"(?<![\w/.-])((?:infra|db|tests)/[\w./-]+\.(?:sh|py|mjs))")
FENCE = re.compile(r"^(```|~~~)")


def md_files():
    for p in ROOT.rglob("*.md"):
        if not SKIP.intersection(p.relative_to(ROOT).parts):
            yield p


def slug(title: str) -> str:
    t = re.sub(r"[`*]", "", title)
    t = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", t).strip().lower()
    t = "".join(c for c in t if c.isalnum() or c in " -_" or unicodedata.category(c).startswith("L"))
    return t.replace(" ", "-")


def headings(path: Path):
    seen, out, fenced = {}, set(), False
    for line in path.read_text(encoding="utf-8").splitlines():
        if FENCE.match(line):
            fenced = not fenced
        m = None if fenced else HEAD.match(line)
        if m:
            s = slug(m.group(2))
            n = seen.get(s, 0)
            seen[s] = n + 1
            out.add(s if n == 0 else f"{s}-{n}")
    return out


def curl_examples_not_in_script():
    guide, script = ROOT / "docs/guides/api-guide.md", ROOT / "tests/docs/api-guide-examples.sh"
    squash = lambda t: re.sub(r"\s+", " ", t.replace("\\\n", " ")).strip()
    known, out = squash(script.read_text(encoding="utf-8")), []
    for block in re.findall(r"```bash\n(.*?)```", guide.read_text(encoding="utf-8"), re.S):
        for line in block.replace("\\\n", " ").splitlines():
            if line.strip().startswith("curl") and squash(line) not in known:
                out.append(f"docs/guides/api-guide.md: nümunə skriptdə yoxdur: {squash(line)[:110]}")
    return out


def main() -> int:
    errors, cache = [], {}
    for md in md_files():
        rel = md.relative_to(ROOT)
        text, fenced = md.read_text(encoding="utf-8"), False
        for no, line in enumerate(text.splitlines(), 1):
            if FENCE.match(line):
                fenced = not fenced
            if fenced:
                continue
            for target in LINK.findall(line):
                if re.match(r"^[a-z][a-z0-9+.-]*:", target):
                    continue
                path_part, _, anchor = target.partition("#")
                dest = md if not path_part else (md.parent / path_part).resolve()
                if not dest.exists():
                    errors.append(f"{rel}:{no}: fayl yoxdur: {target}")
                    continue
                if anchor and dest.suffix == ".md":
                    heads = cache.setdefault(dest, headings(dest))
                    if anchor.lower() not in heads:
                        errors.append(f"{rel}:{no}: başlıq yoxdur: {target}")
        if rel.parts[:2] == ("docs", "guides"):
            for no, line in enumerate(text.splitlines(), 1):
                for f in CMD.findall(line):
                    if not (ROOT / f).exists():
                        errors.append(f"{rel}:{no}: skript yoxdur: {f}")
    errors += curl_examples_not_in_script()
    adr = ROOT / "docs/adr"
    index = (adr / "README.md").read_text(encoding="utf-8")
    for f in sorted(adr.glob("[0-9]*.md")):
        if f.name not in index:
            errors.append(f"docs/adr/README.md: {f.name} siyahıda yoxdur")
    for e in errors:
        print(e)
    print(f"{len(errors)} problem" if errors else "OK: sənəd istinadları düzgündür")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
