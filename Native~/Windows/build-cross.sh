#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
package_dir="$(cd "$script_dir/../.." && pwd)"
output_dir="$package_dir/Runtime/Plugins/Windows/x86_64"
compiler="${MINGW_CXX:-x86_64-w64-mingw32-g++}"
strip_tool="${MINGW_STRIP:-x86_64-w64-mingw32-strip}"
unity_plugin_api="${UNITY_PLUGIN_API:-}"
openh264_version="2.6.0"
openh264_commit="652bdb7719f30b52b08e506645a7322ff1b2cc6f"
openh264_root="${OPENH264_ROOT:-$script_dir/.build/openh264-$openh264_version}"

if [[ -z "$unity_plugin_api" ]]; then
  unity_plugin_api="$(find /Applications/Unity/Hub/Editor -path '*/Unity.app/Contents/PluginAPI/IUnityGraphicsD3D12.h' -print 2>/dev/null | sort | tail -n 1)"
  unity_plugin_api="${unity_plugin_api%/IUnityGraphicsD3D12.h}"
fi
if [[ ! -f "$unity_plugin_api/IUnityGraphicsD3D12.h" ]]; then
  echo "Unity PluginAPI headers were not found. Set UNITY_PLUGIN_API to <Unity.app>/Contents/PluginAPI." >&2
  exit 1
fi

if [[ ! -f "$openh264_root/codec/api/wels/codec_api.h" ]]; then
  mkdir -p "$(dirname "$openh264_root")"
  git clone --no-checkout https://github.com/cisco/openh264.git "$openh264_root"
  git -C "$openh264_root" checkout "$openh264_commit"
fi

if git -C "$openh264_root" rev-parse HEAD >/dev/null 2>&1; then
  actual_openh264_commit="$(git -C "$openh264_root" rev-parse HEAD)"
  if [[ "$actual_openh264_commit" != "$openh264_commit" ]]; then
    echo "OpenH264 source must be pinned to $openh264_commit; found $actual_openh264_commit in $openh264_root." >&2
    exit 1
  fi
fi

make -C "$openh264_root" -j"$(sysctl -n hw.logicalcpu 2>/dev/null || echo 4)" \
  OS=mingw_nt ARCH=x86_64 USE_ASM=No BUILDTYPE=Release \
  CC=x86_64-w64-mingw32-gcc CXX=x86_64-w64-mingw32-g++ \
  AR=x86_64-w64-mingw32-ar STRIP=x86_64-w64-mingw32-strip libraries

mkdir -p "$output_dir"
"$compiler" \
  -std=c++17 -O2 -Wall -Wextra -Werror -shared -static \
  -DUNICODE -D_UNICODE \
  -I"$unity_plugin_api" \
  -I"$openh264_root/codec/api/wels" \
  -o "$output_dir/MacacaBeaconVideoWindows.dll" \
  "$script_dir/MacacaBeaconVideoWindows.cpp" \
  "$script_dir/MacacaBeaconSoftwareVideo.cpp" \
  "$openh264_root/libopenh264.a" \
  -lmfplat -lmfreadwrite -lmfuuid -lwindowscodecs -lshlwapi -lole32 \
  -ld3d11 -ld3d12 -ldxgi

if command -v "$strip_tool" >/dev/null 2>&1; then
  "$strip_tool" --strip-unneeded "$output_dir/MacacaBeaconVideoWindows.dll"
fi

echo "Built $output_dir/MacacaBeaconVideoWindows.dll"
