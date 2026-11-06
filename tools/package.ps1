#requires -Version 5.1
param(
 [string]$Publisher, [string]$IdentityName, [string]$PublisherDisplayName, [version]$PackageVersion,
 [string]$CertificatePath, [string]$CertificatePassword,
 [string]$CertificateThumbprint, [switch]$NoRestore, [switch]$Unsigned, [switch]$UseInstalledDotNet, [switch]$SharedWindowsAppSdk, [string]$ArtifactDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$artifacts = if ($ArtifactDirectory) { [IO.Path]::GetFullPath($ArtifactDirectory) } else { $artifactRoot }
if ($artifacts -ne $artifactRoot -and !$artifacts.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay within workspace artifacts' }
if (!$Unsigned -and !$CertificatePath -and !$CertificateThumbprint) { throw 'Supply -CertificateThumbprint or -CertificatePath; use -Unsigned explicitly for a development candidate or Store submission.' }
if ($Unsigned -and ($CertificatePath -or $CertificateThumbprint)) { throw 'Unsigned cannot be combined with a signing certificate' }
if ($SharedWindowsAppSdk) {
 [xml]$project = Get-Content "$root/src/LockPeek.App/LockPeek.App.csproj"
 $runtime = $project.Project.ItemGroup.PackageReference | Where-Object { $_.Include -eq 'Microsoft.WindowsAppSDK.Runtime' }
 if ($runtime.Version -ne '2.6.5') { throw 'Update the shared Windows App SDK dependency in this script to match the runtime package.' }
}
$stage = Join-Path $artifacts ('package.build-' + [Guid]::NewGuid().ToString('N'))
$finalStage = Join-Path $artifacts 'package'
$backup = Join-Path $artifacts ('package.previous-' + [Guid]::NewGuid().ToString('N'))
$builtMsix = Join-Path $artifacts ('LockPeek.build-' + [Guid]::NewGuid().ToString('N') + '.msix')
$finalMsix = Join-Path $artifacts 'LockPeek-x64.msix'
function Assert-ArtifactPath([string]$path) {
 $resolved = [IO.Path]::GetFullPath($path)
 if (!$resolved.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact path escaped workspace' }
}
foreach ($path in @($stage,$finalStage,$backup,$builtMsix,$finalMsix)) { Assert-ArtifactPath $path }
New-Item -ItemType Directory -Force $stage | Out-Null
$resourceMapName = if ($IdentityName) { $IdentityName } else { ([xml](Get-Content "$root/packaging/AppxManifest.xml")).Package.Identity.Name }
$selfContained = if ($UseInstalledDotNet) { 'false' } else { 'true' }
$sharedUi = if ($SharedWindowsAppSdk) { 'true' } else { 'false' }
$embeddedUi = if ($SharedWindowsAppSdk) { 'false' } else { 'true' }
$publishArgs = @('publish',"$root/src/LockPeek.App",'-c','Release','-o',$stage,'--self-contained',$selfContained,"-p:UseSharedWindowsAppSdk=$sharedUi","-p:WindowsAppSDKSelfContained=$embeddedUi","-p:LockPeekResourceMapName=$resourceMapName")
if ($SharedWindowsAppSdk) { $publishArgs += '-p:WindowsAppSdkBootstrapInitialize=false' }
if ($NoRestore) { $publishArgs += '--no-restore' }
$dotnet = if (Test-Path "$root/tools/toolchain/dotnet10/dotnet.exe") { "$root/tools/toolchain/dotnet10/dotnet.exe" } else { "dotnet" }
& $dotnet @publishArgs
if ($LASTEXITCODE) { throw 'WinUI publish failed; previous package preserved' }
& "$PSScriptRoot/build-shell.ps1"
Copy-Item -LiteralPath "$root/artifacts/native/LockPeek.Shell.dll" -Destination $stage
New-Item -ItemType Directory -Force "$stage/assets/context-menu" | Out-Null
Copy-Item "$root/assets/context-menu/lockpeek-*.ico" "$stage/assets/context-menu"
Copy-Item -LiteralPath "$root/assets/context-menu/recycle.ico" -Destination "$stage/assets/context-menu"
# Keep the manifest-referenced tile images at the package root. This also
# gives package validators a direct file path independent of nested PRI lookup.
foreach ($logo in (Get-ChildItem -Path "$root/assets/msix/*.png" | Select-Object -ExpandProperty Name)) {
 Copy-Item -LiteralPath (Join-Path "$root/assets/msix" $logo) -Destination (Join-Path $stage $logo)
}
[xml]$manifest = Get-Content "$root/packaging/AppxManifest.xml"
if ($Publisher) { $manifest.Package.Identity.Publisher = $Publisher }
if ($IdentityName) { $manifest.Package.Identity.Name = $IdentityName }
if ($PublisherDisplayName) { $manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName }
$manifest.Package.Properties.Logo = 'StoreLogo.png'
$visualElements = $manifest.Package.Applications.Application.VisualElements
$visualElements.Square44x44Logo = 'Square44x44Logo.png'
$visualElements.Square150x150Logo = 'Square150x150Logo.png'
if ($PackageVersion) {
 if ($PackageVersion.Build -lt 0 -or $PackageVersion.Revision -ne 0) { throw 'Supply a four-part package version with a final component of zero.' }
 $manifest.Package.Identity.Version = $PackageVersion.ToString()
}
if ($SharedWindowsAppSdk) {
 # Keep these values aligned with the Microsoft.WindowsAppSDK.Runtime package in the app project.
 $dependency = $manifest.CreateElement('PackageDependency', $manifest.DocumentElement.NamespaceURI)
 $dependency.SetAttribute('Name', 'Microsoft.WindowsAppRuntime.2')
 $dependency.SetAttribute('Publisher', 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US')
 $dependency.SetAttribute('MinVersion', '2.6.5.0')
 [void]$manifest.Package.Dependencies.AppendChild($dependency)
}
$manifest.Save("$stage/AppxManifest.xml")
$sdk = Get-Item "$env:USERPROFILE/.nuget/packages/microsoft.windows.sdk.buildtools/10.0.26100.4654/bin/10.0.26100.0/x64/makeappx.exe" -ErrorAction SilentlyContinue
if (!$sdk) { throw 'Windows SDK makeappx.exe not available' }
# The package primary map must include compiled XAML AND qualified icons.
# The WinUI build indexes both; never replace it with an icon-only map.
Copy-Item -LiteralPath "$stage/LockPeek.pri" -Destination "$stage/resources.pri" -Force
$resourceDump = Join-Path $artifacts 'package-resource-map.xml'
& (Join-Path $sdk.DirectoryName 'makepri.exe') dump /if "$stage/resources.pri" /of $resourceDump /o > "$artifacts/resource-verification.log"
if ($LASTEXITCODE) { throw 'Cannot validate package resource map' }
$resourceText = Get-Content -LiteralPath $resourceDump -Raw
[xml]$resourceXml = $resourceText
$primaryMap = @($resourceXml.PriInfo.ResourceMap) | Where-Object { $_.primary -eq 'true' }
if (@($primaryMap).Count -ne 1 -or $primaryMap.name -ne $manifest.Package.Identity.Name) {
 throw "Primary resource map must match package identity '$($manifest.Package.Identity.Name)'; found '$($primaryMap.name)'. Clean and rebuild the app."
}
foreach ($required in @('App.xbf','MainWindow.xbf','StoreLogo.png','Square44x44Logo.png','Square150x150Logo.png')) {
 if (!$resourceText.Contains('name="' + $required + '"')) { throw "Package resource map is missing $required" }
}
foreach ($logo in @($manifest.Package.Properties.Logo, $manifest.Package.Applications.Application.VisualElements.Square44x44Logo, $manifest.Package.Applications.Application.VisualElements.Square150x150Logo)) {
 if (!(Test-Path -LiteralPath (Join-Path $stage $logo) -PathType Leaf)) { throw "Manifest logo file is missing: $logo" }
}

& $sdk.FullName pack /d $stage /p $builtMsix /o > "$artifacts/makeappx.log"
if ($LASTEXITCODE) { throw 'MSIX validation/pack failed; previous package preserved' }
if ($CertificatePath -and $CertificateThumbprint) { throw 'Choose certificate file or store thumbprint, not both' }
if ($CertificatePath -or $CertificateThumbprint) {
 $sign = Join-Path $sdk.DirectoryName 'signtool.exe'
 $signArgs = @('sign','/fd','SHA256')
 if ($CertificateThumbprint) { $signArgs += @('/sha1',$CertificateThumbprint,'/s','My') }
 else {
  $signArgs += @('/f',$CertificatePath)
  if ($CertificatePassword) { $signArgs += @('/p',$CertificatePassword) }
 }
 & $sign @signArgs $builtMsix
 if ($LASTEXITCODE) { throw 'Signing failed; previous installer preserved' }
 if ((Get-AuthenticodeSignature -LiteralPath $builtMsix).Status -ne 'Valid') { throw 'Signature trust verification failed; previous installer preserved' }
}
# Promote only after publish, validation and requested signing all succeed.
if (Test-Path -LiteralPath $finalStage) { Move-Item -LiteralPath $finalStage -Destination $backup }
try { Move-Item -LiteralPath $stage -Destination $finalStage }
catch {
 if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $finalStage }
 throw
}
Move-Item -LiteralPath $builtMsix -Destination $finalMsix -Force
if (Test-Path -LiteralPath $backup) {
 Assert-ArtifactPath $backup
 try { Remove-Item -LiteralPath $backup -Recurse -Force }
 catch { Write-Warning "Previous package retained at $backup because files are in use." }
}
Get-Item -LiteralPath $finalMsix | Select-Object FullName,Length
