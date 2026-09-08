targetScope = 'resourceGroup'

@description('Lowercase alphanumeric workload prefix, 2-8 characters. Keep stable after bootstrap.')
@minLength(2)
@maxLength(8)
param resourcePrefix string = 'dvaudit'

@description('Environment name included in deterministic resource names. Keep stable after bootstrap.')
@allowed(['dev', 'test', 'prod'])
param environmentName string = 'dev'

@description('Azure region supporting the selected services; defaults to the existing resource group region.')
param location string = resourceGroup().location

@description('Non-secret resource tags.')
param tags object = {
  workload: 'dataverse-audit-exporter'
  environment: environmentName
}

@description('False bootstraps infrastructure without starting an app. Not a stop/delete switch for an existing app.')
param deployApplication bool = false

@description('Dataverse hostname or HTTPS origin, or a comma-separated list of these. Short names default to crm.dynamics.com. Required for the run stage.')
param organizationName string = ''

@description('State table: 3-63 alphanumeric characters, starting with a letter.')
@minLength(3)
@maxLength(63)
param stateTableName string = 'DataverseAuditExporter'

@description('Logical checkpoint/ownership identity; changing it deliberately replays into fresh state.')
@minLength(1)
@maxLength(128)
param stateId string = 'default'

@description('Delay after each poll cycle, seconds.')
@minValue(1)
@maxValue(86400)
param intervalSeconds int = 5

@description('Optional offset-bearing ISO 8601 initial timestamp. Existing durable state takes precedence.')
param startFrom string = ''

@description('Enable Event Hubs resources and output. Disabling does not delete previously deployed resources in Incremental mode.')
param enableEventHubOutput bool = true

@description('Enable private Blob container, container-scoped RBAC and Blob output.')
param enableBlobOutput bool = false

@description('Blob container: 3-63 lowercase letters/digits or single hyphens; start and end with a letter/digit.')
@minLength(3)
@maxLength(63)
param blobContainerName string = 'audits'

@description('Unique, never-reused, locked release tag in dataverse-audit-exporter; never latest. Required for the run stage.')
@maxLength(128)
param imageTag string = ''

@description('Event Hubs Standard throughput units, without auto-inflate.')
@minValue(1)
@maxValue(20)
param eventHubCapacity int = 1

@description('Event hub partition count. Standard partition count cannot be changed after creation.')
@minValue(1)
@maxValue(32)
param eventHubPartitionCount int = 2

@description('Event retention in days (Standard maximum 7); not a long-term archive.')
@minValue(1)
@maxValue(7)
param eventHubRetentionDays int = 1

@description('Log Analytics workspace retention in days.')
@allowed([30, 60, 90, 120, 180, 270, 365, 550, 730])
param logRetentionDays int = 30

var lowercaseLetters = 'abcdefghijklmnopqrstuvwxyz'
var alphanumeric = '${lowercaseLetters}0123456789'
var validPrefix = length(filter(range(0, length(resourcePrefix)), index => !contains(alphanumeric, substring(resourcePrefix, index, 1)))) == 0
var prefix = validPrefix ? resourcePrefix : fail('resourcePrefix must contain only lowercase letters and digits.')
var suffix = uniqueString(resourceGroup().id, prefix, environmentName)
var resourceStem = '${prefix}-${environmentName}-${take(suffix, 8)}'
var validTableName = contains(lowercaseLetters, toLower(take(stateTableName, 1))) && length(filter(range(0, length(stateTableName)), index => !contains(alphanumeric, toLower(substring(stateTableName, index, 1))))) == 0
var tableName = validTableName ? stateTableName : fail('stateTableName must start with a letter and contain only letters and digits.')
var validBlobContainerName = contains(alphanumeric, take(blobContainerName, 1)) && contains(alphanumeric, substring(blobContainerName, length(blobContainerName) - 1, 1)) && !contains(blobContainerName, '--') && length(filter(range(0, length(blobContainerName)), index => !contains('${alphanumeric}-', substring(blobContainerName, index, 1)))) == 0
var containerName = validBlobContainerName ? blobContainerName : fail('blobContainerName must contain lowercase letters/digits or single hyphens and start and end with a letter/digit.')
var validTag = !empty(imageTag) && toLower(imageTag) != 'latest' && !contains('.-', take(imageTag, 1)) && length(filter(range(0, length(imageTag)), index => !contains('${alphanumeric}ABCDEFGHIJKLMNOPQRSTUVWXYZ_.-', substring(imageTag, index, 1)))) == 0
var imageVersion = !deployApplication ? '' : (validTag ? ':${imageTag}' : fail('Run stage requires a valid imageTag (not latest).'))
var organization = deployApplication && empty(trim(organizationName)) ? fail('organizationName is required when deployApplication=true.') : organizationName
var validatedStateId = empty(trim(stateId)) ? fail('stateId must not be whitespace.') : stateId

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'id-${resourceStem}'
  location: location
  tags: tags
}

