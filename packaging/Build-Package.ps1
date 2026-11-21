param([string]$SdkVersion = '10.0.28000.2957', [switch]$ToolsOnly)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$build = Join-Path $project 'bin\Release\net8.0-windows10.0.19041.0'
if (-not (Test-Path (Join-Path $build 'VajraPulse.exe'))) { throw 'Build the Release application first.' }
$tools = Join-Path $PSScriptRoot ('sdk-tools-' + $SdkVersion)
if (-not (Test-Path (Join-Path $tools 'makeappx.exe'))) {
 $download = Join-Path ([System.IO.Path]::GetTempPath()) ('pulse-sdk-' + [Guid]::NewGuid().ToString('N'))
 [void][System.IO.Directory]::CreateDirectory($download)
 $archive = Join-Path $download 'sdk.nupkg'
 Invoke-WebRequest -UseBasicParsing -Uri ('https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/' + $SdkVersion + '/microsoft.windows.sdk.buildtools.' + $SdkVersion + '.nupkg') -OutFile $archive
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $extracted = Join-Path $download 'extracted'
 [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
 $make = [System.IO.Directory]::EnumerateFiles($extracted, 'makeappx.exe', [System.IO.SearchOption]::AllDirectories) | Where-Object { $_ -match '\\x64\\' } | Select-Object -First 1
 if ($null -eq $make) { throw 'Microsoft SDK package contains no x64 MakeAppx tool.' }
 if ((Get-AuthenticodeSignature $make).Status -ne 'Valid') { throw 'MakeAppx signature is not valid.' }
 [void][System.IO.Directory]::CreateDirectory($tools)
 Copy-Item -Path (Join-Path (Split-Path $make -Parent) '*') -Destination $tools -Recurse
}
if ($ToolsOnly) { Write-Output ('Microsoft SDK tools ready: ' + $tools); return }
$stage = Join-Path $PSScriptRoot ('payload-' + [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($stage)
Copy-Item -Path (Join-Path $build '*') -Destination $stage -Recurse
Copy-Item (Join-Path $PSScriptRoot 'Package.appxmanifest') (Join-Path $stage 'AppxManifest.xml')
$assets = Join-Path $stage 'Assets'
[void][System.IO.Directory]::CreateDirectory($assets)
Add-Type -AssemblyName System.Drawing
foreach ($size in @(44,50,150)) {
 $bitmap = New-Object System.Drawing.Bitmap($size,$size)
 $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
 try {
  $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $graphics.Clear([System.Drawing.Color]::Black)
  $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245,245,247))
  try { $points = [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF(($size*0.5),($size*0.2))),(New-Object System.Drawing.PointF(($size*0.72),($size*0.5))),(New-Object System.Drawing.PointF(($size*0.5),($size*0.8))),(New-Object System.Drawing.PointF(($size*0.28),($size*0.5)))); $graphics.FillPolygon($brush,$points) } finally { $brush.Dispose() }
  $bitmap.Save((Join-Path $assets ('Logo' + $size + '.png')),[System.Drawing.Imaging.ImageFormat]::Png)
 } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
$out = Join-Path $PSScriptRoot 'VajraPulse-unsigned.msix'
if (Test-Path $out) { throw 'Package output already exists. Move the previous package before rebuilding.' }
& (Join-Path $tools 'makeappx.exe') pack /d $stage /p $out
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }
Write-Output ('Unsigned notification-capable package: ' + $out)
Write-Output 'No certificates were created or trusted and no package was installed.'
