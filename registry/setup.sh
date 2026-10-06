#!/bin/sh
# Installs the shared registry on this machine, and reinstalls it when it runs
# again: the two files, a Node new enough to run them, the environment file that
# holds the two secrets, and the systemd unit that keeps it running.
#
# Why it is a script and not an image: the service is two files and a runtime,
# and building an image for them would mean a registry to pull it from, a tag to
# keep in step with the code and a `docker` to install on a one-core VM. Copying
# two files and letting systemd hold them is smaller and easier to read.
#
#   sudo registry/setup.sh registry/service.js registry/whatsappforwp-registry.service \
#        --secret "$(head -c 32 /dev/urandom | base64)" --token <github token>
#
# --token -  reads the token from stdin instead, which keeps it out of `ps`.
# Nothing here prints either secret, and the environment file is mode 600.
set -eu

APP_DIR=/opt/whatsappforwp-registry
UNIT=/etc/systemd/system/whatsappforwp-registry.service
UNIT_NAME=whatsappforwp-registry.service
ENV_FILE=/etc/whatsappforwp-registry.env
NODE_DIR=/opt/node
NODE_LINK=/usr/local/bin/node
REPO_DEFAULT=vincenzosco/whatsappforwp-endpoint

SERVICE=""
UNIT_SRC=""
TOKEN=""
SECRET=""
REPO="${REPO_DEFAULT}"
PORT=8787
TTL_MINUTES=1440
PUBLISH_MINUTES=15
NODE_VERSION=""

usage() {
  cat <<'EOF'
usage: setup.sh <service.js> <whatsappforwp-registry.service> [options]

  --token <token>        the GitHub token that writes the endpoint repository
                         (use `--token -` to read it from stdin)
  --secret <secret>      the shared secret every report must carry
  --repo <owner/name>    the repository the list is published to
                         (default: vincenzosco/whatsappforwp-endpoint)
  --port <port>          the port the service listens on (default: 8787)
  --ttl <minutes>        how long a row survives without a report (default: 1440)
  --publish <minutes>    how often the list is pushed to GitHub (default: 15)
  --node-version <v>     install this Node version instead of the latest 22.x
  -h, --help             this text
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --token) TOKEN="${2:-}"; shift 2 ;;
    --secret) SECRET="${2:-}"; shift 2 ;;
    --repo) REPO="${2:-}"; shift 2 ;;
    --port) PORT="${2:-}"; shift 2 ;;
    --ttl) TTL_MINUTES="${2:-}"; shift 2 ;;
    --publish) PUBLISH_MINUTES="${2:-}"; shift 2 ;;
    --node-version) NODE_VERSION="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    -*) echo "setup.sh: unknown option $1" >&2; usage >&2; exit 2 ;;
    *)
      if [ -z "${SERVICE}" ]; then SERVICE="$1"
      elif [ -z "${UNIT_SRC}" ]; then UNIT_SRC="$1"
      else echo "setup.sh: unexpected argument $1" >&2; usage >&2; exit 2
      fi
      shift
      ;;
  esac
done

if [ "$(id -u)" != "0" ]; then
  echo "setup.sh: run me as root (sudo registry/setup.sh ...)" >&2
  exit 1
fi

if [ -z "${SERVICE}" ] || [ ! -f "${SERVICE}" ]; then
  echo "setup.sh: give the path to service.js" >&2
  usage >&2
  exit 2
fi
if [ -z "${UNIT_SRC}" ] || [ ! -f "${UNIT_SRC}" ]; then
  echo "setup.sh: give the path to whatsappforwp-registry.service" >&2
  usage >&2
  exit 2
fi

# `registry/service.js` requires `registry/registry.js` beside it, so the whole
# directory it lives in is the unit of installation.
SOURCE_DIR="$(cd "$(dirname "${SERVICE}")" && pwd)"
if [ ! -f "${SOURCE_DIR}/registry.js" ]; then
  echo "setup.sh: registry.js is not next to ${SERVICE}: copy the whole registry/ directory" >&2
  exit 2
fi

if [ "${TOKEN}" = "-" ]; then
  printf 'GitHub token: ' > /dev/tty 2>/dev/null || printf 'GitHub token: '
  IFS= read -r TOKEN
fi

command -v curl >/dev/null 2>&1 || {
  echo "setup.sh: curl is missing; install it (apt-get install -y curl) and run me again" >&2
  exit 1
}

# ── Node ────────────────────────────────────────────────────────────────────

node_major() {
  major="$("$1" --version 2>/dev/null || true)"
  major="${major#v}"
  printf '%s' "${major%%.*}"
}

