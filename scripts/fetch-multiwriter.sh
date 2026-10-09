#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${ROOT}/native"
mkdir -p "${OUT_DIR}"

REPO="sqliteai/sqlite-multiwriter"
API="https://api.github.com/repos/${REPO}/releases/latest"

echo "Resolving latest release of ${REPO}..."
JSON="$(curl -fsSL "${API}")"
TAG="$(printf '%s' "${JSON}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["tag_name"])')"

OS="$(uname -s | tr '[:upper:]' '[:lower:]')"
ARCH="$(uname -m)"
case "${ARCH}" in
  x86_64|amd64) ARCH_LABEL="x86_64" ;;
  aarch64|arm64) ARCH_LABEL="arm64" ;;
  *) echo "Unsupported arch: ${ARCH}" >&2; exit 1 ;;
esac

case "${OS}" in
  linux)
    PATTERN="multiwriter-linux-${ARCH_LABEL}"
    EXT_NAME="multiwriter.so"
    ;;
  darwin)
    PATTERN="multiwriter-macos-${ARCH_LABEL}"
    EXT_NAME="multiwriter.dylib"
    ;;
  *)
    echo "Unsupported OS: ${OS}" >&2
    exit 1
    ;;
esac

ASSET_URL="$(printf '%s' "${JSON}" | PATTERN="${PATTERN}" python3 -c '
import json, os, sys
pattern = os.environ["PATTERN"]
rel = json.load(sys.stdin)
assets = rel.get("assets") or []
candidates = [a for a in assets if pattern in a["name"] and a["name"].endswith((".tar.gz", ".tgz", ".zip"))]
if not candidates and "linux" in pattern:
    candidates = [
        a for a in assets
        if "linux" in a["name"]
        and ("x86_64" in a["name"] or "amd64" in a["name"])
        and a["name"].endswith((".tar.gz", ".tgz", ".zip"))
        and "musl" not in a["name"]
    ]
if not candidates:
    raise SystemExit(0)
candidates.sort(key=lambda a: (0 if a["name"].endswith(".tar.gz") else 1, a["name"]))
print(candidates[0]["browser_download_url"])
')"

if [[ -z "${ASSET_URL}" ]]; then
  echo "Could not find a release asset matching ${PATTERN}. Assets:" >&2
  printf '%s' "${JSON}" | python3 -c 'import json,sys; [print(" -", a["name"]) for a in json.load(sys.stdin).get("assets",[])]' >&2
  exit 1
fi

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT
ARCHIVE="${TMP}/asset"
echo "Downloading ${ASSET_URL}"
curl -fsSL -L -o "${ARCHIVE}" "${ASSET_URL}"

mkdir -p "${TMP}/extract"
if file "${ARCHIVE}" | grep -qi 'zip archive'; then
  unzip -q "${ARCHIVE}" -d "${TMP}/extract"
else
  tar -xzf "${ARCHIVE}" -C "${TMP}/extract"
fi

FOUND="$(find "${TMP}/extract" -type f \( -name 'multiwriter.so' -o -name 'libmultiwriter.so' -o -name 'multiwriter.dylib' -o -name 'multiwriter.dll' \) | head -n1 || true)"
if [[ -z "${FOUND}" ]]; then
  echo "Archive did not contain multiwriter shared library. Contents:" >&2
  find "${TMP}/extract" -type f >&2
  exit 1
fi

DEST="${OUT_DIR}/${EXT_NAME}"
cp -f "${FOUND}" "${DEST}"
chmod +x "${DEST}"
echo "Installed ${DEST} (release ${TAG})"
echo "Set MULTIWRITER_EXT=${DEST} if you need to override discovery."
