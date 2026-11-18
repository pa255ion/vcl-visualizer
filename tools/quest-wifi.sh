#!/bin/sh
# Link a Quest headset to `vcl serve` on this machine over Wi-Fi, so the cable
# can come out:
#   tools/quest-wifi.sh [HEADSET-IP] [PORT]
# With the headset on USB: switches adb to Wi-Fi and forwards PORT (default
# 8093). Without the cable: reconnects to HEADSET-IP (or the last address
# used), which works until the headset restarts. Run it again whenever the
# link drops. The same thing with buttons: http://localhost:8093/quest.html
set -eu
port=${2:-8093}
saved="${XDG_CACHE_HOME:-$HOME/.cache}/vcl/quest-ip"
ip=${1:-}

command -v adb >/dev/null || { echo "adb isn't installed (android-tools)" >&2; exit 1; }

ready() { adb devices | awk 'NR > 1 && $2 == "device" { print $1 }'; }
wifi_dev() { ready | grep ':' | head -n1 || true; }
usb_dev() { ready | grep -v ':' | head -n1 || true; }

dev=$(wifi_dev)
if [ -z "$dev" ]; then
  usb=$(usb_dev)
  if [ -n "$usb" ]; then
    ip=$(adb -s "$usb" shell ip -4 addr show wlan0 | sed -n 's/.*inet \([0-9.]*\).*/\1/p')
    [ -n "$ip" ] || { echo "The headset has no Wi-Fi address. Is its Wi-Fi on?" >&2; exit 1; }
    echo "Switching adb on the headset to Wi-Fi ($ip)…"
    adb -s "$usb" tcpip 5555 >/dev/null
    sleep 2
  fi
  [ -n "$ip" ] || ip=$(cat "$saved" 2>/dev/null || true)
  if [ -z "$ip" ]; then
    if adb devices | grep -q unauthorized; then
      echo "Put on the headset and allow USB debugging for this computer, then run this again." >&2
    else
      echo "No headset found. Plug it in by USB once, or give its Wi-Fi address." >&2
    fi
    exit 1
  fi
  for _ in 1 2 3; do
    adb connect "$ip:5555" | grep -q '^connected\|already connected' && break
    sleep 2
  done
  dev=$(wifi_dev)
  [ -n "$dev" ] || { echo "Couldn't reach $ip:5555. If the headset restarted, plug it in by USB once." >&2; exit 1; }
fi

mkdir -p "$(dirname "$saved")"
printf '%s\n' "${dev%:*}" > "$saved"
adb -s "$dev" reverse "tcp:$port" "tcp:$port" >/dev/null
echo "Linked over Wi-Fi ($dev), port $port forwarded. You can unplug the cable."
