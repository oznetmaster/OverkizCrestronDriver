param(
	[Parameter(Mandatory)][string] $TargetPath,
	[Parameter(Mandatory)][string] $InputListFile,
	[Parameter(Mandatory)][string] $LibDir,
	[string] $SdkLibDir = $env:CRESTRON_DRIVER_SDK_LIBRARIES,
	[string] $FxRefDir = $env:NET472_REFERENCE_ASSEMBLIES,
	[string] $FxRuntimeDir = $env:NETFX_RUNTIME_DIR
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
if (-not [string]::IsNullOrWhiteSpace($SdkLibDir) -and (Test-Path $SdkLibDir)) {
	$libArgs += "/lib:`"$sdkLibDir`""
}

# Add the .NET Framework 4.7.2 reference assemblies so Cecil can resolve type-forwarder
# chains in net472 facade assemblies without infinite recursion
if (-not [string]::IsNullOrWhiteSpace($FxRefDir) -and (Test-Path $FxRefDir)) {
	$libArgs += "/lib:`"$fxRefDir`""
}

# Also add the runtime dir as a fallback
if (-not [string]::IsNullOrWhiteSpace($FxRuntimeDir) -and (Test-Path $FxRuntimeDir)) {
	$libArgs += "/lib:`"$fxRuntimeDir`""
}

$inputArgs = $inputs | ForEach-Object { "`"$_`"" }

$allArgs = @("/internalize", "/allowdup", "/allowduplicateresources", "/out:`"$TargetPath`"") + $libArgs + $inputArgs

$cmd = "ilrepack " + ($allArgs -join " ")
Write-Host $cmd
Invoke-Expression $cmd
