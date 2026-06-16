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

variable "search_service_name" {
  type        = string
  default     = "receipt-well-search"
  description = "Azure AI Search service name. Must be globally unique (*.search.windows.net)."
}

variable "search_sku" {
  type        = string
  default     = "free"
  description = "Azure AI Search SKU. Use 'free' for MVP (1 index, 50 MB, 1 service per subscription). Change to 'basic' if a Free tier service already exists in the subscription."
}

variable "search_index_name" {
  type        = string
  default     = "receipts"
  description = "Name of the receipts index in Azure AI Search."
}

variable "staging_container_name" {
  type        = string
  default     = "receipt-staging"
  description = "Blob container for SAS-upload staging. Blobs are deleted after 1 day by lifecycle policy."
}

variable "receipts_container_name" {
  type        = string
  default     = "receipts"
  description = "Blob container for confirmed receipt blobs."
}

variable "external_id_authority" {
  type        = string
  description = "Entra External ID (CIAM) authority URL — {tenant-id}.ciamlogin.com/{tenant-id}/v2.0."
  default     = ""
}

variable "external_id_client_id" {
  type        = string
  description = "Backend API app registration client ID in Entra External ID."
  default     = ""
}

variable "openai_account_name" {
  type        = string
  description = "Azure OpenAI (Cognitive Services) account name. Also used as the custom subdomain — must be globally unique (*.openai.azure.com). No default — set in terraform.tfvars (gitignored), not committed."
}

variable "openai_deployment_name" {
  type        = string
  description = "Azure OpenAI model deployment name, used as the deployment identifier the Function's IChatClient targets. No default — set in terraform.tfvars (gitignored), not committed."
}

variable "openai_deployment_capacity" {
  type        = number
  default     = 8
  description = "GPT-4o deployment capacity in thousands of Tokens-per-Minute (TPM), e.g. 8 = 8000 TPM."
}
