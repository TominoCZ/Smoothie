#!/bin/bash
# Assemble a routine and print it as a C# byte list for EnginePatch.cs, with its disassembly
# (to check every instruction against Unity's original) and the offsets of its address slots.
# usage: assemble.sh visibility_scan.s
set -e
o=$(mktemp --suffix=.o); b=$(mktemp --suffix=.bin)
as --64 -o "$o" "$1"
objdump -d -M intel "$o" | tail -n +7
nm "$o" | grep -i slot || true
objcopy -O binary -j .text "$o" "$b"
echo "$(stat -c %s "$b") bytes:"
python3 -c "import sys; b=open(sys.argv[1],'rb').read(); print(',\n'.join(', '.join('0x%02X'%x for x in b[i:i+16]) for i in range(0,len(b),16)))" "$b"
rm -f "$o" "$b"
