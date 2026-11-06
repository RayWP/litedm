[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repository 'artifacts\LiteDM'
Push-Location -LiteralPath $repository
try {
    & dotnet restore LiteDM.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & dotnet build LiteDM.slnx -c Release --no-restore -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & dotnet test tests/LiteDM.Tests/LiteDM.Tests.csproj -c Release --no-build -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
        if (Get-Command node -ErrorAction SilentlyContinue) {
            & node --test tests/browser/background.test.cjs
            if ($LASTEXITCODE -ne 0) { throw 'Browser script tests failed.' }
        }
        else { Write-Warning 'Node.js is unavailable; browser script tests were skipped.' }
    }
    & dotnet publish src/LiteDM.App/LiteDM.App.csproj -c Release --no-restore -p:Platform=x64 -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    & dotnet publish src/LiteDM.Bridge/LiteDM.Bridge.csproj -c Release --no-restore -p:Platform=x64 -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Bridge publish failed.' }
    foreach ($requiredFile in @('LiteDM.App.exe', 'LiteDM.Bridge.exe', 'LiteDM.App.pri', 'App.xbf', 'MainWindow.xbf')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile) -PathType Leaf)) {
            throw "Publish is incomplete: $requiredFile is missing."
        }
    }
    $extensionOutput = Join-Path $repository 'artifacts\extension'
    New-Item -ItemType Directory -Path $extensionOutput -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'extension\manifest.json') -Destination $extensionOutput -Force
    Get-ChildItem -LiteralPath (Join-Path $repository 'extension') -File |
        Where-Object Name -ne 'manifest.json' |
        Copy-Item -Destination $extensionOutput -Force
    $setupOutput = Join-Path $repository 'artifacts\setup'
    New-Item -ItemType Directory -Path $setupOutput -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'register-browser.ps1'), (Join-Path $PSScriptRoot 'unregister-browser.ps1') -Destination $setupOutput -Force
    Copy-Item -LiteralPath (Join-Path $repository 'docs\DISTRIBUTION.md') -Destination (Join-Path $repository 'artifacts\START-HERE.md') -Force
    Copy-Item -LiteralPath (Join-Path $repository 'docs\STUDY-NOTES.md') -Destination (Join-Path $repository 'artifacts\STUDY-NOTES.md') -Force
    Copy-Item -LiteralPath (Join-Path $repository 'docs\VALIDATION.md') -Destination (Join-Path $repository 'artifacts\VALIDATION.md') -Force
    Write-Output "Desktop: $publishDirectory\LiteDM.App.exe"
    Write-Output "Extension: $extensionOutput"
}
finally { Pop-Location }
