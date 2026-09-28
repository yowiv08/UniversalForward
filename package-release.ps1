#requires -Version 7
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$InputDirectory = (Join-Path $PSScriptRoot 'artifacts/plugins'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/release')
)
$ErrorActionPreference = 'Stop'
if ($Tag -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') { throw "无效 Release tag：$Tag" }
$package = Join-Path ([IO.Path]::GetFullPath($InputDirectory)) 'universalforward'
$entryDll = Join-Path $package 'Plugins.UniversalForward.dll'
$manifestPath = Join-Path $package 'plugin.json'
if (-not (Test-Path -LiteralPath $entryDll) -or -not (Test-Path -LiteralPath $manifestPath)) {
    throw '缺少入口 DLL 或 plugin.json。'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$version = [Reflection.AssemblyName]::GetAssemblyName($entryDll).Version.ToString()
if ($manifest.id -cne 'universalforward' -or $manifest.runtime -cne 'dotnet' -or
    $manifest.version -ne $version -or [string]::IsNullOrWhiteSpace($manifest.description)) {
    throw '插件元数据无效。'
}
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
    throw "Release 输出目录必须为空：$OutputDirectory"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$archive = Join-Path $OutputDirectory 'universalforward.zip'
Compress-Archive -LiteralPath $package -DestinationPath $archive
$content = @(Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/')
    "$relative $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
}) -join "`n"
$entry = [ordered]@{
    id = 'universalforward'
    name = 'UniversalForward'
    description = $manifest.description
    runtime = 'dotnet'
    version = $version
    asset = 'universalforward.zip'
    sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    contentSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content))).ToLowerInvariant()
    sizeBytes = (Get-Item -LiteralPath $archive).Length
}
[ordered]@{ schemaVersion = 1; tag = $Tag; plugins = @($entry) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-index.json') -Encoding utf8NoBOM
@(
    "# UniversalForward $Tag"
    ''
    $manifest.description
    ''
    "版本：$version · 运行时：.NET 10 · 下载：universalforward.zip"
) | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-notes.md') -Encoding utf8NoBOM
Write-Output "发行目录：$OutputDirectory"
