using Microsoft.AspNetCore.Mvc;
using UrlShortener.Application;

namespace UrlShortener.Controllers;

[ApiController]
[Route("api/workflows")]
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
}
