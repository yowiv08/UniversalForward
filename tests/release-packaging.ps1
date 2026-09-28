#requires -Version 7
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$ReleaseDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/release')
)
$ErrorActionPreference = 'Stop'
$index = Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'release-index.json') -Raw | ConvertFrom-Json
if ($index.schemaVersion -ne 1 -or $index.tag -cne $Tag -or $index.plugins.Count -ne 1) {
    throw '发行索引无效。'
}
$entry = $index.plugins[0]
if ($entry.id -cne 'universalforward' -or $entry.runtime -cne 'dotnet' -or $entry.asset -cne 'universalforward.zip') {
    throw '插件标识或资产名称无效。'
}
$archivePath = Join-Path $ReleaseDirectory $entry.asset
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256 -or
    (Get-Item -LiteralPath $archivePath).Length -ne $entry.sizeBytes) {
    throw 'ZIP 校验失败。'
}
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($required in @('Plugins.UniversalForward.dll', 'Plugins.UniversalForward.deps.json', 'plugin.json', 'README.md')) {
        if ("universalforward/$required" -cnotin $names) { throw "包内缺少 $required" }
    }
    $content = foreach ($file in ($zip.Entries | Sort-Object FullName)) {
        $name = $file.FullName.Replace('\', '/')
        if (-not $name.StartsWith('universalforward/', [StringComparison]::Ordinal) -or
            $name -match '(^|/)\.\.(/|$)' -or $name -match '(^|/)Router\.Contracts\.' -or
            $name -match '(?i)(^|/)(Config\.json|\.env[^/]*|.*\.(db|key|pem|pfx))$') {
            throw "包内文件无效：$name"
        }
        if ($name.EndsWith('/')) { continue }
        $stream = $file.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        "$($name.Substring('universalforward/'.Length)) $hash"
        if ($name -ceq 'universalforward/plugin.json') {
            $reader = [IO.StreamReader]::new($file.Open())
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
        }
    }
    $contentHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($content -join "`n"))).ToLowerInvariant()
    if ($contentHash -cne $entry.contentSha256) { throw '包内文件内容校验失败。' }
    if ($manifest.id -cne $entry.id -or $manifest.version -cne $entry.version -or
        $manifest.description -cne $entry.description -or $manifest.runtime -cne $entry.runtime) {
        throw '插件元数据与索引不一致。'
    }
}
finally { $zip.Dispose() }
Write-Output 'Release package validation passed.'
