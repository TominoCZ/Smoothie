#!/usr/bin/env python3
"""name.py <publics.txt> <rva hex>...: nearest public symbol at or before each RVA."""
import sys, bisect
p = sorted((int(a, 16), n.strip()) for a, n in (l.split(' ', 1) for l in open(sys.argv[1])))
k = [a for a, _ in p]
for r in sys.argv[2:]:
    v = int(r, 16); i = bisect.bisect_right(k, v) - 1
    print(f'{v:X} {p[i][1][:110]}+0x{v - p[i][0]:X}')
