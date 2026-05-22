---
bootstrapped_at: 2026-05-21T00:00:00Z
starter_id: dotnet
starter_name: ".NET (ASP.NET Core webapi)"
project_name: receipt-well
language_family: dotnet
package_manager: dotnet
cwd_strategy: subdir-then-move
bootstrapper_confidence: verified
phase_3_status: ok
audit_command: "dotnet list package --vulnerable --include-transitive"
---

## Hand-off

```yaml
starter_id: dotnet
package_manager: dotnet
project_name: receipt-well
hints:
  language_family: dotnet
  team_size: solo
  deployment_target: azure-app-service
  ci_provider: github-actions
  ci_default_flow: auto-deploy-on-merge
  bootstrapper_confidence: verified
  path_taken: custom
  quality_override: false
  self_check_answers:
    typed: true
    from_official_starter: true
    conventions: true
    docs_current: true
    can_judge_agent: true
  has_auth: true
  has_payments: false
  has_realtime: false
  has_ai: true
  has_background_jobs: true
```

### Why this stack

Solo developer shipping a receipt-management web app in 3 after-hours weeks with auth, AI/LLM extraction, and async background processing. Custom path — .NET was a deliberate choice over the JS recommended default. ASP.NET Core webapi is the only .NET card in the registry and clears all four quality gates: C# is strongly typed, the framework's conventions are well-established (controllers, DI, middleware, EF Core migrations), .NET dominates its training-data family, and Microsoft docs are current and version-linked. Bootstrapper confidence is verified. Auth is handled by ASP.NET Core's built-in JWT bearer middleware; AI/LLM integration is a NuGet package (Anthropic or OpenAI SDK for .NET); async extraction runs via IHostedService or Hangfire; tag-based search pairs with PostgreSQL full-text search through EF Core. The starter scaffolds the backend only — the frontend (Razor Pages, Blazor, or a separate SPA) is wired separately as a follow-on step. Deployment targets Azure App Service; CI runs on GitHub Actions with auto-deploy on merge.

## Pre-scaffold verification

| Signal      | Value    | Severity | Notes                                                                                 |
| ----------- | -------- | -------- | ------------------------------------------------------------------------------------- |
| npm package | not run  | n/a      | dotnet starter is not a JS-family package; no npm registry check applies              |
| GitHub repo | not run  | n/a      | docs_url (learn.microsoft.com/aspnet/core) is not a GitHub URL; no pushed_at check available |

No recency signal available for this starter. Proceeded without warning.

## Scaffold log

**Resolved invocation**: `dotnet new webapi -n receipt-well -o .bootstrap-scaffold --no-restore`
**Strategy**: scaffold into a temp directory then move files up (subdir-then-move)
**Exit code**: 0
**Files moved**: 6
  - `appsettings.Development.json`
  - `appsettings.json`
  - `Program.cs`
  - `receipt-well.csproj`
  - `receipt-well.http`
  - `Properties/launchSettings.json`
**Conflicts (.scaffold siblings)**: none
**.gitignore handling**: absent in scaffold (dotnet new webapi does not generate a .gitignore)
**.bootstrap-scaffold cleanup**: deleted

_Note_: The `cmd_template` in the registry (`dotnet new webapi -n {name} --no-restore`) uses `-n` for the project name, not the output directory. For subdir-then-move, the `-o .bootstrap-scaffold` flag was added to direct output into the temp directory while using `receipt-well` as the actual project name — avoiding an invalid `.bootstrap-scaffold` C# namespace.

## Post-scaffold audit

**Tool**: `dotnet list package --vulnerable --include-transitive`
**Summary**: 0 CRITICAL, 0 HIGH, 0 MODERATE, 0 LOW
**Direct vs transitive**: not distinguished by `dotnet list package` output format

The given project `receipt-well` has no vulnerable packages given the current sources (NuGet + local Microsoft SDK cache). Clean tree at time of scaffold.

## Hints recorded but not acted on

| Hint                    | Value                              |
| ----------------------- | ---------------------------------- |
| bootstrapper_confidence | verified                           |
| quality_override        | false                              |
| path_taken              | custom                             |
| self_check_answers      | typed: true, from_official_starter: true, conventions: true, docs_current: true, can_judge_agent: true |
| team_size               | solo                               |
| deployment_target       | azure-app-service                  |
| ci_provider             | github-actions                     |
| ci_default_flow         | auto-deploy-on-merge               |
| has_auth                | true                               |
| has_payments            | false                              |
| has_realtime            | false                              |
| has_ai                  | true                               |
| has_background_jobs     | true                               |

v1 bootstrapper reads these hints for audit-trail completeness but takes no automated action on them. Auth middleware, AI/LLM NuGet integration, background job setup (IHostedService / Hangfire), Azure App Service configuration, and GitHub Actions CI are all deferred to downstream tasks or the future M1L4 skill.

## Next steps

Next: a future skill will set up agent context (CLAUDE.md, AGENTS.md). For now, your project is scaffolded and verified — happy hacking.

Useful manual steps in the meantime:
- `git init` (if you have not already) to start your own repo history.
- Review any `.scaffold` siblings the conflict policy created and decide which version of each file to keep (none were created in this run).
- Address audit findings per your project's risk tolerance — the full breakdown is in this log (0 findings; clean tree).
- Consider adding a `.gitignore` — the dotnet webapi template does not generate one. Run `dotnet new gitignore` from the project root to add the standard .NET gitignore.
