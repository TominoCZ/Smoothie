#!/usr/bin/env python3
"""Call stacks for one thread of the game, from a `perf record --call-graph dwarf` recording.

perf can't follow callers through Unity's and Mono's code: they're Windows DLLs (run by Wine)
built without frame pointers, and perf's own unwinder only reads Linux debug info. Every x64
Windows DLL carries exact unwind tables for its functions (.pdata/.xdata), so this script unwinds
the stack copies perf saved with those, the way Windows (and Wine) do. C# code compiled by Mono's
JIT keeps frame pointers and is followed by rbp. Wine's Linux side (syscalls, waits, timers) runs
on a separate stack, so those samples get their Linux leaf and the Windows function that made
the call, nothing above it.

Plain `-e cycles` matters: perf's default is precise sampling (cycles:P), which on AMD takes the
instruction pointer from a different moment than the registers and stack, and every walk breaks.
Record (the main thread's id is the game's pid; no bots needed unless bots are the question):
  pid=$(pgrep -f 'EscapeFromTarkov.exe' | while read p; do [ "$(cat /proc/$p/comm)" = EscapeFromTarko ] && echo $p; done)
  perf record -e cycles -F 999 -t $pid --call-graph dwarf,65528 -o x.data -- sleep 10
  cp /proc/$pid/maps x.maps
All threads (job workers, render thread) instead: -p $pid in place of -t $pid, --threads (or --tid N) in the
report; -F 499 and dwarf,32768 keep the file to a few hundred MB.
Report:
  stacks.py x.data x.maps --syms UnityPlayer.dll=<publics.txt> --syms mono-2.0-bdwgc.dll=<publics.txt>
    [--names BepInEx]   name C# frames through a Debug build's JitNames (same game process)
    [--focus TEXT]      callers and callees of functions whose name contains TEXT
    [--tree]            top-down tree from the thread's start
    [--folded FILE]     folded stacks for flamegraph.pl or https://www.speedscope.app
    [--check]           fail unless most stacks reach the thread's start (the unwinder works)
publics.txt files come from publics.py over Unity's public PDBs (see EnginePatch.cs).
"""
import argparse, bisect, collections, mmap, os, struct, subprocess, sys, time

u16 = lambda d, o: struct.unpack_from('<H', d, o)[0]
u32 = lambda d, o: struct.unpack_from('<I', d, o)[0]
u64 = lambda d, o: struct.unpack_from('<Q', d, o)[0]


def demangle(n):
    """Enough of MSVC's decoration to read: ?Name@Class@NS@@... -> NS::Class::Name."""
    if not n.startswith('?') or n.startswith('??_') or n.startswith('??$'):
        return n
    special = None
    if n.startswith('??0') or n.startswith('??1'):
        special, n = n[2], n[3:]
    else:
        n = n[1:]
    parts = n.split('@@')[0].split('@')
    if special:
        cls = parts[0]
        parts = [cls if special == '0' else '~' + cls] + parts
    return '::'.join(reversed([p for p in parts if p]))


