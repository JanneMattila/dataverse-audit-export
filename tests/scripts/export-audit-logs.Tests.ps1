[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot '../../scripts/export-audit-logs.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
	$scriptPath, [ref] $tokens, [ref] $parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }

foreach ($name in @('Write-JsonAtomically', 'Export-AuditRecord')) {
	$definition = $ast.Find({
		param($node)
		$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
	}, $true)
	. ([scriptblock]::Create($definition.Extent.Text))
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('audit-memory-test-' + [guid]::NewGuid().ToString('N'))
try {
	$resolvedExportPath = $root
	$organizationHost = 'contoso.crm4.dynamics.com'
	$seenAuditIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
	$script:latestDeliveredOn = $null
	$first = [pscustomobject]@{ auditid = [guid]::NewGuid().ToString('D'); createdon = '2026-09-07T12:00:00Z' }
	$tied = [pscustomobject]@{ auditid = [guid]::NewGuid().ToString('D'); createdon = $first.createdon }
	[void] (Export-AuditRecord $first)
	[void] (Export-AuditRecord $tied)
	if ($seenAuditIds.Count -ne 2 -or (Export-AuditRecord $first)) { throw 'Tied IDs were not deduplicated.' }
	for ($index = 1; $index -le 100; $index++) {
		$audit = [pscustomobject]@{
			auditid = [guid]::NewGuid().ToString('D')
			createdon = ([datetimeoffset] $first.createdon).AddTicks($index).ToString('O')
		}
		[void] (Export-AuditRecord $audit)
		if ($seenAuditIds.Count -ne 1) { throw 'Historical IDs accumulated.' }
	}
	[void] (Export-AuditRecord $first)
	if ($seenAuditIds.Count -ne 1 -or -not $seenAuditIds.Contains($audit.auditid)) {
		throw 'Older retry regressed the memory boundary.'
	}
	$path = Join-Path $root "2026/09/07/$($audit.auditid).json"
	$before = [IO.File]::ReadAllText($path)
	$payload = $before | ConvertFrom-Json
	if ($payload.organization -ne $organizationHost) { throw 'Organization hostname was lost.' }
	$seenAuditIds.Clear()
	$script:latestDeliveredOn = $null
	[void] (Export-AuditRecord $audit)
	if ([IO.File]::ReadAllText($path) -cne $before) { throw 'Restart overwrote an existing file.' }
	if (@(Get-ChildItem $root -Recurse -Filter '*.txt').Count) { throw 'Audit ID index was written.' }
	if ($ast.Extent.Text -match 'auditIdIndexPath|AppendAllText|auditids\.txt') {
		throw 'Legacy index persistence remains.'
	}
	Write-Host 'PASS: PowerShell boundary tracking, timestamp ties, pruning, older retries, restart, hostname, and no index persistence.'
}
finally {
	if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}