#!/bin/sh
# Install the APK on a Quest connected by USB (developer mode on) and let it
# reach `vcl serve` on this machine through the cable:
#   clients/unity/tools/install-quest.sh [ws://host:port/ws]
# With no address the app uses ws://127.0.0.1:8093/ws, forwarded by
# `adb reverse`. Give your desktop's LAN address instead to go wireless
# (and run `vcl serve --host 0.0.0.0`).
set -eu
here=$(cd "$(dirname "$0")" && pwd)
apk="$here/../Builds/vcl-quest.apk"
pkg=io.github.pa255ion.vcl
adb install -r "$apk"
adb reverse tcp:8093 tcp:8093
if [ $# -gt 0 ]; then
  printf '%s\n' "$1" > /tmp/vcl-server.txt
  adb shell mkdir -p "/sdcard/Android/data/$pkg/files"
  adb push /tmp/vcl-server.txt "/sdcard/Android/data/$pkg/files/server.txt"
fi
adb shell am start -n "$pkg/com.unity3d.player.UnityPlayerActivity"
echo "started; logs: adb logcat -s Unity"
