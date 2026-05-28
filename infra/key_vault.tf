resource "azurerm_key_vault" "main" {
  name                       = var.key_vault_name
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  sku_name                   = "standard"
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  rbac_authorization_enabled = true
  soft_delete_retention_days = 90
  purge_protection_enabled   = false
}

# The human identity running terraform apply gets Key Vault Administrator so they
# can write secrets (B2C client secret, etc.) in later phases without switching tools.
# The Managed Identity gets the narrower Key Vault Secrets User in role_assignments.tf.
resource "azurerm_role_assignment" "kv_admin_deployer" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Administrator"
  principal_id         = data.azurerm_client_config.current.object_id
}
