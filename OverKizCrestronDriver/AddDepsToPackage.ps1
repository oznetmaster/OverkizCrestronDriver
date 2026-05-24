param(
	[Parameter(Mandatory)][string] $PkgFile,
	[Parameter(Mandatory)][string] $DepListFile
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

$DepFiles = Get-Content $DepListFile | Where-Object { $_ -ne '' }

$zip = [System.IO.Compression.ZipFile]::Open($PkgFile, [System.IO.Compression.ZipArchiveMode]::Update)
try {
	# Add dependency DLLs
	foreach ($dep in $DepFiles) {
		if (-not (Test-Path $dep)) { continue }
		$entryName = [System.IO.Path]::GetFileName($dep)
		if (-not $zip.GetEntry($entryName)) {
			[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $dep, $entryName) | Out-Null
			Write-Host "Added $entryName"
		}
	}
}
finally {
	$zip.Dispose()
}
