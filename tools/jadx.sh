#!/usr/bin/env sh
#
# tools/jadx.sh - run jadx with the JDK this project keeps for it.
#
# Why this exists: jadx's classes are Java 11 bytecode (class file 55.0) and the
# Java on this machine's PATH is Java 8, so every run answered
# `UnsupportedClassVersionError: jadx/cli/JadxCLI has been compiled by a more
# recent version of the Java Runtime`. The JDK it needs is not installed
# system-wide, so it lives in `.tools/jdk21` (a JRE 21, which is all jadx needs;
# `.tools/` is not in the repository) and this wrapper points JAVA_HOME at it.
#
# Install it once:
#   mkdir -p .tools/jdk21
#   curl -sSL -o .tools/jdk21.zip \
#     "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse"
#   unzip -q .tools/jdk21.zip -d .tools/jdk21
#
# Usage, from anywhere:
#   tools/jadx.sh --version
#   tools/jadx.sh --single-class com.whatsapp.calling.crypto.DefaultCryptoCallback \
#     -d .tools/apk/out /path/to/WhatsApp.apk
#
# The jar is the one jadx installed on this machine; JADX overrides it.

set -e

ROOT=$(cd "$(dirname "$0")/.." && pwd)

if [ -n "$JADX" ]; then
  JADX_BIN=$JADX
elif [ -x "$ROOT/.tools/jadx/bin/jadx" ]; then
  JADX_BIN="$ROOT/.tools/jadx/bin/jadx"
elif [ -x "$HOME/AppData/Local/jadx/bin/jadx" ]; then
  JADX_BIN="$HOME/AppData/Local/jadx/bin/jadx"
else
  JADX_BIN=$(command -v jadx || true)
fi

if [ -z "$JADX_BIN" ] || [ ! -x "$JADX_BIN" ]; then
  echo "jadx: no jadx launcher found - set JADX=/path/to/jadx/bin/jadx" >&2
  exit 2
fi

JDK_HOME=$(ls -d "$ROOT"/.tools/jdk21/*/ 2>/dev/null | head -1)

if [ -z "$JDK_HOME" ]; then
  echo "jadx: no JDK 21 in .tools/jdk21 - see the comment at the top of this file" >&2
  exit 2
fi

JAVA_HOME=${JDK_HOME%/}
export JAVA_HOME

exec "$JADX_BIN" "$@"
