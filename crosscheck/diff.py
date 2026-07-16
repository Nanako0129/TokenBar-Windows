#!/usr/bin/env python3
"""Cross-check verdict: diff Swift vs C# harness outputs.

Usage: python3 crosscheck/diff.py <swift-out-dir> <csharp-out-dir>

Rules (see README.md): strings byte-exact; missing key == null; numbers equal
when exactly equal or within max(1e-12, 1e-9 * max(|a|,|b|)).
"""

import json
import math
import sys
from pathlib import Path

ABS_EPS = 1e-12
REL_EPS = 1e-9


def numbers_equal(a, b):
    # Integers compare exactly: token counts run to Int64 scale, where a
    # relative epsilon masks real divergence (5_600_000_000 vs …005) and
    # adjacent ints collapse to the same binary64 (Codex review finding).
    if isinstance(a, int) and isinstance(b, int):
        return a == b
    if a == b:
        return True
    fa, fb = float(a), float(b)
    if math.isnan(fa) or math.isnan(fb):
        return math.isnan(fa) and math.isnan(fb)
    return abs(fa - fb) <= max(ABS_EPS, REL_EPS * max(abs(fa), abs(fb)))


def diff(a, b, path, out):
    is_num = lambda v: isinstance(v, (int, float)) and not isinstance(v, bool)
    if is_num(a) and is_num(b):
        if not numbers_equal(a, b):
            out.append(f"{path}: {a!r} != {b!r}")
    elif isinstance(a, dict) and isinstance(b, dict):
        for key in sorted(set(a) | set(b)):
            # missing key == null by contract
            diff(a.get(key), b.get(key), f"{path}.{key}", out)
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append(f"{path}: list length {len(a)} != {len(b)}")
        else:
            for i, (x, y) in enumerate(zip(a, b)):
                diff(x, y, f"{path}[{i}]", out)
    elif a != b:
        out.append(f"{path}: {a!r} != {b!r}")


def self_test():
    int64_max = 9223372036854775807
    assert not numbers_equal(5_600_000_000, 5_600_000_005), "large-int drift must fail"
    assert not numbers_equal(int64_max, int64_max - 9_000_000_000), "near-max drift must fail"
    assert not numbers_equal(int64_max, int64_max - 1), "adjacent ints must fail"
    assert numbers_equal(int64_max, int64_max)
    assert numbers_equal(42, 42.0), "int/float cross-type uses the float path"
    assert numbers_equal(0.1 + 0.2, 0.3), "serializer digit noise stays tolerated"
    assert not numbers_equal(1.0, 1.001)
    assert not numbers_equal(0.0, -0.001)
    print("diff.py self-test OK")


def main():
    if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
        return self_test()
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    left_dir, right_dir = Path(sys.argv[1]), Path(sys.argv[2])
    left_files = {p.name for p in left_dir.glob("*.actual.json")}
    right_files = {p.name for p in right_dir.glob("*.actual.json")}
    if not left_files and not right_files:
        sys.exit("no *.actual.json in either directory — run both harnesses first")

    failures = []
    for name in sorted(left_files | right_files):
        if name not in left_files or name not in right_files:
            failures.append(f"{name}: present on one side only")
            continue
        left = json.loads((left_dir / name).read_text())
        right = json.loads((right_dir / name).read_text())
        for case in sorted(set(left) | set(right)):
            if case not in left or case not in right:
                failures.append(f"{name}:{case}: present on one side only")
                continue
            out = []
            diff(left[case], right[case], case, out)
            failures.extend(f"{name}:{line}" for line in out)

    total = sum(
        len(json.loads((left_dir / n).read_text())) for n in left_files if n in right_files
    )
    if failures:
        print(f"CROSSCHECK FAILED — {len(failures)} difference(s) over {total} case(s):")
        print("\n".join(f"  {f}" for f in failures))
        sys.exit(1)
    print(f"CROSSCHECK OK — {total} case(s), zero material difference")


if __name__ == "__main__":
    main()
