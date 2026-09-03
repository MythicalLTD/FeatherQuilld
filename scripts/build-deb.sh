#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}"

OUT_DIR="${OUT_DIR:-dist}"
ARCHES="${ARCHES:-amd64 arm64}"
PACKAGE_CHANNEL="${PACKAGE_CHANNEL:-prod}"
DOTNET_CONFIGURATION="${DOTNET_CONFIGURATION:-Release}"
BASE_VERSION="${BASE_VERSION:-}"

if [[ -z "${BASE_VERSION}" ]]; then
  BASE_VERSION="$(
    grep -oP '(?<=<Version>)[^<]+' FeatherQuilld.csproj 2>/dev/null | head -1 || echo "0.1.0"
  )"
fi

if [[ -z "${VERSION:-}" ]]; then
  if [[ "${PACKAGE_CHANNEL}" == "dev" ]]; then
    short_sha="$(git rev-parse --short HEAD 2>/dev/null || echo local)"
    stamp="$(date -u +%Y%m%d%H%M%S)"
    VERSION="${BASE_VERSION}~dev+${stamp}.${short_sha}"
  elif git describe --tags --exact-match >/dev/null 2>&1; then
    VERSION="$(git describe --tags --exact-match | sed 's/^v//')"
  else
    VERSION="${BASE_VERSION}"
  fi
fi

VERSION="${VERSION#v}"

case "${PACKAGE_CHANNEL}" in
  dev)
    NFPM_NAME="featherquilld-dev"
    NFPM_CONFLICT="featherquilld"
    ;;
  prod|*)
    NFPM_NAME="featherquilld"
    NFPM_CONFLICT="featherquilld-dev"
    ;;
esac

rid_for_arch() {
  case "$1" in
    amd64|x64) echo "linux-x64" ;;
    arm64) echo "linux-arm64" ;;
    *)
      echo "Unsupported arch: $1" >&2
      exit 1
      ;;
  esac
}

binary_arch_label() {
  case "$1" in
    amd64|x64) echo "amd64" ;;
    arm64) echo "arm64" ;;
    *) echo "$1" ;;
  esac
}

ensure_nfpm() {
  if command -v nfpm >/dev/null 2>&1; then
    return 0
  fi

  echo "nfpm not found; installing from Goreleaser apt repo..."
  if ! command -v sudo >/dev/null 2>&1; then
    echo "nfpm is required. Install it from https://nfpm.goreleaser.com/install/" >&2
    exit 1
  fi

  echo 'deb [trusted=yes] https://repo.goreleaser.com/apt/ /' | sudo tee /etc/apt/sources.list.d/goreleaser.list >/dev/null
  sudo apt-get update
  sudo apt-get install -y nfpm
}

mkdir -p "${OUT_DIR}"
ensure_nfpm

VERSION="${VERSION}" ./scripts/generate-deb-metadata.sh

echo "Building FeatherQuilld ${VERSION} (${NFPM_NAME}) for: ${ARCHES}"

: > "${OUT_DIR}/.deb-manifest"

for arch in ${ARCHES}; do
  rid="$(rid_for_arch "${arch}")"
  label="$(binary_arch_label "${arch}")"
  publish_dir="${OUT_DIR}/publish-${label}"

  echo ""
  echo "==> ${arch} (${rid})"

  rm -rf "${publish_dir}"
  mkdir -p "${publish_dir}"

  dotnet publish FeatherQuilld.csproj \
    -c "${DOTNET_CONFIGURATION}" \
    -r "${rid}" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:AssemblyName=featherquilld \
    -p:Version="${VERSION%%~*}" \
    -p:InformationalVersion="${VERSION}" \
    -o "${publish_dir}" \
    --nologo

  if [[ -f "${publish_dir}/featherquilld" ]]; then
    cp -f "${publish_dir}/featherquilld" "${OUT_DIR}/featherquilld"
  elif [[ -f "${publish_dir}/FeatherQuilld" ]]; then
    cp -f "${publish_dir}/FeatherQuilld" "${OUT_DIR}/featherquilld"
  else
    echo "Published binary not found in ${publish_dir}" >&2
    ls -la "${publish_dir}" >&2 || true
    exit 1
  fi
  chmod 755 "${OUT_DIR}/featherquilld"

  cp -f "${OUT_DIR}/featherquilld" "${OUT_DIR}/featherquilld_linux_${label}"
  chmod 755 "${OUT_DIR}/featherquilld_linux_${label}"

  NFPM_NAME="${NFPM_NAME}" \
  NFPM_CONFLICT="${NFPM_CONFLICT}" \
  NFPM_ARCH="${arch}" \
  NFPM_VERSION="${VERSION}" \
  nfpm pkg \
    --packager deb \
    --config nfpm.yaml \
    --target "${OUT_DIR}/"

  echo "${OUT_DIR}/${NFPM_NAME}_${VERSION}_${arch}.deb" >> "${OUT_DIR}/.deb-manifest"
done

echo "${VERSION}" > "${OUT_DIR}/.deb-version"
echo "${NFPM_NAME}" > "${OUT_DIR}/.deb-package"

echo ""
echo "Built packages:"
ls -lh "${OUT_DIR}"/*.deb "${OUT_DIR}"/featherquilld_linux_* 2>/dev/null || true

if command -v sha256sum >/dev/null 2>&1; then
  (
    cd "${OUT_DIR}"
    sha256sum featherquilld_linux_* *.deb > checksums.txt
  )
  echo ""
  echo "Checksums written to ${OUT_DIR}/checksums.txt"
fi
