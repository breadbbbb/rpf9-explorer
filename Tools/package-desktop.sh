set -euo pipefail
rid=${1:?Pass a Linux or macOS runtime identifier}
output=${2:?Pass an output directory}
case "$rid" in linux-x64|linux-arm64|osx-x64|osx-arm64) ;; *) exit 2 ;; esac
root=$(cd "$(dirname "$0")/.." && pwd)
mkdir -p "$output"
output=$(cd "$output" && pwd)
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$root/Desktop/RpfExplorer.Desktop.csproj")
label="rpf9-explorer-${version%.0}-$rid"
package="$stage/$label"
mkdir -p "$package"
if [[ "$rid" == osx-* ]]; then
  app="$package/rpf9-explorer.app"
  desktop="$app/Contents/MacOS"
  mkdir -p "$desktop"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>CFBundleName</key><string>rpf9-explorer</string>
<key>CFBundleIdentifier</key><string>io.github.breadbbbb.rpf9-explorer</string>
<key>CFBundleExecutable</key><string>rpf9-explorer</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>$version</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
else
  desktop="$package"
fi
publish() {
  dotnet publish "$root/$1" -c Release -r "$rid" --self-contained true -o "$stage/publish-$2" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
  install -m 755 "$stage/publish-$2/$2" "$3/$2"
}
publish Desktop/RpfExplorer.Desktop.csproj rpf9-explorer "$desktop"
publish Cli/RpfPreview.Cli.csproj rpf9-cli "$package"
media="${RPF_MEDIA_BUILD:-$stage/media}"
if [[ ! -x "$media/bin/ffmpeg" ]]; then bash "$root/Tools/build-unix-media.sh" "$media"; fi
mkdir -p "$desktop/Tools"
cp "$media/bin/"* "$desktop/Tools/"
cp "$root/README.md" "$package/"
cp -R "$root/docs" "$package/"
mkdir "$package/notices"
cp "$media/notices/"* "$package/notices/"
python3 - "$root/Desktop/obj/project.assets.json" "$package/notices" <<'PY'
import json, pathlib, sys
assets = json.loads(pathlib.Path(sys.argv[1]).read_text())
dest = pathlib.Path(sys.argv[2])
for key, lib in assets['libraries'].items():
    if lib.get('type') != 'package':
        continue
    for folder in assets['packageFolders']:
        base = pathlib.Path(folder, lib['path'])
        if not base.is_dir():
            continue
        for item in base.iterdir():
            if item.is_file() and (item.name.lower().startswith(('license','licence','third-party','thirdparty','copying')) or item.suffix == '.nuspec'):
                (dest / (key.replace('/', '-') + '-' + item.name)).write_bytes(item.read_bytes())
        break
PY
if [[ "$rid" == osx-* ]]; then
  codesign --force --sign - "$package/rpf9-cli"
  for tool in "$desktop"/Tools/*; do codesign --force --sign - "$tool"; done
  codesign --force --sign - "$app"
  codesign --verify --strict "$app"
fi
tar -C "$stage" -czf "$output/$label.tar.gz" "$label"
