#!/bin/sh
# Build the Quest APK (Builds/vcl-quest.apk) headless. Needs the `unity` editor
# from the NixOS config; the Android SDK/NDK/JDK come from the repo flake.
#   clients/unity/tools/build-quest.sh
set -eu
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
mkdir -p "$here/../Builds"
if [ -z "${VCL_ANDROID_SDK:-}" ]; then
  exec nix develop "$root#unity" -c "$0" "$@"
fi
"$here/unity.sh" -batchmode -nographics -quit \
  -executeMethod Vcl.EditorTools.VclBuild.Quest \
  -logFile "$here/../Builds/build.log" || {
  echo "build failed; see clients/unity/Builds/build.log" >&2
  grep -E 'error|Error|\[vcl\]' "$here/../Builds/build.log" | tail -20 >&2
  exit 1
}
grep '\[vcl\] build' "$here/../Builds/build.log"
