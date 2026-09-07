## Dataverse Audit Export

The .NET 10 console application continuously reads Dataverse audit summary records and delivers them to JSON files, Azure Event Hubs, or both. Azure Table Storage holds resumable checkpoints, per-output delivery receipts, and renewable ownership. The [PowerShell example](scripts/README.md) remains available separately.

### Build and Test

Requires the .NET 10 SDK:

```powershell
dotnet build
dotnet test
dotnet run --project src/DataverseAuditExporter -- --help
```

### Local Run

Create the Azure resources with [the Bicep deployment guide](deploy/README.md), or supply an existing Table endpoint and table. Grant your Azure CLI user **Storage Table Data Contributor** on the table and, when publishing events, **Azure Event Hubs Data Sender** on the hub. Your user also needs Dataverse environment access and a security role with **View Audit Summary** (`prvReadAuditSummary`). Azure subscription permissions alone do not grant Dataverse access.

```powershell
az login --tenant <tenant-id>

dotnet run --project src/DataverseAuditExporter -- `
	--OrganizationName <organization> `
	--StorageTableEndpoint https://<account>.table.core.windows.net `
	--AuthenticationMode AzureCli `
	--ExportPath D:\DataverseAudit
```

To publish to Event Hubs instead, replace `--ExportPath` with:

```powershell
--EventHubNamespace <namespace>.servicebus.windows.net --EventHubName audits
```

Supply all three output options to enable both. Omitting both outputs fails startup. No local export directory is created when `ExportPath` is absent. Polling starts immediately and continues until Ctrl+C; there is no `Once` option.

### Configuration

Use `--Name value` or `--Name=value`. Precedence is CLI, then `DATAVERSE_EXPORTER_<Name>` environment variables, then the application's [settings file](src/DataverseAuditExporter/appsettings.json), then defaults. For example, `DATAVERSE_EXPORTER_AuthenticationMode=AzureCli`. Environment variable names are case-sensitive on Linux. Settings files are loaded from the executable directory. Client secrets, connection strings, and `.env` files are not supported.

| Setting | Meaning / Default |
| --- | --- |
| `OrganizationName` | Required: short name, Dynamics hostname, or HTTPS origin |
| `StorageTableEndpoint` | Required: HTTPS Table service origin |
| `StateTableName` | Existing table; `DataverseAuditExporter` |
| `StateId` | Logical export identity; `default` |
| `ExportPath` | Optional file directory; disabled by default |
| `EventHubNamespace`, `EventHubName` | Optional pair enabling Event Hubs; no SAS connection string |
| `IntervalSeconds` | Delay between cycles, 1-86400; `5` |
| `StartFrom` | ISO timestamp with offset or `Z`; used only for fresh state |
| `AuthenticationMode` | `ManagedIdentity` (default) or explicit `AzureCli` for local use |
| `ManagedIdentityClientId` | Optional user-assigned identity client ID; otherwise system-assigned |
| `TenantId` | Optional tenant for Azure CLI authentication |

In Azure, the selected managed identity is used for Dataverse, Table Storage, and Event Hubs. There is no fallback to a developer identity. Add the managed identity to Dataverse as an application user using its **application/client ID**, and assign `prvReadAuditSummary`. No client secret or tenant setting is required for managed identity. See [deployment instructions](deploy/README.md) for identity, RBAC, image publishing, and Container Apps setup.

### Delivery and Recovery

- The API is `/api/data/v9.2/audits`, ordered by `createdon`, following every `@odata.nextLink`. Output preserves the returned audit JSON, including annotations. This exports summary records, not the additional old/new field values returned by `RetrieveAuditDetails`.
- Files use UTC `yyyy/MM/dd/<auditid>.json`, UTF-8 without BOM, and atomic same-directory moves. Existing files are not overwritten. Treat successful output files as immutable; deleting a file does not clear its delivery receipt.
- Event Hubs receives one audit JSON object per event. Properties include `auditid`, `organization`, and `createdon`. Records are batched to the SDK's size limit; an oversized single record fails the cycle without skipping it.
- A checkpoint advances only when all pages have reached every enabled output and their delivery receipts have been saved. On failure the next cycle resumes the last durable checkpoint, skipping known successful deliveries per output.
- Queries restart inclusively at the last `createdon`, preserving fractional precision and handling records with equal timestamps. Records that become visible late with timestamps **older** than that boundary can be missed; this is the reference script's watermark behavior, not an arbitrary-lateness guarantee.
- Event Hubs delivery is **at least once**, not exactly once: sending can succeed before its Table receipt is saved. Consumers must deduplicate by organization plus `auditid`. No cross-partition global ordering is guaranteed.
- Table state contains a versioned checkpoint and one receipt per audit/output. It grows with exported history; there is no automatic retention cleanup. Failed initial historical scans restart from their saved initial lower bound and skip receipts.
- Instances coordinate ownership using a 60-second renewable Table lease with ETags. Standby instances wait; failed renewal cancels work before lease expiry. Container Apps can briefly overlap replicas during rollout, so min/max replicas of one alone is insufficient. Cooperative ownership cannot fence an already in-flight Event Hubs send; duplicate handling is still required.
- Saved state takes precedence over `StartFrom`. To intentionally replay or change destinations, use a new `StateId`; changing destinations with existing state fails. Replaying to the same file path leaves existing files intact. Do not run local and cloud instances with different outputs under the same state ID.

State tables must already exist. The application does not migrate the PowerShell state or audit-ID files, create Azure resources, or grant itself permissions. Neither tokens nor audit payloads are logged. An exporter intentionally blocked on a bad/oversized record needs operator intervention; it never silently discards the record.

### Container

```powershell
docker build -t dataverse-audit-exporter:dev src/DataverseAuditExporter
docker run --rm dataverse-audit-exporter:dev --help
```

The Linux image runs as a non-root user. The project-only build context excludes PowerShell exports and credentials. Container Apps uses Event Hubs output only because its local filesystem is ephemeral. Actual Azure deployment is a separate operation described in [deploy/README.md](deploy/README.md).

## Links

[Retrieve the history of audited data changes](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/auditing/retrieve-audit-data?tabs=webapi)

[audit EntityType](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/reference/audit?view=dataverse-latest)
