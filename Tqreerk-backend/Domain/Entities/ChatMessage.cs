using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class ChatMessage : BaseEntity
{
    public Guid SessionId { get; set; }
    public string Role { get; set; } = string.Empty;      // "user" | "assistant"
    public string Content { get; set; } = string.Empty;

    /// <summary>jsonb — page numbers used to answer, e.g. [7, 23, 41]</summary>
    public string? SourcePages { get; set; }

    public ChatSession Session { get; set; } = null!;
}
