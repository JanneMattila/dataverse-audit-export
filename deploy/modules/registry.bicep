targetScope = 'resourceGroup'

@description('Deterministic registry name.')
param name string
@description('Azure region.')
param location string
@description('Resource tags.')
param tags object
@description('Runtime managed identity object ID; receives pull only.')
param principalId string

var pullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Enabled'
    policies: {
      azureADAuthenticationAsArmPolicy: {
        status: 'enabled'
      }
    }
  }
}

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, principalId, pullRoleId)
  scope: registry
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: pullRoleId
  }
}

output registryName string = registry.name
output loginServer string = registry.properties.loginServer
output registryResourceId string = registry.id