node_is_new_enough() {
  command -v node >/dev/null 2>&1 || return 1
  major="$(node_major "$(command -v node)")"
  case "${major}" in ''|*[!0-9]*) return 1 ;; esac
  [ "${major}" -ge 18 ]
}

install_node() {
  version="${NODE_VERSION}"
  if [ -z "${version}" ]; then
    # The index is one object per line, so the newest 22.x is the first match.
    version="$(curl -fsSL https://nodejs.org/dist/index.json \
      | grep -o '"version": *"v22[^"]*"' | head -1 | cut -d'"' -f4 | sed 's/^v//' || true)"
  fi
  if [ -z "${version}" ]; then
    echo "setup.sh: could not find a Node 22 release; pass --node-version" >&2
    exit 1
  fi

  case "$(uname -m)" in
    x86_64) arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) echo "setup.sh: no Node build for $(uname -m)" >&2; exit 1 ;;
  esac

  name="node-v${version}-linux-${arch}"
  tmp="$(mktemp -d)"
  echo "setup.sh: installing Node ${version} (${arch}) into ${NODE_DIR}"
  curl -fsSL -o "${tmp}/node.tar.gz" "https://nodejs.org/dist/v${version}/${name}.tar.gz"
  tar -xzf "${tmp}/node.tar.gz" -C "${tmp}"
  rm -rf "${NODE_DIR}"
  mkdir -p "$(dirname "${NODE_DIR}")"
  mv "${tmp}/${name}" "${NODE_DIR}"
  rm -rf "${tmp}"
}

if node_is_new_enough; then
  echo "setup.sh: using the Node already installed ($(node --version))"
  ln -sf "$(command -v node)" "${NODE_LINK}"
else
  install_node
  ln -sf "${NODE_DIR}/bin/node" "${NODE_LINK}"
fi

# The unit starts the interpreter by absolute path, and /usr/local/bin/node is
# the name that means "the Node this machine installed for the registry".
if [ ! -x "${NODE_LINK}" ]; then
  echo "setup.sh: ${NODE_LINK} is not executable after install" >&2
  exit 1
fi

# ── The files ───────────────────────────────────────────────────────────────

install -D -m 0644 "${SOURCE_DIR}/registry.js" "${APP_DIR}/registry.js"
install -D -m 0644 "${SOURCE_DIR}/service.js" "${APP_DIR}/service.js"
install -D -m 0644 "${UNIT_SRC}" "${UNIT}"

# ── The secrets ─────────────────────────────────────────────────────────────

if [ -z "${SECRET}" ]; then
  echo "setup.sh: no --secret given: any server that can reach ${PORT} may register" >&2
fi
if [ -z "${TOKEN}" ]; then
  echo "setup.sh: no --token given: the list is served but never published" >&2
fi

umask 077
tmp_env="${ENV_FILE}.new"
{
  echo "# The shared registry. Mode 600: it holds the GitHub token and the secret"
  echo "# every server reports with. Written by registry/setup.sh."
  printf 'REGISTRY_PORT=%s\n' "${PORT}"
  printf 'REGISTRY_REPO=%s\n' "${REPO}"
  printf 'REGISTRY_FILE=/var/lib/whatsappforwp-registry/registry.json\n'
  printf 'REGISTRY_TTL_MINUTES=%s\n' "${TTL_MINUTES}"
  printf 'REGISTRY_PUBLISH_MINUTES=%s\n' "${PUBLISH_MINUTES}"
  printf 'REGISTRY_TOKEN=%s\n' "${SECRET}"
  printf 'REGISTRY_GH_TOKEN=%s\n' "${TOKEN}"
} > "${tmp_env}"
chmod 600 "${tmp_env}"
mv "${tmp_env}" "${ENV_FILE}"

# ── The service ─────────────────────────────────────────────────────────────

systemctl daemon-reload
systemctl enable "${UNIT_NAME}" >/dev/null 2>&1 || true
# `restart` also starts it: on a second run this is the upgrade path.
systemctl restart "${UNIT_NAME}"

sleep 1
if systemctl is-active --quiet "${UNIT_NAME}"; then
  echo "setup.sh: ${UNIT_NAME} is running"
  echo "setup.sh: check it with  curl -fsS http://127.0.0.1:${PORT}/health"
else
  echo "setup.sh: ${UNIT_NAME} did not stay up; the log says why:" >&2
  systemctl --no-pager --lines=20 status "${UNIT_NAME}" >&2 || true
  journalctl --no-pager --lines=20 -u "${UNIT_NAME}" >&2 || true
  exit 1
fi
