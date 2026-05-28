variable "location" {
  type        = string
  default     = "westeurope"
  description = "Azure region for all resources."
}

variable "resource_group_name" {
  type        = string
  default     = "receipt-well-rg"
  description = "Name of the resource group."
}

variable "key_vault_name" {
  type        = string
  default     = "receipt-well-kv"
  description = "Key Vault name. Must be globally unique across Azure DNS (vault.azure.net)."
}

variable "storage_account_name" {
  type        = string
  default     = "receiptwellstorage"
  description = "Storage account name. Must be globally unique, 3-24 lowercase alphanumeric characters only (no hyphens)."
}

variable "app_service_plan_name" {
  type        = string
  default     = "receipt-well-plan"
  description = "App Service Plan name."
}

variable "web_app_name" {
  type        = string
  default     = "receipt-well-api"
  description = "Windows Web App name. Must be globally unique (*.azurewebsites.net)."
}

variable "static_web_app_name" {
  type        = string
  default     = "receipt-well-web"
  description = "Azure Static Web Apps resource name."
}

variable "oidc_app_registration_name" {
  type        = string
  default     = "receipt-well-github-deploy"
  description = "Display name for the Azure AD App Registration used by GitHub Actions OIDC."
}

variable "github_org" {
  type        = string
  description = "GitHub organization or username. Used in the OIDC federated credential subject claim."
}

variable "github_repo" {
  type        = string
  default     = "receipt-well"
  description = "GitHub repository name."
}

variable "github_deploy_branch" {
  type        = string
  default     = "develop"
  description = "Branch name for the GitHub Actions OIDC federated credential subject claim. Must match exactly: repo:<org>/<repo>:ref:refs/heads/<branch>."
}

variable "external_id_authority" {
  type        = string
  description = "Entra External ID authority URL (ciamlogin.com endpoint)."
  default     = ""
}

variable "external_id_client_id" {
  type        = string
  description = "Backend API app registration client ID in Entra External ID."
  default     = ""
}
