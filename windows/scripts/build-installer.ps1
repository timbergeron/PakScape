param(
    [string]$Version = "1.0.1"
)

$ErrorActionPreference = "Stop"

$WindowsDirectory = Split-Path -Parent $PSScriptRoot
$PublishDirectory = Join-Path $WindowsDirectory "artifacts/publish/win-x64"
$ArtifactDirectory = Join-Path $WindowsDirectory "artifacts"
$ProjectPath = Join-Path $WindowsDirectory "PakStudio.App/PakStudio.App.csproj"
$InstallerScript = Join-Path $WindowsDirectory "installer/PakScape.iss"

dotnet publish $ProjectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $PublishDirectory `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$Compiler = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($null -eq $Compiler) {
    $Candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7/ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 7/ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6/ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6/ISCC.exe")
    )
    $CompilerPath = $Candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
} else {
    $CompilerPath = $Compiler.Source
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    throw "Inno Setup 6 or newer was not found. Install it or add ISCC.exe to PATH."
}

New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
& $CompilerPath `
    "/DSourceDir=$PublishDirectory" `
    "/DOutputDir=$ArtifactDirectory" `
    "/DAppVersion=$Version" `
    $InstallerScript
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
