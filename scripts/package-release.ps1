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
# Use the license and notices shipped in the restored NuGet packages, not a mutable URL.
$assets = Get-Content -Raw (Join-Path $root 'obj\project.assets.json') | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
# Culture-invariant: on Turkish Windows 'NOTICE' would not match 'Notices' case-insensitively (dotted/dotless i).
$nameMatch = [System.Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant'
foreach ($packageId in @('microsoft.ml.onnxruntime','microsoft.ml.onnxruntime.managed')) {
    $relative = ($assets.libraries.PSObject.Properties | Where-Object { $_.Name -like "$packageId/*" } | Select-Object -First 1).Value.path
    if (-not $relative) { throw "Missing restored package $packageId" }
    $package = $packageRoots | ForEach-Object { Join-Path $_ $relative } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $package) { throw "Missing package directory: $packageId" }
    $documents = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object { [regex]::IsMatch($_.Name, '^(LICENSE|LICENCE|ThirdPartyNotices|THIRD-PARTY-NOTICES|NOTICE)(\.[a-z0-9]+)?$', $nameMatch) })
    if (-not ($documents | Where-Object { [regex]::IsMatch($_.Name, '^(LICENSE|LICENCE)(\.|$)', $nameMatch) })) { throw "Missing license in $packageId" }
    if ($packageId -eq 'microsoft.ml.onnxruntime' -and -not ($documents | Where-Object { [regex]::IsMatch($_.Name, 'NOTICE', $nameMatch) })) { throw "Missing notices in $packageId" }
    foreach ($doc in $documents) {
        $relativeDoc = $doc.FullName.Substring($package.Length).TrimStart('\','/')
        $name = "$packageId-$($relativeDoc -replace '[\\/]','-')"
        Copy-Item -LiteralPath $doc.FullName -Destination (Join-Path $stage "licenses\$name")
    }
}
# Record the source revision inside the distributable.
$commit = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Unable to identify source revision' }
Set-Content -LiteralPath (Join-Path $stage 'BUILD-COMMIT.txt') -Value $commit -Encoding ascii

$zip = Join-Path $delivery 'Petglare-Windows-x64.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Get-FileHash -Algorithm SHA256 -LiteralPath $zip
