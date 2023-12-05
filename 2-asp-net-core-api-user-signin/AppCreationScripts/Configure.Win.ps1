[CmdletBinding()]
param(
    [PSCredential] $Credential,
    [Parameter(Mandatory=$False)] [string] $tenantId,
    [string] $appName,
    [switch] $UpdateAppSettings=$True
)
if ($PSVersionTable.PSEdition -eq "Core") {
    Write-error "Wrong Powershell Edition. You need Windows Powershell for this script"
    exit 1
} 

$appConfig = (Get-Content "$PSScriptRoot\configure-app.json" | ConvertFrom-json)
if ( "" -eq $appName ) {
    $appName = $appConfig.appName
}
$appUriName = $appName.Replace(" ", "").Replace(".", "").Replace("(", "").Replace(")", "").ToLowerInvariant()
# Pre-requisites
if ((Get-Module -ListAvailable -Name "AzureAD") -eq $null) { 
    Install-Module "AzureAD" -Scope CurrentUser 
} 
Import-Module AzureAD

if (!$Credential -and $TenantId) {
    $creds = Connect-AzureAD -TenantId $tenantId
} else {
    if (!$TenantId) {
        $creds = Connect-AzureAD -Credential $Credential
    } else {
        $creds = Connect-AzureAD -TenantId $tenantId -Credential $Credential
    }
}
if (!$tenantId) {
    $tenantId = $creds.Tenant.Id
}
$tenant = Get-AzureADTenantDetail
$tenantName =  ($tenant.VerifiedDomains | Where { $_._Default -eq $True }).Name
Write-Host "TenantID: $tenantId"
# Get the user running the script
$user = Get-AzureADUser -ObjectId $creds.Account.Id

# Create a password that can be used as an application key
Function ComputePassword {
    $aesManaged = New-Object "System.Security.Cryptography.AesManaged"
    $aesManaged.Mode = [System.Security.Cryptography.CipherMode]::CBC
    $aesManaged.Padding = [System.Security.Cryptography.PaddingMode]::Zeros
    $aesManaged.BlockSize = 128
    $aesManaged.KeySize = 256
    $aesManaged.GenerateKey()
    return [System.Convert]::ToBase64String($aesManaged.Key)
}

# Create an application key
# See https://www.sabin.io/blog/adding-an-azure-active-directory-application-and-key-using-powershell/
Function CreateAppKey([string]$pw) {
    $key = New-Object Microsoft.Open.AzureAD.Model.PasswordCredential
    $key.StartDate = [DateTime]::Now
    $key.EndDate = $key.StartDate.AddYears(1)
    $key.Value = $pw
    $key.KeyId = (New-Guid).ToString()
    return $key
}

Function AddResourcePermission($requiredAccess, $exposedPermissions, [string]$requiredAccesses, [string]$permissionType) {
    foreach($permission in $requiredAccesses.Trim().Split("|")) {
        foreach($exposedPermission in $exposedPermissions) {
            if ($exposedPermission.Value -eq $permission) {
                $resourceAccess = New-Object Microsoft.Open.AzureAD.Model.ResourceAccess
                $resourceAccess.Type = $permissionType # Scope = Delegated permissions | Role = Application permissions
                $resourceAccess.Id = $exposedPermission.Id # Read directory data
                $requiredAccess.ResourceAccess.Add($resourceAccess)
                }
        }
    }
}

Function GetRequiredPermissions([string] $appDisplayName, [string] $delegatedPermissions, [string]$appPermissions ) {
    $sp = Get-AzureADServicePrincipal -Filter "DisplayName eq '$appDisplayName'"
    $requiredAccess = New-Object Microsoft.Open.AzureAD.Model.RequiredResourceAccess
    $requiredAccess.ResourceAppId = $sp.AppId 
    $requiredAccess.ResourceAccess = New-Object System.Collections.Generic.List[Microsoft.Open.AzureAD.Model.ResourceAccess]
    if ($delegatedPermissions){
        Write-Host "Granting delegated permission(s) for $appDisplayName $delegatedPermissions"
        AddResourcePermission $requiredAccess -exposedPermissions $sp.Oauth2Permissions -requiredAccesses $delegatedPermissions -permissionType "Scope"
    }    
    if ($appPermissions) {
        Write-Host "Granting app permission(s) for $appDisplayName $appPermissions"
        AddResourcePermission $requiredAccess -exposedPermissions $sp.AppRoles -requiredAccesses $appPermissions -permissionType "Role"
    }
    return $requiredAccess
}

Write-Host "Creating the Entra ID application ($appName)"
$clientAppKey = ComputePassword
$key = CreateAppKey $clientAppKey
$clientAadApplication = New-AzureADApplication -DisplayName $appName -IdentifierUris "https://$tenantName/$appUriName" `
                                                -PasswordCredentials $key -PublicClient $False `
                                                -ReplyUrls $appConfig.redirectUrl 

$currentAppId = $clientAadApplication.AppId
$clientServicePrincipal = New-AzureADServicePrincipal -AppId $currentAppId -Tags {WindowsAzureActiveDirectoryIntegratedApp}

Write-Host "AppId (client_id): $currentAppId"
# add the user running the script as an app owner if needed
$owner = Get-AzureADApplicationOwner -ObjectId $clientAadApplication.ObjectId
if ($null -eq $owner) { 
    Add-AzureADApplicationOwner -ObjectId $clientAadApplication.ObjectId -RefObjectId $user.ObjectId
    Write-Host "'$($user.UserPrincipalName)' added as an application owner to app '$($clientServicePrincipal.DisplayName)'"
}

# Add Required permissions
$requiredResourcesAccess = New-Object System.Collections.Generic.List[Microsoft.Open.AzureAD.Model.RequiredResourceAccess]
foreach( $permission in $appConfig.apiPermissions) {
    $requiredPermissions = GetRequiredPermissions -appDisplayName $permission.appName -appPermissions $permission.appPermissions -delegatedPermissions $permission.delegatedPermissions
    $requiredResourcesAccess.Add($requiredPermissions)
}
Set-AzureADApplication -ObjectId $clientAadApplication.ObjectId -RequiredResourceAccess $requiredResourcesAccess

# Update config file 
if ( $true -eq $UpdateAppSettings ) {
    $configFile = $PSSCriptRoot + "\..\appsettings.json"
    & $PSSCriptRoot\UpdateAppSettings.ps1 -ConfigFile $configFile `
        -Settings @{ "TenantId" = $tenantId;"ClientId" = $clientAadApplication.AppId;"ClientSecret" = $clientAppKey }
} else {
    write-host "clientSecret: $clientAppKey"
}

if ( $null -ne $appConfig.groupName ) {
    $grp = Get-AzureADGroup -SearchString $appConfig.groupName
    if ( $grp ) {
        Write-Host "Group already exist $($appConfig.groupName)"
    } else {
        Write-Host "Creating group $($appConfig.groupName)"
        New-AzureADGroup -DisplayName $appConfig.groupName -MailEnabled $false -SecurityEnabled $true -MailNickname "NotSet"
    }
}

# Report outcome
$appPortalUrl = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/$($clientAadApplication.AppId)/isMSAApp~/false"
Write-Host ""
Write-Host "IMPORTANT: Please follow the instructions below to complete a few manual step(s) in the Entra portal":
Write-Host "- For '$appName'"
Write-Host "  - Navigate to '$appPortalUrl'"
Write-Host "  - Navigate to the API permissions page and click on 'Grant admin consent for $tenantName'"
Write-Host "  - When you test with ngrok or Azure AppServices, remember to add new Redirect URIs"
