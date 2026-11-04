"""Find the call sites of AssetDecompressor.decompress/decompress_with_ref.

This reads bytecode, not decompiled source: the code items are decoded with the
Dalvik instruction sizes so the invoke instructions are found where they really
are, and the const-string values in front of them are reported as the arguments
the code is about to pass.

The expensive part (decoding every method of 114k classes) is skipped by a first
pass that looks for the two-unit shape of an invoke whose method index is one of
the targets.
"""
import json
import struct
import sys
import zipfile

TARGET_CLASS = "Lcom/facebook/superpack/AssetDecompressor;"
TARGET_NAMES = [
    "decompress", "decompress_with_ref", "decompress_from_so",
    "decompress_range", "decompress_range_from_so", "decompress_legacy",
]

# Size in 16-bit code units for every opcode. Data is not here: payloads are
# handled separately in decode().
SIZES = []
for _op, _size in (
        (0x00, 1), (0x01, 1), (0x02, 2), (0x03, 3), (0x04, 1), (0x05, 2),
        (0x06, 3), (0x07, 1), (0x08, 2), (0x09, 3), (0x0a, 1), (0x0b, 1),
        (0x0c, 1), (0x0d, 1), (0x0e, 1), (0x0f, 1), (0x10, 2), (0x11, 1),
        (0x12, 1), (0x13, 2), (0x14, 3), (0x15, 2), (0x16, 2), (0x17, 3),
        (0x18, 5), (0x19, 2), (0x1a, 2), (0x1b, 3), (0x1c, 2), (0x1d, 2),
        (0x1e, 2), (0x1f, 2), (0x20, 2), (0x21, 1), (0x22, 2), (0x23, 2),
        (0x24, 3), (0x25, 3), (0x26, 3), (0x27, 1), (0x28, 1), (0x29, 2),
        (0x2a, 3), (0x2b, 2), (0x2c, 2), (0x2d, 1), (0x2e, 1), (0x2f, 1),
        (0x30, 1), (0x31, 1), (0x32, 2), (0x33, 2), (0x34, 2), (0x35, 2),
        (0x36, 2), (0x37, 2), (0x38, 2), (0x39, 2), (0x3a, 2), (0x3b, 2),
        (0x3c, 2), (0x3d, 2), (0x3e, 1), (0x3f, 1), (0x40, 1), (0x41, 1),
        (0x42, 1), (0x43, 1), (0x44, 2), (0x45, 2), (0x46, 2), (0x47, 2),
        (0x48, 2), (0x49, 2), (0x4a, 2), (0x4b, 2), (0x4c, 2), (0x4d, 2),
        (0x4e, 2), (0x4f, 2), (0x50, 2), (0x51, 2), (0x52, 2), (0x53, 2),
        (0x54, 2), (0x55, 2), (0x56, 2), (0x57, 2), (0x58, 2), (0x59, 2),
        (0x5a, 2), (0x5b, 2), (0x5c, 2), (0x5d, 2), (0x5e, 2), (0x5f, 2),
        (0x60, 2), (0x61, 2), (0x62, 2), (0x63, 2), (0x64, 2), (0x65, 2),
        (0x66, 2), (0x67, 2), (0x68, 2), (0x69, 2), (0x6a, 2), (0x6b, 2),
        (0x6c, 2), (0x6d, 2), (0x6e, 3), (0x6f, 3), (0x70, 3), (0x71, 3),
        (0x72, 3), (0x73, 1), (0x74, 3), (0x75, 3), (0x76, 3), (0x77, 3),
        (0x78, 3), (0x79, 1), (0x7a, 1)):
    while len(SIZES) <= _op:
        SIZES.append(1)
    SIZES[_op] = _size

for _op in range(0x7b, 0x90):
    while len(SIZES) <= _op:
        SIZES.append(1)
    SIZES[_op] = 1
for _op in range(0x90, 0xb0):
    SIZES.append(2) if len(SIZES) <= _op else None
for _op in range(0xb0, 0xd0):
    if len(SIZES) <= _op:
        SIZES.append(1)
for _op in range(0xd0, 0xd8):
    if len(SIZES) <= _op:
        SIZES.append(2)
for _op in range(0xd8, 0xe3):
    if len(SIZES) <= _op:
        SIZES.append(2)
while len(SIZES) < 0x100:
    SIZES.append(1)
