param(
[Parameter(Mandatory)][string] $PkgFile,
[Parameter(Mandatory)][string] $ProcessorIP,
[Parameter(Mandatory)][string] $User,
[Parameter(Mandatory)][string] $Password,
[switch] $Clean   # pass -Clean to also remove the stale UsedThirdPartyDrivers entry before importing
)

Import-Module Posh-SSH -ErrorAction Stop

$cred            = [System.Management.Automation.PSCredential]::new($User, (ConvertTo-SecureString $Password -AsPlainText -Force))
$importPath      = "/user/ThirdPartyDrivers/Import"
$usedPath        = "/user/Data/UsedThirdPartyDrivers"
$deviceManifest  = "/user/Data/PyngDeviceManifest/DeviceManifest.cfg"
$localManifest   = "$env:TEMP\DeviceManifest.cfg"

# ?? Derive the UsedThirdPartyDrivers folder name from the driver manifest ?????
# Convention: <manufacturer>.<basemodel>.<connectiontype>.<company>  (all lowercase, spaces stripped)
# e.g. "Overkiz" + "Overkiz Gateway" + "cloud" + "Neil Colvin" -> "overkiz.overkizgateway.cloud.neilcolvin"
# The connection-type segment varies; use a wildcard for that position.
$driverManifestPath = Join-Path $PSScriptRoot "Shade_Overkiz_IP_V2.json"
$driverManifest     = Get-Content $driverManifestPath -Raw | ConvertFrom-Json
$manufacturer       = ($driverManifest.GeneralInformation.Manufacturer        -replace '\s','').ToLower()
$model              = ($driverManifest.GeneralInformation.BaseModel            -replace '\s','').ToLower()
$company            = ($driverManifest.GeneralInformation.Developer.Company   -replace '\s','').ToLower()
$newVersion         =  $driverManifest.GeneralInformation.DriverVersion        # e.g. "1.0.000.0050"
$usedFolderPattern  = "$manufacturer.$model*$company"

Write-Host "Connecting to $ProcessorIP..."
$sftpSession = New-SFTPSession -ComputerName $ProcessorIP -Credential $cred -AcceptKey -ErrorAction Stop

# ?? Helper: recursively delete a remote directory by walking it manually ??????
# Posh-SSH Remove-SFTPItem has no -Recurse; delete files then the empty dir.
function Remove-SFTPDirectory {
    param([int]$SessionId, [string]$Path)
    $children = Get-SFTPChildItem -SessionId $SessionId -Path $Path -ErrorAction SilentlyContinue
    foreach ($child in $children) {
        $childPath = "$Path/$($child.Name)"
        if ($child.IsDirectory) {
            Remove-SFTPDirectory -SessionId $SessionId -Path $childPath
        } else {
            Write-Host "  Deleting file : $childPath"
            Remove-SFTPItem -SessionId $SessionId -Path $childPath -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "  Deleting dir  : $Path"
    Remove-SFTPItem -SessionId $SessionId -Path $Path -Force -ErrorAction SilentlyContinue
}

try {
    # ?? Optional: remove stale UsedThirdPartyDrivers entry ???????????????????
    if ($Clean) {
        Write-Host "Scanning $usedPath for entries matching '$usedFolderPattern'..."
        $stale = Get-SFTPChildItem -SessionId $sftpSession.SessionId -Path $usedPath -ErrorAction SilentlyContinue |
                 Where-Object { $_.Name -like $usedFolderPattern }
        if ($stale) {
            foreach ($dir in $stale) {
                Write-Host "Removing stale entry: $($dir.Name)"
                Remove-SFTPDirectory -SessionId $sftpSession.SessionId -Path "$usedPath/$($dir.Name)"
            }
            Write-Host "Stale entries removed."
        } else {
            Write-Host "No stale entries found (pattern: $usedFolderPattern)."
        }
    }

    # ?? Patch DeviceManifest.cfg — update version references for this driver ?
    Write-Host "Patching DeviceManifest.cfg (new version: $newVersion)..."
    Get-SFTPItem -SessionId $sftpSession.SessionId -Path $deviceManifest -Destination $env:TEMP -Force -ErrorAction Stop

    $cfgRaw = Get-Content $localManifest -Raw

    # The DriverInformation block for this driver contains three version-bearing fields.
    # DriverPath and DriverFolderPath embed the version as a path segment after the GUID prefix;
    # DriverVersion sits just before the DriverGuid field in the minified JSON — patch all three.
    $driverGuidBase = "$manufacturer.$model"

    # DriverPath: replace version segment between GUID prefix and /DllName.dll
    $cfgRaw = $cfgRaw -replace "(?<=`"DriverPath`":`"$driverGuidBase[^/]+/)[^/]+(?=/)", $newVersion

    # DriverFolderPath: replace version segment after GUID prefix (ends at closing quote)
    $cfgRaw = $cfgRaw -replace "(?<=`"DriverFolderPath`":`"$driverGuidBase[^/]+/)[^`"]+", $newVersion

    # DriverVersion: the value that immediately precedes the DriverGuid field for this driver.
    # Match the version value in "DriverVersion":"X.X.XXX.XXXX","DriverGuid":"overkiz.overkizgateway..."
    $cfgRaw = $cfgRaw -replace "(?<=`"DriverVersion`":`")[^`"]+(?=`",`"DriverGuid`":`"$driverGuidBase)", $newVersion

    Set-Content -Path $localManifest -Value $cfgRaw -NoNewline -Encoding UTF8
    Set-SFTPItem -SessionId $sftpSession.SessionId -Path $localManifest -Destination ($deviceManifest.Substring(0, $deviceManifest.LastIndexOf('/'))) -Force -ErrorAction Stop
    Write-Host "DeviceManifest.cfg patched and uploaded."

    # ?? Upload .pkg to Import ?????????????????????????????????????????????????
    Write-Host "Uploading $(Split-Path $PkgFile -Leaf) to $importPath ..."
    Set-SFTPItem -SessionId $sftpSession.SessionId -Path $PkgFile -Destination $importPath -Force -ErrorAction Stop
    Write-Host "Deploy complete."
}
finally {
    Remove-SFTPSession -SessionId $sftpSession.SessionId | Out-Null
}
