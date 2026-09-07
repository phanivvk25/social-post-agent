using SocialPostAgent.Domain.Common;

namespace SocialPostAgent.Domain.Entities;

/// <summary>
/// A single guardrail rule's verdict against one <see cref="Post"/>, kept for the
/// reviewer's "why was this flagged" view and for the autonomous-mode audit trail.
/// </summary>
public class GuardrailLog : BaseEntity
{
    public Guid PostId { get; set; }

    public Post Post { get; set; } = null!;

    /// <summary>Stable identifier of the rule that produced this result, e.g. "caption-length".</summary>
    public string RuleName { get; set; } = string.Empty;

    public GuardrailSeverity Severity { get; set; }

    public bool Passed { get; set; }

    /// <summary>Human-readable explanation, written for a PR/marketing reviewer, not an engineer.</summary>
    public string Message { get; set; } = string.Empty;
}
