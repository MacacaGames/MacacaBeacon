#!/usr/bin/env bash
set -euo pipefail
package_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
beacon_java_bin=${BEACON_JAVA_BIN:-/Applications/Unity/Hub/Editor/6000.3.10f1/PlaybackEngines/AndroidPlayer/OpenJDK/bin}
if [[ ! -x "$beacon_java_bin/javac" ]]; then beacon_java_bin=$(dirname "$(command -v javac)"); fi
classes_dir=$(mktemp -d)
trap 'rm -rf "$classes_dir"' EXIT
"$beacon_java_bin/javac" -Xlint:all -Werror -d "$classes_dir" \
    "$package_dir/Runtime/Plugins/Android/BeaconEncodedVideoBuffer.java" \
    "$package_dir/Tests~/Android/BeaconEncodedVideoBufferTest.java"
"$beacon_java_bin/java" -cp "$classes_dir" com.macacagames.beacon.BeaconEncodedVideoBufferTest
