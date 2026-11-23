param([string]$Output = 'artifacts')
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $root $Output }
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version: $version" }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'installer/ACadSmartLeader.bundle/PackageContents.xml') -Raw
if ($manifest.ApplicationPackage.AppVersion -ne $version) { throw 'PackageContents.xml AppVersion must match Directory.Build.props Version' }

# Validate every input before replacing any previous output. A fresh staging
# directory prevents stale DLLs from an earlier build leaking into the release.
$targets = @(@('net47','AutoCAD2020'), @('net48','AutoCAD2021-2024'), @('net8.0-windows','AutoCAD2025'))
$inputs = @('installer/INSTALL.md', 'THIRD-PARTY-NOTICES.txt', 'examples/label-rules.json', 'examples/demo-schema.json')
$inputs += $targets | ForEach-Object { 'src/ACadSmartLeader/bin/Release/' + $_[0] + '/ACadSmartLeader.dll' }
foreach ($inputFile in $inputs) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $inputFile) -PathType Leaf)) { throw "Missing release input: $inputFile" }
}

New-Item -ItemType Directory -Force $Output | Out-Null
$staging = Join-Path $Output ('manual-install-' + [Guid]::NewGuid().ToString('N'))
$archive = Join-Path $Output "ACadSmartLeader-manual-install-v$version.zip"
try {
    $bundle = Join-Path $staging 'ACadSmartLeader.bundle'
    New-Item -ItemType Directory -Force $bundle | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'installer/ACadSmartLeader.bundle/PackageContents.xml') -Destination $bundle
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Destination $bundle
    Copy-Item -LiteralPath (Join-Path $root 'installer/INSTALL.md') -Destination $staging
    foreach ($target in $targets) {
        $destination = Join-Path $bundle ('Contents/' + $target[1])
        New-Item -ItemType Directory -Force $destination | Out-Null
        Copy-Item -LiteralPath (Join-Path $root ('src/ACadSmartLeader/bin/Release/' + $target[0] + '/ACadSmartLeader.dll')) -Destination $destination
        Copy-Item -LiteralPath (Join-Path $root 'examples/label-rules.json'), (Join-Path $root 'examples/demo-schema.json') -Destination $destination
    }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $Output 'release-manifest.txt'), "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
    Write-Host "RELEASE_PACKAGE_OK: $archive"
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
