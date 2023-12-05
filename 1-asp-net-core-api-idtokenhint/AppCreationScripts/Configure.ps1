[CmdletBinding()]
param(
    [PSCredential] $Credential,
    [Parameter(Mandatory=$False)] [string] $tenantId,
    [string] $appName,
    [switch] $UpdateAppSettings = $true,
    [Parameter(Mandatory=$False, HelpMessage='Switch if you want to generate a client_secret for the app')][switch]$ClientSecret = $False,
    [Parameter(Mandatory=$False, HelpMessage='Switch if you want to generate a client certificate for the app')][switch]$ClientCertificate = $False
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
$appUriName = $appName.Replace(" ", "").Replace(".", "").Replace("(", "").Replace(")", "").ToLowerInvariant()
$isMacLinux = ($PSVersionTable.Platform -eq "Unix" )
if ( !$ClientSecret -and !$ClientCertificate ) { $ClientSecret = $True }
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
$tenant = Get-AzTenant
$tenantDomainName =  ($tenant | Where { $_.Id -eq $tenantId }).Domains[0]
$tenantName =  ($tenant | Where { $_.Id -eq $tenantId }).Name
Write-Host "TenantID: $tenantId"
##############################################################################################################
# Create the Entra ID application
##############################################################################################################
$clientAadApplication = Get-AzADApplication -DisplayName $appName
if ($null -ne $clientAadApplication) {
    Write-Host "App $appName ($($clientAadApplication.AppId)) already exists"
    exit
}
Write-Host "Creating the Entra ID application ($appName)"
$clientAadApplication = New-AzADApplication -DisplayName $appName `
                                            -IdentifierUris "https://$tenantDomainName/$appUriName" -ReplyUrls $appConfig.redirectUrl 
$clientServicePrincipal = ($clientAadApplication | New-AzADServicePrincipal)
Write-Host "AppId $($clientAadApplication.AppId)"
# Generate a certificate or client_secret
$client_secret = ""
$certSubject = ""
if ( $ClientCertificate ) {
    $certSubject = "CN=$appUriName"
    Write-Host "Generating self-signed certificate $certSubject"
    # generating a self signed certificate is done differently on Windows vs Mac/Linux
    if ( $False -eq $isMacLinux ) {
        $certificate = New-SelfSignedCertificate -Subject $certSubject -CertStoreLocation "Cert:\CurrentUser\My" `
                                                 -KeyExportPolicy "Exportable" -KeySpec "Signature"
        $certData = [System.Convert]::ToBase64String($certificate.RawData, 'InsertLineBreaks')
    } else { # Mac/Linux - generate the self-signed certificate via openssl
        & openssl genrsa -out ./appcert.pem 2048 
        & openssl req -new -key ./appcert.pem -out ./appcert.csr -subj "/$certSubject"
        & openssl x509 -req -days 365 -in ./appcert.csr -signkey ./appcert.pem -out ./appcert.crt
        $certData = Get-Content ./appcert.crt | Out-String
        $certData =[Convert]::ToBase64String( [System.Text.Encoding]::Ascii.GetBytes($certData) )   
    }
    $clientAadApplication | New-AzADAppCredential -CertValue $certData        
}
if ( $ClientSecret ) {
    # Get a 1 year client secret for the client Application
    Write-Host "Generating client_secret"
    $fromDate = [DateTime]::Now
    $appCreds = ($clientAadApplication | New-AzADAppCredential -StartDate $fromDate -EndDate $fromDate.AddYears(1) )
    $client_secret = $appCreds.SecretText
}

# Add Required Resources Access (from 'client' to 'Verifiable Credential Request Service')
foreach( $permission in $appConfig.apiPermissions) {
    $spPerm = Get-AzADServicePrincipal -DisplayName $permission.appName
    if ( $null -ne $permission.appPermissions ) {
        Write-Host "Granting app permission(s) for $($permission.appName) $($permission.appPermissions)"
        foreach($perm in $permission.appPermissions.Trim().Split("|")) {
            $permissionId = ($spPerm.AppRole | where {$_.Value -eq $perm}).Id
            Add-AzADAppPermission -ObjectId $clientAadApplication.Id -ApiId $spPerm.AppId -PermissionId $permissionId -Type "Role"
        }
    }
    if ( $null -ne $permission.delegatedPermissions ) {
        Write-Host "Granting delegated permission(s) for $($permission.appName) $($permission.delegatedPermissions)"
        foreach($perm in $permission.delegatedPermissions.Trim().Split("|")) {
            $permissionId = ($spPerm.Oauth2PermissionScope | where {$_.Value -eq $perm}).Id
            Add-AzADAppPermission -ObjectId $clientAadApplication.Id -ApiId $spPerm.AppId -PermissionId $permissionId -Type "Scope"
        }
    }
}
##############################################################################################################
# Update config file for the app
##############################################################################################################
if ( $true -eq $UpdateAppSettings ) {
    $configFile = $PSSCriptRoot + "\..\appsettings.json"
    & $PSSCriptRoot\UpdateAppSettings.ps1 -ConfigFile $configFile `
        -Settings @{ "TenantId" = $tenantId;"ClientId" = $clientAadApplication.AppId;"ClientSecret" = $client_secret;"CertificateName" = $certSubject }
} else {
    write-host "clientSecret: $client_secret"
}
##############################################################################################################
# Report outcome
##############################################################################################################
$appPortalUrl = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/$($clientAadApplication.AppId)/isMSAApp~/false"
Write-Host ""
Write-Host "IMPORTANT: Please follow the instructions below to complete a few manual step(s) in the Entra portal":
Write-Host "- For '$appName'"
Write-Host "  - Navigate to '$appPortalUrl'"
Write-Host "  - Navigate to the API permissions page and click on 'Grant admin consent for $tenantName'"
Write-Host "  - When you test with ngrok or Azure AppServices, remember to add new Redirect URIs"
