Param (
    [Parameter(HelpMessage = "Deployment target resource group")] 
    [string] $ResourceGroupName = "rg-dataverse-audit-exporter-dev",

    [Parameter(HelpMessage = "Deployment target resource group location")] 
    [string] $Location = "Sweden Central",

    [Parameter(HelpMessage = "Bicep parameter file referencing the deployment template")]
    [ValidateScript({
        if ([System.IO.Path]::GetExtension($_) -notin @('.bicepparam', '.json') -or -not (Test-Path -LiteralPath $_ -PathType Leaf)) {
            throw "Specify an existing .bicepparam or ARM parameters JSON file."
        }
        $true
    })]
    [string] $TemplateParameterFile = (Join-Path $PSScriptRoot "main.bicepparam")
)

$ErrorActionPreference = "Stop"

$date = (Get-Date).ToString("yyyy-MM-dd-HH-mm-ss")
$deploymentName = "Local-$date"

if ($env:GITHUB_ACTIONS -eq 'true') {
    $deploymentName = "GitHub-$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)"
}
else {
    Write-Host (@"
Not executing inside GitHub Actions.
Make sure you have done "Connect-AzAccount" and
"Select-AzSubscription -SubscriptionName name"
so that script continues to work correctly for you.
"@)
}

# Target deployment resource group
if ($null -eq (Get-AzResourceGroup -Name $ResourceGroupName -Location $Location -ErrorAction SilentlyContinue)) {
    Write-Warning "Resource group '$ResourceGroupName' doesn't exist and it will be created."
    New-AzResourceGroup -Name $ResourceGroupName -Location $Location -Verbose
}

$deploymentParameters = @{
    DeploymentName        = $deploymentName
    ResourceGroupName      = $ResourceGroupName
    TemplateParameterFile = $TemplateParameterFile
    Mode                   = 'Incremental'
    Force                  = $true
    Verbose                = $true
}

if ([System.IO.Path]::GetExtension($TemplateParameterFile) -eq '.json') {
    $deploymentParameters.TemplateFile = Join-Path $PSScriptRoot 'main.bicep'
}

$result = New-AzResourceGroupDeployment @deploymentParameters

$result
