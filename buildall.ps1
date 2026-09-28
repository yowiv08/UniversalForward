#requires -Version 7
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/plugins')
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -OutputDirectory $OutputDirectory
