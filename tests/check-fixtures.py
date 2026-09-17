# -*- coding: utf-8 -*-
"""Compare the fixture run against _fixtures_expected.json.  Exit code 1 on any
mismatch so the suite can be used as a gate."""
import io
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
exp = json.load(io.open(os.path.join(HERE, "_fixtures_expected.json"), encoding="utf-8"))

# Fail loudly when the harness never produced output.  Symptom of a broken runner
# (e.g. a launcher that does not actually wait) is an EMPTY stdout here, which is
# easy to mistake for "the fixtures themselves failed" - or worse, for a pass.
got_path = os.path.join(HERE, "_fixtures_got.txt")
if not os.path.exists(got_path):
    print("FATAL: %s not found - the fixture harness did not run (or did not finish)." % got_path)
    sys.exit(2)
if os.path.getsize(got_path) == 0:
    print("FATAL: %s is empty - the fixture harness ran but wrote nothing." % got_path)
    sys.exit(2)

got = {}
for line in io.open(got_path, encoding="utf-8"):
    parts = line.rstrip("\n").split("\t")
    if len(parts) == 3 and parts[1] not in ("THREW",):
        try:
            got[parts[0]] = (int(parts[1]), int(parts[2]))
        except ValueError:
            got[parts[0]] = ("error", parts[2])
    elif len(parts) == 3 and parts[1] == "THREW":
        got[parts[0]] = ("threw", parts[2])

fails = []
print("%-14s %8s %10s   %s" % ("source", "expect", "actual", "verdict"))
print("-" * 52)
for name in sorted(exp):
    e = exp[name]
    g = got.get(name)
    if g is None:
        fails.append("%s: adapter was never run" % name)
        print("%-14s %8d %10s   MISSING" % (name, e, "-"))
        continue
    if g[0] == "threw":
        fails.append("%s threw: %s" % (name, g[1]))
        print("%-14s %8d %10s   THREW" % (name, e, "-"))
        continue
    ok = (g[1] == e)
    if not ok:
        fails.append("%s: expected %d got %d (%d events)" % (name, e, g[1], g[0]))
    print("%-14s %8d %10d   %s" % (name, e, g[1], "ok" if ok else "FAIL"))

# A source the harness reported but the expectations do not know about means a
# renamed or typo'd id - silent unless flagged.
for name in sorted(got):
    if name not in exp:
        fails.append("%s: reported by the harness but absent from expectations" % name)
        print("%-14s %8s %10s   UNKNOWN" % (name, "-", "-"))

print()
if fails:
    print("FAILED %d/%d" % (len(fails), len(exp)))
    for f in fails:
        print("  - " + f)
    sys.exit(1)

print("ALL %d SOURCES OK" % len(exp))
