#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
	[Parameter(Mandatory)]
	[ValidatePattern('(?i)^/subscriptions/[0-9a-f-]{36}/resourceGroups/[^/]+/providers/Microsoft\.ManagedIdentity/userAssignedIdentities/[^/]+$')]
	[string] $ManagedIdentityResourceId,

	[ValidateNotNullOrEmpty()]
	[string] $DisplayName = 'Dataverse Audit Exporter',

	[ValidatePattern('^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$')]
	[string] $ApplicationClientId,

	[ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{2,119}$')]
	[string] $FederatedCredentialName = 'dataverse-exporter-managed-identity'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-AzJson {
	param([Parameter(Mandatory)][string[]] $Arguments)

	$output = & az @Arguments --only-show-errors --output json
	if ($LASTEXITCODE -ne 0) {
		throw "Azure CLI command failed (exit $LASTEXITCODE): az $($Arguments[0..([Math]::Min(2, $Arguments.Length - 1))] -join ' '). Resolve the error and rerun; existing resources are not rolled back."
	}
	if ($output) {
		return ($output -join [Environment]::NewLine) | ConvertFrom-Json
	}
}

Get-Command az -ErrorAction Stop | Out-Null
if ([string]::IsNullOrWhiteSpace($DisplayName)) {
	throw 'DisplayName must not be whitespace.'
}
$cloud = Invoke-AzJson -Arguments @('cloud', 'show')
if ($cloud.name -ne 'AzureCloud') {
	throw 'This script supports Azure public cloud only (AzureCloud). No directory changes were made.'
}
$account = Invoke-AzJson -Arguments @('account', 'show')
$identity = Invoke-AzJson -Arguments @('identity', 'show', '--ids', $ManagedIdentityResourceId)
$tenantId = ([guid] $identity.tenantId).ToString('D')
$principalId = ([guid] $identity.principalId).ToString('D')
$identityClientId = ([guid] $identity.clientId).ToString('D')
if ($tenantId -ne $account.tenantId) {
	throw "The selected Azure CLI tenant must match the managed identity tenant $tenantId. Sign in/select a subscription in that tenant and rerun."
}
if ([guid]::Empty.ToString() -in @($tenantId, $principalId, $identityClientId)) {
	throw 'The managed identity does not have valid tenant, principal and client IDs.'
}

$issuer = "https://login.microsoftonline.com/$tenantId/v2.0"
$audience = 'api://AzureADTokenExchange'
$application = $null
if ($ApplicationClientId) {
	$application = Invoke-AzJson -Arguments @('ad', 'app', 'show', '--id', $ApplicationClientId)
}
else {
	$escapedName = $DisplayName.Replace("'", "''")
	$matches = @(Invoke-AzJson -Arguments @('ad', 'app', 'list', '--filter', "displayName eq '$escapedName'", '--all'))
	if ($matches.Count -gt 1) {
		throw 'Multiple app registrations have this display name. Specify ApplicationClientId to select one explicitly.'
	}
	if ($matches.Count -eq 1) {
		$application = $matches[0]
	}
}

if ($application -and $application.signInAudience -ne 'AzureADMyOrg') {
	throw 'The selected app is not single-tenant (AzureADMyOrg). Use a dedicated single-tenant app; this script will not modify its audience.'
}
$credential = $null
$servicePrincipal = $null
if ($application) {
	$credentials = @(Invoke-AzJson -Arguments @('ad', 'app', 'federated-credential', 'list', '--id', $application.id))
	$named = @($credentials | Where-Object { $_.name -eq $FederatedCredentialName })
	$matchingTrust = @($credentials | Where-Object { $_.issuer -ceq $issuer -and $_.subject -ceq $principalId })
	if ($named.Count -gt 0) {
		$credential = $named[0]
		if ($credential.issuer -cne $issuer -or $credential.subject -cne $principalId -or
			@($credential.audiences).Count -ne 1 -or $credential.audiences[0] -cne $audience) {
			throw 'The named federated credential already exists with different trust settings. No credentials were changed. Choose another name or review the existing trust manually.'
		}
	}
	elseif ($matchingTrust.Count -gt 0) {
		$credential = $matchingTrust[0]
		if (@($credential.audiences).Count -ne 1 -or $credential.audiences[0] -cne $audience) {
			throw 'A credential already trusts this issuer/subject with a different audience. Review it manually before retrying.'
		}
	}
	elseif ($credentials.Count -ge 20) {
		throw 'The application already has 20 federated credentials. Review existing credentials before adding another.'
	}
	$principals = @(Invoke-AzJson -Arguments @('ad', 'sp', 'list', '--filter', "appId eq '$($application.appId)'", '--all'))
	if ($principals.Count -gt 1) {
		throw 'Multiple service principals found for the application; review the directory before retrying.'
	}
	if ($principals.Count -eq 1) {
		$servicePrincipal = $principals[0]
	}
}

if (-not $application -or -not $credential -or -not $servicePrincipal) {
	$target = if ($application) { "$($application.displayName) ($($application.appId))" } else { $DisplayName }
	if (-not $PSCmdlet.ShouldProcess($target, "Ensure single-tenant app registration, service principal and federated trust for managed identity $principalId in tenant $tenantId")) {
		return
	}
	if (-not $application) {
		$application = Invoke-AzJson -Arguments @('ad', 'app', 'create', '--display-name', $DisplayName, '--sign-in-audience', 'AzureADMyOrg')
		Write-Host "Created app registration $($application.appId). Retain this client ID for reruns."
	}
	if (-not $credential) {
		$parameters = [ordered]@{
			name = $FederatedCredentialName
			issuer = $issuer
			subject = $principalId
			audiences = @($audience)
			description = 'Allow the Dataverse audit exporter managed identity to authenticate as this application.'
		}
		$temporaryPath = [System.IO.Path]::GetTempFileName()
		try {
			[System.IO.File]::WriteAllText($temporaryPath, ($parameters | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
			$credential = Invoke-AzJson -Arguments @('ad', 'app', 'federated-credential', 'create', '--id', $application.id, '--parameters', $temporaryPath)
		}
		finally {
			Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
		}
	}
	if (-not $servicePrincipal) {
		$servicePrincipal = Invoke-AzJson -Arguments @('ad', 'sp', 'create', '--id', $application.appId)
	}
}

$verified = Invoke-AzJson -Arguments @('ad', 'app', 'federated-credential', 'show', '--id', $application.id, '--federated-credential-id', $credential.id)
if ($verified.issuer -cne $issuer -or $verified.subject -cne $principalId -or
	@($verified.audiences).Count -ne 1 -or $verified.audiences[0] -cne $audience) {
	throw 'Federated credential read-back did not match the requested trust.'
}

[pscustomobject]@{
	TenantId = $tenantId
	ApplicationClientId = $application.appId
	ApplicationObjectId = $application.id
	ServicePrincipalObjectId = $servicePrincipal.id
	ManagedIdentityClientId = $identityClientId
	ManagedIdentityPrincipalId = $principalId
	FederatedCredentialName = $verified.name
	Issuer = $issuer
	Subject = $principalId
	Audience = $audience
}