SIZES[0xfa] = 4
SIZES[0xfb] = 4
SIZES[0xfc] = 3
SIZES[0xfd] = 3
SIZES[0xfe] = 2
SIZES[0xff] = 2

INVOKES = set(range(0x6E, 0x73)) | set(range(0x74, 0x79)) | {0xFA, 0xFB, 0xFC, 0xFD}


def uleb(data, off):
    result = 0
    shift = 0
    while True:
        b = data[off]
        off += 1
        result |= (b & 0x7F) << shift
        if not b & 0x80:
            return result, off
        shift += 7


class Dex(object):
    def __init__(self, data):
        self.d = data
        (self.string_ids_size, self.string_ids_off) = struct.unpack_from("<II", data, 0x38)
        (self.type_ids_size, self.type_ids_off) = struct.unpack_from("<II", data, 0x40)
        (self.proto_ids_size, self.proto_ids_off) = struct.unpack_from("<II", data, 0x48)
        (self.method_ids_size, self.method_ids_off) = struct.unpack_from("<II", data, 0x58)
        (self.class_defs_size, self.class_defs_off) = struct.unpack_from("<II", data, 0x60)

    def string(self, i):
        off = struct.unpack_from("<I", self.d, self.string_ids_off + 4 * i)[0]
        _, pos = uleb(self.d, off)
        end = self.d.index(b"\x00", pos)
        return self.d[pos:end].decode("utf-8", "replace")

    def type(self, i):
        return self.string(struct.unpack_from("<I", self.d, self.type_ids_off + 4 * i)[0])

    def proto(self, i):
        _, ret, params_off = struct.unpack_from("<III", self.d, self.proto_ids_off + 12 * i)
        args = []
        if params_off:
            n = struct.unpack_from("<I", self.d, params_off)[0]
            for k in range(n):
                args.append(self.type(struct.unpack_from("<H", self.d, params_off + 4 + 2 * k)[0]))
        return "(" + "".join(args) + ")" + self.type(ret)

    def method(self, i):
        cls, proto, name = struct.unpack_from("<HHI", self.d, self.method_ids_off + 8 * i)
        return self.type(cls), self.string(name), self.proto(proto)

    def target_method_ids(self):
        ids = {}
        for i in range(self.method_ids_size):
            cls, name, _desc = self.method(i)
            if cls == TARGET_CLASS and name in TARGET_NAMES:
                ids[i] = name
        return ids

    def code_items(self):
        """(class, method, descriptor, insns bytes) for every method with code."""
        for c in range(self.class_defs_size):
            base = self.class_defs_off + 32 * c
            (class_idx, _access, _super, _ifaces, _src, _annot,
             class_data_off, _static) = struct.unpack_from("<IIIIIIII", self.d, base)
            class_name = self.type(class_idx)
            if not class_data_off:
                continue

            off = class_data_off
            static_fields, off = uleb(self.d, off)
            instance_fields, off = uleb(self.d, off)
            direct, off = uleb(self.d, off)
            virtual, off = uleb(self.d, off)
            for _ in range(static_fields + instance_fields):
                _, off = uleb(self.d, off)
                _, off = uleb(self.d, off)

            for count in (direct, virtual):
                idx = 0
                for _ in range(count):
                    diff, off = uleb(self.d, off)
                    _acc, off = uleb(self.d, off)
                    code_off, off = uleb(self.d, off)
                    idx += diff
                    if not code_off:
                        continue
                    mclass, name, desc = self.method(idx)
                    registers, ins_size, _, _, _, insns_size = struct.unpack_from(
                        "<HHHHII", self.d, code_off)
                    start = code_off + 16
                    insns = struct.unpack_from("<%dH" % insns_size, self.d, start)
                    yield mclass, name, desc, ins_size, insns


