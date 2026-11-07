#!/usr/bin/env python3
"""TRX fayllarında atlanmış (NotExecuted/Skipped) test varsa uğursuz olur. CI-də bütün servis konteynerləri qalxıb, atlanma = test mühiti pozulub
(məs. DENTACORE_TEST_ES təyin olunmayıb) deməkdir və səssiz keçməməlidir. İstifadə: assert-no-skips.py <qovluq> [--allow-skips N]"""
import pathlib
import sys
import xml.etree.ElementTree as ET

root = pathlib.Path(sys.argv[1])
allowed = int(sys.argv[sys.argv.index("--allow-skips") + 1]) if "--allow-skips" in sys.argv else 0
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
total = passed = skipped = failed = 0
skipped_names = []
files = list(root.rglob("*.trx"))
if not files:
    print(f"TRX tapılmadı: {root}", file=sys.stderr)
    sys.exit(2)
for f in files:
    for r in ET.parse(f).getroot().iterfind(".//t:UnitTestResult", ns):
        total += 1
        outcome = r.get("outcome")
        if outcome == "Passed":
            passed += 1
        elif outcome in ("NotExecuted", "Skipped"):
            skipped += 1
            skipped_names.append(r.get("testName"))
        else:
            failed += 1
print(f"total={total} passed={passed} skipped={skipped} failed={failed}")
if failed:
    sys.exit(1)
if skipped > allowed:
    print(f"ATLANMIŞ TESTLƏR ({skipped} > icazə {allowed}):", *sorted(skipped_names)[:20], sep="\n  ", file=sys.stderr)
    sys.exit(1)
