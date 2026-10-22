#!/bin/bash
# A GameAssembly.dll function (all .pdata chunks) disassembled, calls named from a publics file
# (tools/il2cpp-xref: dotnet run -- publics x x > ga_publics.txt) and every IL2CPP metadata global
# it loads (type, method, field, string literal) resolved inline by this folder's dumper.
# usage: SPT_PATH=... gadis.sh <ga_publics.txt> <RVA hex>   (build first: dotnet build -c Release -o bin)
S=$(cd "$(dirname "$0")" && pwd)
PUB=$1; shift
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
python3 "$S/../engine-patches/fnall.py" "$SPT_PATH/GameAssembly.dll" "$PUB" "$1" \
  | cut -c11- | sed -E 's/   ; ([^|]*)( \|.*)?$/   ; \1/' > "$T/dis.txt"
G=$(grep -o '# 0x18[0-9a-f]*' "$T/dis.txt" | awk '{print $2}' | sed 's/0x//' | sort -u)
(cd "$S" && dotnet bin/il2cpp-dump.dll globals $G 2>/dev/null | grep -v '^\[') | sed -E 's/Metadata Usage \{type=[A-Za-z]+, Value=//; s/\}$//' > "$T/globals.txt"
DIS="$T/dis.txt" GLOBALS="$T/globals.txt" python3 - <<'PY'
g={}
import os
for l in open(os.environ['GLOBALS']):
    a,_,rest=l.rstrip("\n").partition(" ")
    if not a or not all(c in "0123456789ABCDEFabcdef" for c in a): continue
    if rest and not rest.startswith('?'): g[int(a,16)]=rest[:110]
import re
for l in open(os.environ['DIS']):
    l=l.rstrip('\n')
    m=re.search(r'# 0x(18[0-9a-f]+)',l)
    if m and int(m.group(1),16) in g: l=l[:m.start()]+'# '+g[int(m.group(1),16)]
    # IL2CPP's lazy metadata init and class init calls of this build (noise around every global).
    if re.search(r'call +0x1801cf680|call +0x1800df7d0',l): continue
    print(l[:200])
PY
