$output = Join-Path $PWD 'OverkizCrestronDriver.ProcessorTests/bin/Release/net472'
$extracted = Join-Path $env:RUNNER_TEMP 'driver-package-tests'
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $output 'OverkizCrestronDriver.ProcessorTests.pkg'), $extracted)
./tools/Test-DiscoveredCoverage.ps1 -Stage Package -SdkRoot ../CrestronHomeNUnit -PackageAssembly (Join-Path $extracted 'OverkizCrestronDriver.ProcessorTests.dll') -SourceInventory TestResults/OverkizCrestronDriver.Lifecycle.Tests/inventory.json -ResultsDirectory TestResults
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
