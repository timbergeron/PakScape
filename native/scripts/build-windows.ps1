$ErrorActionPreference = "Stop"

$NativeDirectory = Split-Path -Parent $PSScriptRoot
$BuildDirectory = Join-Path $NativeDirectory "build/windows"

cmake -S $NativeDirectory -B $BuildDirectory -A x64 `
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5
cmake --build $BuildDirectory --config Release --parallel 2
ctest --test-dir $BuildDirectory --build-config Release --output-on-failure
