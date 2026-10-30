# Copyright (c) 2026 Neil Colvin. See LICENSE for terms.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactDirectory,
    [string]$PlanPath = (Join-Path $PSScriptRoot '../FrozenRelease.json'))
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
if ($plan.schemaVersion -ne 1 -or $plan.publicVersion -notmatch '^\d+\.\d+\.\d+$' -or !$plan.files.Count) { throw 'Invalid frozen release plan.' }
& "$PSScriptRoot/Assert-PublicDistributionVersion.ps1" -ReleaseVersion $plan.publicVersion
foreach ($file in $plan.sourceFiles) {
    if ($file.name -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($file.name) -or $file.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid source inventory.' }
    $sourceText = [IO.File]::ReadAllText((Join-Path "$PSScriptRoot/.." $file.name)).Replace("`r`n", "`n")
    $sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourceText)))
    if ($sourceHash -ine $file.sha256) { throw "Frozen source differs: $($file.name)" }
}
$names = @{}
foreach ($file in $plan.files) {
    if ($file.name -notmatch '^[A-Za-z0-9_.-]+$' -or $file.sha256 -notmatch '^[a-f0-9]{64}$' -or $file.bytes -le 0 -or $names.ContainsKey($file.name)) { throw 'Invalid frozen asset.' }
    $names[$file.name] = $true
    $path = Join-Path $ArtifactDirectory $file.name
    if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Frozen asset differs: $($file.name)" }
}
if (@(Get-ChildItem -LiteralPath $ArtifactDirectory -File).Count -ne $names.Count) { throw 'Unexpected release assets.' }
$pkgName = 'Shade_Overkiz_IP_V2.pkg'
$nugetName = "CrestronHomeDriver.Overkiz.Shades.$($plan.publicVersion).nupkg"
if (!$names.ContainsKey($pkgName) -or !$names.ContainsKey($nugetName)) { throw 'Freeze both driver and NuGet payloads.' }
$pkg = Join-Path $ArtifactDirectory $pkgName
& "$PSScriptRoot/Assert-DriverPackageVersion.ps1" -PackagePath $pkg -ManifestPath "$PSScriptRoot/../OverKizCrestronDriver/Shade_Overkiz_IP_V2.json" -ReleaseVersion $plan.publicVersion
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $pkg))
try {
    $entries = @($zip.Entries.FullName)
    $required = @('Shade_Overkiz_IP_V2.dll','Shade_Overkiz_IP_V2.dat','translations/en-US.json','uidefinitions/UiDefinition.xml','room/uidefinitions/UiDefinition.xml')
    if ($entries.Count -ne $required.Count -or @($entries | Sort-Object -Unique -CaseSensitive).Count -ne $entries.Count) { throw 'Unexpected public package contents.' }
    foreach ($name in $required) { if ($name -cnotin $entries) { throw "Missing public payload: $name" } }
} finally { $zip.Dispose() }
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath (Join-Path $ArtifactDirectory $nugetName)))
try {
    $spec = @($zip.Entries | Where-Object FullName -like '*.nuspec')
    if ($spec.Count -ne 1) { throw 'Expected one NuGet manifest.' }
    $reader = [IO.StreamReader]::new($spec[0].Open())
    try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($metadata.package.metadata.id -cne 'CrestronHomeDriver.Overkiz.Shades' -or $metadata.package.metadata.version -cne $plan.publicVersion) { throw 'Wrong NuGet identity.' }
    if ([string]::IsNullOrWhiteSpace($metadata.package.metadata.releaseNotes)) { throw 'NuGet release notes missing.' }
    $payloads = @($zip.Entries | Where-Object FullName -like '*.pkg')
    if ($payloads.Count -ne 1 -or $payloads[0].FullName -cne $pkgName) { throw 'Invalid NuGet payload inventory.' }
    $stream = $payloads[0].Open()
    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
    if ($hash -ne (Get-FileHash -LiteralPath $pkg).Hash.ToLowerInvariant()) { throw 'NuGet and standalone driver differ.' }
    $reader = [IO.StreamReader]::new($zip.GetEntry('crestron-driver-package.json').Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.packageId -cne 'CrestronHomeDriver.Overkiz.Shades' -or $manifest.packageVersion -cne $plan.publicVersion -or $manifest.payloadFile -cne $pkgName -or $manifest.packageType -cne 'CrestronHomeDriver' -or $manifest.containsManagedDllPayload -ne $false) { throw 'Invalid distribution manifest.' }
    $reader = [IO.StreamReader]::new($zip.GetEntry('README.md').Open())
    try { $readme = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($readme.Replace("`r`n", "`n") -cne [IO.File]::ReadAllText("$PSScriptRoot/../README.md").Replace("`r`n", "`n")) { throw 'Packaged README differs from source.' }
    if ($readme -match '\]\((?!https?://|#)[^)]+\)') { throw 'Relative link in NuGet README.' }
    if (@($zip.Entries | Where-Object FullName -Match '(?i)(LiveTestSettings|\.local\.|\.csproj.user|\.pfx$|signed\.pdf|Evidence-Annex|email-preview|\.dll$)').Count) { throw 'Private, submission-only or DLL payload in distribution wrapper.' }
} finally { $zip.Dispose() }
Write-Output "Verified $($plan.files.Count) frozen public assets, standard naming, metadata, documentation and identical NuGet driver bytes."
