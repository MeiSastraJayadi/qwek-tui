[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Qwek.csproj'

if (-not $OutputDir) {
    $OutputDir = Join-Path $root 'dist'
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

if (-not $Version) {
    try {
        $Version = & git -C $root describe --tags --always 2>$null | Select-Object -First 1
    } catch {
    }
}
if (-not $Version) {
    $Version = '0.0.0'
}

$numericVersion = if ($Version -match '^v?(\d+\.\d+\.\d+)') { $Matches[1] } else { '0.0.0' }

$tar = Get-Command tar -ErrorAction SilentlyContinue
if (-not $tar) {
    throw 'tar is required to package Linux builds.'
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Get-ChildItem -Path $OutputDir -File |
    Where-Object {
        $_.Name -match '^qwek-(win|linux)-(x64|arm64)\.(zip|tar\.gz)$' -or
        $_.Name -eq 'qwek-checksums.txt'
    } |
    Remove-Item -Force
Remove-Item (Join-Path $OutputDir '.work') -Recurse -Force -ErrorAction SilentlyContinue

$rids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')

foreach ($rid in $rids) {
    $work = Join-Path $OutputDir ".work/$rid"

    Write-Host "==> publishing $rid"
    & dotnet publish $project `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:AssemblyName=qwek `
        -p:Version=$numericVersion `
        -o $work

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid."
    }

    if ($rid -like 'win-*') {
        Compress-Archive -Path (Join-Path $work 'qwek.exe') `
            -DestinationPath (Join-Path $OutputDir "qwek-$rid.zip") -Force
    } else {
        Push-Location $work
        try {
            & tar --mode=755 -czf "../../qwek-$rid.tar.gz" 'qwek'
            if ($LASTEXITCODE -ne 0) {
                throw "tar failed for $rid."
            }
        } finally {
            Pop-Location
        }
    }
}

$archives = Get-ChildItem -Path $OutputDir -File |
    Where-Object { $_.Name -like 'qwek-*.zip' -or $_.Name -like 'qwek-*.tar.gz' } |
    Sort-Object Name

$lines = foreach ($archive in $archives) {
    $hash = (Get-FileHash -Path $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($archive.Name)"
}
$lines | Set-Content -Path (Join-Path $OutputDir 'qwek-checksums.txt') -Encoding ascii

Remove-Item (Join-Path $OutputDir '.work') -Recurse -Force

Write-Host ''
Write-Host "Built Qwek TUI $numericVersion packages in ${OutputDir}:"
foreach ($archive in $archives) {
    Write-Host ("  {0} ({1:N1} MB)" -f $archive.Name, ($archive.Length / 1MB))
}
