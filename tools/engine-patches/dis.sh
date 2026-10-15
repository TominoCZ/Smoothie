#!/bin/bash
# Disassemble UnityPlayer.dll at an RVA, with calls and jumps named from publics.txt.
# usage: dis.sh <UnityPlayer.dll> <publics.txt> <RVA hex, e.g. 3EE860> [bytes, default 400]
# The image base is 0x180000000 (check with: python3 -c "import pefile; print(hex(pefile.PE('UnityPlayer.dll').OPTIONAL_HEADER.ImageBase))").
DLL=$1; PUB=$2; B=0x180000000; R=$((16#$3)); N=${4:-400}
objdump -d -M intel --start-address=$((B+R)) --stop-address=$((B+R+N)) "$DLL" | tail -n +7 | PUB="$PUB" python3 -c "
import os, sys, re, bisect
pubs = sorted((int(a, 16), n.strip()) for a, n in (l.split(' ', 1) for l in open(os.environ['PUB'])))
addrs = [a for a, _ in pubs]
def name(v):
    i = bisect.bisect_right(addrs, v) - 1
    return pubs[i][1][:90] + ('+%x' % (v - addrs[i]) if v != addrs[i] else '') if i >= 0 else '?'
for line in sys.stdin:
    m = re.search(r'(call|j[a-z]+)\s+(?:QWORD PTR \[rip\+0x[0-9a-f]+\]\s+# )?0x([0-9a-f]+)', line)
    if m: line = line.rstrip() + '   ; ' + name(int(m.group(2), 16) - 0x180000000) + '\n'
    sys.stdout.write(line)
"
