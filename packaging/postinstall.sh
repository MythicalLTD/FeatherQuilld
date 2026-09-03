#!/bin/bash
set -e

CONFIG="/etc/featherquilld/config.yml"
BINARY="/usr/local/bin/featherquilld"
SERVICE_UNIT="/lib/systemd/system/featherquilld.service"

case "${1:-}" in
  configure)
    ;;
  *)
    exit 0
    ;;
esac

mkdir -p /etc/featherquilld /var/lib/featherquilld
chmod 755 /etc/featherquilld /var/lib/featherquilld

systemctl daemon-reload || true

if [ -f "${CONFIG}" ]; then
  systemctl enable featherquilld.service >/dev/null 2>&1 || true
  if systemctl is-enabled --quiet featherquilld.service 2>/dev/null; then
    systemctl restart featherquilld.service >/dev/null 2>&1 || systemctl start featherquilld.service >/dev/null 2>&1 || true
  else
    systemctl start featherquilld.service >/dev/null 2>&1 || true
  fi
  echo "FeatherQuilld upgraded. Existing configuration preserved and service restarted."
  exit 0
fi

systemctl disable featherquilld.service >/dev/null 2>&1 || true

if [ -t 0 ] && [ -t 1 ] && [ "${DEBIAN_FRONTEND:-noninteractive}" != "noninteractive" ]; then
  echo ""
  echo "FeatherQuilld installed."
  echo "Launching the configuration wizard..."
  echo ""
  if "${BINARY}" configure --no-service; then
    systemctl enable featherquilld.service >/dev/null 2>&1 || true
    systemctl start featherquilld.service >/dev/null 2>&1 || true
    echo ""
    echo "FeatherQuilld configured and started."
  else
    echo ""
    echo "Configuration was not completed."
    echo "Run 'sudo featherquilld configure' when ready, then:"
    echo "  sudo systemctl enable --now featherquilld"
    echo ""
  fi
else
  echo ""
  echo "FeatherQuilld installed successfully."
  echo ""
  echo "Next steps:"
  echo "  1. sudo featherquilld configure"
  echo "  2. sudo systemctl enable --now featherquilld"
  echo ""
fi

exit 0
