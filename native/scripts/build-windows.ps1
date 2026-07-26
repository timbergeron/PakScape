$ErrorActionPreference = "Stop"

$NativeDirectory = Split-Path -Parent $PSScriptRoot
$BuildDirectory = Join-Path $NativeDirectory "build/windows"

cmake -S $NativeDirectory -B $BuildDirectory -A x64 `
    "-DCMAKE_POLICY_VERSION_MINIMUM=3.5"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

cmake --build $BuildDirectory --config Release --parallel 2
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

ctest --test-dir $BuildDirectory --build-config Release --output-on-failure
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
