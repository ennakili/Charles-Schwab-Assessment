using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UrlShortener.Application;

namespace UrlShortener.Controllers;

[ApiController]
[Route("api/workflows")]
[Authorize(Policy = ApiKeyAuthenticationOptions.PolicyName)]
public sealed class WorkflowController(OrchestrationService orchestration, IWorkflowStateStore stateStore) : ControllerBase
{
    /// <summary>Executes the governed SDLC workflow for a requirement.</summary>
    [HttpPost("execute")]
    [ProducesResponseType(typeof(WorkflowResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<WorkflowResult>> Execute(WorkflowRequest request, CancellationToken cancellationToken)
    {
        var result = await orchestration.ExecuteAsync(request, cancellationToken);
        return Ok(result);
    }

    /// <summary>Returns the append-only workflow audit trail for operational review.</summary>
    [HttpGet("audit")]
    public ActionResult<IReadOnlyList<AuditEvent>> Audit() => Ok(orchestration.ReadAudit());

    /// <summary>Returns persisted entry and exit gate evidence for operational review.</summary>
    [HttpGet("{workflowId}/gates")]
    public async Task<ActionResult<IReadOnlyList<WorkflowGateEvidence>>> Gates(string workflowId, CancellationToken cancellationToken) =>
        Ok(await stateStore.GetGateEvidenceAsync(workflowId, cancellationToken));

    /// <summary>Records a human approval or rejection decision for a high-impact workflow stage. Call Execute again to resume the workflow after approval.</summary>
    [HttpPost("{workflowId}/approvals")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Approve(string workflowId, WorkflowApprovalRequest request, CancellationToken cancellationToken)
    {
        if (request.Decision is not ("approved" or "rejected"))
            return BadRequest("Decision must be 'approved' or 'rejected'.");
        await stateStore.SaveApprovalDecisionAsync(workflowId, request.Stage, request.Decision, request.Approver, request.Reason, DateTimeOffset.UtcNow, cancellationToken);
        return Accepted();
    }

    /// <summary>Returns the recorded human approval decision for a workflow stage, if any.</summary>
    [HttpGet("{workflowId}/approvals/{stage}")]
    public async Task<ActionResult<WorkflowApprovalDecision>> GetApproval(string workflowId, string stage, CancellationToken cancellationToken)
    {
        var approval = await stateStore.GetApprovalDecisionAsync(workflowId, stage, cancellationToken);
        return approval is null ? NotFound() : Ok(approval);
    }
}

/// <summary>Payload for recording a human approval or rejection decision on a high-impact workflow stage.</summary>
public sealed record WorkflowApprovalRequest(string Stage, string Decision, string Approver, string? Reason);