def decode(insns):
    """Yield (pc, opcode, length, info) for real instructions."""
    pc = 0
    n = len(insns)
    while pc < n:
        unit = insns[pc]
        op = unit & 0xFF

        # Pseudo-instructions: nop followed by a payload identifier.
        if op == 0x00 and pc + 1 < n and insns[pc + 1] in (0x0100, 0x0200, 0x0300):
            ident = insns[pc + 1]
            size = insns[pc + 2]
            if ident == 0x0100:
                length = 4 + size * 2
            elif ident == 0x0200:
                length = 2 + size * 4
            else:
                width = insns[pc + 3]
                length = 4 + (size * width + 1) // 2
            yield pc, -1, length, None
            pc += length
            continue

        length = SIZES[op] if op < len(SIZES) else 1
        info = None
        if op == 0x1A:  # const-string
            info = ("string", (unit >> 8) & 0xFF, insns[pc + 1])
        elif op == 0x1B:  # const-string/jumbo
            info = ("string", (unit >> 8) & 0xFF,
                    insns[pc + 1] | (insns[pc + 2] << 16))
        elif op == 0x13:  # const/16
            info = ("int", (unit >> 8) & 0xFF, insns[pc + 1])
        elif op == 0x12:  # const/4
            info = ("int", (unit >> 8) & 0xF, (unit >> 12) & 0xF)
        elif op == 0x22:  # new-instance
            info = ("new", (unit >> 8) & 0xFF, insns[pc + 1])
        elif op in INVOKES:
            if op in (0x6E, 0x6F, 0x70, 0x71, 0x72, 0xFC):
                regs = [(insns[pc + 2] >> 8) & 0xF, (insns[pc + 2] >> 12) & 0xF,
                        insns[pc + 2] & 0xF, (insns[pc + 2] >> 4) & 0xF,
                        (unit >> 8) & 0xF]
                argc = (unit >> 12) & 0xF
                info = ("invoke", insns[pc + 1], regs[:argc])
            elif op in (0x74, 0x75, 0x76, 0x77, 0x78, 0xFD):
                argc = (unit >> 8) & 0xFF
                first = insns[pc + 2]
                info = ("invoke", insns[pc + 1], list(range(first, first + argc)))
            else:  # 45cc / 4rcc: polymorphic, method index at +3
                info = ("invoke", insns[pc + 3], [])
        yield pc, op, length, info
        pc += length


def main():
    apk = sys.argv[1]
    hits = []
    interesting_strings = set()

    with zipfile.ZipFile(apk) as z:
        for entry in sorted(z.namelist()):
            if not (entry.startswith("classes") and entry.endswith(".dex")):
                continue
            dex = Dex(z.read(entry))
            targets = dex.target_method_ids()
            if not targets:
                continue

            for i in range(dex.string_ids_size):
                s = dex.string(i)
                if ".spo" in s or ".pack" in s:
                    interesting_strings.add(s)

            for cls, name, desc, ins_size, insns in dex.code_items():
                # Cheap filter first: an invoke opcode followed by a target id.
                candidate = False
                for pc in range(len(insns) - 1):
                    if (insns[pc] & 0xFF) in INVOKES and insns[pc + 1] in targets:
                        candidate = True
                        break
                if not candidate:
                    continue

                decoded = list(decode(insns))
                all_strings = []
                for _, _, _, sinfo in decoded:
                    if sinfo and sinfo[0] == "string":
                        value = dex.string(sinfo[2])
                        if value not in all_strings:
                            all_strings.append(value)

                for k, (pc, op, length, info) in enumerate(decoded):
                    if not info or info[0] != "invoke":
                        continue
                    method_idx, regs = info[1], info[2]
                    if method_idx not in targets:
                        continue

                    # Walk back for constants written into the argument registers.
                    wanted = set(regs)
                    found = {}
                    for back in range(k - 1, max(k - 25, -1), -1):
                        _, _, _, cinfo = decoded[back]
                        if not cinfo:
                            continue
                        kind, reg, value = cinfo
                        if reg in wanted and reg not in found:
                            if kind == "string":
                                found[reg] = ("string", dex.string(value))
                            elif kind == "int":
                                found[reg] = ("int", value)
                            elif kind == "new":
                                found[reg] = ("new", dex.type(value))
                        if len(found) == len(wanted):
                            break

                    hits.append({
                        "dex": entry,
                        "caller": cls + "->" + name + desc,
                        "call": targets[method_idx],
                        "args": [[r, found.get(r, [None, None])[0], found.get(r, [None, None])[1]]
                                 for r in regs],
                        "strings": all_strings,
                    })

    print("== calls to AssetDecompressor")
    for hit in hits:
        print("%s\n  %s\n  in %s (%s)" % (hit["call"], ", ".join(
            "v%d=%s:%s" % (a[0], a[1], a[2]) for a in hit["args"]), hit["caller"], hit["dex"]))
        for s in hit["strings"][:20]:
            print("      str %r" % s)

    print("\n== strings in the DEX with .spo or .pack (%d)" % len(interesting_strings))
    for s in sorted(interesting_strings)[:40]:
        print("  " + s)


main()
