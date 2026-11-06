<#
.SYNOPSIS
Removes LiteDM's Chrome and/or Edge native messaging registration.
.DESCRIPTION
Removes only the selected LiteDM registry value when it points to the expected LiteDM manifest and removes that manifest file from the publish directory.
.PARAMETER Browser
Select Chrome, Edge, or Both.
.PARAMETER PublishDirectory
Directory containing the LiteDM browser manifests.
.EXAMPLE
./unregister-browser.ps1 -Browser Both -PublishDirectory 'C:\Program Files\LiteDM'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Chrome', 'Edge', 'Both')]
    [string] $Browser,

    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory
)

$ErrorActionPreference = 'Stop'
$publishRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $PublishDirectory -ErrorAction Stop).ProviderPath)
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "PublishDirectory must be an existing directory: '$publishRoot'."
}

function Unregister-NativeHost([string] $name, [string] $browserKey, [string] $manifestName) {
    $manifestPath = [IO.Path]::GetFullPath((Join-Path $publishRoot $manifestName))
    $registryPath = "HKCU:\Software\$browserKey\NativeMessagingHosts\com.litedm.bridge"
    if (Test-Path -LiteralPath $registryPath) {
        $key = Get-Item -LiteralPath $registryPath
        $registeredPath = $key.GetValue('')
        if ($registeredPath -and [IO.Path]::GetFullPath([string]$registeredPath) -ieq $manifestPath) {
            $key.DeleteValue('', $false)
            $key.Close()
            $currentKey = Get-Item -LiteralPath $registryPath
            if ($currentKey.ValueCount -eq 0 -and $currentKey.SubKeyCount -eq 0) {
                Remove-Item -LiteralPath $registryPath -Force
            }
            Write-Output "$name native messaging registry value removed: $registryPath"
        } else {
            $key.Close()
            Write-Output "$name registration was left unchanged because it points to a different manifest."
        }
    }
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
            $expectedHostPath = [IO.Path]::GetFullPath((Join-Path $publishRoot 'LiteDM.Bridge.exe'))
            if ($manifest.name -eq 'com.litedm.bridge' -and $manifest.type -eq 'stdio' -and
                [IO.Path]::GetFullPath([string]$manifest.path) -ieq $expectedHostPath -and
                $manifest.allowed_origins -is [array] -and $manifest.allowed_origins.Count -eq 1 -and
                [string]$manifest.allowed_origins[0] -match '^chrome-extension://[a-p]{32}/$') {
                Remove-Item -LiteralPath $manifestPath -Force
                Write-Output "$name manifest removed: $manifestPath"
            } else {
                Write-Output "$name manifest was left unchanged because it does not match the expected LiteDM format and path."
            }
        } catch {
            Write-Output "$name manifest was left unchanged because it could not be validated."
        }
    }
}

if ($Browser -in @('Chrome', 'Both')) {
    Unregister-NativeHost 'Chrome' 'Google\Chrome' 'com.litedm.bridge.chrome.json'
}
if ($Browser -in @('Edge', 'Both')) {
    Unregister-NativeHost 'Edge' 'Microsoft\Edge' 'com.litedm.bridge.edge.json'
}
