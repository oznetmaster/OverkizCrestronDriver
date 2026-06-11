param(
	[Parameter(Mandatory)][string] $ManifestPath,
	[string] $Configuration = 'Debug'
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

if (-not (Test-Path $ManifestPath)) {
	exit 0
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
$build = [int]$match.Groups['build'].Value

switch ($Configuration) {
	'Debug' {
		$newVersion = '{0}.{1}.{2}.{3}' -f $major, $minor, $release.ToString('000'), ($build + 1).ToString('0000')
	}
	'Release' {
		if (Test-IsBelowMinimumReleaseVersion -Major $major -Minor $minor -Release $release) {
			$newVersion = '{0}.{1}.{2}.0000' -f $minimumReleaseMajor, $minimumReleaseMinor, $minimumReleaseNumber.ToString('000')
		}
		else {
			$newVersion = '{0}.{1}.{2}.0000' -f $major, $minor, ($release + 1).ToString('000')
		}
	}
	default {
		exit 0
	}
}

$content = [regex]::Replace($content, '(?<="DriverVersion":\s*")[^"]+(?=")', $newVersion, 1)
$content = [regex]::Replace($content, '(?<="VersionDate":\s*")[^"]+(?=")', (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'), 1)
Set-Content -Path $ManifestPath -Value $content -NoNewline
