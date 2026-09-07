targetScope = 'resourceGroup'

@description('Deterministic namespace name.')
param name string
@description('Azure region.')
param location string
@description('Resource tags.')
param tags object
@description('Runtime managed identity object ID.')
param principalId string
@description('Standard throughput units.')
@minValue(1)
@maxValue(20)
param capacity int
@description('Event hub partition count; Standard cannot change this after creation.')
@minValue(1)
@maxValue(32)
param partitionCount int
@description('Event retention in days, not archival storage.')
@minValue(1)
@maxValue(7)
param retentionDays int

var senderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2b629674-e913-4c01-ae53-ef4638d8f975')

resource eventNamespace 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
    capacity: capacity
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    isAutoInflateEnabled: false
  }
}

resource hub 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = {
  parent: eventNamespace
  name: 'audits'
  properties: {
    partitionCount: partitionCount
    messageRetentionInDays: retentionDays
    status: 'Active'
  }
}

resource sender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hub.id, principalId, senderRoleId)
  scope: hub
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: senderRoleId
  }
}

output namespaceHost string = first(split(replace(replace(eventNamespace.properties.serviceBusEndpoint, 'https://', ''), '/', ''), ':'))
output hubName string = hub.name
output hubResourceId string = hub.id
