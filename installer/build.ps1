# Publishes and packages StickFix into installer/output/StickFix-Setup-<version>.exe.
# Needs the .NET 10 SDK and Inno Setup 6. Driver setups are downloaded once and pinned by SHA-256.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Set-Location $root

$deps = @(
    @{ Url = 'https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe'; Sha256 = '89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A' },
    @{ Url = 'https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe'; Sha256 = 'F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6' }
)
New-Item -ItemType Directory -Force installer\deps | Out-Null
foreach ($dep in $deps) {
    $file = Join-Path 'installer\deps' ($dep.Url -split '/')[-1]
    if (-not (Test-Path $file)) { Invoke-WebRequest -UseBasicParsing $dep.Url -OutFile $file }
    if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $dep.Sha256) { throw "$file doesn't match its pinned hash. Not packaging it." }
    if ((Get-AuthenticodeSignature $file).Status -ne 'Valid') { throw "$file has no valid signature. Not packaging it." }
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" }
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
& $dotnet publish src\StickFix.App -c Release -r win-x64 --self-contained -p:DebugType=none -o publish\app --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$iscc = @((Get-Command ISCC -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
& $iscc "/DAppVersion=$version" /Q installer\StickFix.iss
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
Get-ChildItem installer\output | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB) } }
