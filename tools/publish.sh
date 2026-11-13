#!/bin/sh
# Build the viewer and copy it into a static site checkout:
#   tools/publish.sh SITE_DIR [SUBDIR]     (SUBDIR defaults to vcl)
# The copy is marked static (<html data-static>): it never looks for a
# `vcl serve` and parses lessons with the bundled wasm. The commit hash is
# stamped into every module import and data URL, so caches never mix versions.
# Then commit and push the site and upload it with the site's ./deploy.sh.
set -eu
site=${1:?usage: tools/publish.sh SITE_DIR [SUBDIR]}
sub=${2:-vcl}
[ -d "$site" ] || { echo "publish: $site is not a directory" >&2; exit 1; }
cd "$(dirname "$0")/.."

zig build
zig-out/bin/vcl check viewer/lessons/*.vcl viewer/lessons/*.md
version=$(git rev-parse --short HEAD)$(git diff --quiet HEAD -- viewer core || echo "-dirty")

dest="$site/$sub"
rm -rf "$dest"
mkdir -p "$dest/js" "$dest/lessons" "$dest/licenses"
cp viewer/index.html viewer/style.css viewer/vcl.wasm viewer/tts.wasm "$dest/"
cp viewer/js/*.js "$dest/js/"
cp viewer/lessons/* "$dest/lessons/"
cp viewer/licenses/* "$dest/licenses/"

v="?v=$version"
sed -i -e 's|<html lang="en">|<html lang="en" data-static>|' \
       -e "s|href=\"style.css\"|href=\"style.css$v\"|" \
       -e "s|src=\"js/main.js\"|src=\"js/main.js$v\"|" "$dest/index.html"
# Relative module imports: './x.js' → './x.js?v=…' (every importer must agree,
# or the browser loads two copies of a module).
sed -i -E "s#(from '\./[A-Za-z0-9_-]+\.js)'#\1$v'#g" "$dest"/js/*.js

grep -q 'data-static' "$dest/index.html" || { echo "publish: index.html not stamped" >&2; exit 1; }
echo "published $version to $dest ($(du -sh "$dest" | cut -f1))"
echo "next: commit and push the site, then run its ./deploy.sh (never \`neocities push .\`: it uploads .git/)"
