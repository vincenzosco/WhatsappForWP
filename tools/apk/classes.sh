#!/bin/bash
# Read the four types out of the APK, write their class files, then prove that
# the JVM accepts them: javap for the shape, Class.forName for the loading.
set +e
B=/root/bionic
W=$(cd "$(dirname "$0")/../.." && pwd)
APK=${APK:?set APK to the path of WhatsApp.apk}

command -v python3 > /dev/null || { echo "python3 missing"; exit 1; }

echo "== what the APK says"
python3 "$W/tools/apk/dexinfo.py" "$APK" > "$B/lx.json" || echo "dexinfo failed"
python3 - "$B/lx.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
for name, row in d.items():
    print("%-8s defined_in=%s access=%s super=%s ifaces=%s methods=%d" % (
        name, row["defined_in"], hex(row["access"]) if row["access"] else None,
        row["super"], row["interfaces"], len(row["methods"])))
    for m in row["methods"][:8]:
        print("        %s %s access=%s code=%s" % (m[0], m[1], hex(m[2]), m[3]))
PY

echo "== class files"
rm -rf "$B/classes"
python3 "$W/tools/apk/mkclass.py" "$B/lx.json" "$B/classes" || exit 1

echo "== javap"
for f in "$B"/classes/X/*.class; do
    javap -p "$f" 2>&1 | head -12
done

echo "== load in a JVM"
cat > "$B/classes/Load.java" <<'JAVA'
public class Load {
    public static void main(String[] args) throws Exception {
        String[] names = {"X.022", "X.01J", "X.00I", "X.01S"};
        for (String n : names) {
            Class<?> c = Class.forName(n);
            System.out.println(n + " -> loaded, interface=" + c.isInterface()
                + " abstract=" + java.lang.reflect.Modifier.isAbstract(c.getModifiers())
                + " methods=" + c.getDeclaredMethods().length);
        }
    }
}
JAVA
cd "$B/classes" && javac Load.java 2>&1 | head -5 && java Load 2>&1 | head -10

