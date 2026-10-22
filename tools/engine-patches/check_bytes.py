#!/usr/bin/env python3
"""Checks everything Smoothie writes into UnityPlayer.dll against the DLL and the sources here.

usage: check_bytes.py <UnityPlayer.dll>

- SceneSkip.BuildId is the DLL's PDB id.
- Each EnginePatch: its Original bytes are the DLL's at its RVA, its Routine bytes are what its .s
  file assembles to, and each address slot it fills is one of that routine's *_slot labels (the
  visibility scan's single slot has no label: it's the routine's last 8 bytes).
- AnimatorPause, LodRemoveHook: the hooked functions start with the bytes they copy into their trampolines.
Needs as, objcopy, nm (binutils) and pefile."""
import re, subprocess, sys, tempfile
from pathlib import Path
import pefile

here = Path(__file__).resolve().parent
src = here.parent.parent / 'src' / 'Smoothie'
dll = pefile.PE(sys.argv[1], fast_load=True)
failed = False

def check(ok, what):
    global failed
    print(('ok    ' if ok else 'FAIL  ') + what)
    failed |= not ok

def bytes_of(text):
    return bytes(int(x, 16) for x in re.findall(r'0x([0-9A-F]{2})\b', text))

raw = Path(sys.argv[1]).read_bytes()
rsds = raw.index(b'RSDS')
g = raw[rsds + 4:rsds + 20]
build = '%08X%04X%04X%s%X' % (int.from_bytes(g[0:4], 'little'), int.from_bytes(g[4:6], 'little'),
                              int.from_bytes(g[6:8], 'little'), g[8:].hex().upper(), int.from_bytes(raw[rsds + 20:rsds + 24], 'little'))
scene = (src / 'SceneSkip.cs').read_text()
check(f'"{build}"' in scene, f'SceneSkip.BuildId is the DLL\'s build {build}')

engine = (src / 'EnginePatch.cs').read_text()
files = {'VisibilityScan': 'visibility_scan', 'LodLoop': 'lod_loop', 'NavCrowd': 'nav_crowd',
         'NavVelocity': 'nav_velocity', 'LightCount': 'light_count'}
for field, name in files.items():
    block = engine[engine.index(f'{field} = new('):]
    block = block[:block.index(';', block.index('}', block.index('new byte[]\n'))) + 1]
    rva = int(re.search(r'"[^"]+",\s*0x([0-9A-F]+)', block).group(1), 16)
    original, routine = (bytes_of(a) for a in re.findall(r'new byte\[\]\s*\{(.*?)\}', block, re.S)[:2])
    tail = block[block.index('}', block.index('new byte[]\n')):]
    slots = [int(o, 16) for o, _ in re.findall(r'\(0x([0-9A-F]+),\s*0x([0-9A-F]+)\)', tail)]
    slots += [int(o, 16) for o in re.findall(r'_(?:cell|flags)Slot = 0x([0-9A-F]+)', tail)]
    check(dll.get_data(rva, len(original)) == original, f'{field}: original {len(original)} bytes at 0x{rva:X}')
    with tempfile.TemporaryDirectory() as t:
        subprocess.run(['as', '--64', '-o', f'{t}/r.o', str(here / f'{name}.s')], check=True)
        subprocess.run(['objcopy', '-O', 'binary', '-j', '.text', f'{t}/r.o', f'{t}/r.bin'], check=True)
        assembled = Path(f'{t}/r.bin').read_bytes()
        labels = sorted(int(line.split()[0], 16) for line in subprocess.run(['nm', f'{t}/r.o'], capture_output=True, text=True).stdout.splitlines()
                        if line.split()[2].endswith('_slot'))
    check(assembled == routine, f'{field}: routine is {name}.s ({len(routine)} bytes)')
    check(sorted(slots) == (labels or [len(routine) - 8]), f'{field}: slots {[hex(s) for s in slots]} are the routine\'s')

for cls, fns in (('AnimatorPause', ('UpdateAvatars', 'PrepareFrame')), ('LodRemoveHook', ('Remove',))):
    hooks = engine[engine.index(f'class {cls}'):]
    for fn in fns:
        rva = int(re.search(fn + r'Rva = 0x([0-9A-F]+)', hooks).group(1), 16)
        start = bytes_of(re.search(fn + r'Start = \{(.*?)\}', hooks, re.S).group(1))
        check(dll.get_data(rva, len(start)) == start and len(start) >= 14, f'{cls}: {fn} starts with its {len(start)} bytes at 0x{rva:X}')

sys.exit(1 if failed else 0)
