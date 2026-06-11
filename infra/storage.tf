resource "azurerm_storage_account" "main" {
  name                     = var.storage_account_name
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  min_tls_version          = "TLS1_2"

  # azurerm 4.x renamed this from allow_blob_public_access
  allow_nested_items_to_be_public = false

  blob_properties {
    # Required for browser SAS PUT uploads (preflight + actual request).
    # Allowed origins: local dev + the deployed SWA frontend.
    cors_rule {
      allowed_origins = [
        "http://localhost:4200",
        "https://${azurerm_static_web_app.web.default_host_name}"
      ]
      allowed_methods    = ["PUT", "OPTIONS"]
      allowed_headers    = ["*"]
      exposed_headers    = ["ETag", "x-ms-request-id"]
      max_age_in_seconds = 3600
    }
  }
}

# azurerm 4.x: storage_account_id (full resource ID), not storage_account_name.
# This changed from provider 3.x — using the name string causes a schema error.
resource "azurerm_storage_container" "data_protection" {
  name                  = "data-protection"
  storage_account_id    = azurerm_storage_account.main.id
  container_access_type = "private"
}

# Temporary landing zone for direct SAS uploads from the browser.
# Blobs here are confirmed → copied to the receipts container, then deleted.
# The lifecycle policy below handles blobs that were never confirmed.
resource "azurerm_storage_container" "staging" {
  name                  = var.staging_container_name
  storage_account_id    = azurerm_storage_account.main.id
  container_access_type = "private"
}

# Permanent home for confirmed receipt blobs.
# Blobs are stored as {userId}/{receiptId} with Content-Type and Content-Disposition set.
resource "azurerm_storage_container" "receipts" {
  name                  = var.receipts_container_name
  storage_account_id    = azurerm_storage_account.main.id
  container_access_type = "private"
}

# Lifecycle policy: delete staging blobs older than 1 day.
# Covers: (a) abandoned uploads where confirm was never called,
#         (b) confirmed blobs where the backend's staging-delete step failed silently.
#
# Azure allows only one azurerm_storage_management_policy per storage account.
# Add further rules here as additional rule {} blocks if needed.
resource "azurerm_storage_management_policy" "main" {
  storage_account_id = azurerm_storage_account.main.id

  rule {
    name    = "staging-blob-expiry"
    enabled = true

    filters {
      # Prefix format is {container-name}/ — targets every blob in the staging container.
      prefix_match = ["${var.staging_container_name}/"]
      blob_types   = ["blockBlob"]
    }

    actions {
      base_blob {
        delete_after_days_since_modification_greater_than = 1
      }
    }
  }
}
