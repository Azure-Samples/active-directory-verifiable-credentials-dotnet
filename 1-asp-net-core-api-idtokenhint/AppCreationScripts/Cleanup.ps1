[CmdletBinding()]
param(    
    [Parameter(Mandatory=$False, HelpMessage='Tenant ID (This is a GUID which represents the "Directory ID" of the AzureAD tenant into which you want to create the apps')][string] $tenantId
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

$isMacLinux = ($env:PATH -imatch "/usr/bin" )

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

$tenant = Get-AzTenant
$tenantDomainName =  ($tenant | Where { $_.Id -eq $tenantId }).Domains[0]
    
# Removes the applications
Write-Host "Cleaning-up application from tenant '$tenantDomainName'"
$appName = "Verifiable Credentials ASP.Net core sample"
Write-Host "Removing 'client' ($appName) if needed"
$app = Get-AzADApplication -DisplayName $appName
if ($null -ne $app) {
    $app | Remove-AzADApplication
    Write-Host "Removed app $($app.AppId)"
}

$certSubject = "CN=vcaspnetcoresample"
if ( $False -eq $isMacLinux ) {
    foreach($cert in Get-ChildItem Cert:\CurrentUser\My | Where-Object {$_.Subject -match $certSubject}) {
        write-host "Removing self-signed certificate $certSubject $($cert.Thumbprint)"
        $cert | Remove-Item
    }
} else {
    write-host "Removing self-signed certificate $certSubject files ./aadappcert*"
    & rm ./aadappcert*
}
