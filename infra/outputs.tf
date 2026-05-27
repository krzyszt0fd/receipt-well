# --- Post-apply actions ---
# After terraform apply, run: terraform output
# Then paste the following values into GitHub → Settings → Secrets and variables → Actions:
#   AZURE_CLIENT_ID          = azure_client_id
#   AZURE_TENANT_ID          = azure_tenant_id
#   AZURE_SUBSCRIPTION_ID    = azure_subscription_id
#   AZURE_STATIC_WEB_APPS_API_TOKEN = (see static_web_app_api_key below)
#
# Retrieve the sensitive SWA token with:
#   terraform output -raw static_web_app_api_key

output "resource_group_name" {
  value       = azurerm_resource_group.main.name
  description = "Resource group name — used in GitHub Actions azure/webapps-deploy resource-group-name parameter."
}

output "web_app_hostname" {
  value       = "https://${azurerm_windows_web_app.api.default_hostname}"
  description = "Backend API public URL. Used for Phase 5 smoke tests and frontend environment.prod.ts apiUrl."
}

output "web_app_name" {
  value       = azurerm_windows_web_app.api.name
  description = "Web App resource name — used as app-name in the backend GitHub Actions deploy workflow."
}

output "static_web_app_hostname" {
  value       = "https://${azurerm_static_web_app.web.default_host_name}"
  description = "Frontend SWA public URL. Verify in browser after first deploy."
}

output "static_web_app_api_key" {
  value       = azurerm_static_web_app.web.api_key
  sensitive   = true
  description = "SWA deployment token. Paste as GitHub secret AZURE_STATIC_WEB_APPS_API_TOKEN. Retrieve with: terraform output -raw static_web_app_api_key"
}

output "key_vault_uri" {
  value       = azurerm_key_vault.main.vault_uri
  description = "Key Vault URI. Used in Program.cs PersistKeysToAzureBlobStorage URI construction."
}

output "storage_account_name" {
  value       = azurerm_storage_account.main.name
  description = "Storage account name. Already wired into App Service app_settings; informational only."
}

output "managed_identity_principal_id" {
  value       = azurerm_windows_web_app.api.identity[0].principal_id
  description = "System-assigned Managed Identity object ID. Use to verify role assignments landed on the correct identity."
}

output "azure_client_id" {
  value       = azuread_application.github_deploy.client_id
  description = "App Registration Application (client) ID. Paste as GitHub secret AZURE_CLIENT_ID."
}

output "azure_tenant_id" {
  value       = data.azurerm_client_config.current.tenant_id
  description = "Azure AD tenant ID. Paste as GitHub secret AZURE_TENANT_ID."
}

output "azure_subscription_id" {
  value       = data.azurerm_client_config.current.subscription_id
  description = "Azure subscription ID. Paste as GitHub secret AZURE_SUBSCRIPTION_ID."
}
