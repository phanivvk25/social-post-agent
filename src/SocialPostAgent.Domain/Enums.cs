namespace SocialPostAgent.Domain;

/// <summary>The three social platforms this agent can publish to.</summary>
public enum PlatformType
{
    Facebook = 0,
    Instagram = 1,
    LinkedIn = 2
}

/// <summary>
/// Controls whether a post must be approved by a human before it is scheduled for
/// publishing, or whether it may proceed automatically once guardrails pass.
/// </summary>
public enum PostMode
{
    Hitl = 0,
    Autonomous = 1
}

/// <summary>Lifecycle of a post as it moves through generation, review and publishing.</summary>
public enum PostStatus
{
    Draft = 0,
    PendingReview = 1,
    Approved = 2,
    Rejected = 3,
    Scheduled = 4,
    Publishing = 5,
    Published = 6,
    Failed = 7
}

/// <summary>
/// How seriously a failed guardrail check should be treated. <see cref="Block"/> stops
/// autonomous publishing and forces the post back to <see cref="PostStatus.PendingReview"/>;
/// <see cref="Warn"/> is surfaced to the reviewer but never blocks by itself.
/// </summary>
public enum GuardrailSeverity
{
    Info = 0,
    Warn = 1,
    Block = 2
}

/// <summary>Outcome of a single platform publish attempt.</summary>
public enum PublishStatus
{
    Success = 0,
    Failed = 1
}
