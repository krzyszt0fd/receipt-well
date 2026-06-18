resource "azurerm_service_plan" "main" {
  name                = var.app_service_plan_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location

  # D1 Shared is a Windows-only tier — os_type = "Windows" is required.
  # Do not add --is-linux equivalent here; D1 is not available on Linux plans.
  os_type  = "Windows"
  sku_name = "D1"
}

resource "azurerm_windows_web_app" "api" {
  name                = var.web_app_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  service_plan_id     = azurerm_service_plan.main.id
  https_only          = true

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      # "dotnet" + "v9.0" is how azurerm expresses .NET 9 on Windows App Service.
      # This is NOT the same string as az webapp list-runtimes output ("dotnet:9").
      current_stack  = "dotnet"
      dotnet_version = "v9.0"
    }

    # D1 Shared does not support always_on. Must be false explicitly — omitting
    # it causes a permadiff in azurerm 4.x because Azure returns false and the
    # provider computes null != false as drift.
    always_on = false

    # D1 Shared only supports 32-bit worker processes. Setting false or omitting
    # this causes the app to fail to start with a worker process bitness error.
    use_32_bit_worker = true
  }

  logs {
    application_logs {
      file_system_level = "Information"
    }
  }

  app_settings = {
    # AllowedOrigins__0 uses the SWA computed hostname. Terraform resolves this
    # dependency automatically: SWA is created first, then this setting is applied.
    "AllowedOrigins__0"                  = "https://${azurerm_static_web_app.web.default_host_name}"
    "AzureStorage__AccountName"          = var.storage_account_name
    "AzureStorage__KeyRingContainerName" = "data-protection"
    # BlobServiceUri + empty ConnectionString → backend uses DefaultAzureCredential (managed identity).
    "AzureStorage__BlobServiceUri"        = "https://${var.storage_account_name}.blob.core.windows.net"
    "AzureStorage__StagingContainerName"  = var.staging_container_name
    "AzureStorage__ReceiptsContainerName" = var.receipts_container_name
    # QueueServiceUri + empty ConnectionString -> the API enqueues via DefaultAzureCredential
    # (managed identity), granted Storage Queue Data Message Sender in role_assignments.tf.
    "AzureStorage__QueueServiceUri"     = "https://${var.storage_account_name}.queue.core.windows.net"
    "AzureStorage__ExtractionQueueName" = var.extraction_queue_name
    "AzureSearch__ServiceUri"           = "https://${azurerm_search_service.main.name}.search.windows.net"
    "AzureSearch__IndexName"            = var.search_index_name
    "AzureSearch__ApiKey"               = azurerm_search_service.main.primary_key
    "AzureExternalId__Authority"        = var.external_id_authority
    "AzureExternalId__ClientId"         = var.external_id_client_id
    # Required by azure/webapps-deploy@v3 for zip package deployment from GitHub Actions.
    "WEBSITE_RUN_FROM_PACKAGE" = "1"
  }
}
