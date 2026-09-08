using './main.bicep'

param resourcePrefix = 'dvaudit'
param environmentName = readEnvironmentVariable('DEPLOYMENT_ENVIRONMENT', 'dev')
param deployApplication = bool(readEnvironmentVariable('DEPLOY_APPLICATION', 'false'))
param organizationName = readEnvironmentVariable('DATAVERSE_ORGANIZATION_NAME', '')
param imageTag = readEnvironmentVariable('IMAGE_TAG', '')
param stateTableName = 'DataverseAuditExporter'
param stateId = 'default'
param intervalSeconds = 5
param startFrom = ''
param enableEventHubOutput = true
param enableBlobOutput = false
param blobContainerName = 'audits'
param eventHubCapacity = 1
param eventHubPartitionCount = 2
param eventHubRetentionDays = 1
param logRetentionDays = 30
