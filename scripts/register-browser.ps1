<#
.SYNOPSIS
Registers LiteDM's native messaging bridge for Chrome, Edge, or both.
.DESCRIPTION
Writes the exact extension ID allowlist manifest beside the published bridge and adds one HKCU native-host registration per selected browser.
.PARAMETER Browser
Select Chrome, Edge, or Both.
.PARAMETER PublishDirectory
Directory containing LiteDM.Bridge.exe; generated manifests are written here too.
.PARAMETER ChromeExtensionId
The 32-character Chrome extension ID, required for Chrome or Both.
.PARAMETER EdgeExtensionId
The 32-character Edge extension ID, required for Edge or Both.
.EXAMPLE
./register-browser.ps1 -Browser Chrome -PublishDirectory 'C:\Program Files\LiteDM' -ChromeExtensionId 'abcdefghijklmnopabcdefghijklmnop'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Chrome', 'Edge', 'Both')]
    [string] $Browser,

    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,

    [string] $ChromeExtensionId,
    [string] $EdgeExtensionId
)

$ErrorActionPreference = 'Stop'
$publishRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $PublishDirectory -ErrorAction Stop).ProviderPath)
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "PublishDirectory must be an existing directory: '$publishRoot'."
}

function Assert-ExtensionId([string] $id, [string] $parameterName) {
    if ($id -notmatch '^[a-p]{32}$') {
        throw "$parameterName must be exactly 32 letters from a through p."
    }
}

if ($Browser -in @('Chrome', 'Both')) {
    if (-not $ChromeExtensionId) { throw '-ChromeExtensionId is required for Chrome or Both.' }
    Assert-ExtensionId $ChromeExtensionId '-ChromeExtensionId'
}
if ($Browser -in @('Edge', 'Both')) {
    if (-not $EdgeExtensionId) { throw '-EdgeExtensionId is required for Edge or Both.' }
    Assert-ExtensionId $EdgeExtensionId '-EdgeExtensionId'
}

$bridgePath = [IO.Path]::GetFullPath((Join-Path $publishRoot 'LiteDM.Bridge.exe'))
if (-not (Test-Path -LiteralPath $bridgePath -PathType Leaf)) {
    throw "LiteDM.Bridge.exe was not found at '$bridgePath'. Publish LiteDM first."
}

function Register-NativeHost([string] $name, [string] $id, [string] $browserKey, [string] $manifestName) {
    $manifestPath = [IO.Path]::GetFullPath((Join-Path $publishRoot $manifestName))
    $manifest = [ordered]@{
        name = 'com.litedm.bridge'
        description = 'LiteDM browser handoff'
        path = $bridgePath
        type = 'stdio'
        allowed_origins = @("chrome-extension://$id/")
    }
    $json = $manifest | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))
    $registryPath = "HKCU:\Software\$browserKey\NativeMessagingHosts\com.litedm.bridge"
    New-Item -Path $registryPath -Force | Out-Null
    Set-Item -Path $registryPath -Value $manifestPath
    Write-Output "$name native messaging host registered: $registryPath -> $manifestPath"
}

if ($Browser -in @('Chrome', 'Both')) {
    Register-NativeHost 'Chrome' $ChromeExtensionId 'Google\Chrome' 'com.litedm.bridge.chrome.json'
}
if ($Browser -in @('Edge', 'Both')) {
    Register-NativeHost 'Edge' $EdgeExtensionId 'Microsoft\Edge' 'com.litedm.bridge.edge.json'
}
