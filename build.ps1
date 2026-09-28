#requires -Version 7
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/plugins')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Plugins.UniversalForward/Plugins.UniversalForward.csproj'
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'universalforward'
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
    throw "发行目录必须为空：$output"
}
$source = Join-Path $PSScriptRoot "artifacts/builds/$([Guid]::NewGuid().ToString('N'))"
& dotnet publish $project -c $Configuration --no-self-contained -o $source `
    --disable-build-servers -p:CopyLocalLockFileAssemblies=true
if ($LASTEXITCODE -ne 0) { throw '插件构建失败。' }

New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    if ($file.Name -like 'Router.Contracts.*') { continue }
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $destination = Join-Path $output $relative
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$entryDll = Join-Path $output 'Plugins.UniversalForward.dll'
if (-not (Test-Path -LiteralPath $entryDll)) { throw '缺少插件入口 DLL。' }
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$description = $projectXml.SelectSingleNode('/Project/PropertyGroup/Description').InnerText.Trim()
if ([string]::IsNullOrWhiteSpace($description)) { throw '插件 Description 不能为空。' }
[ordered]@{
    schemaVersion = 1
    id = 'universalforward'
    name = 'UniversalForward'
    description = $description
    runtime = 'dotnet'
    version = [Reflection.AssemblyName]::GetAssemblyName($entryDll).Version.ToString()
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'plugin.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/Plugins.UniversalForward/README.md') -Destination $output
Write-Output "插件目录：$output"
