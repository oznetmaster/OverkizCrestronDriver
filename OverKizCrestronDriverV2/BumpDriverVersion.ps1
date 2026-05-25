param (
	[Parameter(Mandatory)]
	[string]$ManifestPath,
	[Parameter(Mandatory)]
	[string]$Configuration
)

$content = Get-Content $ManifestPath -Raw

if ($Configuration -eq 'Release')
	{
	# Release: increment the 3rd component (minor release), reset the 4th (debug build) to 0000
	$content = $content -replace '(?<="DriverVersion":\s*"\d+\.\d+\.)(\d+)\.\d+', {
		$_.Groups[1].Value.PadLeft(3, '0') | ForEach-Object {
			([int]$_ + 1).ToString('D3') + '.0000'
		}
	}
	}
else
	{
	# Debug: increment only the 4th component (build counter)
	$content = $content -replace '(?<="DriverVersion":\s*"\d+\.\d+\.\d+\.)(\d+)', {
		([int]$_.Value + 1).ToString('D4')
	}
	}

$now = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
$content = $content -replace '(?<="VersionDate":\s*")[^"]+', $now

Set-Content -Path $ManifestPath -Value $content -NoNewline
Write-Host "DriverVersion bumped ($Configuration) and VersionDate set to $now in $ManifestPath"
