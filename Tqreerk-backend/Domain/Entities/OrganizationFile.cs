using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class OrganizationFile : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public string FileType { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public Guid UploadedBy { get; set; }

    public Organization Organization { get; set; } = null!;
}
