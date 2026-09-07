#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../../deploy/create-dataverse-app-registration.ps1'
$identityResourceId = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/exporter'
$tenantId = '22222222-2222-2222-2222-222222222222'
$principalId = '33333333-3333-3333-3333-333333333333'
$identityClientId = '44444444-4444-4444-4444-444444444444'
$appClientId = '55555555-5555-5555-5555-555555555555'
$script:passed = 0

function Reset-Fixture {
	$script:fixture = @{
		Applications = @()
		Credentials = @()
		Principals = @()
		Writes = 0
		Cloud = 'AzureCloud'
		Tenant = $tenantId
		FailCommand = ''
		ParameterFile = $null
	}
	$global:DataverseRegistrationTestFixture = $script:fixture
}

function Assert-True([bool] $Condition, [string] $Message) {
	if (-not $Condition) { throw $Message }
}

function Assert-Fails([scriptblock] $Action, [string] $MessagePattern) {
	try { & $Action | Out-Null }
	catch {
		Assert-True ($_.Exception.Message -like $MessagePattern) "Unexpected error: $($_.Exception.Message)"
		return
	}
	throw "Expected failure: $MessagePattern"
}

function az {
	$fixture = $global:DataverseRegistrationTestFixture
	$arguments = @($args)
	$command = $arguments -join ' '
	$global:LASTEXITCODE = 0
	if ($fixture.FailCommand -and $command.StartsWith($fixture.FailCommand)) {
		$global:LASTEXITCODE = 1
		return
	}
	$result = switch -Regex ($command) {
		'^cloud show ' { @{ name = $fixture.Cloud }; break }
		'^account show ' { @{ tenantId = $fixture.Tenant }; break }
		'^identity show ' { @{ tenantId = $tenantId; principalId = $principalId; clientId = $identityClientId }; break }
		'^ad app list ' { ,$fixture.Applications; break }
		'^ad app show ' { $fixture.Applications[0]; break }
		'^ad app create ' {
			$fixture.Writes++
			$app = [pscustomobject]@{ id = 'application-object-id'; appId = $appClientId; displayName = 'Test App'; signInAudience = 'AzureADMyOrg' }
			$fixture.Applications = @($app)
			$app
			break
		}
		'^ad app federated-credential list ' { ,$fixture.Credentials; break }
		'^ad app federated-credential create ' {
			$fixture.Writes++
			$path = $arguments[[array]::IndexOf($arguments, '--parameters') + 1]
			$fixture.ParameterFile = $path
			$credential = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
			$credential | Add-Member -NotePropertyName id -NotePropertyValue 'credential-id'
			$fixture.Credentials = @($credential)
			$credential
			break
		}
		'^ad app federated-credential show ' { $fixture.Credentials[0]; break }
		'^ad sp list ' { ,$fixture.Principals; break }
		'^ad sp create ' {
			$fixture.Writes++
			$principal = [pscustomobject]@{ id = 'service-principal-object-id' }
			$fixture.Principals = @($principal)
			$principal
			break
		}
		default { throw "Unexpected Azure CLI command: $command" }
	}
	ConvertTo-Json -InputObject $result -Depth 10 -Compress
}

Reset-Fixture
$result = & $scriptPath -ManagedIdentityResourceId $identityResourceId -DisplayName 'Test App' -Confirm:$false
Assert-True ($script:fixture.Writes -eq 3) 'Expected app, credential and service principal creation.'
Assert-True ($result.ApplicationClientId -eq $appClientId) 'Wrong Dataverse client ID.'
Assert-True ($result.Subject -ceq $principalId) 'Trust must use the managed identity principal ID.'
Assert-True ($result.Issuer -ceq "https://login.microsoftonline.com/$tenantId/v2.0") 'Incorrect issuer.'
Assert-True ($result.Audience -ceq 'api://AzureADTokenExchange') 'Incorrect audience.'
Assert-True (-not (Test-Path -LiteralPath $script:fixture.ParameterFile)) 'Temporary JSON file leaked.'
$script:passed++

$null = & $scriptPath -ManagedIdentityResourceId $identityResourceId -ApplicationClientId $appClientId -Confirm:$false
Assert-True ($script:fixture.Writes -eq 3) 'Rerun must not create duplicates.'
$script:passed++

$script:fixture.Credentials[0].name = 'another-trust-name'
$null = & $scriptPath -ManagedIdentityResourceId $identityResourceId -Confirm:$false
Assert-True ($script:fixture.Writes -eq 3) 'Matching issuer/subject should be reused under another name.'
$script:passed++

$script:fixture.Credentials[0].name = 'dataverse-exporter-managed-identity'
$script:fixture.Credentials[0].subject = $identityClientId
Assert-Fails { & $scriptPath -ManagedIdentityResourceId $identityResourceId -Confirm:$false } '*different trust settings*'
Assert-True ($script:fixture.Writes -eq 3) 'Conflict must not mutate directory objects.'
$script:passed++

$script:fixture.Applications += $script:fixture.Applications[0]
Assert-Fails { & $scriptPath -ManagedIdentityResourceId $identityResourceId } '*Multiple app registrations*'
$script:passed++

Reset-Fixture
$null = & $scriptPath -ManagedIdentityResourceId $identityResourceId -WhatIf
Assert-True ($script:fixture.Writes -eq 0) 'WhatIf must not mutate directory objects.'
$script:passed++

$script:fixture.Tenant = $identityClientId
Assert-Fails { & $scriptPath -ManagedIdentityResourceId $identityResourceId } '*tenant must match*'
Assert-True ($script:fixture.Writes -eq 0) 'Tenant mismatch must not mutate directory objects.'
$script:passed++

Reset-Fixture
$script:fixture.Cloud = 'AzureUSGovernment'
Assert-Fails { & $scriptPath -ManagedIdentityResourceId $identityResourceId } '*public cloud only*'
$script:passed++

Reset-Fixture
$script:fixture.FailCommand = 'ad app federated-credential create'
Assert-Fails { & $scriptPath -ManagedIdentityResourceId $identityResourceId -Confirm:$false } '*Azure CLI command failed*'
Assert-True ($script:fixture.Writes -eq 1) 'Stop after failed credential creation; keep the app for retry.'
$script:fixture.FailCommand = ''
$null = & $scriptPath -ManagedIdentityResourceId $identityResourceId -Confirm:$false
Assert-True ($script:fixture.Writes -eq 3) 'Partial failure retry should reuse the app.'
$script:passed++

Write-Output "PASS: $script:passed offline app-registration scenarios. No Azure services accessed."