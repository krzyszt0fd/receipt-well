---
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
---

## Why this stack

Solo developer shipping a receipt-management web app in 3 after-hours weeks with auth, AI/LLM extraction, and async background processing. Custom path — .NET was a deliberate choice over the JS recommended default, Angular is the choice for frontend (UI) part. ASP.NET Core webapi is the only .NET card in the registry and clears all four quality gates: C# is strongly typed, the framework's conventions are well-established (controllers, DI, middleware), .NET dominates its training-data family, and Microsoft docs are current and version-linked. Bootstrapper confidence is verified. Auth is handled by ASP.NET Core's built-in JWT bearer middleware; AI/LLM integration is a NuGet package (Anthropic or OpenAI SDK for .NET); async extraction runs via Azure Functions; tag-based search pairs Azure Search. Deployment targets Azure App Service; CI runs on GitHub Actions with auto-deploy on merge.
