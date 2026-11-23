param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$rpfProjectRoot = $PSScriptRoot
$rpfPublishPath = [IO.Path]::GetFullPath($OutputDirectory)
$rpfOfflineFfmpeg = Join-Path $rpfProjectRoot 'Tools\ffmpeg.exe'
if (!(Test-Path -LiteralPath $rpfOfflineFfmpeg)) {
    $rpfOfflineFfmpeg = Join-Path $rpfPublishPath 'Tools\ffmpeg.exe'
}
if (Test-Path -LiteralPath $rpfOfflineFfmpeg) {
    $rpfFfmpegConfiguration = & {
        $ErrorActionPreference = 'Continue'
        & $rpfOfflineFfmpeg -hide_banner -buildconf 2>&1 | ForEach-Object { $_.ToString() }
    }
    if ($LASTEXITCODE -ne 0 -or !($rpfFfmpegConfiguration -match '^\s*--disable-network\s*$')) {
        throw 'Build Tools\ffmpeg.exe with --disable-network before publishing.'
    }
    $rpfFfmpegProtocols = & $rpfOfflineFfmpeg -hide_banner -protocols
    $rpfProtocolNames = $rpfFfmpegProtocols | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notin @('Supported file protocols:', 'Input:', 'Output:') }
    if ($LASTEXITCODE -ne 0 -or 'file' -notin $rpfProtocolNames -or 'pipe' -notin $rpfProtocolNames -or ($rpfProtocolNames | Where-Object { $_ -notin @('file', 'pipe') })) {
        throw 'Enable only the file and pipe protocols in Tools\ffmpeg.exe.'
    }
}
dotnet publish (Join-Path $rpfProjectRoot 'App\RpfPreview.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $rpfPublishPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'rpf9-explorer publish failed.' }
dotnet publish (Join-Path $rpfProjectRoot 'Cli\RpfPreview.Cli.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $rpfPublishPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'rpf9-cli publish failed.' }
Copy-Item -LiteralPath (Join-Path $rpfProjectRoot 'README.txt') -Destination (Join-Path $rpfPublishPath 'README.txt')
Copy-Item -LiteralPath (Join-Path $rpfProjectRoot 'README.md') -Destination (Join-Path $rpfPublishPath 'README.md')
$rpfScreenshotOutput = Join-Path $rpfPublishPath 'docs\screenshots'
New-Item -ItemType Directory -Path $rpfScreenshotOutput -Force | Out-Null
foreach ($rpfDoc in @('rpf9-format-findings.md','rpf9-index-provider.md')) {
    Copy-Item -LiteralPath (Join-Path $rpfProjectRoot ('docs\' + $rpfDoc)) -Destination (Join-Path $rpfPublishPath ('docs\' + $rpfDoc))
}
foreach ($rpfScreenshotName in @('texture-search.png','texture-dictionary.png','model-preview.png','staged-texture.png','character-parts.png','model-assembly.png')) {
    Copy-Item -LiteralPath (Join-Path $rpfProjectRoot ('docs\screenshots\' + $rpfScreenshotName)) -Destination (Join-Path $rpfScreenshotOutput $rpfScreenshotName)
}
$rpfLicenseSourceRoot = Join-Path $rpfProjectRoot 'Licenses'
if (Test-Path -LiteralPath $rpfLicenseSourceRoot) {
    $rpfLicenseOutputRoot = Join-Path $rpfPublishPath 'Licenses'
    New-Item -ItemType Directory -Path $rpfLicenseOutputRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $rpfLicenseSourceRoot '*') -Destination $rpfLicenseOutputRoot -Force
}
$rpfNativeHelper = Join-Path $rpfProjectRoot 'Tools\bink-preview.exe'
if (Test-Path -LiteralPath $rpfNativeHelper) {
    $rpfToolsOutput = Join-Path $rpfPublishPath 'Tools'
    New-Item -ItemType Directory -Path $rpfToolsOutput -Force | Out-Null
    Copy-Item -LiteralPath $rpfNativeHelper -Destination (Join-Path $rpfToolsOutput 'bink-preview.exe')
}
Write-Host ('Built: ' + (Join-Path $rpfPublishPath 'rpf9-explorer.exe'))
$rpfPlayer = Join-Path $rpfProjectRoot 'Tools\bink-player.exe'
if (Test-Path -LiteralPath $rpfPlayer) {
    $rpfToolsOutput = Join-Path $rpfPublishPath 'Tools'
    New-Item -ItemType Directory -Path $rpfToolsOutput -Force | Out-Null
    Copy-Item -LiteralPath $rpfPlayer -Destination (Join-Path $rpfToolsOutput 'bink-player.exe')
}
foreach ($rpfToolName in @('opus-preview.exe','ffmpeg.exe')) {
    $rpfToolSource = Join-Path $rpfProjectRoot ('Tools\'+$rpfToolName)
    if (Test-Path -LiteralPath $rpfToolSource) {
        $rpfToolsOutput = Join-Path $rpfPublishPath 'Tools'
        New-Item -ItemType Directory -Path $rpfToolsOutput -Force | Out-Null
        Copy-Item -LiteralPath $rpfToolSource -Destination (Join-Path $rpfToolsOutput $rpfToolName)
    }
}
foreach ($rpfLicenseName in @('OPUS-LICENSE.txt','FFMPEG-LICENSE.txt','FFMPEG-BUILD-README.txt')) {
    $rpfLicenseSource = Join-Path $rpfProjectRoot ('Tools\'+$rpfLicenseName)
    if (Test-Path -LiteralPath $rpfLicenseSource) {
        $rpfLicenseOutput = Join-Path $rpfPublishPath 'Licenses'
        New-Item -ItemType Directory -Path $rpfLicenseOutput -Force | Out-Null
        Copy-Item -LiteralPath $rpfLicenseSource -Destination (Join-Path $rpfLicenseOutput $rpfLicenseName)
    }
}
