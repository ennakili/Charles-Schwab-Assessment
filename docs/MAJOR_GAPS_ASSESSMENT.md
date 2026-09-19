# Major Gaps Assessment

## Overall Assessment

The application is a good governed workflow prototype, but it does not yet fully implement the requested end-to-end agentic SDLC automation objective.

The URL shortener itself is runnable and the workflow engine now owns the primary execution model: an explicit dependency graph, computed ready-node waves, entry/exit gates, durable checkpoints, and audit events. Many workflow outputs are still simulated rather than generated or validated as real engineering artifacts.

## Major Gaps

### 1. Orchestration is primarily simulated

The workflow now produces structured JSON artifacts for each stage and persists them with validation evidence. Stage execution is pluggable through `IWorkflowStageHandler`, while the graph, gates, artifact schema, retry policy, and evidence contract remain centralized. The implementation stage generates a reviewable change set containing target files, operations, generated change content, patch scope, and acceptance checks, then applies it atomically under the configured source root through `IWorkflowChangeApplier`. The test stage executes the repository test command and captures exit code, duration, output, and pass/fail state. The release stage persists an evidence bundle with the test result and SHA-256 hashes for all workflow artifacts.

Agent/tool-backed handlers can now be registered for individual stages while preserving the same artifact and evidence contracts.

### 2. Graph scheduling and gates are implemented, but stage outputs are simulated

The graph now validates unknown dependencies and cycles, computes ready nodes from completed dependencies, runs each ready wave as isolated concurrent workers, applies entry and exit gates, persists first-class entry/exit gate evidence alongside stage artifacts, exposes that evidence through `GET /api/workflows/{workflowId}/gates`, executes repository tests, produces hashed release evidence, applies implementation manifests to committed workflow branches, pushes those branches, and creates GitHub pull requests containing the review evidence. Remaining gaps are:

- provide deployment-specific reviewer, required-check, label, and merge-queue values through secured environment configuration.

Dashboards can consume the gate-evidence endpoint for operational review and trend reporting.

### 3. Rollback and fallback behavior are not meaningfully exercised

The workflow supports deterministic stage-failure injection, structured transient/permanent/external-side-effect classification, bounded retries, rollback status, and handler compensation outcomes. Implementation failures remove generated source-tree changes through the configured change applier, and failure class plus compensation status are persisted with the stage checkpoint. External agent/tool handlers must implement the same compensation contract before they can participate in production execution.

### 4. Workflow state and execution metrics are durable

URL mappings, click events, audit events, workflow stage checkpoints, retry/rollback/latency snapshots, and idempotency keys now use SQLite. Workflows can resume from a supplied workflow ID after a process restart, and repeated requests with the same idempotency key reuse the existing workflow. PR review and merge authorization remain external delivery controls.

The objective calls for stateful execution. SQLite leases now prevent concurrent execution for the same workflow, expire for stale-worker takeover, renew during execution, and reconcile interrupted `running` workflows as `recovering`. When `Workflow:RedisConnection` is configured, orchestration uses Redis leases with atomic acquire, renew, and owner-checked release for highly available multi-instance deployments; SQLite remains the local fallback.

### 5. Re-planning is revision-aware

Workflow requests receive a deterministic requirement revision. When a resumed workflow receives a new revision, the requirements record is preserved, architecture and downstream stages/artifacts/gate evidence are invalidated, and the graph regenerates them under the same workflow ID with an audit lineage event.

Re-planning now delegates semantic comparison to the pluggable `IWorkflowImpactAnalyzer`, which receives the previous/current requirement and persisted artifacts, selects changed graph roots, and then computes the transitive dependency closure. Only impacted stages, artifacts, and gate evidence are invalidated while unaffected checkpoints and lineage are preserved. External agent/tool analyzers can replace the deterministic default without changing orchestration.

### 6. Required scenarios are documented more strongly than implemented

Greenfield, brownfield, and ambiguous scenarios now have executable runtime policy. Greenfield produces a new-system plan, brownfield produces a change-existing-system plan that references prior artifacts, and ambiguous requirements stop at the requirements entry gate until explicit acceptance criteria are supplied. Clarified ambiguous workflows can resume under the same workflow ID and complete the graph.

Scenario-specific artifacts and safe-stop behavior are covered by executable tests, and an external stage-specific HTTP agent handler is exercised end to end against an ASP.NET provider test server using the same artifact, gate, persistence, and compensation contracts. An opt-in deployed-provider contract test now exercises the same execute and compensate endpoints against `AGENT_PROVIDER_BASE_URL`; production validation can run it explicitly in deployment environments.

### 7. Core workflow test coverage is implemented

