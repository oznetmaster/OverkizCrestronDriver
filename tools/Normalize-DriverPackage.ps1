# Copyright (c) 2026 Neil Colvin. See LICENSE for terms.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $PackagePath).Path
$temporary = "$path.normalized-$([Guid]::NewGuid().ToString('N'))"
$source = [IO.Compression.ZipFile]::OpenRead($path)
try {
    $target = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $seen = @{}
        foreach ($entry in $source.Entries) {
            $name = $entry.FullName.Replace('\','/')
            if ($name.StartsWith('/') -or $name.Contains(':') -or $name -match '(^|/)\.\.(/|$)' -or $seen.ContainsKey($name)) { throw 'Invalid or duplicate package path.' }
            $seen[$name] = $true
            $newEntry = $target.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $newEntry.LastWriteTime = $entry.LastWriteTime
            $input = $entry.Open(); $output = $newEntry.Open()
            try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
        }
    } finally { $target.Dispose() }
} catch {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    throw
} finally { $source.Dispose() }
Move-Item -LiteralPath $temporary -Destination $path -Force
