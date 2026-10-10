$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$DotnetDir = Join-Path $ProjectRoot '.dotnet'
$DotnetExe = Join-Path $DotnetDir 'dotnet.exe'
$SdkVersion = '8.0.425'

function Get-Arch {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    switch ($arch) {
        'x64'   { return 'x64' }
        'arm64' { return 'arm64' }
        'x86'   { return 'x86' }
        default { throw "ClipVoice: unsupported Windows architecture: $arch. Supported: x64, x86, arm64." }
    }
}

function Test-RequiredSdk {
    if (-not (Test-Path $DotnetExe)) { return $false }
    try {
        $installed = & $DotnetExe --list-sdks 2>$null
        return ($installed | Select-String -SimpleMatch $SdkVersion) -ne $null
    }
    catch {
        return $false
    }
}

if (Test-RequiredSdk) {
    Write-Host "ClipVoice: portable .NET SDK $SdkVersion is ready." -ForegroundColor Green
    exit 0
}

$arch = Get-Arch
$urls = @(
    "https://builds.dotnet.microsoft.com/dotnet/Sdk/$SdkVersion/dotnet-sdk-$SdkVersion-win-$arch.zip",
    "https://dotnetcli.azureedge.net/dotnet/Sdk/$SdkVersion/dotnet-sdk-$SdkVersion-win-$arch.zip"
)
$tempZip = Join-Path $env:TEMP "clipvoice-dotnet-sdk-$SdkVersion-win-$arch.zip"
$tempDir = Join-Path $env:TEMP "clipvoice-dotnet-$([guid]::NewGuid().ToString('N'))"

Write-Host "ClipVoice: first debug run. Downloading portable .NET SDK $SdkVersion ($arch)..." -ForegroundColor Cyan
Write-Host "Nothing will be installed into Windows. SDK will live only in: $DotnetDir" -ForegroundColor DarkGray

try {
    if (Test-Path $tempZip) { Remove-Item $tempZip -Force }
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    # TLS 1.2 for older Windows PowerShell builds.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $downloaded = $false
    foreach ($url in $urls) {
        try {
            Write-Host "ClipVoice: download source $url" -ForegroundColor DarkGray
            Invoke-WebRequest -Uri $url -OutFile $tempZip -UseBasicParsing
            if ((Test-Path $tempZip) -and ((Get-Item $tempZip).Length -gt 100MB)) { $downloaded = $true; break }
        }
        catch {
            Write-Host "ClipVoice: this download source failed, trying another..." -ForegroundColor Yellow
            if (Test-Path $tempZip) { Remove-Item $tempZip -Force -ErrorAction SilentlyContinue }
        }
    }
    if (-not $downloaded) { throw 'Could not download the official portable .NET SDK. Check internet access and Windows firewall/proxy.' }

    Write-Host 'ClipVoice: unpacking local SDK...' -ForegroundColor Cyan
    Expand-Archive -Path $tempZip -DestinationPath $tempDir -Force

    if (-not (Test-Path (Join-Path $tempDir 'dotnet.exe'))) {
        throw 'Downloaded archive does not contain dotnet.exe.'
    }

    if (Test-Path $DotnetDir) { Remove-Item $DotnetDir -Recurse -Force }
    Move-Item $tempDir $DotnetDir

    $env:DOTNET_ROOT = $DotnetDir
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    $actual = & $DotnetExe --version
    if ($actual -ne $SdkVersion) {
        throw "Expected SDK $SdkVersion, got $actual"
    }

    Write-Host "ClipVoice: portable .NET SDK $actual is ready." -ForegroundColor Green
}
finally {
    if (Test-Path $tempZip) { Remove-Item $tempZip -Force -ErrorAction SilentlyContinue }
    if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
}
