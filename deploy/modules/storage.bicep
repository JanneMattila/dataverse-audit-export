targetScope = 'resourceGroup'

@description('Deterministic storage account name.')
param name string
@description('Azure region.')
param location string
@description('Resource tags.')
param tags object
@description('Pre-created state table name.')
param tableName string
@description('Runtime managed identity object ID.')
param principalId string
@description('Create a private audit export container and grant the runtime Blob data access. Keep enabled once provisioned.')
param enableBlobOutput bool = false
@description('Validated audit export container name.')
param blobContainerName string = 'audits'

var tableContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
var blobContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Allow'
    }
    encryption: {
      keySource: 'Microsoft.Storage'
      services: {
        table: {
          enabled: true
          keyType: 'Account'
        }
      }
    }
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: account
  name: 'default'
}

resource table 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: tableName
}

resource contributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(table.id, principalId, tableContributorRoleId)
  scope: table
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: tableContributorRoleId
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (enableBlobOutput) {
  parent: account
  name: 'default'
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (enableBlobOutput) {
  parent: blobService
  name: blobContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource blobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (enableBlobOutput) {
  name: guid(container.id, principalId, blobContributorRoleId)
  scope: container
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobContributorRoleId
  }
}

output blobEndpoint string = account.properties.primaryEndpoints.blob
output blobContainerResourceId string = enableBlobOutput ? container!.id : ''
output accountName string = account.name
output tableEndpoint string = account.properties.primaryEndpoints.table
output tableResourceId string = table.id
