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
}

# azurerm 4.x: storage_account_id (full resource ID), not storage_account_name.
# This changed from provider 3.x — using the name string causes a schema error.
resource "azurerm_storage_container" "data_protection" {
  name                  = "data-protection"
  storage_account_id    = azurerm_storage_account.main.id
  container_access_type = "private"
}
