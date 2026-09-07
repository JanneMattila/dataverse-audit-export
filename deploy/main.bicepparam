using './main.bicep'

param resourcePrefix = 'dvaudit'
param environmentName = 'dev'
param deployApplication = false
param organizationName = ''
param imageTag = ''
param imageDigest = ''
param stateTableName = 'DataverseAuditExporter'
param stateId = 'default'
param intervalSeconds = 5
param startFrom = ''
param eventHubCapacity = 1
param eventHubPartitionCount = 2
param eventHubRetentionDays = 1
param logRetentionDays = 30
