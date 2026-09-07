# Azure infrastructure

Resource-group-scoped Bicep for the Dataverse audit polling worker. These files describe infrastructure; **no Azure resources have been deployed or live-tested as part of authoring them**. Commands below are operator instructions for a separately approved rollout, not commands that have already run.

## Resources and boundaries

| Resource | Configuration |
| --- | --- |
| State storage | StorageV2, Standard_LRS, pre-created `DataverseAuditExporter` table; HTTPS/TLS 1.2, shared keys and anonymous blob access disabled |
| Event Hubs | Standard, 1 throughput unit, `audits`, 2 partitions, 1-day retention; TLS 1.2, local/SAS authentication disabled |
| Identity | One user-assigned identity for Dataverse, Table, Event Hubs and image pulls |
| Registry | ACR Basic, registry RBAC mode, admin and anonymous pull disabled; ARM-audience authentication enabled for managed identity pulls |
| Host | Consumption Container Apps environment; app has no ingress, Single revision mode, min=max=1, fixed 0.5 vCPU / 1Gi RAM |
| Logs | Log Analytics, 30-day default retention, local authentication disabled; environment `azure-monitor` destination and console/system diagnostic categories |

The runtime gets **Storage Table Data Contributor at table scope**, **Azure Event Hubs Data Sender at hub scope**, and **AcrPull at registry scope**. Role names use `guid(scope, principalId, roleDefinitionId)` and `ServicePrincipal` type. It gets no registry push, hub receive, subscription-wide or resource-group-wide data role. Outputs separate the identity client/application ID (Dataverse) from principal/object ID (Azure RBAC) and ARM resource ID (Container Apps identity attachment).

**Intentional network exception:** Storage, Event Hubs, ACR and Log Analytics use public endpoints with Entra authorization. There are no private endpoints, VNets, private DNS zones or IP allowlists. Entra-only access does not provide network isolation: authorized credentials can access these endpoints from the internet. This is the approved design, and may conflict with organizational policies. Container App ingress is explicitly null, so the worker exposes no HTTP/TCP endpoint. No credentials, connection strings, `listKeys`, workspace shared-key settings, persistent file mounts or cloud `ExportPath` are configured.

## Parameters

Start with [main.bicepparam](main.bicepparam). Keep the resource group, `resourcePrefix`, `environmentName` and location unchanged between stages and updates. Names use a deterministic hash of the group ID, prefix and environment. Storage/ACR names use the full 13-character hash; other names use 8 characters. Changing these inputs creates different resources and does not migrate state. Global name availability is checked by Azure, not by compilation.

| Parameter | Default / constraints |
| --- | --- |
| `resourcePrefix` | `dvaudit`; 2-8 lowercase letters/digits |
| `environmentName` | `dev`; `dev`, `test`, `prod` |
| `location` | Existing resource group's location; select a supported region before bootstrap |
| `tags` | Workload and environment tags; non-secret values only |
| `deployApplication` | `false`; creates backing resources, identity, registry, environment and logging without an app |
| `organizationName` | Empty during bootstrap; required for run. Short name, Dynamics hostname or HTTPS origin |
| `imageTag` / `imageDigest` | Empty during bootstrap; run requires exactly one. Tag must be valid, unique, locked and never reused; `latest` is rejected. Prefer `sha256:` plus 64 lowercase hex characters |
| `stateTableName` | `DataverseAuditExporter`; 3-63 alphanumeric characters starting with a letter |
| `stateId` | `default`; 1-128 characters, not whitespace; preserve for resume |
| `intervalSeconds` | `5`; 1-86400 |
| `startFrom` | Empty; optional offset-bearing ISO 8601 value such as `2026-09-01T00:00:00Z`, validated by the application |
| `eventHubCapacity` | `1`; 1-20 Standard throughput units; auto-inflate disabled |
| `eventHubPartitionCount` | `2`; 1-32; **Standard cannot change partition count after creation** |
| `eventHubRetentionDays` | `1`; 1-7 |
| `logRetentionDays` | `30`; 30, 60, 90, 120, 180, 270, 365, 550 or 730 |

