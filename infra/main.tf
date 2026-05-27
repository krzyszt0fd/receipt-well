terraform {
  required_version = ">= 1.9, < 2.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
  }

  # Local state for MVP — one developer, one machine.
  # To migrate to Azure Blob Storage backend (team / CI use):
  #
  # backend "azurerm" {
  #   resource_group_name  = "receipt-well-rg"
  #   storage_account_name = "receiptwellstorage"
  #   container_name       = "tfstate"
  #   key                  = "receipt-well.tfstate"
  # }
  #
  # Note: use a separate container ("tfstate"), not "data-protection".
  backend "local" {
    path = "terraform.tfstate"
  }
}

provider "azurerm" {
  features {
    key_vault {
      # Keep soft-deleted vaults recoverable for 90 days after terraform destroy.
      # Setting purge_soft_delete_on_destroy = true would immediately and
      # irrecoverably delete all secrets — unsafe for dev mistakes.
      purge_soft_delete_on_destroy    = false
      recover_soft_deleted_key_vaults = true
    }
    resource_group {
      # Block terraform destroy if any resource inside the group is not tracked
      # by this configuration — prevents accidental mass deletion from drift.
      prevent_deletion_if_contains_resources = true
    }
  }
}

provider "azuread" {}

# The identity running terraform apply: used for Key Vault Administrator assignment
# and for tenant_id / subscription_id outputs.
data "azurerm_client_config" "current" {}
