#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
package_dir="${script_dir:h:h}"
ndk_root="${ANDROID_NDK_ROOT:-/Applications/Unity/Hub/Editor/2022.3.62f2/PlaybackEngines/AndroidPlayer/NDK}"
toolchain="$ndk_root/toolchains/llvm/prebuilt/darwin-x86_64"
sysroot="$toolchain/sysroot"
output_dir="$package_dir/Runtime/Plugins/Android/arm64-v8a"
unity_plugin_api="/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/PluginAPI"
mkdir -p "$output_dir"

"$toolchain/bin/clang++" \
  --target=aarch64-linux-android24 \
  --sysroot="$sysroot" \
  -shared -fPIC -O2 -std=c++17 -Wall -Wextra -Werror -Wno-missing-field-initializers \
  -DVK_USE_PLATFORM_ANDROID_KHR \
  -I"$unity_plugin_api" \
  -I"$sysroot/usr/include" \
  -landroid -lEGL -lGLESv3 -lvulkan -llog \
  "$script_dir/MacacaBeaconAndroidVideo.cpp" \
  -o "$output_dir/libMacacaBeaconAndroidVideo.so"

echo "Built $output_dir/libMacacaBeaconAndroidVideo.so"
