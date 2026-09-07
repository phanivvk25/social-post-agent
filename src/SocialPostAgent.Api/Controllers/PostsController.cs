using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialPostAgent.Application.DTOs;
using SocialPostAgent.Application.Orchestration;
using SocialPostAgent.Domain;
using SocialPostAgent.Domain.Repositories;

namespace SocialPostAgent.Api.Controllers;

/// <summary>
/// The API surface the React dashboard talks to: compose a post, list/inspect posts and
/// their guardrail findings, and approve or reject anything sitting in Pending Review.
/// Actual publishing is triggered indirectly — approving a post hands it to the
/// scheduler, which calls back into <see cref="IPostOrchestrator.PublishScheduledPostAsync"/>.
/// </summary>
[ApiController]
[Route("api/posts")]
public sealed class PostsController : ControllerBase
{
    private readonly IPostOrchestrator _orchestrator;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<PostsController> _logger;

    public PostsController(IPostOrchestrator orchestrator, IUnitOfWork unitOfWork, ILogger<PostsController> logger)
    {
        _orchestrator = orchestrator;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    /// <summary>Generates a new post (copy + image) from a brief and routes it per its mode.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(PostDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PostDetailDto>> Compose([FromBody] ComposePostRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Brief))
        {
            return BadRequest("A brief is required.");
        }

        try
        {
            var post = await _orchestrator.ComposeAsync(request, cancellationToken);
            var dto = PostDetailDto.FromEntity(post);
            return CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Lists posts, optionally filtered by status, newest first — the review queue and history views.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PostSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PostSummaryDto>>> List(
        [FromQuery] PostStatus? status,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 200);

        var posts = await _unitOfWork.Posts
            .Query()
            .Where(p => status == null || p.Status == status)
            .OrderByDescending(p => p.CreatedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

        return Ok(posts.Select(PostSummaryDto.FromEntity).ToList());
    }

    /// <summary>Full detail for one post, including its guardrail findings and per-platform publish results.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PostDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PostDetailDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var post = await _unitOfWork.Posts
            .Query()
            .Include(p => p.GuardrailLogs)
            .Include(p => p.PlatformResults)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        return post is null ? NotFound() : Ok(PostDetailDto.FromEntity(post));
    }

    /// <summary>Approves a post pending human review — the HITL "approve" button.</summary>
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(typeof(PostDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PostDetailDto>> Approve(Guid id, [FromBody] ApprovePostRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var post = await _orchestrator.ApproveAsync(id, request, cancellationToken);
            return Ok(PostDetailDto.FromEntity(post));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary>Rejects a post pending human review — the HITL "reject" button.</summary>
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType(typeof(PostDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PostDetailDto>> Reject(Guid id, [FromBody] RejectPostRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var post = await _orchestrator.RejectAsync(id, request, cancellationToken);
            return Ok(PostDetailDto.FromEntity(post));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }
}
