[CmdletBinding()]
param(
	[Alias("Org")]
	[string] $OrganizationName,

	[switch] $Once,

	[ValidateRange(1, 86400)]
	[int] $IntervalSeconds = 5,

	[Alias("Path")]
	[string] $ExportPath = (Join-Path $PSScriptRoot "Exports"),

	[Nullable[datetimeoffset]] $StartFrom,

	[string] $TenantId,

	[string] $ClientId,

	[string] $ClientSecret,

	[string] $StatePath = (Join-Path $PSScriptRoot "export-audit-logs.state.json")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-DotEnvValues {
	param([string] $Path)

	$values = @{}
	if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
		return $values
	}

	foreach ($line in Get-Content -LiteralPath $Path) {
		$trimmedLine = $line.Trim()
		if (-not $trimmedLine -or $trimmedLine.StartsWith("#")) {
			continue
		}

		$separatorIndex = $trimmedLine.IndexOf("=")
		if ($separatorIndex -lt 1) {
			continue
		}

		$name = $trimmedLine.Substring(0, $separatorIndex).Trim()
		$value = $trimmedLine.Substring($separatorIndex + 1).Trim()
		if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) {
			$value = $value.Substring(1, $value.Length - 2)
		}

		$values[$name] = $value
	}

	return $values
}

function Get-FirstValue {
	param([AllowNull()][object[]] $Values)

	foreach ($value in $Values) {
		if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string] $value)) {
			return [string] $value
		}
	}

	return $null
}

function Get-OrganizationUrl {
	param([Parameter(Mandatory)][string] $Name)

	$normalizedName = $Name.Trim().TrimEnd("/")
	if ($normalizedName -match '^https?://') {
		return $normalizedName
	}
	if ($normalizedName -match '\.crm[0-9]*\.dynamics\.com$') {
		return "https://$normalizedName"
	}

	return "https://$normalizedName.crm.dynamics.com"
}

function Write-JsonAtomically {
	param(
		[Parameter(Mandatory)][string] $Path,
		[Parameter(Mandatory)][object] $Value,
		[switch] $Compress
	)

	$parentPath = Split-Path -Parent $Path
	if ($parentPath) {
		[System.IO.Directory]::CreateDirectory($parentPath) | Out-Null
	}

	$json = if ($Compress) {
		$Value | ConvertTo-Json -Depth 100 -Compress
	}
	else {
		$Value | ConvertTo-Json -Depth 100
	}

	$operationId = [guid]::NewGuid().ToString("N")
	$temporaryPath = "$Path.$operationId.tmp"
	$backupPath = "$Path.$operationId.bak"
	try {
		[System.IO.File]::WriteAllText($temporaryPath, $json, [System.Text.UTF8Encoding]::new($false))
		if (Test-Path -LiteralPath $Path -PathType Leaf) {
			[System.IO.File]::Replace($temporaryPath, $Path, $backupPath)
		}
		else {
			[System.IO.File]::Move($temporaryPath, $Path)
		}
	}
	finally {
		if (Test-Path -LiteralPath $temporaryPath) {
			Remove-Item -LiteralPath $temporaryPath -Force
		}
		if (Test-Path -LiteralPath $backupPath) {
			Remove-Item -LiteralPath $backupPath -Force
		}
	}
}

$dotEnv = Get-DotEnvValues -Path (Join-Path $PSScriptRoot ".env")
$OrganizationName = Get-FirstValue @($OrganizationName, $env:DATAVERSE_ORGANIZATION_NAME, $env:organization_name, $dotEnv["organization_name"])
$TenantId = Get-FirstValue @($TenantId, $env:DATAVERSE_TENANT_ID, $env:tenant, $dotEnv["tenant"])
$ClientId = Get-FirstValue @($ClientId, $env:DATAVERSE_CLIENT_ID, $env:client_id, $dotEnv["client_id"])
$ClientSecret = Get-FirstValue @($ClientSecret, $env:DATAVERSE_CLIENT_SECRET, $env:client_secret, $dotEnv["client_secret"])

$missingSettings = @()
if (-not $OrganizationName) { $missingSettings += "OrganizationName" }
if (-not $TenantId) { $missingSettings += "TenantId" }
if (-not $ClientId) { $missingSettings += "ClientId" }
if (-not $ClientSecret) { $missingSettings += "ClientSecret" }
if ($missingSettings.Count -gt 0) {
	throw "Missing required settings: $($missingSettings -join ', '). Supply parameters, DATAVERSE_* environment variables, or scripts/.env values."
}

$organizationUrl = Get-OrganizationUrl -Name $OrganizationName
$resolvedExportPath = [System.IO.Path]::GetFullPath($ExportPath)
$resolvedStatePath = [System.IO.Path]::GetFullPath($StatePath)
$auditIdIndexPath = [System.IO.Path]::ChangeExtension($resolvedStatePath, "auditids.txt")
[System.IO.Directory]::CreateDirectory($resolvedExportPath) | Out-Null
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $resolvedStatePath)) | Out-Null

$state = $null
if (Test-Path -LiteralPath $resolvedStatePath -PathType Leaf) {
	try {
		$state = Get-Content -LiteralPath $resolvedStatePath -Raw | ConvertFrom-Json
	}
	catch {
		throw "State file '$resolvedStatePath' is not valid JSON. Restore or remove it before retrying. $($_.Exception.Message)"
	}

	if ($state.organizationUrl -ne $organizationUrl -or [System.IO.Path]::GetFullPath([string] $state.exportPath) -ne $resolvedExportPath) {
		throw "State file '$resolvedStatePath' belongs to another organization or export path. Use a different StatePath."
	}
}

