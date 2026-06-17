# Azure Functions (Consumption) — async S-03 extraction worker.
# Queue-triggered (storage.tf's extraction queue), GPT-4o via Azure OpenAI (openai.tf).
# Role assignments for this Function's managed identity live in role_assignments.tf.

resource "azurerm_service_plan" "functions" {
  name                = var.function_service_plan_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location

  os_type  = "Windows"
  sku_name = "Y1" # Consumption
}

resource "azurerm_windows_function_app" "extraction" {
  name                = var.function_app_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  service_plan_id     = azurerm_service_plan.functions.id
  https_only          = true

  identity {
    type = "SystemAssigned"
  }

  # Identity-based AzureWebJobsStorage: the host authenticates to the storage
  # account via this Function's managed identity instead of an access key.
  # Requires Storage Blob Data Owner + Storage Queue Data Contributor
  # (role_assignments.tf) — Reader is not sufficient for the host's own
  # state/lease/queue operations.
  storage_account_name          = azurerm_storage_account.main.name
  storage_uses_managed_identity = true

  # content_share_force_disabled is intentionally NOT set: on this Windows
  # Consumption plan + storage_uses_managed_identity, the API always reports
  # it back as false regardless of the configured value (provider/API quirk,
  # azurerm 4.74.0) — setting true here only produces a permanent no-op diff.
  # No WEBSITE_CONTENTAZUREFILECONNECTIONSTRING/WEBSITE_CONTENTSHARE app
  # settings were added, so the identity-only storage story holds in practice.

  # AzureWebJobsDashboard (classic, connection-string-only) is superseded here
  # by the Function's own OpenTelemetry/App Insights wiring (Program.cs) — and
  # would conflict with the identity-only storage story above.
  builtin_logging_enabled = false

  site_config {
    # Y1 Consumption does not support always_on; Azure returns false regardless
    # of what's set, so this must be explicit to avoid a permadiff (same
    # reasoning as the D1 web app in app_service.tf).
    always_on = false

    application_stack {
      dotnet_version              = "v9.0"
      use_dotnet_isolated_runtime = true
    }
  }

  app_settings = {
    "WEBSITE_RUN_FROM_PACKAGE" = "1"
    "ExtractionQueueName"      = var.extraction_queue_name

    "AzureStorage__BlobServiceUri"        = "https://${var.storage_account_name}.blob.core.windows.net"
    "AzureStorage__ReceiptsContainerName" = var.receipts_container_name

    "AzureSearch__ServiceUri" = "https://${azurerm_search_service.main.name}.search.windows.net"
    "AzureSearch__IndexName"  = var.search_index_name
    "AzureSearch__ApiKey"     = azurerm_search_service.main.primary_key

    # AzureOpenAI__ApiKey is intentionally omitted: absent -> the Function's
    # ApiKey-or-MI switch (Program.cs) falls back to DefaultAzureCredential,
    # using the Cognitive Services OpenAI User role granted below.
    "AzureOpenAI__Endpoint"       = azurerm_cognitive_account.openai.endpoint
    "AzureOpenAI__DeploymentName" = azurerm_cognitive_deployment.gpt4o.name
  }
}
