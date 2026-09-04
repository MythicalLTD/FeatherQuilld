#!/usr/bin/env bash
# Optional dev helper production nodes auto-download fusequota via FeatherQuilld on first boot.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${ROOT}/bins/fusequota"
REPO="${FUSEQUOTA_REPO:-https://github.com/calagopus/fusequota}"

arch="$(uname -m)"
case "$arch" in
    x86_64) fq_arch="x86_64" ;;
    aarch64 | arm64) fq_arch="aarch64" ;;
    ppc64le) fq_arch="ppc64le" ;;
    riscv64) fq_arch="riscv64" ;;
    *)
        echo "fusequota: no prebuilt binary for architecture '$arch'" >&2
        echo "fusequota: build from source with: make build-fusequota-source" >&2
        exit 1
        ;;
esac

asset="fusequota-${fq_arch}-linux"
url="${REPO}/releases/latest/download/${asset}"

mkdir -p "${ROOT}/bins"
tmp="${OUT}.download"

echo "fusequota: downloading ${asset} from GitHub releases…"
if command -v curl >/dev/null 2>&1; then
    curl -fsSL -o "$tmp" "$url"
elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$tmp" "$url"
else
    echo "fusequota: curl or wget required" >&2
    exit 1
fi

chmod +x "$tmp"
mv -f "$tmp" "$OUT"
echo "fusequota: installed → ${OUT}"
