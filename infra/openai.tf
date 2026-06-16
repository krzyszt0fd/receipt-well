# Azure OpenAI — GPT-4o vision extraction for the S-03 async pipeline.
# Provisioned ahead of the Function (Phase 4) so a real endpoint exists for
# local validation via ApiKey. Role assignment for the Function's managed
# identity (Cognitive Services OpenAI User, ApiKey-or-MI switch) lands in
# Phase 5; this slice is OpenAI-account-only.
resource "azurerm_cognitive_account" "openai" {
  name                = var.openai_account_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  kind                = "OpenAI"
  sku_name            = "S0"

  # Required for Azure OpenAI: the data-plane endpoint becomes
  # https://{custom_subdomain_name}.openai.azure.com/, and Entra ID
  # token-based auth (the Function's managed identity, Phase 5) depends on it.
  custom_subdomain_name = var.openai_account_name

  # ApiKey auth is used for local Function development (Phase 4); in Azure
  # the Function uses its managed identity instead — same ApiKey-or-MI
  # switch already used for Blob/Search elsewhere in this stack.
  local_auth_enabled = true
}

# GPT-4o vision deployment. No rai_policy_name is set, so the deployment
# keeps Azure OpenAI's default content filter for MVP (see plan.md Critical
# Implementation Details -> Content filter; a loosened policy needs
# Microsoft Limited Access approval and is deferred to post-MVP).
resource "azurerm_cognitive_deployment" "gpt4o" {
  name                 = var.openai_deployment_name
  cognitive_account_id = azurerm_cognitive_account.openai.id

  model {
    format = "OpenAI"
    name   = "gpt-4o"
    # Pinned to the version Azure auto-assigned on first apply (confirmed via
    # smoke test). Without an explicit version, Terraform shows a permadiff
    # on every plan (wants to null out the value Azure fills in server-side).
    version = "2024-11-20"
  }

  sku {
    name     = "GlobalStandard"
    capacity = var.openai_deployment_capacity
  }
}
