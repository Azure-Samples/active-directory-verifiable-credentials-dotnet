[CmdletBinding()]
param(
    [PSCredential] $Credential,
    [Parameter(Mandatory=$True, HelpMessage='Tenant ID (This is a GUID which represents the "Directory ID" of the AzureAD tenant into which you want to create the apps')]
    [string] $tenantId
)

Function ConfigureVCService
{
<#.Description
   This function adds the service principal for the verifiable credentials request service (client API)
   It exposes the VerifiableCredential.Create.All scope which is needed by the applications permissions for
   the issuance and verification applications
#> 
    # $tenantId is the Active Directory Tenant. This is a GUID which represents the "Directory ID" of the AzureAD tenant
    # into which you want to create the apps. Look it up in the Azure portal in the "Properties" of the Azure AD.

    # Login to Azure PowerShell (interactive if credentials are not already provided:
    # you'll need to sign-in with creds enabling your to create apps in the tenant)
    if (!$Credential -and $TenantId)
    {
        $creds = Connect-AzAccount -TenantId $tenantId
    }
    else
    {
        if (!$TenantId)
        {
            $creds = Connect-AzAccount -Credential $Credential
        }
        else
        {
            $creds = Connect-AzAccount -TenantId $tenantId -Credential $Credential
        }
    }
    
    $appId = "bbb94529-53a3-4be5-a069-7eaf2712b826"
    if ( $null -eq (Get-AzADServicePrincipal -ApplicationId $appId) ) {
      New-AzADServicePrincipal -ApplicationId $appId -DisplayName "Verifiable Credential Request Service"
    }
    
}

# Pre-requisites
if ($null -eq (Get-Module -ListAvailable -Name "Az.Accounts")) {  
    Install-Module -Name "Az.Accounts" -Scope CurrentUser 
}
if ($null -eq (Get-Module -ListAvailable -Name "Az.Resources")) {  
    Install-Module "Az.Resources" -Scope CurrentUser 
}
Import-Module -Name "Az.Accounts"
Import-Module -Name "Az.Resources"

ConfigureVCService -Credential $Credential -tenantId $TenantId
