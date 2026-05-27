resource "azurerm_static_web_app" "web" {
  name                = var.static_web_app_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku_tier            = "Free"
  sku_size            = "Free"

  # GitHub repo linking is intentionally omitted. Linking requires a GitHub PAT
  # with repo+workflow scopes, and causes Azure to auto-commit a workflow file
  # that Terraform cannot manage. Instead, use the api_key output to set the
  # AZURE_STATIC_WEB_APPS_API_TOKEN GitHub secret and create the workflow manually.
}
