#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
native_directory="$(cd -- "${script_directory}/.." && pwd)"
build_directory="${native_directory}/build/linux"

cmake -S "${native_directory}" -B "${build_directory}" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5
cmake --build "${build_directory}" --config Release --parallel 2
ctest --test-dir "${build_directory}" --build-config Release --output-on-failure
