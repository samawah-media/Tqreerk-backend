using System.Collections.Concurrent;

namespace Taqreerk.Application.Common;

/// <summary>
/// Holds Excel <c>keywords</c> per import row until the processor creates the
/// <see cref="Domain.Entities.Report"/> and writes <c>report_keywords</c>.
/// Not persisted — survives only for in-flight jobs in this process.
/// </summary>
public static class BulkImportKeywordsCache
{
    private static readonly ConcurrentDictionary<(Guid JobId, int RowIndex), string?> Store = new();

    public static void Set(Guid jobId, int rowIndex, string? keywords) =>
        Store[(jobId, rowIndex)] = keywords;

    public static string? Get(Guid jobId, int rowIndex) =>
        Store.TryGetValue((jobId, rowIndex), out var v) ? v : null;

    public static void ClearJob(Guid jobId)
    {
        foreach (var key in Store.Keys.Where(k => k.JobId == jobId).ToList())
            Store.TryRemove(key, out _);
    }
}
