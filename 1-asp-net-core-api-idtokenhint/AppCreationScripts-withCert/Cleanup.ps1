[CmdletBinding()]
param(    
    [PSCredential] $Credential,
    [Parameter(Mandatory=$False, HelpMessage='Tenant ID (This is a GUID which represents the "Directory ID" of the AzureAD tenant into which you want to create the apps')]
    [string] $tenantId
)

# Pre-requisites
if ($null -eq (Get-Module -ListAvailable -Name "Az.Accounts")) {  
    Install-Module -Name "Az.Accounts" -Scope CurrentUser 
}
if ($null -eq (Get-Module -ListAvailable -Name "Az.Resources")) {  
    Install-Module "Az.Resources" -Scope CurrentUser 
}
Import-Module -Name "Az.Accounts"
Import-Module -Name "Az.Resources"
$ErrorActionPreference = 'Stop'

Function Cleanup
{
<#
.Description
This function removes the Azure AD applications for the sample. These applications were created by the Configure.ps1 script
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
    
    if (!$tenantId)
    {
        $tenantId = $creds.Context.Account.Tenants[0]
    }
    $tenant = Get-AzTenant
    $tenantDomainName =  ($tenant | Where { $_.Id -eq $tenantId }).Domains[0]
    
    # Removes the applications
    Write-Host "Cleaning-up applications from tenant '$tenantDomainName'"

    Write-Host "Removing 'client' (Verifiable Credentials ASP.Net core sample) if needed"
    $app = Get-AzADApplication -DisplayName "Verifiable Credentials ASP.Net core sample"  

    if ($null -ne $app)
    {
        $app | Remove-AzADApplication
        Write-Host "Removed."
    }

}

Cleanup -Credential $Credential -tenantId $TenantId
