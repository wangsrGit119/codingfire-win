# -*- coding: utf-8 -*-
"""Assert the deterministic core of the hover-card token rate (_rate_got.txt).

What is being pinned down here is *not* the displayed number - that one carries
deliberate jitter and is therefore not reproducible.  It is the candidate value
that feeds the smoothing:

    instant = min(320, max(measured_inflows, flame_derived))
    measured_inflows = sum(min(event, 15000) for events inside the 60s window) / 60

The expected values below are hand-computed from that spec, with the arithmetic
written out so a future change to the spec is easy to re-derive.

Exit code 1 on any mismatch, 2 when the harness produced nothing - so this is
usable as a gate and cannot pass by accident.
"""
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
GOT = os.path.join(HERE, "_rate_got.txt")
TOL = 1e-9

# label -> (expected, why)
EXPECT = {
    # Timestamp 600s in the past: outside the 60s window, so it must not show up
    # as a *current* burn rate.
    "late_event_excluded": (0.0, "no inflow inside the window -> 0"),
    # ...but the fire itself should still catch: a log flushed late still means
    # tokens were really burned.
    "late_event_still_flares": (1.0, "flame-derived rate must be > 0"),
    # T-61s is outside the window, T-59s is inside -> 15000 / 60
    "window_boundary": (15000.0 / 60.0, "only the in-window event counts"),
    # 40000 is capped to 15000, plus 5000 -> 20000 / 60
    "per_event_cap": (20000.0 / 60.0, "per-event credit capped at 15000"),
    # That 333.3 already exceeds the display ceiling, so it must be clamped.
    "display_cap": (320.0, "candidate clamped to 320 tok/s"),
    # instant must equal min(cap, max(measured, flame)) exactly.
    "composition_gap": (0.0, "min/max composition must be exact"),
}

if not os.path.exists(GOT):
    print("FATAL: %s not found - the fixture harness did not run (or did not finish)." % GOT)
    sys.exit(2)
if os.path.getsize(GOT) == 0:
    print("FATAL: %s is empty - the harness ran but wrote nothing." % GOT)
    sys.exit(2)

got = {}
for line in io.open(GOT, encoding="utf-8"):
    line = line.rstrip("\n")
    if not line or "\t" not in line:
        continue
    label, value = line.split("\t", 1)
    try:
        got[label] = float(value)
    except ValueError:
        got[label] = None

fails = []
print("%-26s %14s %14s   %s" % ("check", "expect", "actual", "verdict"))
print("-" * 74)
for label in sorted(EXPECT):
    want, why = EXPECT[label]
    have = got.get(label)
    if have is None:
        fails.append("%s: never reported by the harness" % label)
        print("%-26s %14s %14s   MISSING" % (label, "-", "-"))
        continue
    ok = abs(have - want) <= TOL
    if not ok:
        fails.append("%s: expected %r got %r (%s)" % (label, want, have, why))
    print("%-26s %14.6f %14.6f   %s" % (label, want, have, "ok" if ok else "FAIL"))

for label in sorted(got):
    if label not in EXPECT:
        fails.append("%s: reported but absent from expectations" % label)
        print("%-26s %14s %14s   UNKNOWN" % (label, "-", "-"))

print()
if fails:
    print("FAILED %d/%d" % (len(fails), len(EXPECT)))
    for f in fails:
        print("  - " + f)
    sys.exit(1)

print("RATE CORE OK (%d checks)" % len(EXPECT))