Bicep decorators validate ranges and allowed values; template expressions reject invalid naming and missing run-stage inputs during ARM evaluation. Compilation alone does not evaluate all deployment-time expressions or confirm image existence. Dataverse URL and timestamp semantics remain application validation responsibilities. Registry host and repository are not caller-controlled: images must be in the new registry's `dataverse-audit-exporter` repository. Bicep cannot prove that a tag is immutable; lock it before use or use a digest.

## Prerequisites and local checks

Use a recent Azure CLI with Bicep supporting `.bicepparam`, lambda expressions and `fail()`. Build/security checks do not require a resource deployment:

```powershell
az bicep version
az bicep build --file deploy/main.bicep
az bicep build-params --file deploy/main.bicepparam
```

[Checkov](https://www.checkov.io/) is an optional security scanner that checks infrastructure code for risky configurations. It does not deploy or modify Azure resources.

To install on Windows, first install Python with pip, then run:

```powershell
py -m pip install --user pipx
py -m pipx ensurepath
py -m pipx install checkov
```

Open a new terminal (restart VS Code if needed), then verify the installation and scan from the repository root:

```powershell
checkov --version
checkov -d deploy --framework bicep
```

Review warnings and all scanner findings. No Checkov skip directives are included. Public-network/private-endpoint findings represent the explicit exception above, not an unnoticed security pass. Basic ACR and LRS storage also deliberately lack Premium/geo-redundancy features. Record findings and their risk decisions before deployment. If Checkov is unavailable, do not claim a clean security scan. Generated ARM JSON files are local build artifacts.

Before cloud operations, the deployer needs resource creation rights, role assignment write rights at the resource group (for example, Contributor plus Role Based Access Control Administrator with suitable conditions), and permissions to attach the managed identity. Ensure `Microsoft.App`, `Microsoft.OperationalInsights`, `Microsoft.Insights`, `Microsoft.Storage`, `Microsoft.EventHub`, `Microsoft.ContainerRegistry` and `Microsoft.ManagedIdentity` providers are registered. Check regional service availability, quotas and organizational Azure Policy. These checks are not performed by local compilation.

The application and its project-local Dockerfile are developed separately. **Do not start the run stage until the app is built/tested, its managed identity configuration works, and cooperative renewable Table ownership is implemented and tested.** The image must be Linux/amd64 compatible, non-root, use an exec-form entrypoint, and handle SIGTERM cleanly. Its project-local `.dockerignore` must exclude build artifacts, credentials and local output. Never use the repository root as the Docker build context.

## 1. Bootstrap (later, with approval)

Commands are PowerShell, run from the repository root. Replace placeholders deliberately. Login is interactive; do not paste secrets into parameters or source files.

```powershell
az login
$subscriptionId = '<subscription-id>'
$resourceGroup = '<resource-group>'
$location = '<supported-region>'
az account set --subscription $subscriptionId
```

Use an existing selected resource group. Only if creating a new group is separately approved:

```powershell
az group create --name $resourceGroup --location $location
```

Leave `deployApplication=false` and image/organization empty in the parameter file. Compile it, then review server-side validation and what-if before creation:

```powershell
az deployment group validate --resource-group $resourceGroup --parameters deploy/main.bicepparam
az deployment group what-if --resource-group $resourceGroup --parameters deploy/main.bicepparam
az deployment group create --name audit-bootstrap --resource-group $resourceGroup --mode Incremental --parameters deploy/main.bicepparam
$outputs = az deployment group show --name audit-bootstrap --resource-group $resourceGroup --query properties.outputs -o json | ConvertFrom-Json
$registryName = $outputs.registryName.value
$registryServer = $outputs.registryLoginServer.value
```

Check each command's exit code before proceeding. Do not use Complete deployment mode or deployment-stack deletion semantics. `deployApplication=false` is **only bootstrap**: after an app exists, an incremental deployment with false does not stop/delete that app and can still update its backing resources.

## 2. Publish an immutable image

An authorized RBAC operator grants the separate publisher `AcrPush` on the new registry. For a human publisher use principal type `User`; for a build identity use `ServicePrincipal`. Never assign push to the runtime identity.

```powershell
$publisherObjectId = '<publisher-object-id>'
az role assignment create --assignee-object-id $publisherObjectId --assignee-principal-type User --role AcrPush --scope $outputs.registryResourceId.value
```

After RBAC propagation, authenticate as the publisher. With the project-local Dockerfile ready and Docker configured for Linux/amd64 images:

```powershell
$tag = '<unique-release-tag>'
$image = "${registryServer}/dataverse-audit-exporter:${tag}"
docker build -t $image src/DataverseAuditExporter
az acr login --name $registryName
docker push $image
az acr repository update --name $registryName --image "dataverse-audit-exporter:${tag}" --write-enabled false --delete-enabled false
$digest = az acr repository show --name $registryName --image "dataverse-audit-exporter:${tag}" --query digest -o tsv
```

The build command is `docker build -t REGISTRY/dataverse-audit-exporter:TAG src/DataverseAuditExporter`, with project-directory context, never scripts or existing exports. The tag-lock operation requires appropriate registry permissions; treat failure as a stop condition. Prefer the returned digest for run-stage pinning. Keep prior release digests for rollback; never overwrite a release tag. Image pull failures may reflect RBAC propagation: wait and retry the deployment/pull after permissions propagate, not by enabling admin credentials.

## 3. Onboard the Dataverse identity

Choose one identity model. **The current exporter supports direct managed identity only.** The app-registration script below provisions an alternative trust for a client-assertion/token-exchange implementation; running it does not change the exporter or the Bicep runtime configuration.

### Direct managed identity (current exporter)

In Power Platform admin center, select the Dataverse environment, then Settings > Users + permissions > Application users. Add the managed identity using **`$outputs.managedIdentityClientId.value`**, its application/client ID, not its Azure principal ID or display name. Select the correct business unit and assign a dedicated security role containing **`prvReadAuditSummary`** (View Audit Summary) with the required environment-wide audit read access. The environment must have auditing enabled and audit data available.

Bicep does not create Dataverse application users or security roles. Azure RBAC assignments do not grant Dataverse permissions. Confirm the identity can read the environment's audit table before expecting the poller to work. Audit-detail enrichment is not included. See [application users](https://learn.microsoft.com/en-us/power-platform/admin/manage-application-users) and [audit retrieval privileges](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/auditing/retrieve-audit-data).

### App registration trusting the managed identity

[create-dataverse-app-registration.ps1](create-dataverse-app-registration.ps1) creates a single-tenant Entra app registration, its enterprise application (service principal), and a federated identity credential **on the app registration** that trusts the bootstrap user-assigned managed identity. No client secrets, certificates, delegated API permissions, admin consent, Azure RBAC changes, or Dataverse application users are created by the script.

Prerequisites: PowerShell 7+, a recent Azure CLI with `az ad app federated-credential`, an existing user-assigned managed identity, and an Azure CLI login in the **same tenant** as that identity. This script supports Azure public cloud only. The signed-in operator needs read access to the managed identity's ARM resource and Entra rights to create/read the app and service principal and manage its federated credentials. Appropriate app ownership/directory policy or an Entra role such as Application Administrator or Cloud Application Administrator is required; Azure subscription Contributor alone is insufficient. The script does not sign in or switch your subscription automatically.

After bootstrap, using `$outputs` from step 1:

```powershell
# Preview only: performs directory/identity reads but no writes.
./deploy/create-dataverse-app-registration.ps1 `
	-ManagedIdentityResourceId $outputs.managedIdentityResourceId.value `
	-DisplayName 'Dataverse Audit Exporter - dev' `
	-WhatIf

$registration = ./deploy/create-dataverse-app-registration.ps1 `
	-ManagedIdentityResourceId $outputs.managedIdentityResourceId.value `
	-DisplayName 'Dataverse Audit Exporter - dev'

$registration | Format-List
```

The returned `ApplicationClientId` is the **new app registration's application/client ID**. In Power Platform admin center, open the environment's Settings > Users + permissions > Application users > New app user > Add an app, search by that ID, choose the business unit, and assign a security role with `prvReadAuditSummary`. Do not use `ApplicationObjectId`, `ServicePrincipalObjectId`, or the managed identity's client ID for this app-registration-based user.

| Trust setting / output | Meaning |
| --- | --- |
| `Issuer` | `https://login.microsoftonline.com/<managed-identity-tenant-id>/v2.0` |
| `Subject` | Managed identity **principal/object ID**, not its client ID |
| `Audience` | `api://AzureADTokenExchange` |
| `ApplicationClientId` | New app ID for the Dataverse application user and token exchange |
| `ManagedIdentityClientId` | Existing identity to select when acquiring the assertion token |

To rerun or finish a partially completed creation, select the returned client ID explicitly:

```powershell
$registration = ./deploy/create-dataverse-app-registration.ps1 `
	-ManagedIdentityResourceId $outputs.managedIdentityResourceId.value `
	-ApplicationClientId $registration.ApplicationClientId
```

Without `ApplicationClientId`, the script searches for an exact display-name match and reuses a unique single-tenant app. Multiple matches cause an error; names are not unique directory identifiers. Use a dedicated app and retain its client ID. Avoid concurrent first-time runs with the same name. `FederatedCredentialName` defaults to `dataverse-exporter-managed-identity` and can be overridden. Matching trust is reused, including an identical issuer/subject under another name; conflicting trust is never overwritten. A different identity requires deliberate review or a new credential name. Read-back verifies issuer, subject, and audience. Entra replication delays can cause a later step/read-back to fail: keep the printed app client ID and rerun after propagation. The script stops on CLI failures and leaves completed objects in place rather than deleting potentially shared directory resources.

**Runtime integration is still required for this alternative.** The workload must acquire a managed-identity token for `api://AzureADTokenExchange/.default`, use it as a `ClientAssertionCredential` assertion for `TenantId` + `ApplicationClientId`, and request the Dataverse organization `/.default` scope through that credential. This produces a token for the app registration, which Dataverse maps to its application user. The current [credential factory](../src/DataverseAuditExporter/CredentialFactory.cs) does not perform this exchange. Do not replace `DATAVERSE_EXPORTER_ManagedIdentityClientId` with the app registration ID: that option must remain the actual managed identity ID. Keep Table, Event Hubs, and registry access on the existing managed identity and its existing RBAC roles; the app registration does not inherit those roles. Local `AzureCli` mode still authenticates the developer, not this app registration.

See Microsoft's [managed-identity federation configuration and token-exchange examples](https://learn.microsoft.com/en-us/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity). The identity must be attached to the workload host. Anyone able to run code under that identity can use this trust, so grant the Dataverse app user only the required permissions.

Offline script checks (mocked Azure CLI; no login, resources or directory writes):

```powershell
pwsh -NoProfile -File tests/deploy/create-dataverse-app-registration.Tests.ps1
```

## 4. Run and update

Edit [main.bicepparam](main.bicepparam): set `deployApplication=true`, supply the actual `organizationName`, set `imageDigest` to the published `$digest` (or `imageTag` to the locked release tag), and leave the other image selector empty. Keep all resource naming and checkpoint/destination settings unchanged. Optionally set `startFrom` before the first run; absent means all available audit history.

```powershell
az bicep build --file deploy/main.bicep
az bicep build-params --file deploy/main.bicepparam
az deployment group validate --resource-group $resourceGroup --parameters deploy/main.bicepparam
az deployment group what-if --resource-group $resourceGroup --parameters deploy/main.bicepparam
az deployment group create --name audit-run --resource-group $resourceGroup --mode Incremental --parameters deploy/main.bicepparam
$outputs = az deployment group show --name audit-run --resource-group $resourceGroup --query properties.outputs -o json | ConvertFrom-Json
az containerapp logs show --name $outputs.containerAppName.value --resource-group $resourceGroup --type system --tail 50
az containerapp logs show --name $outputs.containerAppName.value --resource-group $resourceGroup --type console --tail 50
```

Logs are also routed by Azure Monitor diagnostic settings to the workspace, without workspace keys in the environment. Query `ContainerAppConsoleLogs_CL` and `ContainerAppSystemLogs_CL` after ingestion starts. Operators need explicitly granted workspace query access (for example Log Analytics Reader); the runtime identity does not. Check image pull/startup, managed identity authorization, ownership acquisition, successful cycles and advancing Table checkpoints without logging audit payloads or tokens. A running revision alone does not prove successful export. Verify delivery using a separate authorized receiver with **Azure Event Hubs Data Receiver**, never extra runtime permissions.

For updates, build and publish a new unique tag/digest, change only the image selector, repeat validation/what-if, then deploy incrementally. Rollback uses a retained prior digest only when the application's state schema remains compatible. Do not delete the table/receipt rows as a routine restart mechanism.

## Runtime contract and delivery limits

The template sets these exact variables, all with the `DATAVERSE_EXPORTER_` prefix:

```text
DATAVERSE_EXPORTER_OrganizationName
DATAVERSE_EXPORTER_StorageTableEndpoint
DATAVERSE_EXPORTER_StateTableName
DATAVERSE_EXPORTER_StateId
DATAVERSE_EXPORTER_IntervalSeconds
DATAVERSE_EXPORTER_AuthenticationMode=ManagedIdentity
DATAVERSE_EXPORTER_ManagedIdentityClientId
DATAVERSE_EXPORTER_EventHubNamespace
DATAVERSE_EXPORTER_EventHubName
DATAVERSE_EXPORTER_StartFrom (only when supplied)
```

Cloud output is Event Hubs only; `DATAVERSE_EXPORTER_ExportPath` is absent. The Table account stores checkpoints, receipts and ownership, not audit-export files. `StartFrom` affects only fresh state; existing state wins. Use a new `stateId` for deliberate replay, recognizing that this resends audits. A changed destination requires deliberate fresh-state handling in the application; do not silently repoint a running checkpoint.

Single revision and maxReplicas=1 are **not singleton guarantees**: maintenance and rollout can temporarily overlap replicas. The separately developed app must acquire/renew/release cooperative Table ownership, wait in standby when another owner holds it, and abort work/checkpoint writes when ownership is lost. This is not atomic fencing of Event Hubs. Delivery remains **at least once**, with possible duplicates after send-before-receipt failures and stale/in-flight sends. Consumers must deduplicate by organization + auditid. No exactly-once or no-loss guarantee is claimed. An inclusive createdon watermark can miss records that become visible later with timestamps older than the checkpoint.

## Local developer access

No developer roles are granted automatically. An authorized operator can grant a developer **Storage Table Data Contributor** on `stateTableResourceId` and **Azure Event Hubs Data Sender** on `eventHubResourceId` outputs, using the developer's Entra object ID. Those are the same narrow data scopes as the runtime; subscription Contributor alone is insufficient. Use explicit `AuthenticationMode=AzureCli` locally, independently grant the developer Dataverse environment access and `prvReadAuditSummary`, and omit managed-identity-specific settings. Do not have local testing export against the production `stateId` or destination unintentionally. A receiver verification identity needs the separate receiver role mentioned above.

## Cost, resilience and validation status

Bootstrap itself creates billable services even without an app: Event Hubs Standard capacity and ACR storage/service charges, plus any logs or Table activity. Run stage adds an always-on Container App (minReplicas=1); Consumption is a billing model, not a scale-to-zero guarantee here. Throughput units, poll frequency, full-history backfills, Event Hubs ingress, network transfer, Table operations and ever-growing delivery receipts all affect cost. Log ingestion and retention also incur charges. No currency estimate or free-operation guarantee is provided.

Event Hubs' 1-day default retention is a consumer recovery window, **not archival storage**. Plan consumer durability separately. LRS state storage, a single-region deployment and Basic ACR are not a cross-region disaster recovery design. There are no backups, receipt cleanup, alerts, dashboards, private-network controls or geo-failover in this scope.

Local compilation does not verify Azure Policy, regional capacity, role propagation, Dataverse access, image contents/existence, cooperative ownership behavior, log ingestion or end-to-end export. These remain preflight/live acceptance gates before production. No image build, push, Azure deployment, Dataverse onboarding or live acceptance is claimed by these files.

References: [managed identity image pulls](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity-image-pull), [Container Apps scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app), [Azure Monitor logging](https://learn.microsoft.com/en-us/azure/container-apps/log-options).