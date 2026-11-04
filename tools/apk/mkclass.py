"""Write class files for names javac cannot spell.

The four types are definitions in the APK (read from the DEX, not guessed), so
their kind and method declarations are copied: X/022 is an interface, the other
three are public classes. No method body is written — nothing here can implement
an obfuscated class, and nothing needs to: the native code only passes these
around as types.

A descriptor can name another class (X/01S returns X/01R, takes X/01V and
X/01o). The JVM resolves those when the methods are reflected, so every
referenced X/ type that is not one of the four gets an empty stub. Without them
the class loads and then throws NoClassDefFoundError at the first getMethods.
"""
import json
import os
import re
import struct
import sys

TYPE_RE = re.compile(r"L([A-Za-z0-9_$/]+);")


def u2(value):
    return struct.pack(">H", value)


class Pool(object):
    def __init__(self):
        self.items = []

    def utf8(self, text):
        data = text.encode("utf-8")
        self.items.append(b"\x01" + u2(len(data)) + data)
        return len(self.items)

    def klass(self, name_index):
        self.items.append(b"\x07" + u2(name_index))
        return len(self.items)


def build(name, methods, is_interface, major=50):
    pool = Pool()
    this_class = pool.klass(pool.utf8(name))
    super_class = pool.klass(pool.utf8("java/lang/Object"))
    declared = [(pool.utf8(mname), pool.utf8(mdesc)) for mname, mdesc in methods]

    access = 0x0601 if is_interface else 0x0421  # public (+ abstract, + interface)

    out = bytearray()
    out += b"\xca\xfe\xba\xbe" + u2(0) + u2(major)
    out += u2(len(pool.items) + 1) + b"".join(pool.items)
    out += u2(access) + u2(this_class) + u2(super_class)
    out += u2(0)  # no interfaces: a name that may not exist here is worse than none
    out += u2(0)  # no fields: only the type is needed
    out += u2(len(declared))
    for name_index, desc_index in declared:
        out += u2(0x0401) + u2(name_index) + u2(desc_index) + u2(0)
    out += u2(0)  # no attributes

    return bytes(out)


def write_class(out_dir, name, methods, is_interface):
    path = os.path.join(out_dir, name + ".class")
    directory = os.path.dirname(path)
    if directory and not os.path.isdir(directory):
        os.makedirs(directory)
    with open(path, "wb") as f:
        f.write(build(name, methods, is_interface))

    return path


def main():
    info = json.load(open(sys.argv[1]))
    out_dir = sys.argv[2]

    written = []
    referenced = set()

    for descriptor, row in sorted(info.items()):
        # The DEX keys are descriptors; a class file wants the internal name.
        name = descriptor[1:-1] if descriptor.startswith("L") else descriptor
        is_interface = bool((row.get("access") or 0) & 0x0200)

        methods = []
        seen = set()
        for mname, mdesc, _access, _has_code in row.get("methods") or []:
            if mname in ("<init>", "<clinit>"):
                continue
            key = (mname, mdesc)
            if key in seen:
                continue
            seen.add(key)
            methods.append(key)
            referenced.update(TYPE_RE.findall(mdesc))

        path = write_class(out_dir, name, methods, is_interface)
        written.append((name, "interface" if is_interface else "class",
                        len(methods), path))

    # Everything the declarations point at, so the JVM can resolve them.
    stubs = []
    for name in sorted(referenced):
        if name in ("java/lang/Object",) or name.startswith("java/") \
                or name.startswith("android/") or name.startswith("["):
            continue
        if not name.startswith("X/"):
            continue
        path = os.path.join(out_dir, name + ".class")
        if os.path.exists(path):
            continue
        write_class(out_dir, name, [], False)
        stubs.append(name)

    for name, kind, count, path in written:
        print("%-8s %-8s %2d method(s)  %s" % (name, kind, count, path))
    if stubs:
        print("stubs: " + ", ".join(stubs))


main()