module storage './modules/storage.bicep' = {
  name: 'storage-${resourceStem}'
  params: {
    name: 'st${prefix}${suffix}'
    location: location
    tags: tags
    tableName: tableName
    principalId: identity.properties.principalId
    enableBlobOutput: enableBlobOutput
    blobContainerName: containerName
  }
}

module eventHubs './modules/event-hubs.bicep' = if (enableEventHubOutput) {
  name: 'event-hubs-${resourceStem}'
  params: {
    name: 'eh-${resourceStem}'
    location: location
    tags: tags
    principalId: identity.properties.principalId
    capacity: eventHubCapacity
    partitionCount: eventHubPartitionCount
    retentionDays: eventHubRetentionDays
  }
}

module registry './modules/registry.bicep' = {
  name: 'registry-${resourceStem}'
  params: {
    name: 'cr${prefix}${suffix}'
    location: location
    tags: tags
    principalId: identity.properties.principalId
  }
}

var exporterEnvironment = !deployApplication || enableEventHubOutput || enableBlobOutput ? [
  { name: 'DATAVERSE_EXPORTER_OrganizationName', value: organization }
  { name: 'DATAVERSE_EXPORTER_StorageTableEndpoint', value: storage.outputs.tableEndpoint }
  { name: 'DATAVERSE_EXPORTER_StateTableName', value: tableName }
  { name: 'DATAVERSE_EXPORTER_StateId', value: validatedStateId }
  { name: 'DATAVERSE_EXPORTER_IntervalSeconds', value: string(intervalSeconds) }
  { name: 'DATAVERSE_EXPORTER_AuthenticationMode', value: 'ManagedIdentity' }
  { name: 'DATAVERSE_EXPORTER_ManagedIdentityClientId', value: identity.properties.clientId }
] : fail('Enable at least one output when deployApplication=true.')

module hosting './modules/container-apps.bicep' = {
  name: 'hosting-${resourceStem}'
  params: {
    appName: 'ca-${resourceStem}'
    environmentName: 'cae-${resourceStem}'
    workspaceName: 'log-${resourceStem}'
    location: location
    tags: tags
    deployApplication: deployApplication
    identityResourceId: identity.id
    registryServer: registry.outputs.loginServer
    image: deployApplication ? '${registry.outputs.loginServer}/dataverse-audit-exporter${imageVersion}' : ''
    environmentVariables: concat(exporterEnvironment, enableEventHubOutput ? [
      { name: 'DATAVERSE_EXPORTER_EventHubNamespace', value: eventHubs!.outputs.namespaceHost }
      { name: 'DATAVERSE_EXPORTER_EventHubName', value: eventHubs!.outputs.hubName }
    ] : [], enableBlobOutput ? [
      { name: 'DATAVERSE_EXPORTER_BlobStorageEndpoint', value: storage.outputs.blobEndpoint }
      { name: 'DATAVERSE_EXPORTER_BlobContainerName', value: containerName }
    ] : [], empty(startFrom) ? [] : [
      { name: 'DATAVERSE_EXPORTER_StartFrom', value: startFrom }
    ])
    logRetentionDays: logRetentionDays
  }
}

output managedIdentityClientId string = identity.properties.clientId
output managedIdentityPrincipalId string = identity.properties.principalId
output managedIdentityResourceId string = identity.id
output storageTableEndpoint string = storage.outputs.tableEndpoint
output stateTableResourceId string = storage.outputs.tableResourceId
output stateTableName string = tableName
output blobStorageEndpoint string = enableBlobOutput ? storage.outputs.blobEndpoint : ''
output blobContainerName string = enableBlobOutput ? containerName : ''
output blobContainerResourceId string = storage.outputs.blobContainerResourceId
output eventHubNamespace string = enableEventHubOutput ? eventHubs!.outputs.namespaceHost : ''
output eventHubName string = enableEventHubOutput ? eventHubs!.outputs.hubName : ''
output eventHubResourceId string = enableEventHubOutput ? eventHubs!.outputs.hubResourceId : ''
output registryName string = registry.outputs.registryName
output registryLoginServer string = registry.outputs.loginServer
output registryResourceId string = registry.outputs.registryResourceId
output containerAppName string = 'ca-${resourceStem}'
output containerAppResourceId string = hosting.outputs.appResourceId
output containerAppsEnvironmentResourceId string = hosting.outputs.environmentResourceId
output logAnalyticsWorkspaceResourceId string = hosting.outputs.workspaceResourceId
output logAnalyticsWorkspaceCustomerId string = hosting.outputs.workspaceCustomerId
output applicationRequested bool = deployApplication
