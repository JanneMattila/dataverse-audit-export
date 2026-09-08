targetScope = 'resourceGroup'

@description('Container app name, stable across releases.')
param appName string
@description('Managed environment name.')
param environmentName string
@description('Log Analytics workspace name.')
param workspaceName string
@description('Azure region.')
param location string
@description('Resource tags.')
param tags object
@description('Bootstrap false creates only the environment and logging.')
param deployApplication bool
@description('User-assigned runtime and registry-pull identity resource ID.')
param identityResourceId string
@description('New ACR login server, without a scheme.')
param registryServer string
@description('Validated immutable image reference, empty only during bootstrap.')
param image string
@description('Non-secret exporter environment variables.')
param environmentVariables array
@description('Workspace retention in days.')
@allowed([30, 60, 90, 120, 180, 270, 365, 550, 730])
param logRetentionDays int

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionDays
    features: {
      disableLocalAuth: true
      enableLogAccessUsingOnlyResourcePermissions: false
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'azure-monitor'
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'container-app-logs'
  scope: managedEnvironment
  properties: {
    workspaceId: workspace.id
    logs: [
      {
        category: 'ContainerAppConsoleLogs'
        enabled: true
      }
      {
        category: 'ContainerAppSystemLogs'
        enabled: true
      }
    ]
  }
}

resource app 'Microsoft.App/containerApps@2025-01-01' = if (deployApplication) {
  name: appName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
    }
  }
  properties: {
    environmentId: managedEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: null
      registries: [
        {
          server: registryServer
          identity: identityResourceId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'exporter'
          image: image
          env: environmentVariables
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    diagnostics
  ]
}

output appResourceId string = deployApplication ? app!.id : ''
output environmentResourceId string = managedEnvironment.id
output workspaceResourceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId
