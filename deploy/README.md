# Azure infrastructure

Resource-group-scoped Bicep for the Dataverse audit polling worker. These files describe infrastructure; **no Azure resources have been deployed or live-tested as part of authoring them**. Commands below are operator instructions for a separately approved rollout, not commands that have already run.

## Resources and boundaries

| Resource | Configuration |
| --- | --- |
| State storage | StorageV2, Standard_LRS, pre-created `DataverseAuditExporter` table; HTTPS/TLS 1.2, shared keys and anonymous blob access disabled |
| Blob output | Optional private `audits` container in the same storage account; disabled by default; no lifecycle or deletion policy |
| Event Hubs | Enabled by default; Standard, 1 throughput unit, `audits`, 2 partitions, 1-day retention; TLS 1.2, local/SAS authentication disabled |
| Identity | One user-assigned identity for Dataverse, Table, enabled Blob/Event Hubs outputs and image pulls |
| Registry | ACR Basic, registry RBAC mode, admin and anonymous pull disabled; ARM-audience authentication enabled for managed identity pulls |
| Host | Consumption Container Apps environment; app has no ingress, Single revision mode, min=max=1, fixed 0.5 vCPU / 1Gi RAM |
| Logs | Log Analytics, 30-day default retention, local authentication disabled; environment `azure-monitor` destination and console/system diagnostic categories |

The runtime gets **Storage Table Data Contributor at table scope**, **Storage Blob Data Contributor at container scope** when Blob output is enabled, **Azure Event Hubs Data Sender at hub scope** when Event Hubs output is enabled, and **AcrPull at registry scope**. Role names use `guid(scope, principalId, roleDefinitionId)` and `ServicePrincipal` type. It gets no registry push, hub receive, subscription-wide or resource-group-wide data role. Outputs separate the identity client/application ID (Dataverse) from principal/object ID (Azure RBAC) and ARM resource ID (Container Apps identity attachment).

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
| `organizationName` | Empty during bootstrap; required for run. One organization, or a string containing a JSON array of organizations |
| `imageTag` / `imageDigest` | Empty during bootstrap; run requires exactly one. Tag must be valid, unique, locked and never reused; `latest` is rejected. Prefer `sha256:` plus 64 lowercase hex characters |
| `stateTableName` | `DataverseAuditExporter`; 3-63 alphanumeric characters starting with a letter |
| `stateId` | `default`; 1-128 characters, not whitespace; preserve for resume |
| `intervalSeconds` | `5`; 1-86400. Full-round target interval; processing time is subtracted, with at least 1 second of sleep |
| `startFrom` | Empty; optional offset-bearing ISO 8601 value such as `2026-09-01T00:00:00Z`, validated by the application |
| `enableEventHubOutput` | `true`; provision Event Hubs resources/RBAC and configure output |
| `enableBlobOutput` | `false`; provision private container/RBAC and configure Blob output |
| `blobContainerName` | `audits`; 3-63 lowercase letters/digits or single hyphens, starting and ending with a letter/digit |
| `eventHubCapacity` | `1`; 1-20 Standard throughput units; auto-inflate disabled |
| `eventHubPartitionCount` | `2`; 1-32; **Standard cannot change partition count after creation** |
| `eventHubRetentionDays` | `1`; 1-7 |
| `logRetentionDays` | `30`; 30, 60, 90, 120, 180, 270, 365, 550 or 730 |

Bicep decorators validate ranges and allowed values; template expressions reject invalid naming and missing run-stage inputs during ARM evaluation. Compilation alone does not evaluate all deployment-time expressions or confirm image existence. Dataverse URL and timestamp semantics remain application validation responsibilities. Registry host and repository are not caller-controlled: images must be in the new registry's `dataverse-audit-exporter` repository. Bicep cannot prove that a tag is immutable; lock it before use or use a digest.

