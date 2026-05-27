resource "azuread_application" "github_deploy" {
  display_name = var.oidc_app_registration_name
}

resource "azuread_service_principal" "github_deploy" {
  client_id = azuread_application.github_deploy.client_id
}

resource "azuread_application_federated_identity_credential" "github_develop" {
  application_id = azuread_application.github_deploy.id
  display_name   = "github-${var.github_deploy_branch}"
  description    = "GitHub Actions OIDC for ${var.github_org}/${var.github_repo} branch ${var.github_deploy_branch}"
  audiences      = ["api://AzureADTokenExchange"]
  issuer         = "https://token.actions.githubusercontent.com"
  # Subject must match exactly what GitHub sends in the OIDC token sub claim.
  # Any character difference — including trailing slash or wrong prefix — causes
  # AADSTS70021 at GitHub Actions runtime, not at terraform apply time.
  subject = "repo:${var.github_org}/${var.github_repo}:ref:refs/heads/${var.github_deploy_branch}"
}

# Contributor on the resource group (not the subscription) — principle of least privilege.
# The GitHub Actions workflow only needs to deploy to resources within this group.
resource "azurerm_role_assignment" "github_sp_contributor" {
  scope                = azurerm_resource_group.main.id
  role_definition_name = "Contributor"
  principal_id         = azuread_service_principal.github_deploy.object_id
  principal_type       = "ServicePrincipal"
}
