using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class ReportCommentsService : IReportCommentsService
{
    private const int MaxPageSize = 50;

    private readonly TaqreerkDbContext _db;

    public ReportCommentsService(TaqreerkDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<ReportCommentDto>> ListAsync(
        Guid reportId, Guid? viewerUserId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var q = _db.ReportComments
            .AsNoTracking()
            .Where(c => c.ReportId == reportId);

        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.ReportId,
                c.UserId,
                UserFullName = c.User.FullName,
                c.Body,
                c.CreatedAt,
            })
            .ToListAsync(ct);

        var items = rows
            .Select(r => new ReportCommentDto(
                r.Id, r.ReportId, r.UserId, r.UserFullName, r.Body, r.CreatedAt,
                IsMine: viewerUserId.HasValue && r.UserId == viewerUserId.Value))
            .ToList();

        return new PagedResult<ReportCommentDto>(items, total, page, pageSize);
    }

    public async Task<ReportCommentDto> CreateAsync(
        Guid userId, Guid reportId, CreateCommentRequest req, CancellationToken ct = default)
    {
        var publishedReportExists = await _db.Reports
            .AsNoTracking()
            .AnyAsync(r => r.Id == reportId && r.Status == ReportStatus.Published, ct);
        if (!publishedReportExists)
            throw new KeyNotFoundException("Report not found.");

        var comment = new ReportComment
        {
            ReportId = reportId,
            UserId = userId,
            Body = req.Body.Trim(),
        };
        _db.ReportComments.Add(comment);
        await _db.SaveChangesAsync(ct);

        // Reload with the user name projected in (cheaper than tracking the
        // navigation through SaveChanges and pulling FullName out of the
        // attached entity).
        var fullName = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        return new ReportCommentDto(
            comment.Id, comment.ReportId, comment.UserId, fullName,
            comment.Body, comment.CreatedAt, IsMine: true);
    }

    public async Task DeleteAsync(Guid userId, Guid commentId, CancellationToken ct = default)
    {
        var row = await _db.ReportComments.FirstOrDefaultAsync(c => c.Id == commentId, ct)
            ?? throw new KeyNotFoundException("Comment not found.");
        if (row.UserId != userId)
            throw new UnauthorizedAccessException("You can only delete your own comments.");

        row.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public Task<int> CountForReportAsync(Guid reportId, CancellationToken ct = default)
        => _db.ReportComments.AsNoTracking().CountAsync(c => c.ReportId == reportId, ct);
}
