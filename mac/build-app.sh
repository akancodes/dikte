#!/bin/bash
# Builds dist/Dikte.app (Apple silicon + Intel) and dist/Dikte-mac.zip. Needs macOS with Xcode.
# The app is ad-hoc signed, not notarized: macOS asks once before the first launch.
set -euo pipefail
cd "$(dirname "$0")"

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' ../src/Dikte/Dikte.csproj)
dist=../dist
app=$dist/Dikte.app

swift build -c release --arch arm64 --arch x86_64
bin=$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)

rm -rf "$app" "$dist/Dikte-mac.zip"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$bin/Dikte" "$app/Contents/MacOS/Dikte"
sed "s/__VERSION__/$version/g" Info.plist > "$app/Contents/Info.plist"
cp AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
# The window is the same page as on Windows.
cp ../src/Dikte/ui/index.html "$app/Contents/Resources/index.html"

codesign --force --sign - --identifier dev.akan.dikte "$app"
codesign --verify --verbose "$app"
lipo -info "$app/Contents/MacOS/Dikte"

ditto -c -k --sequesterRsrc --keepParent "$app" "$dist/Dikte-mac.zip"
echo "Built Dikte $version: $dist/Dikte-mac.zip"
