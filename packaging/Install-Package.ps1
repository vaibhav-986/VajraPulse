[CmdletBinding()]
param([switch]$ApproveLocalDevelopmentCertificate)
$ErrorActionPreference = 'Stop'
if (-not $ApproveLocalDevelopmentCertificate) {
 throw 'This optional install creates a local code-signing certificate, trusts its PUBLIC key in CurrentUser\TrustedPeople, and installs the notification-capable package for this Windows user. It does not enable notifications automatically. Read README.md, then rerun with -ApproveLocalDevelopmentCertificate only if you approve those changes.'
}
if (Get-Process VajraPulse -ErrorAction SilentlyContinue) { throw 'Quit Vajra Pulse from its tray menu before installing.' }
$package = Join-Path $PSScriptRoot 'VajraPulse-unsigned.msix'
$tools = Join-Path $PSScriptRoot 'sdk-tools-10.0.28000.2957'
$signTool = Join-Path $tools 'signtool.exe'
if (-not (Test-Path $package) -or -not (Test-Path $signTool)) { throw 'Build the package first with Build-Package.ps1.' }
if ((Get-AuthenticodeSignature $signTool).Status -ne 'Valid') { throw 'Microsoft SignTool signature is not valid.' }
$subject = 'CN=Vajra Pulse Local Development'
$signed = Join-Path $PSScriptRoot 'VajraPulse-local-signed.msix'
if (Test-Path $signed) { throw 'A signed package already exists. Move the previous package before creating another.' }
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(7) } | Select-Object -First 1
if ($null -eq $cert) {
 $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'Vajra Pulse local package signing' -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyUsage DigitalSignature -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}') -NotAfter (Get-Date).AddYears(1)
}
$publicCert = Join-Path $PSScriptRoot 'VajraPulse-public.cer'
Export-Certificate -Cert $cert -FilePath $publicCert | Out-Null
Import-Certificate -FilePath $publicCert -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
Copy-Item $package $signed
& $signTool sign /fd SHA256 /s My /sha1 $cert.Thumbprint $signed
if ($LASTEXITCODE -ne 0) { throw 'Package signing failed.' }
Add-AppxPackage -Path $signed
Write-Output 'Installed for the current Windows user. Launch Vajra Pulse from Start, open Settings, and explicitly enable notification previews to request Windows permission.'
Write-Output ('Local signing certificate thumbprint: ' + $cert.Thumbprint)
Write-Output 'The private key remains in your Windows certificate store; no private-key file was exported.'
