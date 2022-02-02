[CmdletBinding()]
param(
    [Parameter(Mandatory=$False, HelpMessage='Tenant ID (This is a GUID which represents the "Directory ID" of the AzureAD tenant into which you want to create the apps')][string] $tenantId,
    [Parameter(Mandatory=$False, HelpMessage='Switch if you want to generate a client_secret for the app')][switch]$ClientSecret = $False,
    [Parameter(Mandatory=$False, HelpMessage='Switch if you want to generate a client certificate for the app')][switch]$ClientCertificate = $False
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
# default to client_secret
if ( !$ClientSecret -and !$ClientCertificate ) { $ClientSecret = $True }

Function UpdateLine([string] $line, [string] $value)
{
    $index = $line.IndexOf(':')
    $delimiter = ','
    if ($index -eq -1) {
        $index = $line.IndexOf('=')
        $delimiter = ';'
    }
    if ($index -ige 0) {
        $line = $line.Substring(0, $index+1) + " "+'"'+$value+'"'+$delimiter
    }
    return $line
}

Function UpdateTextFile([string] $configFilePath, [System.Collections.HashTable] $dictionary)
{
    $lines = Get-Content $configFilePath
    for( $index = 0; $index -lt $lines.Length; $index++ ) {
        foreach($key in $dictionary.Keys) {
            if ($lines[$index].Contains($key)) {
                $lines[$index] = UpdateLine $lines[$index] $dictionary[$key]
            }
        }
    }
    Set-Content -Path $configFilePath -Value $lines -Force
}

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

# Create the client AAD application
$appName = "Verifiable Credentials ASP.Net core sample"
$clientAadApplication = Get-AzADApplication -DisplayName $appName
if ($null -ne $clientAadApplication) {
    Write-Host "App $appName ($($clientAadApplication.AppId)) already exists"
    exit
}
Write-Host "Creating the AAD application ($appName)"
$clientAadApplication = New-AzADApplication -DisplayName $appName `
                                            -IdentifierUris "https://$tenantDomainName/vcaspnetcoresample" 
$clientServicePrincipal = ($clientAadApplication | New-AzADServicePrincipal)
Write-Host "AppId $($clientAadApplication.AppId)"
# Generate a certificate or client_secret
$client_secret = ""
$certSubject = ""
if ( $ClientCertificate ) {
    $certSubject = "CN=vcaspnetcoresample"
    Write-Host "Generating self-signed certificate $certSubject"
    # generating a self signed certificate is done differently on Windows vs Mac/Linux
    if ( $False -eq $isMacLinux ) {
        $certificate = New-SelfSignedCertificate -Subject $certSubject -CertStoreLocation "Cert:\CurrentUser\My" `
                                                 -KeyExportPolicy "Exportable" -KeySpec "Signature"
        $certData = [System.Convert]::ToBase64String($certificate.RawData, 'InsertLineBreaks')
    } else { # Mac/Linux - generate the self-signed certificate via openssl
        & openssl genrsa -out ./aadappcert.pem 2048 
        & openssl req -new -key ./aadappcert.pem -out ./aadappcert.csr -subj "/$certSubject"
        & openssl x509 -req -days 365 -in ./aadappcert.csr -signkey ./aadappcert.pem -out ./aadappcert.crt
        $certData = Get-Content ./aadappcert.crt | Out-String
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
$permissionName = "VerifiableCredential.Create.All"
Write-Host "Adding API Permission $permissionName"
$spVCRS = Get-AzADServicePrincipal -DisplayName "Verifiable Credential Request Service"
$permissionId = ($spVCRS.AppRole | where {$_.DisplayName -eq $permissionName}).Id
Add-AzADAppPermission -ObjectId $clientAadApplication.Id -ApiId $spVCRS.AppId -PermissionId $permissionId -Type "Role"

Write-Host "Done creating the client application ($appName)"

# URL of the AAD application in the Azure portal
# Future? $clientPortalUrl = "https://portal.azure.com/#@"+$tenantName+"/blade/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/Overview/appId/"+$clientAadApplication.AppId+"/objectId/"+$clientAadApplication.ObjectId+"/isMSAApp/"
$clientPortalUrl = "https://portal.azure.com/#blade/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/CallAnAPI/appId/"+$clientAadApplication.AppId+"/objectId/"+$clientAadApplication.ObjectId+"/isMSAApp/"

# create the HTML file with deployment details
Set-Content -Value "<html><body><table>" -Path createdApps.html
Add-Content -Value "<thead><tr><th>Application</th><th>AppId</th><th>Url in the Azure portal</th></tr></thead><tbody>" -Path createdApps.html
Add-Content -Value "<tr><td>$appName</td><td>$($clientAadApplication.AppId)</td><td><a href='$clientPortalUrl'>$appName</a></td></tr>" -Path createdApps.html
Add-Content -Value "</tbody></table></body></html>" -Path createdApps.html  

# Update config file for the app
$configFile = $pwd.Path + "$([IO.Path]::DirectorySeparatorChar)..$([IO.Path]::DirectorySeparatorChar)appsettings.json"
Write-Host "Updating the sample code ($configFile)"
$dictionary = @{ "TenantId" = $tenantId; "ClientId" = $clientAadApplication.AppId; "ClientSecret" = $client_secret; "CertificateName" = $certSubject };
UpdateTextFile -configFilePath $configFile -dictionary $dictionary
Write-Host ""
Write-Host "IMPORTANT: Please follow the instructions below to complete a few manual step(s) in the Azure portal":
Write-Host "- For '$appName'"
Write-Host "  - Navigate to $clientPortalUrl"
Write-Host "  - Click on 'Grant admin consent for $tenantName' in the API Permissions page"
