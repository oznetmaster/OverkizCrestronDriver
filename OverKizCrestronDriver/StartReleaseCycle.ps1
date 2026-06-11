param (
	[Parameter(Mandatory)]
	[string]$ManifestPath
)

$minimumReleaseMajor = 2
$minimumReleaseMinor = 3
$minimumReleaseNumber = 1

function Test-IsBelowMinimumReleaseVersion {
	param(
		[int] $Major,
		[int] $Minor,
		[int] $Release
	)

	if ($Major -ne $minimumReleaseMajor) {
		return $Major -lt $minimumReleaseMajor
	}

	if ($Minor -ne $minimumReleaseMinor) {
		return $Minor -lt $minimumReleaseMinor
	}

	return $Release -lt $minimumReleaseNumber
}

$content = Get-Content $ManifestPath -Raw

$match = [regex]::Match($content, '(?<="DriverVersion":\s*")(?<major>\d+)\.(?<minor>\d+)\.(?<release>\d+)\.(?<build>\d+)(?=")')
if (-not $match.Success) {
	Write-Warning 'DriverVersion must contain four numeric components.'
	exit 0
}

$major = [int]$match.Groups['major'].Value
$minor = [int]$match.Groups['minor'].Value
$release = [int]$match.Groups['release'].Value

if (Test-IsBelowMinimumReleaseVersion -Major $major -Minor $minor -Release $release) {
	$newVersion = '{0}.{1}.{2}.0' -f $minimumReleaseMajor, $minimumReleaseMinor, $minimumReleaseNumber
}
else {
	$newVersion = '{0}.{1}.{2}.0' -f $major, $minor, ($release + 1)
}

$content = [regex]::Replace($content, '(?<="DriverVersion":\s*")[^"]+(?=")', $newVersion, 1)

$now = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
$content = $content -replace '(?<="VersionDate":\s*")[^"]+', $now

Set-Content -Path $ManifestPath -Value $content -NoNewline

$updatedContent = Get-Content $ManifestPath -Raw
$versionMatch = [regex]::Match($updatedContent, '(?<="DriverVersion":\s*")(?<version>\d+\.\d+\.\d+\.\d+)(?=")')
$nextTag = if ($versionMatch.Success) { 'v' + $versionMatch.Groups['version'].Value } else { $null }

Write-Host "Started new release cycle and set VersionDate to $now in $ManifestPath"
if ($nextTag) {
	Write-Host "Next release tag must be $nextTag"
}
