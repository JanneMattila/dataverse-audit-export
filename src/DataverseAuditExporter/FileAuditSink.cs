using System.Globalization;
using System.Text;

namespace DataverseAuditExporter;

public sealed class FileAuditSink(string exportPath) : IAuditSink
{
    public string Id { get; } = "file-" + ExporterOptions.Hash(Path.GetFullPath(exportPath));

    public async Task DeliverAsync(IReadOnlyList<AuditRecord> records, Func<AuditRecord, Task> acknowledge, CancellationToken cancellationToken)
    {
        foreach (var audit in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(exportPath, audit.CreatedOn.UtcDateTime.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
            var path = Path.Combine(directory, $"{audit.Id:D}.json");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(directory);
                var temporary = path + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, audit.Json, new UTF8Encoding(false), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    try { File.Move(temporary, path); }
                    catch (IOException) when (File.Exists(path)) { }
                }
                finally
                {
                    File.Delete(temporary);
                }
            }
            await acknowledge(audit);
        }
    }
}