For Blob-only deployment, set `enableBlobOutput=true` and `enableEventHubOutput=false` in the parameter file before bootstrap. Set both true for both outputs. At least one must be enabled for the run stage. Blob output uses the storage account's HTTPS Blob endpoint and a private container, not a filesystem mount. Outputs `blobStorageEndpoint`, `blobContainerName` and `blobContainerResourceId` identify it. Event Hubs outputs are empty when disabled.

Changing enabled outputs or the container name changes the application's destination fingerprint; use a new `stateId` for a deliberate fresh export. In Incremental mode, disabling an output does not delete its previously deployed resources, revoke its old RBAC assignments or stop their costs. Review any cleanup separately; never delete stored audits as part of switching outputs.

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

## GitHub Actions deployments

The two manual workflows use GitHub OIDC and GitHub Environments named `dev`, `test`, or `prod`. Configure each environment with federated credentials for its deployment identity and these values:

| Type | Name | Value |
| --- | --- | --- |
| Secret | `AZURE_CLIENT_ID` | Deployment identity application/client ID |
| Secret | `AZURE_TENANT_ID` | Microsoft Entra tenant ID |
| Secret | `AZURE_SUBSCRIPTION_ID` | Target Azure subscription ID |
| Variable | `AZURE_RESOURCE_GROUP` | Stable target resource group for that environment |
| Variable | `AZURE_LOCATION` | Resource group and deployment location |
| Variable | `DATAVERSE_ORGANIZATION_NAME` | Organization hostname, short name, HTTPS origin, or JSON array string; required by the app workflow |

The deployment identity needs the resource-group permissions described above. The app workflow also needs `AcrPush` on the registry created by the infrastructure workflow. Keep environment approval rules enabled where deployment requires review.

Run **Deploy infrastructure** first. It forces `deployApplication=false` while preserving the other settings in `main.bicepparam`. After Dataverse application-user onboarding and registry push access are complete, run **Deploy container app**. It tests the application, publishes a unique commit/run tag, disables writes and deletion for that tag, and deploys the resulting digest with `deployApplication=true`. Both workflows serialize deployments to the same environment.

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

Use the user-assigned managed identity created during bootstrap directly as the Dataverse application user. No separate Entra application or federated token exchange is needed.

In Power Platform admin center, select the Dataverse environment, then Settings > Users + permissions > Application users. Add the managed identity using **`$outputs.managedIdentityClientId.value`**, its application/client ID, not its Azure principal ID or display name. Select the correct business unit and assign a dedicated security role containing **`prvReadAuditSummary`** (View Audit Summary) with the required environment-wide audit read access. The environment must have auditing enabled and audit data available.

Bicep does not create Dataverse application users or security roles. Azure RBAC assignments do not grant Dataverse permissions. Confirm the identity can read the environment's audit table before expecting the poller to work. Audit-detail enrichment is not included. See [application users](https://learn.microsoft.com/en-us/power-platform/admin/manage-application-users) and [audit retrieval privileges](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/auditing/retrieve-audit-data).

### Minimal Dataverse security role

The current exporter only queries `GET /api/data/v9.2/audits`. Microsoft's [audit retrieval documentation](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/auditing/retrieve-audit-data) distinguishes the required privileges:

| Operation | Required audit privileges | Used by this exporter? |
| --- | --- | --- |
| Query audit summary records (`/audits`) | **View Audit Summary** (`prvReadAuditSummary`) | Yes |
| `RetrieveAuditDetails`, `RetrieveRecordChangeHistory`, `RetrieveAttributeChangeHistory` | **View Audit Summary** plus **View Audit History** (`prvReadRecordAuditHistory`) | No |

Granting View Audit History does not make the exporter retrieve old/new field values; that would require additional implementation. It is not needed for the current summary-only export.

The built-in **System Administrator** role can read audits, but is substantially overprivileged for this workload. There is no documented predefined audit-summary-only role. Create a dedicated role instead of assigning or copying an administrator role; Basic User and App Opener are not audit-export roles.

An administrator with Dataverse **System Administrator** permissions should perform these steps in each source environment:

