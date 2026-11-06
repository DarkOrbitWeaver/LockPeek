param([Parameter(Mandatory)][string]$CertificateThumbprint, [switch]$Install)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$package = Join-Path $root 'artifacts\candidate\LockPeek-x64.msix'
if (!(Test-Path -LiteralPath $package)) { throw 'Build the current candidate first' }
& dotnet run --project "$root/tests/LockPeek.Tests" -c Release --no-build -- --integration | Tee-Object -FilePath "$root/artifacts/test-results-integration-current.txt"
if ($LASTEXITCODE) { throw 'Integration tests failed; package not signed or installed' }
$certificate = Get-Item -LiteralPath ("Cert:\CurrentUser\My\" + $CertificateThumbprint) -ErrorAction Stop
if (!$certificate.HasPrivateKey) { throw 'Development certificate private key unavailable' }
$signTool = Get-ChildItem "$env:USERPROFILE/.nuget/packages/microsoft.windows.sdk.buildtools/*/bin/*/x64/signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (!$signTool) { throw 'Windows SDK signing tool unavailable' }
& $signTool.FullName sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $package
if ($LASTEXITCODE) { throw 'Signing failed' }
if ((Get-AuthenticodeSignature -LiteralPath $package).Status -ne 'Valid') { throw 'Signature is not trusted' }
if ($Install) { Add-AppxPackage -Path $package -ErrorAction Stop }
Get-Item -LiteralPath $package | Select-Object FullName,Length
Get-AuthenticodeSignature -LiteralPath $package | Select-Object Status,StatusMessage
if ($Install) { Get-AppxPackage -Name LockPeek.Development | Select-Object Name,Version,Status }
