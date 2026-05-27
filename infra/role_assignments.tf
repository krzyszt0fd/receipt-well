# Managed Identity role assignments. Both assignments use the same principal
# (the Web App's system-assigned identity) across two different resource scopes.

# Allows the App Service to read secrets via Key Vault references.
resource "azurerm_role_assignment" "mi_kv_secrets_user" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_windows_web_app.api.identity[0].principal_id
  # Explicit principal_type avoids an AAD lookup that can return the wrong type
  # for a Managed Identity, which would generate a permadiff on every plan.
  principal_type = "ServicePrincipal"
}

# Allows the App Service to read/write the Data Protection key ring blob
# and, later, to write receipt blobs.
resource "azurerm_role_assignment" "mi_storage_blob_contributor" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_windows_web_app.api.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}
