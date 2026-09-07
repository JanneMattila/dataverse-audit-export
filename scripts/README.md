## Export audit data script

The export script uses Dataverse Web API v9.2 and OAuth client credentials. Settings can be supplied as parameters, as `DATAVERSE_*` environment variables, or in `scripts/.env` using `tenant`, `client_id`, `client_secret`, and `organization_name`.

Run continuously, starting with all historical audit data and then polling every five seconds:

```powershell
./scripts/export-audit-logs.ps1 -OrganizationName org1c361550
```

Start continuous polling from a specific UTC timestamp instead of fetching all history:

```powershell
./scripts/export-audit-logs.ps1 `
	-OrganizationName org1c361550 `
	-StartFrom "2026-09-03T15:29:11.512Z"
```

Run only one complete export cycle:

```powershell
./scripts/export-audit-logs.ps1 `
	-OrganizationName org1c361550 `
	-Once `
	-ExportPath D:\DataverseAudit
```

Use `-IntervalSeconds` to change the default five-second delay between scheduled pulls.

By default, assets are written to `scripts/Exports/yyyy/MM/dd/<auditid>.json`. The script follows every `@odata.nextLink` and stores its checkpoint in `scripts/export-audit-logs.state.json`. Delta requests start inclusively at the exact last successful `createdon` timestamp, preventing a timestamp gap even when multiple records share the boundary. The checkpoint advances only after the complete paged response has been persisted successfully.

Duplicate tracking is memory-only: the script retains all IDs at the latest successfully delivered timestamp, including ties across pages and polling cycles, and clears older IDs when that timestamp advances. It never loads or writes an audit-ID index. On retry or restart, older records may be processed again, but existing JSON files are not overwritten. Memory use depends on the number of IDs sharing the latest timestamp, not the full export history.

Use a separate `-StatePath` when changing the organization or export path. Remove the state JSON only when intentionally starting a fresh export. Existing `.auditids.txt` files from older versions are ignored; they can be deleted after old script instances have stopped. This update does not delete them automatically.

Newly exported JSON files include the configured organization hostname as a top-level field, for example `"organization": "contoso.crm4.dynamics.com"`. The script adds or replaces this field while preserving the other audit fields and annotations. Existing exported files are not rewritten.

Run the offline regression checks with `pwsh -File tests/scripts/export-audit-logs.Tests.ps1` from the repository root. They use synthetic audits and temporary output files, without contacting Dataverse.

Here's an example output:

```console
$ ./scripts/export-audit-logs.ps1
Audit export completed: 1364 exported, 0 duplicates skipped, checkpoint 2026-09-07T09:35:44.0860000+00:00.
Audit export completed: 0 exported, 1 duplicates skipped, checkpoint 2026-09-07T09:35:44.0860000+00:00.
Audit export completed: 0 exported, 1 duplicates skipped, checkpoint 2026-09-07T09:35:44.0860000+00:00.
Audit export completed: 0 exported, 1 duplicates skipped, checkpoint 2026-09-07T09:35:44.0860000+00:00.
```
