dotnet build OverkizCrestronDriver.ProcessorTests/OverkizCrestronDriver.ProcessorTests.csproj -c Release -p:BuildProcessorTestPackages=true -p:ReleaseVersion=1.1.0 -p:DeployAfterBuild=false "-p:ManifestUtilExe=$env:MANIFEST_UTIL_EXE"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
