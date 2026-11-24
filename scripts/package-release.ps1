$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$root = (Get-Location).Path
$delivery = Join-Path $root 'Teslim'
$stage = Join-Path $delivery 'release-package'
dotnet publish .\Petglare.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=None -o $delivery
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage 'Assets') | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage 'licenses') | Out-Null
Copy-Item -LiteralPath (Join-Path $delivery 'Petglare.exe') -Destination $stage
foreach ($file in @('Kur.cmd','Hooklari-Kaldir.cmd','README.md','README.tr.md','LICENSE','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $stage }
Copy-Item -LiteralPath (Join-Path $root 'Assets\Petglare-README-Banner-2400x750.png') -Destination (Join-Path $stage 'Assets')
# Include the license and notices for the exact native runtime version distributed.
$upstream = 'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.20.1'
Invoke-WebRequest -UseBasicParsing -Uri "$upstream/LICENSE" -OutFile (Join-Path $stage 'licenses\ONNXRuntime-LICENSE.txt')
Invoke-WebRequest -UseBasicParsing -Uri "$upstream/ThirdPartyNotices.txt" -OutFile (Join-Path $stage 'licenses\ONNXRuntime-ThirdPartyNotices.txt')
$zip = Join-Path $delivery 'Petglare-Windows-x64.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Get-FileHash -Algorithm SHA256 -LiteralPath $zip
