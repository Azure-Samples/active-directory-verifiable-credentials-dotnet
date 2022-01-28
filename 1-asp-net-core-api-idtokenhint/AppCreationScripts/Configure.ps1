[CmdletBinding()]
param(
    [PSCredential] $Credential,
    [Parameter(Mandatory=$False, HelpMessage='Tenant ID (This is a GUID which represents the "Directory ID" of the AzureAD tenant into which you want to create the apps')]
    [string] $tenantId
)

$cmd = (get-command Add-AzADAppPermission -ErrorAction SilentlyContinue)
if ( $null -eq $cmd) {
    Write-Error "You need to update to the latest 'Az' module`nPlease run:`n`nUpdate-Module -Name Az -Force" -ErrorAction Stop
}
<#
 This script creates the Azure AD applications needed for this sample and updates the configuration files
 for the visual Studio projects from the data in the Azure AD applications.

 Before running this script you need to install the AzureAD cmdlets as an administrator. 
 For this:
 1) Run Powershell as an administrator
 2) in the PowerShell window, type: Install-Module AzureAD

 There are four ways to run this script. For more information, read the AppCreationScripts.md file in the same folder as this script.
#>

# Create a password that can be used as an application key
Function UpdateLine([string] $line, [string] $value)
{
    $index = $line.IndexOf('=')
    $delimiter = ';'
    if ($index -eq -1)
    {
        $index = $line.IndexOf(':')
        $delimiter = ','
    }
    if ($index -ige 0)
    {
        $line = $line.Substring(0, $index+1) + " "+'"'+$value+'"'+$delimiter
    }
    return $line
}

Function UpdateTextFile([string] $configFilePath, [System.Collections.HashTable] $dictionary)
{
    $lines = Get-Content $configFilePath
    $index = 0
    while($index -lt $lines.Length)
    {
        $line = $lines[$index]
        foreach($key in $dictionary.Keys)
        {
            if ($line.Contains($key))
            {
                $lines[$index] = UpdateLine $line $dictionary[$key]
            }
        }
        $index++
    }

    Set-Content -Path $configFilePath -Value $lines -Force
}

Set-Content -Value "<html><body><table>" -Path createdApps.html
Add-Content -Value "<thead><tr><th>Application</th><th>AppId</th><th>Url in the Azure portal</th></tr></thead><tbody>" -Path createdApps.html

Function ConfigureApplications
{
<#.Description
   This function creates the Azure AD applications for the sample in the provided Azure AD tenant and updates the
   configuration files in the client and service project  of the visual studio solution (App.Config and Web.Config)
   so that they are consistent with the Applications parameters
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
    $tenantName =  ($tenant | Where { $_.Id -eq $tenantId }).Name

    # Get the user running the script
    $user = Get-AzADUser -Mail $creds.Context.Account.Id

    # Create the client AAD application
    Write-Host "Creating the AAD application (Verifiable Credentials ASP.Net core sample)"
    $clientAadApplication = New-AzADApplication -DisplayName "Verifiable Credentials ASP.Net core sample" `
                                                -IdentifierUris "https://$tenantDomainName/vcaspnetcoresample" 
    $clientServicePrincipal = ($clientAadApplication | New-AzADServicePrincipal)
    # Get a 2 years application key for the client Application
    $fromDate = [DateTime]::Now
    $endDate = $fromDate.AddYears(2)
    $appCreds = ($clientAadApplication | New-AzADAppCredential -StartDate $fromDate -EndDate $endDate)

    # Add Required Resources Access (from 'client' to 'Verifiable Credential Request Service')
    Write-Host "Getting access from 'client' to 'Microsoft Graph'"
    $spVCRS = Get-AzADServicePrincipal -DisplayName "Verifiable Credential Request Service"
    $permissionId = ($spVCRS.AppRole | where {$_.DisplayName -eq "VerifiableCredential.Create.All"}).Id
    Add-AzADAppPermission -ObjectId $clientAadApplication.Id -ApiId $spVCRS.AppId -PermissionId $permissionId -Type "Role"
    Write-Host "Granted permissions."

    Write-Host "Done creating the client application (VC Asp.net core sample)"

    # URL of the AAD application in the Azure portal
    # Future? $clientPortalUrl = "https://portal.azure.com/#@"+$tenantDomainName+"/blade/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/Overview/appId/"+$clientAadApplication.AppId+"/objectId/"+$clientAadApplication.ObjectId+"/isMSAApp/"
    $clientPortalUrl = "https://portal.azure.com/#blade/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/CallAnAPI/appId/"+$clientAadApplication.AppId+"/objectId/"+$clientAadApplication.ObjectId+"/isMSAApp/"
    Add-Content -Value "<tr><td>client</td><td>$($clientAadApplication.AppId)</td><td><a href='$clientPortalUrl'>VC Asp.net core sample</a></td></tr>" -Path createdApps.html

    # Update config file for 'client'
    $configFile = $pwd.Path + "\..\appsettings.json"
    Write-Host "Updating the sample code ($configFile)"
    $dictionary = @{ "TenantId" = $tenantId;"ClientId" = $clientAadApplication.AppId;"ClientSecret" = $appCreds.SecretText };
    UpdateTextFile -configFilePath $configFile -dictionary $dictionary
    Write-Host ""
    Write-Host "IMPORTANT: Please follow the instructions below to complete a few manual step(s) in the Azure portal":
    Write-Host "- For 'client'"
    Write-Host "  - Navigate to '$clientPortalUrl'"
    Write-Host "  - Navigate to the API permissions page and click on 'Grant admin consent for $tenantName'"

    Add-Content -Value "</tbody></table></body></html>" -Path createdApps.html  
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

# Run interactively (will ask you for the tenant ID)
ConfigureApplications -Credential $Credential -tenantId $TenantId