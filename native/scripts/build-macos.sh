#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
native_directory="$(cd -- "${script_directory}/.." && pwd)"
build_directory="${native_directory}/build/macos"

cmake -S "${native_directory}" -B "${build_directory}" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES="arm64;x86_64" \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0 \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5
cmake --build "${build_directory}" --config Release --parallel 2
ctest --test-dir "${build_directory}" --build-config Release --output-on-failure