1. Open [Power Platform admin center](https://admin.powerplatform.microsoft.com/) > **Manage** > **Environments** > select the environment > **Settings** > **Users + permissions** > **Security roles**.
2. Select **+ New role**, name it **Dataverse Audit Exporter - Summary Reader**, and choose the business unit where you will assign it to the application user.
3. Turn **Include App Opener privileges for running Model-Driven apps** **Off**. This is a headless Web API application, not a model-driven app. Save the new role.
4. Open **Miscellaneous privileges**, show **All privileges**, and search for **View Audit Summary** (`prvReadAuditSummary`). Set it to **Organization**, then save. Miscellaneous privileges support Organization or None, not per-record/business-unit depth.
5. Leave **View Audit History**, audit deletion, audit configuration, impersonation, unrelated table privileges and privacy/export privileges at **None**. In particular, **Export to Excel** is not required for this Web API export. Review the assigned-privileges filters to ensure the role contains only the intended audit privilege; do not add broad Account/Contact read access merely to read audit summaries.
6. Return to **Users + permissions** > **Application users**, select the managed identity's application user, edit **Security roles**, and assign this role. Identify the application user by the managed identity's **client/application ID**, not its principal/object ID. Review other assigned roles and team memberships: Dataverse permissions are cumulative, so this role does not remove access granted elsewhere.
7. With auditing already enabled and a known audit record present, validate as the **application identity**, not an administrator or your Azure CLI user, using the read-only request below. Expect HTTP 200 and confirm the known record is accessible. Then validate the export in a nonproduction environment. A successful request under an administrator's identity does not validate this role.

```http
GET https://<organization-host>/api/data/v9.2/audits?$select=auditid,createdon&$orderby=createdon%20desc&$top=1
Authorization: Bearer <token-for-this-organization-issued-to-the-managed-identity>
Accept: application/json
```

Keep tokens private; do not paste them into source files or logs. For a 403 response, check the application-user mapping, environment, effective role assignments and any named missing privilege before granting more access. This minimal role is based on the documented API requirements, not a live verification in your environment. Environment policies or customizations may need additional investigation. Enabling/configuring auditing remains an administrator task, separate from the exporter. Azure Table/Blob/Event Hubs data roles are still required separately.

Microsoft references: [create a security role](https://learn.microsoft.com/en-us/power-platform/admin/create-edit-security-role#create-a-security-role), [privilege categories and access levels](https://learn.microsoft.com/en-us/power-platform/admin/security-roles-privileges), and [audit retrieval requirements](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/auditing/retrieve-audit-data).

## 4. Run and update

Edit [main.bicepparam](main.bicepparam): set `deployApplication=true`, supply the actual `organizationName`, set `imageDigest` to the published `$digest` (or `imageTag` to the locked release tag), and leave the other image selector empty. Keep all resource naming and checkpoint/destination settings unchanged. Optionally set `startFrom` before the first run; absent means all available audit history.

For multiple organizations in one container, the existing string parameter can carry a JSON array:

```bicep
param organizationName = '["contoso.crm4.dynamics.com","fabrikam.crm4.dynamics.com"]'
```

Grant the runtime identity Dataverse application-user access in every listed environment. Organizations are processed sequentially, with independent checkpoints and leases in the shared table. After the round, the app sleeps for `max(1 second, intervalSeconds - total round duration)`. Output destinations and authentication settings are shared. Blob names always use `<organization-host>/yyyy/MM/dd/<auditid>.json` with UTC dates. Adding an organization does not reset existing organizations' checkpoints or change their blob paths.

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

For updates, build and publish a new unique tag/digest, change only the image selector, repeat validation/what-if, then deploy incrementally. Rollback uses a retained prior digest only when the application's state schema remains compatible. Do not delete checkpoint rows as a routine restart mechanism. Historical receipt rows from older versions are ignored by the current exporter and can be removed after all old instances are stopped and upgraded; keep every `checkpoint` row.

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
DATAVERSE_EXPORTER_EventHubNamespace (only when enableEventHubOutput=true)
DATAVERSE_EXPORTER_EventHubName (only when enableEventHubOutput=true)
DATAVERSE_EXPORTER_BlobStorageEndpoint (only when enableBlobOutput=true)
DATAVERSE_EXPORTER_BlobContainerName (only when enableBlobOutput=true)
DATAVERSE_EXPORTER_StartFrom (only when supplied)
```

Cloud output is Event Hubs and/or Blob Storage; `DATAVERSE_EXPORTER_ExportPath` is absent. The Table service stores checkpoints and ownership, not audit-export files or per-audit receipts; the optional Blob container stores audit JSON. Duplicate tracking is memory-only, retaining only IDs at the latest delivered timestamp per output. Blob uploads are conditional and never overwrite existing blobs. All enabled outputs must succeed before a checkpoint advances. `StartFrom` affects only fresh state; existing state wins. Use a new `stateId` for deliberate replay, recognizing that this resends events but leaves existing blobs intact. A changed destination requires deliberate fresh-state handling in the application; do not silently repoint a running checkpoint.

Single revision and maxReplicas=1 are **not singleton guarantees**: maintenance and rollout can temporarily overlap replicas. The separately developed app must acquire/renew/release cooperative Table ownership, wait in standby when another owner holds it, and abort work/checkpoint writes when ownership is lost. This is not atomic fencing of Event Hubs. Delivery remains **at least once**, with possible duplicates on retries, restarts, and stale/in-flight sends. Restart loses the in-memory IDs; the inclusive checkpoint timestamp and records sent since the last saved checkpoint may be delivered again. Consumers must deduplicate by organization + auditid. No exactly-once or no-loss guarantee is claimed. An inclusive createdon watermark can miss records that become visible later with timestamps older than the checkpoint.

## Local developer access

No developer roles are granted automatically. An authorized operator can grant a developer **Storage Table Data Contributor** on `stateTableResourceId` and **Azure Event Hubs Data Sender** on `eventHubResourceId` outputs, using the developer's Entra object ID. Those are the same narrow data scopes as the runtime; subscription Contributor alone is insufficient. Use explicit `AuthenticationMode=AzureCli` locally, independently grant the developer Dataverse environment access and `prvReadAuditSummary`, and omit managed-identity-specific settings. Do not have local testing export against the production `stateId` or destination unintentionally. A receiver verification identity needs the separate receiver role mentioned above.

## Cost, resilience and validation status

Blob output incurs storage capacity and transaction costs, including conditional upload attempts on retries/restarts. No lifecycle expiry, immutable retention policy, versioning or backup is configured for the export container. Choose retention and protection based on audit requirements. A separately authorized verifier needs **Storage Blob Data Reader** on `blobContainerResourceId`; a local exporter needs **Storage Blob Data Contributor** there in addition to its Table and Dataverse permissions.

Bootstrap itself creates billable services even without an app: Event Hubs Standard capacity when enabled and ACR storage/service charges, plus any logs or storage activity. Run stage adds an always-on Container App (minReplicas=1); Consumption is a billing model, not a scale-to-zero guarantee here. Throughput units, poll frequency, full-history backfills, Event Hubs ingress, network transfer, and Table checkpoint/ownership operations all affect cost. No new per-audit receipt rows are stored. Log ingestion and retention also incur charges. No currency estimate or free-operation guarantee is provided.

Event Hubs' 1-day default retention is a consumer recovery window, **not archival storage**. Plan consumer durability separately. LRS state storage, a single-region deployment and Basic ACR are not a cross-region disaster recovery design. There are no backups, receipt cleanup, alerts, dashboards, private-network controls or geo-failover in this scope.

Local compilation does not verify Azure Policy, regional capacity, role propagation, Dataverse access, image contents/existence, cooperative ownership behavior, log ingestion or end-to-end export. These remain preflight/live acceptance gates before production. No image build, push, Azure deployment, Dataverse onboarding or live acceptance is claimed by these files.

References: [managed identity image pulls](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity-image-pull), [Container Apps scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app), [Azure Monitor logging](https://learn.microsoft.com/en-us/azure/container-apps/log-options).