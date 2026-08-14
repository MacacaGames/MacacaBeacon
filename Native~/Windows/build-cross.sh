#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
package_dir="$(cd "$script_dir/../.." && pwd)"
output_dir="$package_dir/Runtime/Plugins/Windows/x86_64"
compiler="${MINGW_CXX:-x86_64-w64-mingw32-g++}"
strip_tool="${MINGW_STRIP:-x86_64-w64-mingw32-strip}"
unity_plugin_api="${UNITY_PLUGIN_API:-}"

if [[ -z "$unity_plugin_api" ]]; then
  unity_plugin_api="$(find /Applications/Unity/Hub/Editor -path '*/Unity.app/Contents/PluginAPI/IUnityGraphicsD3D12.h' -print 2>/dev/null | sort | tail -n 1)"
  unity_plugin_api="${unity_plugin_api%/IUnityGraphicsD3D12.h}"
fi
if [[ ! -f "$unity_plugin_api/IUnityGraphicsD3D12.h" ]]; then
  echo "Unity PluginAPI headers were not found. Set UNITY_PLUGIN_API to <Unity.app>/Contents/PluginAPI." >&2
  exit 1
fi

mkdir -p "$output_dir"
"$compiler" \
  -std=c++17 -O2 -Wall -Wextra -Werror -shared -static \
  -DUNICODE -D_UNICODE \
  -I"$unity_plugin_api" \
  -o "$output_dir/MacacaBeaconVideoWindows.dll" \
  "$script_dir/MacacaBeaconVideoWindows.cpp" \
  -lmfplat -lmfreadwrite -lmfuuid -lwindowscodecs -lshlwapi -lole32 \
  -ld3d11 -ld3d12 -ldxgi

if command -v "$strip_tool" >/dev/null 2>&1; then
  "$strip_tool" --strip-unneeded "$output_dir/MacacaBeaconVideoWindows.dll"
fi

echo "Built $output_dir/MacacaBeaconVideoWindows.dll"
