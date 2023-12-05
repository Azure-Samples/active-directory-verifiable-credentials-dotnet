[CmdletBinding()]
param(
    [PSCredential] $Credential,
    [Parameter(Mandatory=$False)] [string] $tenantId,
    [string] $appName
)
##############################################################################################################
# Az.Xxxx modules requires Powershell Core
##############################################################################################################
if ($PSVersionTable.PSEdition -ne "Core") {
    Write-error "Wrong Powershell Edition. You need Powershell Core for this script"
    exit 1
} 
$appConfig = (Get-Content "$PSScriptRoot\configure-app.json" | ConvertFrom-json)
if ( "" -eq $appName ) {
    $appName = $appConfig.appName
}
##############################################################################################################
# Import required modules
##############################################################################################################
if ($null -eq (Get-Module -ListAvailable -Name "Az.Accounts")) {  
    Install-Module -Name "Az.Accounts" -Scope CurrentUser 
}
if ($null -eq (Get-Module -ListAvailable -Name "Az.Resources")) {  
    Install-Module "Az.Resources" -Scope CurrentUser 
}
Import-Module -Name "Az.Accounts"
Import-Module -Name "Az.Resources"
##############################################################################################################
# Authenticate
##############################################################################################################
$ctx = Get-AzContext
if ( !$ctx ) {
    if ( $tenantId ) {
        $creds = Connect-AzAccount -TenantId $tenantId
    } else {
        $creds = Connect-AzAccount
        $tenantId = $creds.Context.Account.Tenants[0]
    }
} else {
    if ( $TenantId -and $TenantId -ne $ctx.Tenant.TenantId ) {
        write-error "You are targeting tenant $tenantId but you are signed in to tennant $($ctx.Tenant.TenantId)"
    }    
    $tenantId = $ctx.Tenant.TenantId
}
Write-Host "TenantID: $tenantId"
##############################################################################################################
# Delete the Entra ID application
##############################################################################################################
$clientAadApplication = Get-AzADApplication -DisplayName $appName
if ($null -eq $clientAadApplication) {
    Write-Host "App $appName ($($clientAadApplication.AppId)) already exists"
    exit
}
Write-Host "Deleting the Entra ID application ($appName)"
$clientAadApplication = Remove-AzADApplication -ObjectId $clientAadApplication.Id

