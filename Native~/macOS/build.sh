#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
package_dir="${script_dir:h:h}"
output_path="$package_dir/Runtime/Plugins/macOS/MacacaBeaconVideo.bundle"

xcrun clang++ \
  -dynamiclib \
  -std=c++17 \
  -fobjc-arc \
  -arch arm64 \
  -arch x86_64 \
  -mmacosx-version-min=11.0 \
  -install_name @rpath/MacacaBeaconVideo.dylib \
  -framework Foundation \
  -framework AVFoundation \
  -framework CoreMedia \
  -framework CoreVideo \
  -framework CoreGraphics \
  -framework ImageIO \
  -framework VideoToolbox \
  "$script_dir/MacacaBeaconVideo.mm" \
  -o "$output_path"

file "$output_path"
