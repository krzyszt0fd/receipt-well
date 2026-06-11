# Azure AI Search — receipt metadata indexing and full-text tag search (S-01 through S-04).
#
# Free tier limits: 1 index, 50 MB storage, 10 000 documents, no SLA.
# Only one Free-tier Search service is allowed per Azure subscription — if you already
# have one, change var.search_sku to "basic" in terraform.tfvars.
resource "azurerm_search_service" "main" {
  name                = var.search_service_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = var.search_sku
  replica_count       = 1
  partition_count     = 1

  # API key auth is used by the backend (AzureKeyCredential).
  # The primary key is injected into App Service app settings via app_service.tf.
  local_authentication_enabled = true
}


