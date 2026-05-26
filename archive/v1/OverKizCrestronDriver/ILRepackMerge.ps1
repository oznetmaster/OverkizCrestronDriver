param(
	[Parameter(Mandatory)][string] $TargetPath,
	[Parameter(Mandatory)][string] $InputListFile,
	[Parameter(Mandatory)][string] $LibDir
)

$inputs = Get-Content $InputListFile | Where-Object { $_ -ne '' } | Where-Object { Test-Path $_ }

if ($inputs.Count -eq 0) {
	Write-Error "No valid input files found in $InputListFile"
	exit 1
}

# All DLLs in LibDir that are NOT in the merge list are passed as /lib references only
$mergeSet = $inputs | ForEach-Object { [System.IO.Path]::GetFileName($_).ToLower() }
$libArgs = @("/lib:`"$LibDir`"")

# Also add the Crestron SDK Libraries dir so EntityModel and other SDK refs resolve
$sdkLibDir = "C:\Applications (x86)\Crestron\Driver SDK\Libraries"
if (Test-Path $sdkLibDir) {
	$libArgs += "/lib:`"$sdkLibDir`""
}

# Add the .NET Framework 4.7.2 reference assemblies so Cecil can resolve type-forwarder
# chains in net472 facade assemblies without infinite recursion
$fxRefDir = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2"
if (Test-Path $fxRefDir) {
	$libArgs += "/lib:`"$fxRefDir`""
}

# Also add the runtime dir as a fallback
$fxRuntimeDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
if (Test-Path $fxRuntimeDir) {
	$libArgs += "/lib:`"$fxRuntimeDir`""
}

$inputArgs = $inputs | ForEach-Object { "`"$_`"" }

$allArgs = @("/internalize", "/allowdup", "/allowduplicateresources", "/out:`"$TargetPath`"") + $libArgs + $inputArgs

$cmd = "ilrepack " + ($allArgs -join " ")
Write-Host $cmd
Invoke-Expression $cmd
