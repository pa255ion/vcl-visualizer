#!/bin/sh
# Run the pinned Unity editor (the `unity` command from the NixOS config) on
# this project, e.g.
#
#   tools/unity.sh                                  open the editor
#   tools/unity.sh -batchmode -quit -executeMethod VclBuild.Quest   build the APK
#
# Workaround first: on NixOS the editor's built-in packages (com.unity.modules.*,
# ugui) live in the read-only nix store. The Package Manager copies them into
# Library/PackageCache through a temporary directory it then fails to clean up
# (EACCES on the read-only copies), and gives up. Seeding the cache with
# writable copies makes it use them as they are.
set -eu
proj=$(cd "$(dirname "$0")/.." && pwd)
editor=$(readlink -f "$(command -v unity)")
builtin=$(dirname "$editor")/../Editor/2022.3.22f1/Editor/Data/Resources/PackageManager/BuiltInPackages
[ -d "$builtin" ] || builtin=$(sed -n 's/^exec -a "\(.*\)\/Unity".*/\1/p' "$editor")/Data/Resources/PackageManager/BuiltInPackages

mkdir -p "$proj/Library/PackageCache"
rm -rf "$proj"/Library/PackageCache/.tmp-* 2>/dev/null || chmod -R u+w "$proj"/Library/PackageCache/.tmp-* && rm -rf "$proj"/Library/PackageCache/.tmp-* 2>/dev/null || true
for pkg in "$builtin"/*; do
  name=$(basename "$pkg")
  ver=$(sed -n 's/^ *"version": *"\([^"]*\)".*/\1/p' "$pkg/package.json" | head -1)
  dest="$proj/Library/PackageCache/$name@$ver"
  [ -d "$dest" ] && continue
  cp -r --no-preserve=mode "$pkg" "$dest"
done

exec unity -projectPath "$proj" "$@"
