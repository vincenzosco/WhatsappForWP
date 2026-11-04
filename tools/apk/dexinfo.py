"""Read class_def entries for four obfuscated names straight out of the APK's DEX.

The names are used as parameter types by the native methods, so what matters is
whether the APK defines them, and as what: the DEX says it, this does not guess.
"""
import json
import struct
import sys
import zipfile

# Type ids hold descriptors, so the names carry their L and ; here.
TARGETS = ["LX/022;", "LX/01J;", "LX/00I;", "LX/01S;"]
SANITY = "Lcom/facebook/superpack/AssetDecompressor;"


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

    def proto_desc(self, i):
        _, ret, params_off = struct.unpack_from("<III", self.d, self.proto_ids_off + 12 * i)
        args = []
        if params_off:
            n = struct.unpack_from("<I", self.d, params_off)[0]
            for k in range(n):
                ti = struct.unpack_from("<H", self.d, params_off + 4 + 2 * k)[0]
                args.append(self.type(ti))
        return "(" + "".join(args) + ")" + self.type(ret)

    def method(self, i):
        _, proto, name = struct.unpack_from("<HHI", self.d, self.method_ids_off + 8 * i)
        return self.string(name), self.proto_desc(proto)

    def type_list(self, off):
        if not off:
            return []
        n = struct.unpack_from("<I", self.d, off)[0]
        return [self.type(struct.unpack_from("<H", self.d, off + 4 + 2 * k)[0])
                for k in range(n)]

    def classes(self):
        for i in range(self.class_defs_size):
            base = self.class_defs_off + 32 * i
            (class_idx, access, super_idx, ifaces_off, _source, _annot,
             class_data_off, _static) = struct.unpack_from("<IIIIIIII", self.d, base)
            name = self.type(class_idx)
            sup = self.type(super_idx) if super_idx != 0xFFFFFFFF else None
            ifaces = self.type_list(ifaces_off)
            methods = []
            if class_data_off:
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
                        acc, off = uleb(self.d, off)
                        code, off = uleb(self.d, off)
                        idx += diff
                        mname, mdesc = self.method(idx)
                        methods.append([mname, mdesc, acc, code != 0])
            yield name, access, sup, ifaces, methods


def main():
    apk = sys.argv[1]
    found = {}
    parsed = 0
    saw_sanity = False
    with zipfile.ZipFile(apk) as z:
        for entry in sorted(z.namelist()):
            if not (entry.startswith("classes") and entry.endswith(".dex")):
                continue
            try:
                dex = Dex(z.read(entry))
                rows = list(dex.classes())
            except Exception as e:  # a dex this parser cannot read is reported, not hidden
                print("skip %s: %s" % (entry, e), file=sys.stderr)
                continue
            parsed += len(rows)
            for name, access, sup, ifaces, methods in rows:
                if name == SANITY:
                    saw_sanity = True
                if name in TARGETS:
                    found.setdefault(name, {"defined_in": [], "access": access,
                                            "super": sup, "interfaces": ifaces,
                                            "methods": methods})
                    found[name]["defined_in"].append(entry)

    for target in TARGETS:
        found.setdefault(target, {"defined_in": [], "access": None, "super": None,
                                  "interfaces": [], "methods": []})

    print("parsed %d class_def(s), saw %s: %s" % (parsed, SANITY, saw_sanity),
          file=sys.stderr)
    print(json.dumps(found, indent=1))


main()
