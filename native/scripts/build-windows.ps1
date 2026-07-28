$ErrorActionPreference = "Stop"

$NativeDirectory = Split-Path -Parent $PSScriptRoot
$BuildDirectory = Join-Path $NativeDirectory "build/windows"

function Find-CMake {
    $Command = Get-Command cmake.exe -ErrorAction SilentlyContinue
    if ($null -ne $Command) {
        return $Command.Source
    }

    $CandidatePaths = @()
    if ($env:VSINSTALLDIR) {
        $CandidatePaths += Join-Path $env:VSINSTALLDIR `
            "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    }

    if (${env:ProgramFiles(x86)}) {
        $VsWhere = Join-Path ${env:ProgramFiles(x86)} `
            "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path $VsWhere) {
            $VisualStudioDirectory = & $VsWhere -latest -products * `
                -property installationPath
            if ($LASTEXITCODE -eq 0 -and $VisualStudioDirectory) {
                $CandidatePaths += Join-Path $VisualStudioDirectory `
                    "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
            }
        }
    }

    if ($env:ProgramFiles) {
        $CandidatePaths += Join-Path $env:ProgramFiles "CMake\bin\cmake.exe"
    }
    if (${env:ProgramFiles(x86)}) {
        $CandidatePaths += Join-Path ${env:ProgramFiles(x86)} "CMake\bin\cmake.exe"
    }

    foreach ($CandidatePath in $CandidatePaths) {
        if ($CandidatePath -and (Test-Path $CandidatePath)) {
            return $CandidatePath
        }
    }

    throw @"
CMake was not found. Install the "C++ CMake tools for Windows" component in
Visual Studio Installer, or install CMake from https://cmake.org/download/ and
add it to PATH.
"@
}

$CMake = Find-CMake
$CTest = Join-Path (Split-Path -Parent $CMake) "ctest.exe"
Write-Host "Using CMake: $CMake"

& $CMake -S $NativeDirectory -B $BuildDirectory -A x64 `
    "-DCMAKE_POLICY_VERSION_MINIMUM=3.5"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $CMake --build $BuildDirectory --config Release --parallel 2
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $CTest --test-dir $BuildDirectory --build-config Release --output-on-failure
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
