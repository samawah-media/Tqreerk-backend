using Microsoft.EntityFrameworkCore;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Common;

/// <summary>
/// Parses admin-supplied comma-separated keyword strings and syncs them
/// into <c>report_keywords</c> for search / AI tooling.
/// </summary>
public static class ReportKeywordHelper
{
    private static readonly char[] Separators = [',', '،', ';', '|', '\n'];

    public static IReadOnlyList<string> ParseCommaSeparated(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var part in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var kw = part.Trim();
            if (kw.Length > 200) kw = kw[..200];
            if (seen.Add(kw)) result.Add(kw);
        }

        return result;
    }

    /// <summary>
    /// Replaces all keywords for a report with the supplied list.
    /// </summary>
    public static async Task ReplaceAsync(
        TaqreerkDbContext db,
        Guid reportId,
        string language,
        IReadOnlyList<string> keywords,
        CancellationToken ct = default)
    {
        await db.ReportKeywords
            .Where(k => k.ReportId == reportId)
            .ExecuteDeleteAsync(ct);

        if (keywords.Count == 0)
            return;

        var lang = string.IsNullOrWhiteSpace(language) ? "ar" : language.Trim().ToLowerInvariant();
        foreach (var kw in keywords)
        {
            db.ReportKeywords.Add(new ReportKeyword
            {
                ReportId = reportId,
                Keyword = kw,
                Language = lang,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
