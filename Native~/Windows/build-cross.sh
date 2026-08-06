#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
package_dir="$(cd "$script_dir/../.." && pwd)"
output_dir="$package_dir/Runtime/Plugins/Windows/x86_64"
compiler="${MINGW_CXX:-x86_64-w64-mingw32-g++}"
strip_tool="${MINGW_STRIP:-x86_64-w64-mingw32-strip}"

mkdir -p "$output_dir"
"$compiler" \
  -std=c++17 -O2 -Wall -Wextra -Werror -shared -static \
  -DUNICODE -D_UNICODE \
  -o "$output_dir/MacacaBeaconVideoWindows.dll" \
  "$script_dir/MacacaBeaconVideoWindows.cpp" \
  -lmfplat -lmfreadwrite -lmfuuid -lwindowscodecs -lshlwapi -lole32

if command -v "$strip_tool" >/dev/null 2>&1; then
  "$strip_tool" --strip-unneeded "$output_dir/MacacaBeaconVideoWindows.dll"
fi

echo "Built $output_dir/MacacaBeaconVideoWindows.dll"
