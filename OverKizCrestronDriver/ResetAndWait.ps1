<#
.SYNOPSIS
	Resets the Overkiz driver on the Crestron processor and waits until it has
	fully initialised (i.e. "Discovery complete" appears in the day log).

.USAGE
	.\ResetAndWait.ps1 -ProcessorIP 192.168.8.241 -User admin -Password secret
#>
param(
	[Parameter(Mandatory)][string] $ProcessorIP,
	[Parameter(Mandatory)][string] $User,
	[Parameter(Mandatory)][string] $Password,
	[int]    $TimeoutSeconds = 120,
	[int]    $PollIntervalSeconds = 5,
	[string] $ReadyMarker = "Discovery complete"
)

Import-Module Posh-SSH -ErrorAction Stop

$cred      = [System.Management.Automation.PSCredential]::new(
				 $User, (ConvertTo-SecureString $Password -AsPlainText -Force))
$logDir    = "/rm/SeawolfDiagnostic"
$localTemp = "$env:TEMP\overkiz_driver.log"

# ── Issue program reset via SSH ──────────────────────────────────────────────
Write-Host "Connecting SSH to $ProcessorIP ..."
$ssh = New-SSHSession -ComputerName $ProcessorIP -Credential $cred -AcceptKey -ErrorAction Stop
try {
	Write-Host "Sending: enableprogramcmd"
	Invoke-SSHCommand -SessionId $ssh.SessionId -Command "enableprogramcmd" | Out-Null
	Start-Sleep -Seconds 1
	Write-Host "Sending: progreset -p:0"
	Invoke-SSHCommand -SessionId $ssh.SessionId -Command "progreset -p:0"  | Out-Null
	Write-Host "Reset commands sent. Waiting for driver to initialise ..."
}
finally {
	Remove-SSHSession -SessionId $ssh.SessionId | Out-Null
}

# ── Poll the day log via SFTP until the ready marker appears ─────────────────
$sftp = New-SFTPSession -ComputerName $ProcessorIP -Credential $cred -AcceptKey -ErrorAction Stop
try {
	$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
	$found    = $false

	while ((Get-Date) -lt $deadline) {
		Start-Sleep -Seconds $PollIntervalSeconds

		# The log file is named after today's date; rebuild the path each iteration
		# in case midnight rolls over during a long wait.
		$logFile = "$logDir/$((Get-Date -Format 'yyyy-MM-dd')).log"

		try {
			Get-SFTPItem -SessionId $sftp.SessionId `
						 -Path $logFile `
						 -Destination $env:TEMP `
						 -Force -ErrorAction Stop

			# Rename the downloaded file to a stable name so we can re-read it.
			$downloaded = Join-Path $env:TEMP (Split-Path $logFile -Leaf)
			if (Test-Path $downloaded) {
				Move-Item $downloaded $localTemp -Force
			}

			if (Select-String -Path $localTemp -Pattern ([regex]::Escape($ReadyMarker)) -Quiet) {
				$found = $true
				break
			}

			Write-Host "  $(Get-Date -Format 'HH:mm:ss')  waiting for '$ReadyMarker' ..."
		}
		catch {
			Write-Host "  $(Get-Date -Format 'HH:mm:ss')  log not yet available ($_)"
		}
	}

	if ($found) {
		Write-Host ""
		Write-Host "Driver ready. Fetching last 40 lines of log ..."
		Write-Host "────────────────────────────────────────────────"
		Get-Content $localTemp | Select-Object -Last 40
		Write-Host "────────────────────────────────────────────────"
	}
	else {
		Write-Warning "Timed out after $TimeoutSeconds seconds waiting for '$ReadyMarker'."
		Write-Host "Last 20 lines of log (if available):"
		if (Test-Path $localTemp) { Get-Content $localTemp | Select-Object -Last 20 }
	}
}
finally {
	Remove-SFTPSession -SessionId $sftp.SessionId | Out-Null
}
