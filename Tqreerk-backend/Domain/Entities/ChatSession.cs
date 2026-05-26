using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class ChatSession : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid ReportId { get; set; }
    public string Title { get; set; } = "New Chat";

    public User User { get; set; } = null!;
    public Report Report { get; set; } = null!;
    public ICollection<ChatMessage> Messages { get; set; } = [];
}
