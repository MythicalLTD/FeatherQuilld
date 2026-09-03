#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}"

VERSION="${VERSION:-}"
if [[ -z "${VERSION}" ]]; then
  if git describe --tags --exact-match >/dev/null 2>&1; then
    VERSION="$(git describe --tags --exact-match | sed 's/^v//')"
  else
    VERSION="dev"
  fi
fi

VERSION="${VERSION#v}"
TAG="v${VERSION%%~*}"
MAINTAINER="${DEB_MAINTAINER:-MythicalSystems <support@mythicalsystems.org>}"
METAINFO="${ROOT}/packaging/featherquilld.metainfo.xml"
CHANGELOG_YAML="${ROOT}/packaging/changelog.yaml"
CHANGELOG_MD="${ROOT}/CHANGELOG.md"

extract_release_notes() {
  local notes=""
  if [[ ! -f "${CHANGELOG_MD}" ]]; then
    notes="FeatherQuilld ${VERSION}"
    printf '%s' "${notes}"
    return 0
  fi

  notes="$(
    awk -v tag="${TAG}" '
      $0 ~ "^## " tag "$" { capture=1; next }
      capture && /^## / { exit }
      capture && /^### / {
        section=$0
        sub(/^### /, "", section)
        if (section != "") {
          printf("%s:\n", section)
        }
        next
      }
      capture && /^[-*] / {
        sub(/^[-*] /, "", $0)
        print
      }
    ' "${CHANGELOG_MD}" \
      | head -n 40
  )"

  if [[ -z "${notes}" ]]; then
    notes="FeatherQuilld ${VERSION}"
  fi

  printf '%s' "${notes}"
}

release_date() {
  local date=""
  if date="$(git log -1 --format=%cs "HEAD" 2>/dev/null)" && [[ -n "${date}" ]]; then
    printf '%s' "${date}"
    return 0
  fi

  date -u +%Y-%m-%d
}

escape_xml() {
  sed \
    -e 's/&/\&amp;/g' \
    -e 's/</\&lt;/g' \
    -e 's/>/\&gt;/g' \
    -e 's/"/\&quot;/g' \
    -e "s/'/\&apos;/g"
}

notes="$(extract_release_notes)"
date="$(release_date)"
iso_date="${date}T00:00:00Z"

mapfile -t note_lines < <(printf '%s\n' "${notes}")

{
  printf '%s\n' "- semver: ${VERSION}"
  printf '%s\n' "  date: ${iso_date}"
  printf '%s\n' "  packager: ${MAINTAINER}"
  printf '%s\n' "  changes:"
  for line in "${note_lines[@]}"; do
    [[ -z "${line}" ]] && continue
    printf '%s\n' "    - note: |-"
    printf '%s\n' "        ${line}"
  done
} > "${CHANGELOG_YAML}"

{
  cat <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<component type="service">
  <id>org.mythicalsystems.FeatherQuilld</id>
  <name>FeatherQuilld</name>
  <summary>Web hosting node daemon for FeatherPanel</summary>
  <metadata_license>CC0-1.0</metadata_license>
  <project_license>MIT</project_license>
  <developer_name>MythicalSystems</developer_name>
  <launchable type="service-name">featherquilld.service</launchable>
  <url type="homepage">https://github.com/mythicalltd/featherquilld</url>
  <url type="bugtracker">https://github.com/mythicalltd/featherquilld/issues</url>
  <url type="vcs-browser">https://github.com/mythicalltd/featherquilld</url>
  <categories>
    <category>System</category>
  </categories>
  <keywords>
    <keyword>featherpanel</keyword>
    <keyword>web-hosting</keyword>
    <keyword>quilld</keyword>
  </keywords>
  <description>
    <p>
      FeatherQuilld is FeatherPanel&apos;s web hosting node daemon. It manages
      WebSpaces, reverse proxy, SFTP/FTP, and exposes an HTTP API for the panel.
    </p>
    <p>Features include:</p>
    <ul>
      <li>WebSpace lifecycle management</li>
      <li>HTTP API and built-in SFTP/FTP access</li>
      <li>Interactive first-run configuration via <code>featherquilld configure</code></li>
      <li>OAuth2 quick setup with FeatherPanel</li>
      <li>systemd service integration for production deployments</li>
    </ul>
  </description>
  <release version="${VERSION}" date="${date}">
    <description>
EOF

  for line in "${note_lines[@]}"; do
    [[ -z "${line}" ]] && continue
    printf '      <p>%s</p>\n' "$(printf '%s' "${line}" | escape_xml)"
  done

  cat <<EOF
    </description>
  </release>
  <content_rating type="oars-1.1"/>
</component>
EOF
} > "${METAINFO}"

echo "Generated ${METAINFO} and ${CHANGELOG_YAML} for version ${VERSION}"
