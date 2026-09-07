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

By default, assets are written to `scripts/Exports/yyyy/MM/dd/<auditid>.json`. The script follows every `@odata.nextLink`, stores its checkpoint in `scripts/export-audit-logs.state.json`, and keeps an audit ID index beside it. Delta requests include the last successful `createdon` timestamp; the inclusive boundary can return records again, so duplicate `auditid` values are skipped. The checkpoint advances only after the complete paged response has been persisted successfully.

Use a separate `-StatePath` when changing the organization or export path. Remove both the state JSON and its `.auditids.txt` index only when intentionally starting a fresh export.

Here's an example output:

```console
$ ./scripts/export-audit-logs.ps1
Audit export completed: 1364 exported, 0 duplicates skipped, checkpoint 2026-09-07T09:35:44.0860000+00:00.
Audit export completed: 0 exported, 1 duplicates skipped, checkpoint 2026-09-07T09:35:44.0860000+00:00.
Audit export completed: 0 exported, 1 duplicates skipped, checkpoint 2026-09-07T09:35:44.0860000+00:00.
Audit export completed: 0 exported, 1 duplicates skipped, checkpoint 2026-09-07T09:35:44.0860000+00:00.
```
