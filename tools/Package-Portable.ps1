param(
    [string]$PublishDirectory = 'artifacts/windows',
    [string]$ZipPath = 'artifacts/SPT-Linux-Installer-Windows-x64.zip'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($PublishDirectory)) {
    $PublishDirectory = Join-Path $repositoryRoot $PublishDirectory
}
if (-not [IO.Path]::IsPathRooted($ZipPath)) {
    $ZipPath = Join-Path $repositoryRoot $ZipPath
}
$executable = Join-Path $PublishDirectory 'SPT-Linux-Installer.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'Publish the Windows application before packaging it.'
}

$encoding = [Text.UTF8Encoding]::new($false)
$content = [Text.StringBuilder]::new()
[void]$content.Append("SPT Linux Installer`r`n===================`r`n`r`n")
[void]$content.Append([IO.File]::ReadAllText((Join-Path $repositoryRoot 'LICENSE'), $encoding))
$licenseRoot = Join-Path $repositoryRoot 'licenses'
$manifest = Get-Content -LiteralPath (Join-Path $licenseRoot 'sources.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    $source = Join-Path $licenseRoot $entry.file
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $entry.sha256) {
        throw "License checksum mismatch: $($entry.file)"
    }
    [void]$content.Append("`r`n`r`n============================================================`r`n")
    [void]$content.Append("$($entry.component) $($entry.version)`r`n$($entry.file)`r`n")
    [void]$content.Append("============================================================`r`n`r`n")
    [void]$content.Append([IO.File]::ReadAllText($source, $encoding))
}
$licenseFile = Join-Path $PublishDirectory 'LICENSE.txt'
[IO.File]::WriteAllText($licenseFile, $content.ToString(), $encoding)
$archiveDirectory = Split-Path -Parent $ZipPath
New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
Compress-Archive -LiteralPath @($executable, $licenseFile) -DestinationPath $ZipPath -Force
Write-Output "Ready: $ZipPath"
