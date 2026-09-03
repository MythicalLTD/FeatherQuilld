#!/bin/bash
set -e

case "${1:-}" in
  upgrade|install)
    if systemctl is-active --quiet featherquilld.service 2>/dev/null; then
      systemctl stop featherquilld.service || true
    fi
    ;;
esac

exit 0
