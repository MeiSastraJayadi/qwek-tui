#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Version,
    [string]$InstallDir,
    [switch]$FromSource,
    [switch]$Uninstall,
    [switch]$NoPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

try {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch {
}

$Repo = 'kadekmei/rtchat-tui'
$AppName = 'rtchat'

function Write-Info([string]$Message) {
    Write-Host "==> $Message"
}

function Write-Err([string]$Message) {
    Write-Host "error: $Message" -ForegroundColor Red
}

$arch = $env:PROCESSOR_ARCHITECTURE
if ($env:PROCESSOR_ARCHITEW6432) {
    $arch = $env:PROCESSOR_ARCHITEW6432
}

switch ($arch) {
    'AMD64' { $RidArch = 'x64' }
    'ARM64' { $RidArch = 'arm64' }
    default { throw "Unsupported CPU architecture: $arch" }
}

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\RtChat'
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$BinaryPath = Join-Path $InstallDir "$AppName.exe"

function Get-UserPathParts {
    $value = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ([string]::IsNullOrWhiteSpace($value)) {
        return @()
    }
    return @($value -split ';' | Where-Object { $_.Trim() })
}

function Test-PathEntry([string]$Dir, [string[]]$Parts) {
    $normalized = $Dir.TrimEnd('\')
    foreach ($part in $Parts) {
        if ($part.TrimEnd('\') -ieq $normalized) {
            return $true
        }
    }
    return $false
}

function Add-ToUserPath([string]$Dir) {
    $parts = Get-UserPathParts
    if (Test-PathEntry $Dir $parts) {
        return
    }

    [Environment]::SetEnvironmentVariable('Path', ((@($parts) + $Dir) -join ';'), 'User')
    Write-Info "added $Dir to the user PATH"
}

function Remove-FromUserPath([string]$Dir) {
    $parts = Get-UserPathParts
    if (-not (Test-PathEntry $Dir $parts)) {
        return
    }

    $newParts = @($parts | Where-Object { $_.TrimEnd('\') -ine $Dir.TrimEnd('\') })
    [Environment]::SetEnvironmentVariable('Path', ($newParts -join ';'), 'User')
    Write-Info "removed $Dir from the user PATH"
}

function Invoke-Download {
    param(
        [string[]]$Urls,
        [string]$Destination
    )

    foreach ($url in $Urls) {
        try {
            Write-Info "downloading $url"
            Invoke-WebRequest -Uri $url -OutFile $Destination -UseBasicParsing
            return $true
        } catch {
            Write-Verbose $_.Exception.Message
            if (Test-Path $Destination) {
                Remove-Item $Destination -Force -ErrorAction SilentlyContinue
            }
        }
    }

    return $false
}

function Get-ReleaseUrls([string]$Asset) {
    $base = $env:RTCHAT_RELEASE_BASE
    if ($base) {
        return @("$($base.TrimEnd('/'))/$Asset")
    }

    if ($Version) {
        $urls = @("https://github.com/$Repo/releases/download/$Version/$Asset")
        if (-not $Version.StartsWith('v')) {
            $urls += "https://github.com/$Repo/releases/download/v$Version/$Asset"
        }
        return $urls
    }

    return @("https://github.com/$Repo/releases/latest/download/$Asset")
}

function Test-Checksum {
    param(
        [string]$Archive,
        [string]$ArchiveName,
        [string]$ChecksumsPath
    )

    if (-not (Test-Path $ChecksumsPath)) {
        Write-Info 'checksum file unavailable, skipping verification'
        return
    }

    $expected = $null
    foreach ($line in Get-Content $ChecksumsPath) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$' -and $Matches[2].Trim() -eq $ArchiveName) {
            $expected = $Matches[1].ToLowerInvariant()
            break
        }
    }

    if (-not $expected) {
        Write-Info "no checksum listed for $ArchiveName, skipping verification"
        return
    }

    $actual = (Get-FileHash -Path $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "Checksum verification failed for $ArchiveName"
    }

    Write-Info 'checksum verified'
}

function Install-Binary([string]$Source) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path $Source -Destination $BinaryPath -Force
}

function Install-FromSource {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The .NET 10 SDK is required for -FromSource: https://dotnet.microsoft.com/download'
    }

    $ref = if ($Version) { "refs/tags/$Version" } else { 'refs/heads/master' }
    $sourceArchive = Join-Path $temp 'source.zip'
    $sourceUrls = @("https://github.com/$Repo/archive/$ref.zip")
    if ($Version -and -not $Version.StartsWith('v')) {
        $sourceUrls += "https://github.com/$Repo/archive/refs/tags/v$Version.zip"
    }

    if (-not (Invoke-Download -Urls $sourceUrls -Destination $sourceArchive)) {
        throw 'Could not download the source archive.'
    }

    $sourceDir = Join-Path $temp 'source'
    Expand-Archive -Path $sourceArchive -DestinationPath $sourceDir -Force
    $projectDir = Get-ChildItem -Path $sourceDir -Directory | Select-Object -First 1
    if (-not $projectDir) {
        throw 'The source archive did not contain a project directory.'
    }

    Write-Info 'building from source (this may take a few minutes)'
    $publishDir = Join-Path $temp 'publish'
    & dotnet publish (Join-Path $projectDir.FullName 'RealTimeAppsTui.csproj') `
        -c Release `
        -r "win-$RidArch" `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:AssemblyName=rtchat `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }

    Install-Binary (Join-Path $publishDir "$AppName.exe")
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("rtchat-install-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    if ($Uninstall) {
        Remove-FromUserPath $InstallDir

        if (Test-Path $InstallDir) {
            Remove-Item $InstallDir -Recurse -Force
            Write-Info "removed $InstallDir"
        } else {
            Write-Info "$AppName is not installed at $InstallDir"
        }
        return
    }

    if ($FromSource) {
        Install-FromSource
    } else {
        $asset = "rtchat-win-$RidArch.zip"
        $archive = Join-Path $temp $asset
        $releaseUrls = Get-ReleaseUrls $asset

        if (Invoke-Download -Urls $releaseUrls -Destination $archive) {
            $checksums = Join-Path $temp 'rtchat-checksums.txt'
            if (Invoke-Download -Urls (Get-ReleaseUrls 'rtchat-checksums.txt') -Destination $checksums) {
                Test-Checksum -Archive $archive -ArchiveName $asset -ChecksumsPath $checksums
            }

            $extractDir = Join-Path $temp 'extract'
            Expand-Archive -Path $archive -DestinationPath $extractDir -Force
            $binary = Get-ChildItem -Path $extractDir -Filter "$AppName.exe" -Recurse | Select-Object -First 1
            if (-not $binary) {
                throw "The downloaded archive does not contain $AppName.exe."
            }

            Install-Binary $binary.FullName
        } elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
            Write-Info 'no release build available to download'
            Write-Info 'falling back to building from source with the .NET SDK'
            Install-FromSource
        } else {
            throw 'No release build available and the .NET 10 SDK was not found. Install the SDK or publish a release first.'
        }
    }

    if (-not $NoPath) {
        Add-ToUserPath $InstallDir
    }

    Write-Host ''
    Write-Info "installed $AppName to $BinaryPath"

    if ($NoPath -and -not (Test-PathEntry $InstallDir (Get-UserPathParts))) {
        Write-Info "add $InstallDir to your PATH to run '$AppName' from anywhere"
    }

    Write-Info "run '$AppName --help' from a new terminal to get started"
} catch {
    Write-Err $_.Exception.Message
    exit 1
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