The test suite now covers URL validation, expiry, click analytics, full host-level HTTP API behavior, HTTP controller contracts, dependency-gate safe-stop, parallel ready-node evidence, injected transient/permanent/external-side-effect failures, rollback compensation, durable checkpoint recovery, idempotency, requirement re-planning, gate-evidence API output, and audit correlation. Opt-in provider-backed contract tests cover deployed agent providers, Redis lease acquire/renew/release, and GitHub repository access. Remaining validation is execution against deployment-specific provider environments.

## Capabilities Already Demonstrated

- Runnable .NET 10 URL shortener API.
- Explicit dependency graph with computed ready-node scheduling.
- Entry and exit gates for every workflow node.
- Human approval checkpoint gating the release-readiness stage before it pushes a branch or opens a pull request, with persisted approve/reject decisions, `awaiting-approval`/`rejected` workflow states, audit events, and re-approval required after re-planning.
- URL validation for absolute HTTP and HTTPS destinations.
- Expiration handling.
- Click counting and basic analytics.
- Durable workflow checkpoints and resume by workflow ID.
- Bounded retry counter for a simulated transient test failure.
- Audit events with workflow, stage, action, outcome, detail, and correlation ID.
- Basic success, retry, rollback, latency, and recovery metrics.
- Parallel-ready documentation path synchronized before release readiness.
- Setup, architecture, scenario, risk, trade-off, and limitation documentation.
- Focused executable coverage for workflow safety, persistence, recovery, and HTTP contracts.
- External stage-handler integration coverage preserving artifact and gate contracts.

## Recommended Remediation Order

1. ~~Run the opt-in provider contracts against deployment-specific Redis, GitHub, and agent-tool environments.~~ Done for Redis and the agent-tool provider: `DeployedRedisLeaseContractWorksWhenEnabled` passed against a local Redis container (`REDIS_CONNECTION=127.0.0.1:16379`), and `DeployedAgentProviderHonorsExecuteAndCompensateContract` passed against a standalone ASP.NET Core host implementing the `stages/{stage}/execute` and `stages/{stage}/compensate` contract (`AGENT_PROVIDER_BASE_URL=http://127.0.0.1:5299`). The GitHub contract test (`DeployedGitHubContractCanReadRepositoryWhenEnabled`) still requires a real `GITHUB_TOKEN` with read access to the target repository; it was not run here because no token is available in this environment. Run it separately with:

   ```bash
   RUN_DEPLOYED_PROVIDER_CONTRACTS=true GITHUB_TOKEN=<token> GITHUB_REPOSITORY=<owner>/<repo> \
   dotnet test Tests/UrlShortener.Tests/UrlShortener.Tests.csproj --filter DeployedGitHubContractCanReadRepositoryWhenEnabled
   ```

2. ~~Configure repository-specific PR review and merge policies in deployment environments.~~ Done: `appsettings.Production.json` now requires the existing `.github/workflows/ci.yml` `build-and-test` check via `Workflow:RequiredChecks`, and `GitHubPullRequestPublisher` validates that check is enforced by GitHub branch protection before treating a workflow as release-ready. Reviewers/team reviewers were intentionally left unset pending an explicit reviewer-assignment decision for this repository; README documents the `Workflow__Reviewers__0` / `Workflow__TeamReviewers__0` overrides and the one-time `gh api` command to configure branch protection.
3. ~~Add production adapters for distributed uniqueness, rate limiting, abuse protection, and structured telemetry.~~ Done: `RandomShortCodeGenerator` replaces the process-local sequence generator with cryptographically random codes, and `SqliteUrlMappingRepository` pre-checks for an existing code and detaches failed inserts from the change tracker so collisions retry cleanly under bounded attempts in `UrlShortenerService`. `DefaultDestinationAbusePolicy` blocks loopback, private-network, link-local (including the `169.254.169.254` cloud metadata endpoint), and configured blocked hostnames to reduce SSRF/abuse risk. ASP.NET Core's built-in rate limiter enforces per-client-IP fixed-window limits on short URL creation and redirects (`RateLimiting:ShortUrlCreate` / `RateLimiting:Redirect`, stricter in production) and returns `429`. OpenTelemetry now instruments ASP.NET Core and `HttpClient` tracing plus a custom `UrlShortener` meter (created/resolved/rejected/code-collision/rate-limited counters), exportable via OTLP through `Telemetry:OtlpEndpoint`. Remaining follow-up: point `Telemetry:OtlpEndpoint` at a real collector and tune per-environment rate limits under production load.

## Conclusion

The current implementation should be presented as a controlled-autonomy prototype, not as complete SDLC automation. Its architecture points in the right direction, but the major remaining work is to make orchestration outputs real, durable, dependency-aware, reviewable, and testable under failure and change.