$checkpoint = $null
if ($null -ne $StartFrom) {
	$checkpoint = ([datetimeoffset] $StartFrom).ToUniversalTime()
}
elseif ($null -ne $state -and $state.lastSuccessfulCreatedOnUtc) {
	$checkpoint = ([datetimeoffset] $state.lastSuccessfulCreatedOnUtc).ToUniversalTime()
}

$seenAuditIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $auditIdIndexPath -PathType Leaf) {
	foreach ($auditId in Get-Content -LiteralPath $auditIdIndexPath) {
		if (-not [string]::IsNullOrWhiteSpace($auditId)) {
			[void] $seenAuditIds.Add($auditId.Trim())
		}
	}
}

$token = $null
$tokenExpiresAt = [datetimeoffset]::MinValue

function Get-AccessToken {
	if ($script:token -and [datetimeoffset]::UtcNow -lt $script:tokenExpiresAt.AddMinutes(-2)) {
		return $script:token
	}

	$tokenResponse = Invoke-RestMethod `
		-Method Post `
		-Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
		-ContentType "application/x-www-form-urlencoded" `
		-Body @{
			client_id     = $ClientId
			client_secret = $ClientSecret
			scope         = "$organizationUrl/.default"
			grant_type    = "client_credentials"
		}

	$script:token = [string] $tokenResponse.access_token
	$script:tokenExpiresAt = [datetimeoffset]::UtcNow.AddSeconds([int] $tokenResponse.expires_in)
	return $script:token
}

function Get-AuditsUri {
	param([AllowNull()][object] $CreatedOn)

	$baseUri = "$organizationUrl/api/data/v9.2/audits"
	if ($null -eq $CreatedOn) {
		return "$baseUri`?`$orderby=createdon asc"
	}

	$createdOnText = ([datetimeoffset] $CreatedOn).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
	$filter = [uri]::EscapeDataString("createdon ge $createdOnText")
	return "$baseUri`?`$filter=$filter&`$orderby=createdon asc"
}

function Export-AuditRecord {
	param([Parameter(Mandatory)][object] $Audit)

	$auditId = [string] $Audit.auditid
	$parsedAuditId = [guid]::Empty
	if (-not [guid]::TryParse($auditId, [ref] $parsedAuditId)) {
		throw "Audit response contained an invalid or missing auditid: '$auditId'."
	}
	$auditId = $parsedAuditId.ToString("D")

	if ($seenAuditIds.Contains($auditId)) {
		return $false
	}

	$createdOn = ([datetimeoffset] $Audit.createdon).ToUniversalTime()
	$partitionPath = Join-Path $resolvedExportPath $createdOn.ToString("yyyy/MM/dd", [System.Globalization.CultureInfo]::InvariantCulture)
	$auditPath = Join-Path $partitionPath "$auditId.json"

	if (-not (Test-Path -LiteralPath $auditPath -PathType Leaf)) {
		[System.IO.Directory]::CreateDirectory($partitionPath) | Out-Null
		Write-JsonAtomically -Path $auditPath -Value $Audit
	}

	[void] $seenAuditIds.Add($auditId)
	[System.IO.File]::AppendAllText($auditIdIndexPath, "$auditId$([Environment]::NewLine)", [System.Text.UTF8Encoding]::new($false))
	return $true
}

function Invoke-AuditExportCycle {
	$cycleCheckpoint = $script:checkpoint
	$highestCreatedOn = $cycleCheckpoint
	$requestUri = Get-AuditsUri -CreatedOn $cycleCheckpoint
	$exportedCount = 0
	$duplicateCount = 0

	do {
		$headers = @{
			Accept              = "application/json"
			"OData-MaxVersion" = "4.0"
			"OData-Version"    = "4.0"
			Prefer              = 'odata.include-annotations="*",odata.maxpagesize=5000'
			Authorization       = "Bearer $(Get-AccessToken)"
		}
		$response = Invoke-RestMethod -Method Get -Uri $requestUri -Headers $headers

		foreach ($audit in @($response.value)) {
			$createdOn = ([datetimeoffset] $audit.createdon).ToUniversalTime()
			if ($null -eq $highestCreatedOn -or $createdOn -gt $highestCreatedOn) {
				$highestCreatedOn = $createdOn
			}

			if (Export-AuditRecord -Audit $audit) {
				$exportedCount++
			}
			else {
				$duplicateCount++
			}
		}

		$nextLinkProperty = $response.PSObject.Properties['@odata.nextLink']
		$requestUri = if ($nextLinkProperty) { [string] $nextLinkProperty.Value } else { $null }
	} while ($requestUri)

	$newState = [ordered]@{
		version                    = 1
		organizationUrl            = $organizationUrl
		exportPath                 = $resolvedExportPath
		lastSuccessfulCreatedOnUtc = if ($null -ne $highestCreatedOn) { $highestCreatedOn.ToString("o") } else { $null }
		lastSuccessfulRunUtc       = [datetimeoffset]::UtcNow.ToString("o")
	}
	Write-JsonAtomically -Path $resolvedStatePath -Value $newState
	$script:checkpoint = $highestCreatedOn

	Write-Host "Audit export completed: $exportedCount exported, $duplicateCount duplicates skipped, checkpoint $($newState.lastSuccessfulCreatedOnUtc)."
}

do {
	try {
		Invoke-AuditExportCycle
	}
	catch {
		if ($Once) {
			throw
		}

		Write-Warning "Audit export failed; checkpoint was not advanced. $($_.Exception.Message)"
	}

	if (-not $Once) {
		Start-Sleep -Seconds $IntervalSeconds
	}
} while (-not $Once)