class Pe:
    """A Windows DLL/EXE: its unwind tables and names, read from the file on disk."""

    def __init__(self, path, base, syms):
        d = self.d = open(path, 'rb').read()
        self.name, self.base = os.path.basename(path), base
        e = u32(d, 0x3c)
        opt = e + 24
        self.size = u32(d, opt + 56)
        self.secs = [struct.unpack_from('<IIII', d, opt + u16(d, e + 20) + s * 40 + 8) for s in range(u16(d, e + 6))]
        rva, size = struct.unpack_from('<II', d, opt + 112 + 3 * 8)  # exception directory
        o = self.off(rva) if size else None
        self.rf = sorted(struct.unpack_from('<III', d, o + i * 12) for i in range(size // 12)) if o is not None else []
        self.rf_begin = [r[0] for r in self.rf]
        self.syms = sorted(syms) if syms is not None else self.exports()
        self.sym_rva = [s[0] for s in self.syms]

    def off(self, rva):
        for vsize, va, rsize, raw in self.secs:
            if va <= rva < va + max(vsize, rsize):
                return rva - va + raw if rva - va < rsize else None
        return rva if rva < 0x1000 else None

    def bytes(self, rva, n):
        o = self.off(rva)
        return self.d[o:o + n] if o is not None else b''

    def exports(self):
        d = self.d
        rva = u32(d, u32(d, 0x3c) + 24 + 112)
        o = self.off(rva) if rva else None
        if o is None:
            return []
        n, functions, names, ordinals = u32(d, o + 24), u32(d, o + 28), u32(d, o + 32), u32(d, o + 36)
        out = []
        for i in range(n):
            no = self.off(u32(d, self.off(names) + 4 * i))
            ordinal = u16(d, self.off(ordinals) + 2 * i)
            out.append((u32(d, self.off(functions) + 4 * ordinal), d[no:d.index(b'\0', no)].decode(errors='replace')))
        return sorted(out)

    def function(self, rva):
        i = bisect.bisect_right(self.rf_begin, rva) - 1
        return self.rf[i] if i >= 0 and self.rf[i][0] <= rva < self.rf[i][1] else None

    def symbol(self, rva):
        """The public name at the function's start; a static (unnamed) function shows its address
        and the public name before it, which is only a hint at what code it's near."""
        fn = self.function(rva)
        start = fn[0] if fn else rva
        i = bisect.bisect_right(self.sym_rva, start) - 1
        if i < 0:
            return f'{self.name}+{start:x}'
        name = demangle(self.syms[i][1])
        if fn and self.sym_rva[i] != start:
            return f'{self.name}!fn_{start:x} (after {name})'
        return f'{self.name}!{name}'


class Elf:
    """A Linux library (Wine's unix side, libc, drivers): names only, from nm."""

    def __init__(self, path, bias):
        self.name, self.bias = os.path.basename(path), bias
        out = subprocess.run(['nm', '-D', '--defined-only', path], capture_output=True, text=True).stdout
        out += subprocess.run(['nm', '--defined-only', path], capture_output=True, text=True).stdout
        self.syms = sorted({(int(p[0], 16), p[2]) for p in (l.split() for l in out.splitlines()) if len(p) == 3 and p[1] in 'tTwW'})
        self.addr = [s[0] for s in self.syms]

    def symbol(self, a):
        i = bisect.bisect_right(self.addr, a - self.bias) - 1
        return f'{self.name}!{self.syms[i][1]}' if i >= 0 else f'{self.name}+{a - self.bias:x}'


class Modules:
    """What an address is: a Windows image, a Linux library, C# (Mono JIT: anonymous executable
    memory), the kernel, or something else ([vdso], Wine's own areas)."""

    def __init__(self, maps, syms):
        pe, elf, jit = [], [], []
        seen = set()
        for line in open(maps):
            p = line.split(maxsplit=5)
            start, end = (int(x, 16) for x in p[0].split('-'))
            path, offset = (p[5].strip() if len(p) > 5 else ''), int(p[2], 16)
            low = path.lower()
            if low.endswith(('.dll', '.exe', '.drv', '.sys')):
                if offset == 0 and path not in seen and os.path.exists(path):
                    seen.add(path)
                    d = open(path, 'rb').read(4096)
                    if d[:2] == b'MZ':
                        pe.append((start, start + u32(d, u32(d, 0x3c) + 24 + 56), 'pe', path, None))
            elif 'x' in p[1] and path.startswith('/'):
                elf.append((start, end, 'elf', path, start - offset))
            elif 'x' in p[1] and not path:
                jit.append((start, end, 'jit', '', None))
        # Windows images first: Wine maps their sections as anonymous memory inside the image's range
        self.tables = [self.table(pe), self.table(elf), self.table(jit)]
        self.syms, self.loaded = syms, {}

    @staticmethod
    def table(ranges):
        ranges.sort()
        return [r[0] for r in ranges], ranges

    def find(self, a):
        """('pe', Pe) / ('elf', Elf) / ('jit', None) / ('kernel', None) / ('other', None)."""
        if a >= 0xffff800000000000:
            return 'kernel', None
        for starts, ranges in self.tables:
            i = bisect.bisect_right(starts, a) - 1
            if i < 0 or a >= ranges[i][1]:
                continue
            start, _, kind, path, bias = ranges[i]
            if kind == 'jit':
                return kind, None
            key = (path, start)
            if key not in self.loaded:
                if kind == 'pe':
                    name = os.path.basename(path)
                    self.loaded[key] = Pe(path, start, load_publics(self.syms[name]) if name in self.syms else None)
                else:
                    self.loaded[key] = Elf(path, bias)
            return kind, self.loaded[key]
        return 'other', None

    def after_call(self, a):
        """Is a a return address: in Windows code right after a call, or in C# code (unverifiable)?"""
        kind, mod = self.find(a)
        if kind == 'jit':
            return True
        if kind != 'pe':
            return False
        b = mod.bytes(a - mod.base - 7, 7)
        if len(b) < 7:
            return False
        if b[2] == 0xe8:  # call rel32
            return True
        for n in range(2, 8):  # call r/m64 (FF /2) of every length ending right at a
            i = 7 - n
            if 0x40 <= b[i] <= 0x4f:
                i += 1
            if i + 1 >= 7 or b[i] != 0xff or (b[i + 1] >> 3) & 7 != 2:
                continue
            modrm = b[i + 1]
            mod, rm, size = modrm >> 6, modrm & 7, i + 2
            if mod != 3 and rm == 4:  # SIB byte
                if i + 2 >= 7:
                    continue
                size += 1
                if mod == 0 and b[i + 2] & 7 == 5:
                    size += 4
            elif mod == 0 and rm == 5:  # [rip+disp32]
                size += 4
            size += {1: 1, 2: 4}.get(mod, 0)
            if size == 7:
                return True
        return False


def load_publics(path):
    out = []
    for line in open(path, errors='replace'):
        p = line.split(' ', 1)
        if len(p) == 2:
            try:
                out.append((int(p[0], 16), p[1].strip()))
            except ValueError:
                pass
    return out


# Windows register numbers in unwind codes: rax rcx rdx rbx rsp rbp rsi rdi r8..r15.
# perf's x86 user registers, by bit: ax bx cx dx si di bp sp ip flags cs ss ds es fs gs r8..r15.
PERF_TO_WIN = {0: 0, 1: 3, 2: 1, 3: 2, 4: 6, 5: 7, 6: 5, 7: 4, **{16 + i: 8 + i for i in range(8)}}
RSP, RBP = 4, 5
OP_SIZE = {0: 1, 2: 1, 3: 1, 4: 2, 5: 3, 6: 2, 7: 3, 8: 2, 9: 3, 10: 1}


def samples(path, names=None):
    """Yields (ip, user ip, registers in Windows order, stack bytes, thread id) per sample; fills
    names (thread id -> thread name) from perf's COMM records when given."""
    f = open(path, 'rb')
    m = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    if m[:8] != b'PERFILE2':
        sys.exit(f'{path}: not a perf.data file (record without -z / pipe mode)')
    attr_size = u64(m, 16)
    attrs_off, attrs_size, data_off, data_size = struct.unpack_from('<QQQQ', m, 24)
    attrs = [m[attrs_off + i * attr_size:attrs_off + (i + 1) * attr_size] for i in range(attrs_size // attr_size)]
    by_id, main = {}, None
    for a in attrs:
        if u64(a, 24) & (1 << 13):  # STACK_USER
            main = a
        ids_off, ids_size = struct.unpack_from('<QQ', a, attr_size - 16)
        for k in range(ids_size // 8):
            by_id[u64(m, ids_off + 8 * k)] = a
    if main is None:
        sys.exit('no event with stack copies: record with --call-graph dwarf')
    for a in attrs:
        if u64(a, 24) & (1 << 4) or u64(a, 24) & (1 << 10) or u64(a, 24) & (1 << 11):
            sys.exit('READ/RAW/BRANCH_STACK sample fields aren\'t handled')
    pos, end = data_off, data_off + data_size
    while pos + 8 <= end:
        kind, size = u32(m, pos), u16(m, pos + 6)
        if size == 0:
            break
        if kind == 3 and names is not None:  # COMM: pid, tid, name
            tid = u32(m, pos + 12)
            raw = m[pos + 16:pos + size]
            names[tid] = raw[:raw.index(b'\0')].decode(errors='replace') if b'\0' in raw else raw.decode(errors='replace')
        if kind == 9:
            st0 = u64(main, 24)
            o = pos + 8
            attr = main
            if st0 & (1 << 16):  # IDENTIFIER
                attr = by_id.get(u64(m, o), main)
                o += 8
            st, mask = u64(attr, 24), u64(attr, 80)
            if not st & (1 << 13):
                pos += size
                continue
            ip = tid = None
            if st & 1: ip = u64(m, o); o += 8
            if st & (1 << 1): tid = u32(m, o + 4); o += 8
            if st & (1 << 2): o += 8  # time
            if st & (1 << 3): o += 8  # addr
            if st & (1 << 6): o += 8  # id
            if st & (1 << 9): o += 8  # stream id
            if st & (1 << 7): o += 8  # cpu
            if st & (1 << 8): o += 8  # period
            if st & (1 << 5): o += 8 + 8 * u64(m, o)  # kernel callchain
            regs = [None] * 16
            uip = None
            if st & (1 << 12):
                abi = u64(m, o); o += 8
                if abi:
                    for bit in range(64):
                        if mask >> bit & 1:
                            v = u64(m, o); o += 8
                            if bit in PERF_TO_WIN:
                                regs[PERF_TO_WIN[bit]] = v
                            elif bit == 8:
                                uip = v
            n = u64(m, o); o += 8
            stack = m[o:o + n]
            o += n
            if n:
                stack = stack[:u64(m, o)]  # dyn_size: bytes actually copied
            if uip is not None and regs[RSP] is not None:
                yield ip, uip, regs, stack, tid
        pos += size


def epilog(pe, rva, regs, rd):
    """If rva is inside an epilog (add/lea rsp, pops, ret), finish it and return the return address."""
    b = pe.bytes(rva, 64)
    i, r = 0, regs[:]
    if b[i:i + 3] == b'\x48\x83\xc4':
        r[RSP] += struct.unpack_from('<b', b, i + 3)[0]; i += 4
    elif b[i:i + 3] == b'\x48\x81\xc4':
        r[RSP] += struct.unpack_from('<i', b, i + 3)[0]; i += 7
    elif b[i:i + 3] == b'\x48\x8d\x65' and r[RBP] is not None:
        r[RSP] = r[RBP] + struct.unpack_from('<b', b, i + 3)[0]; i += 4
    elif b[i:i + 3] == b'\x48\x8d\xa5' and r[RBP] is not None:
        r[RSP] = r[RBP] + struct.unpack_from('<i', b, i + 3)[0]; i += 7
    while i < len(b):
        if 0x58 <= b[i] <= 0x5f and b[i] != 0x5c:
            reg = [0, 1, 2, 3, 4, 5, 6, 7][b[i] - 0x58]
            r[reg] = rd(r[RSP]); r[RSP] += 8; i += 1
        elif b[i] == 0x41 and i + 1 < len(b) and 0x58 <= b[i + 1] <= 0x5f:
            r[8 + b[i + 1] - 0x58] = rd(r[RSP]); r[RSP] += 8; i += 2
        else:
            break
    if i < len(b) and b[i] in (0xc3, 0xc2) or b[i:i + 1] == b'\xe9' or b[i:i + 2] == b'\x48\xff' or b[i:i + 2] == b'\xff\x25':
        if i == 0 and b[0] != 0xc3:
            return None  # a bare jmp isn't an epilog
        ret = rd(r[RSP])
        r[RSP] += 8
        regs[:] = r
        return ret
    return None


def pe_step(pe, rva, regs, rd, leaf):
    """Unwinds one frame with the function's unwind codes (as RtlVirtualUnwind). Returns the caller's ip."""
    fn = pe.function(rva)
    if fn is None:  # leaf function without unwind info: return address on top
        ret = rd(regs[RSP]); regs[RSP] += 8
        return ret
    if leaf:
        ret = epilog(pe, rva, regs, rd)
        if ret is not None:
            return ret
    begin, _, info = fn
    offset = rva - begin
    d = pe.d
    while True:
        while info & 1:  # points at another RUNTIME_FUNCTION
            begin, _, info = struct.unpack_from('<III', d, pe.off(info & ~1))
        o = pe.off(info)
        flags, prolog, count, frame_byte = d[o] >> 3, d[o + 1], d[o + 2], d[o + 3]
        freg, foff = frame_byte & 15, frame_byte >> 4
        codes = [u16(d, o + 4 + 2 * k) for k in range(count)]
        frame = regs[RSP]
        if freg:
            # Has the prolog set the frame register yet? Codes take 1-3 slots; only first slots are codes.
            done, k = offset >= prolog, 0
            while k < count and not done:
                c = codes[k]
                op, opinfo = (c >> 8) & 15, c >> 12
                done = op == 3 and offset >= c & 0xff
                k += (2 if opinfo == 0 else 3) if op == 1 else OP_SIZE.get(op, 1)
            if done:
                if regs[freg] is None:
                    return None
                frame = regs[freg] - foff * 16
        k = 0
        while k < count:
            c = codes[k]
            at, op, opinfo = c & 0xff, (c >> 8) & 15, c >> 12
            size = (2 if opinfo == 0 else 3) if op == 1 else OP_SIZE.get(op, 1)
            if offset >= at:
                if op == 0:
                    regs[opinfo] = rd(regs[RSP]); regs[RSP] += 8
                elif op == 1:
                    regs[RSP] += codes[k + 1] * 8 if opinfo == 0 else codes[k + 1] | codes[k + 2] << 16
                elif op == 2:
                    regs[RSP] += opinfo * 8 + 8
                elif op == 3:
                    regs[RSP] = frame
                elif op == 4:
                    regs[opinfo] = rd(frame + codes[k + 1] * 8)
                elif op == 5:
                    regs[opinfo] = rd(frame + (codes[k + 1] | codes[k + 2] << 16))
                elif op == 10:  # machine frame (an interrupt or exception)
                    sp = regs[RSP] + (8 if opinfo else 0)
                    ret = rd(sp)
                    regs[RSP] = rd(sp + 24)
                    return ret
            k += size
        if not flags & 4:  # no chained info
            break
        begin, _, info = struct.unpack_from('<III', d, o + 4 + 2 * ((count + 1) & ~1))
        offset = 0xffffffff  # a chained entry's codes all apply
    if regs[RSP] is None:
        return None
    ret = rd(regs[RSP]); regs[RSP] += 8
    return ret


def wine_syscall_caller(stack, mods):
    """Wine's Linux side runs on its own stack, topped by the frame its syscall dispatcher saved:
    rip, cs (0x33), flags, rsp, ss (0x2b). Returns the Windows rip it will return to."""
    for k in range(8, len(stack) // 8 - 4):
        if u64(stack, 8 * k) == 0x33 and u64(stack, 8 * k + 24) == 0x2b:
            rip = u64(stack, 8 * k - 8)
            if mods.find(rip)[0] == 'pe':
                return rip
    return None


def unwind(ip, uip, regs, stack, mods):
    """Frames from the leaf up (addresses), how the walk ended, and whether any step had to scan."""
    sp0 = regs[RSP]

    def rd(a):
        if a is None:
            return None
        o = a - sp0
        return u64(stack, o) if 0 <= o <= len(stack) - 8 else None

    def scan(sp):
        """The first slot at or above sp holding a return address (C# frames without a frame
        pointer, Linux code running on the game's stack): (address, the caller's rsp)."""
        o = max(0, (sp or sp0) - sp0)
        for k in range(o - o % 8, min(len(stack) - 8, o + 8192), 8):
            v = u64(stack, k)
            if v > 0x10000 and mods.after_call(v):
                return v, sp0 + k + 8
        return None

    frames = ['[kernel]'] if ip is not None and ip >= 0xffff800000000000 else []
    pc, leaf, scanned = uip, True, False
    for _ in range(300):
        if not pc:
            return frames, 'top', scanned
        frames.append(pc)
        kind, mod = mods.find(pc)
        if kind == 'pe':
            r = regs[:]
            nxt = pe_step(mod, (pc if leaf else pc - 1) - mod.base, r, rd, leaf)
            if nxt is None and r[RSP] is not None and r[RSP] - sp0 >= len(stack) - 64:
                return frames, 'stack copy ended', scanned
            if nxt == 0 or nxt is not None and mods.after_call(nxt):
                regs[:] = r
                pc = nxt
            else:
                found = scan(regs[RSP])
                if not found:
                    return frames, 'lost', scanned
                pc, regs[RSP], scanned = found[0], found[1], True
        elif kind == 'jit':
            bp = regs[RBP]
            ret = rd(bp + 8) if bp and 0 <= bp - regs[RSP] < 0x100000 else None
            if ret is not None and mods.after_call(ret):
                regs[RSP], regs[RBP], pc = bp + 16, rd(bp), ret
            else:
                found = scan(regs[RSP])
                if not found:
                    return frames, 'lost in C#', scanned
                pc, regs[RSP], scanned = found[0], found[1], True
        else:
            if leaf and kind == 'elf':
                caller = wine_syscall_caller(stack, mods)
                if caller is not None:  # Wine's Linux side on its own stack: one Windows caller
                    frames.append(caller)
                    return frames, 'wine syscall', scanned
            found = scan(regs[RSP])
            if not found:
                return frames, 'lost', scanned
            pc, regs[RSP], scanned = found[0], found[1], True
        leaf = False
    return frames, 'too deep', scanned


def name_jit(addresses, bepinex):
    """Asks the game's Debug build (JitNames) to name C# addresses; same game process only."""
    out = {}
    if not bepinex or not addresses:
        return out
    request, reply = os.path.join(bepinex, 'smoothie_resolve.txt'), os.path.join(bepinex, 'smoothie_resolved.txt')
    if os.path.exists(reply):
        os.remove(reply)
    with open(request, 'w') as f:
        f.write('\n'.join(f'{a:x}' for a in sorted(addresses)))
    for _ in range(40):  # polled every 5 s in raid
        if os.path.exists(reply) and not os.path.exists(request):
            break
        time.sleep(0.5)
    else:
        print('C# names: no answer from the game (Debug build, in raid?)', file=sys.stderr)
        return out
    for line in open(reply, errors='replace'):
        p = line.split(' ', 1)
        if len(p) == 2 and p[1].strip() != '?':
            out[int(p[0], 16)] = 'C#!' + p[1].strip()
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('data'); ap.add_argument('maps')
    ap.add_argument('--syms', action='append', default=[], help='DLL=publics.txt')
    ap.add_argument('--names', help='BepInEx folder of a running Debug build, to name C# frames')
    ap.add_argument('--focus', action='append', default=[])
    ap.add_argument('--tree', action='store_true')
    ap.add_argument('--folded')
    ap.add_argument('--top', type=int, default=40)
    ap.add_argument('--min', type=float, default=0.5, help='tree/focus: hide below this %%')
    ap.add_argument('--check', action='store_true')
    ap.add_argument('--threads', action='store_true', help='recorded with -p: group stacks under their thread (job workers merged)')
    ap.add_argument('--tid', type=int, help='recorded with -p: only this thread (the main thread\'s id is the game\'s pid)')
    a = ap.parse_args()
    mods = Modules(a.maps, dict(s.split('=', 1) for s in a.syms))

    stacks, hows, tids, ends, scans = [], [], [], collections.Counter(), 0
    thread_names = {}
    for ip, uip, regs, stack, tid in samples(a.data, thread_names):
        if a.tid is not None and tid != a.tid:
            continue
        tids.append(tid)
        frames, how, scanned = unwind(ip, uip, regs, stack, mods)
        stacks.append(frames)
        hows.append(how)
        ends[how] += 1
        scans += scanned
    total = len(stacks)
    if not total:
        sys.exit('no samples')

    jit = {f for s in stacks for f in s if isinstance(f, int) and mods.find(f)[0] == 'jit'}
    jit_names = name_jit(jit, a.names)
    names = {}

    def name(f):
        if isinstance(f, str):
            return f
        if f not in names:
            kind, mod = mods.find(f)
            names[f] = (jit_names.get(f, f'C#@{f:x}') if kind == 'jit' else '[kernel]' if kind == 'kernel'
                        else f'?@{f:x}' if kind == 'other' else mod.symbol(f - mod.base if kind == 'pe' else f))
        return names[f]

    named = [[name(f) for f in s] for s in stacks]  # leaf first
    if a.threads:
        threads = collections.Counter()
        for s, tid in zip(named, tids):
            t = thread_names.get(tid, str(tid)).rstrip('0123456789 ').strip() or str(tid)
            threads[t] += 1
            s.append(f'[thread {t}]')
        print('samples per thread: ' + ', '.join(f'{t} {k}' for t, k in threads.most_common(12)))
    # Above Unity's player loop are the same few startup frames in every sample: reaching it is complete.
    done = lambda s: any(n.endswith('!PlayerLoop') or n.startswith('EscapeFromTarkov.exe') or n.endswith('!RtlUserThreadStart')
                         or n.endswith('!BaseThreadInitThunk') for n in s)
    # Unity's render path has huge stack frames: many stacks are deeper than perf's 64 KB copy. Their
    # frames are right as far as they go; give each the path its last frame has in complete stacks.
    above = {}
    for s in named:
        if done(s):
            for i, n in enumerate(s):
                above.setdefault(n, collections.Counter())[tuple(s[i + 1:])] += 1
    grafted = 0
    for s, how in zip(named, hows):
        if how == 'stack copy ended' and not done(s) and s[-1] in above and not s[-1].startswith('[thread'):
            s.extend(above[s[-1]].most_common(1)[0][0])
            grafted += 1
    # The rest can't be placed (e.g. under Camera::CustomRender, whose frame is ~1 MB): group them.
    for s, how in zip(named, hows):
        if how == 'stack copy ended' and not done(s):
            s.append('[deeper than the stack copy]')
    complete = sum(1 for s in named if done(s))
    deep = sum(1 for s in named if s[-1] == '[deeper than the stack copy]')
    print(f'stacks deeper than the copy: given their usual path above {100 * grafted / total:.1f}%, '
          f'grouped as "[deeper than the stack copy]" {100 * deep / total:.1f}%')
    print(f'{total} samples; reached the player loop: {100 * complete / total:.1f}%; '
          f'needed a stack scan somewhere: {100 * scans / total:.1f}%')
    for how, k in ends.most_common():
        print(f'  ended {how}: {100 * k / total:.1f}%')

    if a.check:
        ok = complete / total >= 0.6
        print('check:', 'ok' if ok else 'FAIL', '(needs >= 60% of stacks to reach the player loop)')
        sys.exit(0 if ok else 1)

    incl, self_ = collections.Counter(), collections.Counter()
    for s in named:
        self_[s[0]] += 1
        for n in set(s):
            incl[n] += 1
    print(f'\n{"incl":>6} {"self":>6}  function (share of samples)')
    for n, k in incl.most_common(a.top):
        print(f'{100 * k / total:6.2f} {100 * self_[n] / total:6.2f}  {n[:150]}')

    for text in a.focus:
        hits = [n for n in incl if text.lower() in n.lower()]
        for n in sorted(hits, key=lambda h: -incl[h])[:3]:
            callers, callees = collections.Counter(), collections.Counter()
            for s in named:
                if n not in s:
                    continue
                i = s.index(n)
                callers[' <- '.join(s[i + 1:i + 4]) or '(top)'] += 1
                if i > 0:
                    callees[s[i - 1]] += 1
            print(f'\n== {n}: {100 * incl[n] / total:.2f}% inclusive, {100 * self_[n] / total:.2f}% self')
            print('  called from (3 levels):')
            for c, k in callers.most_common(12):
                print(f'  {100 * k / total:6.2f}  {c[:220]}')
            print('  calls:')
            for c, k in callees.most_common(15):
                print(f'  {100 * k / total:6.2f}  {c[:150]}')

    if a.tree:
        root = {}
        for s in named:
            node = root
            for n in reversed(s):
                node = node.setdefault(n, [0, {}])
                node[0] += 1
                node = node[1]
        print('\n== top-down (from the thread start)')

        def show(node, depth):
            for n, (k, kids) in sorted(node.items(), key=lambda x: -x[1][0]):
                if 100 * k / total < a.min:
                    continue
                print(f'{100 * k / total:6.2f} {"  " * depth}{n[:140]}')
                show(kids, depth + 1)
        show(root, 0)

    if a.folded:
        folded = collections.Counter(';'.join(reversed(s)) for s in named)
        with open(a.folded, 'w') as f:
            for k, v in folded.items():
                f.write(f'{k} {v}\n')
        print(f'\nfolded stacks: {a.folded}')


if __name__ == '__main__':
    main()
