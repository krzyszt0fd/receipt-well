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

# S-03 queue producer: the API enqueues receiptId after the pending-doc write.
# Send-only — narrower than the Function's dequeue-capable grant below.
resource "azurerm_role_assignment" "api_storage_queue_sender" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Queue Data Message Sender"
  principal_id         = azurerm_windows_web_app.api.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# Read-only queue metadata access for the API's /health check (QueueHealthCheck
# calls QueueClient.GetPropertiesAsync). Message Sender alone does not cover
# this — it only grants the messages/add data action.
resource "azurerm_role_assignment" "api_storage_queue_reader" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Queue Data Reader"
  principal_id         = azurerm_windows_web_app.api.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# S-03 extraction worker (functions.tf). Identity-based AzureWebJobsStorage
# requires the host itself — not just the queue trigger — to hold these roles:
# Storage Blob Data Owner for host state/leases, Storage Queue Data Contributor
# for the host's own queue operations (this also covers dequeue for the
# extraction + poison triggers, so no separate Message Processor grant is needed).
resource "azurerm_role_assignment" "function_storage_blob_owner" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = azurerm_windows_function_app.extraction.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "function_storage_queue_contributor" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_windows_function_app.extraction.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# Lets the Function call the Phase 3 Azure OpenAI deployment via its managed
# identity when AzureOpenAI__ApiKey is unset (functions.tf).
resource "azurerm_role_assignment" "function_openai_user" {
  scope                = azurerm_cognitive_account.openai.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_windows_function_app.extraction.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}
