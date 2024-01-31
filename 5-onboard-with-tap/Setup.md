# Setup

The sample uses a couple of different Azure and Entra resources, so please follow the setup instructions closely. 
If you only plan to test `Guest Onboarding`, then skip the setup steps marked `Employee Conboarding` as you don't need them.

## Entra ID tenant

You need an Entra ID tenant to get this sample to work. You can set up a [free tenant](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-create-new-tenant) unless you don't have one already. 

## Entra Verified ID setup

You need a working setup on Entra Verified ID. This is required even though you are just asking a user to present a Verified ID because the presentation request needs to come from a signed authority.
To set up Entra Verified ID, please follow the [documented tutorials](https://learn.microsoft.com/en-us/entra/verified-id/verifiable-credentials-configure-tenant-quick). There are two ways of setting up Verified ID, where one is called `Quick` and one is called `Manual`. 
In the manual setup, you have a couple of more steps to perform, like deploying an Azure Key Vault and verifying your domain, and in the quick setup you don't have to do that (given your Entra ID tenant has a verified domain already). 
You decide which model you want to follow, but please be aware that you will have to deploy or reuse an Azure Key Vault instance anyway to get this sample to work.

## Register an application in Entra ID

You need to [register](https://portal.azure.com/#view/Microsoft_AAD_IAM/ActiveDirectoryMenuBlade/~/RegisteredApps) an applications to get this sample to work. 

### Application permissions required (Employee Onboarding)

| Permission | Type | Scenario | Description |
|------|--------|--------|--------|
| User.Read | Delegated |  1+2 | So that the admin can read their own profile |
| User.Read.All | Application | 1 | For admin to read new hire's profile |
| User.ReadWrite.All | Application | 2 | For admin to read/write new hire's profile |
| UserAuthenticationMethod.ReadWrite.All | Application | 1+2 | For admin to create the TAP code for the new hire |
| Group.ReadWrite.All | Application | 2 | For admin to add the new hire to the TAP group |
| VerifiableCredential.Create.PresentationRequest | Application | 1+2 | For application to be able to create a Verified ID presentation request  |

Scenarios:
1) Employee onboarding - You create the new hire user account yourself, using the management portals or other tools outside of the sample. In this case the app does not need User/Group.ReadWrite.All permissions.
2) Employee onboarding - You create the new hire user account in the sample application. In this case the app needs User/Group.ReadWrite.All permissions.

### Application permissions required (Guest Onboarding)

| Permission | Type | Description |
|------|--------|--------|
| User.Read | Delegated | So that the admin can read their own profile |
| User.Read.All | Application | For admin to read new hire's profile |
| User.Invite.All | Application | For the app to have rights to create invites. Not needed if app has User.ReadWrite.All permission |
| User-LifeCycleInfo.ReadWrite.All | Application | If you want to set the `EmployeeLeaveDateTime` attribute to the expiry date of the presented VC |
| VerifiableCredential.Create.PresentationRequest | Application | For application to be able to create a Verified ID presentation request  |


### General steps for registering the application:

1. Open [applications blade in Entra portal](https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade/quickStartType~/null/sourceType/Microsoft_AAD_IAM) 
2.  Select New registration
    - In the Name section, enter a meaningful application name for your issuance and/or verification application
    - In the supported account types section, select Accounts in this organizational directory only ({tenant name})
    - In the Redirect URI section, select `Web` and add `https://localhost:5001/signin-oidc`. You will later come back and add more redirect URIs.
    - If you have deployed the app to `AppServices`, add `https://your-name.azurewebsites.net/signin-oidc` as a Redirect URI (replace with your name).
    - Select Register to create the application
3.	On the app overview page, find the Application (client) ID value and Directory (tenant) ID and record it for later.
4.	From the Certificates & secrets page, in the Client secrets section, create a new client secret and copy it for later use in configuration as it will not be displayed again.
5.	In the list of pages for the app, select API permissions
    - Click the Add a permission button
    - Select the permissions as stated above and add them.
6.  Click Grant admin consent for {tenant name} on top of the API/Permission list and click YES. This allows the application to get the correct permissions

### Creating the UserAdmin AppRole

The admin needs to have an AppRole in order to register new hires or update the trusted partner list as these are sensitive operations that not any signed in user should be able to perform.
To create an AppRole:

1. Select `App registrations` and select the app created above.
1.  Select `App roles`
    - click `+ Create app role`
    - Enter `UserAdmin` as Display name and Value and select `Users/Groups`
    - Click Apply
1. Go to `Enterprise applications` and select the app created
1.  Select `Users and groups`
    - click `+ Add user/group` 
    - select the admin user or a group that the admin user is a member of
    - select the `UserAdmin` role (it is preselected when you only have one role)
    - click Assign

### Create a group for TAP and SSPR (Employee Onboarding)

1. Open [Groups](https://entra.microsoft.com/#view/Microsoft_AAD_IAM/GroupsManagementMenuBlade/~/AllGroups/menuId/AllGroups) in the Entra portal
1. Click `New group` and select Group type `Security`, Membership type `Assigned` and give the group a name.
1. Click `Create` to save the group.

### Enabling the Temporary Access Pass (TAP) (Employee Onboarding)

1.  Open [Authentication Methods](https://entra.microsoft.com/#view/Microsoft_AAD_IAM/AuthenticationMethodsMenuBlade/~/AdminAuthMethods/fromNav/) in Entra portal.
1.  Click on `Temporary Access Pass`, enable it and select the `target group` created in the above step.
1.  Click on Configure, then `Edit` and change the `Minumum lifetime` to 15 minutes  
1.  Click `Save`  

### Enabling Self-Service Password Reset (Employee Onboarding)

This step is only required if you want to change the new hire's password as part of the onboarding.

1.  Open [Password reset](https://entra.microsoft.com/#view/Microsoft_AAD_IAM/PasswordResetMenuBlade/~/Properties/fromNav/Identity) in Entra portal.
1.  Click on `Selected`, and select the same `target group` you selected for TAP codes.
1.  Click `Save`  

### Deploying Azure Key Vault (Employee Onboarding) 

The sample creates a link that is sent to the new hire's private email. The link containes a JWT token signed with an Azure Key Vault key. 
The token is used as proof upon starting the onboarding process (only the new hire has the token) and is also passed to and returned from TrueIdentity for the same reason.
If you have set up Verified ID the manual way, you already have an Azure Key Vault and can reuse it. 

**To create a new Key Vault instance:**
1. Open [Key Vault](https://portal.azure.com/#view/HubsExtension/BrowseResource/resourceType/Microsoft.KeyVault%2Fvaults) in the Azure portal.
1. Click `Create`
1. Select or create a resource group
1. Give the Key Vault instance a name, the region you usually use and keep the pricing tier as `Standard`.
1. Click `Create`.

**To update Access policies for Verified ID:**
1. For verified ID setup, you can follow [these](https://learn.microsoft.com/en-us/entra/verified-id/verifiable-credentials-configure-tenant#create-a-key-vault) instruction for key permissions needed. 

**To add the sample application's permission:**
1. In `Access policies`, click `Create`
1. Select `Get, Sign` key permissions.
1. Select your app, created above
1. Click `Next` and `Create`

**To generate a new signing key:**
1. In `Keys`, click `Generate/Import`
1. Give the key a name, select type `RSA` and size `2048`
1. Click `Create`
1. Open up the created key and copy `Key Identifier` URI from the properties.

## Running the sample locally

### Cloning the sample

```Powershell
git clone https://github.com/Azure-Samples/active-directory-verifiable-credentials-dotnet.git
cd active-directory-verifiable-credentials-dotnet/5-onboard-with-tap
```

### Update appsettings.json

The appsettings.json file have the following settings that needs to be updated. The default values for the settings not listed doesn't need to be changed just to get the sample running. 

| Section | Name | Value | Note |
|------|--------|--------|--------|
| AppSettings | KeyIdentifier | URI | Key Identifier URI of your deployed Azure Key Vault signing key | 
| AzureAd | Instance | https://login.microsoftonline.com/ | you don't need to chnge this value unless you don't run in the global Azure cloud |
| | TenantId | guid | Your tenant id (guid) |
| | ClientId| guid | AppID (client_id) |
| | ClientSecret | string | The client secret generated in the portal |
| | TapGroupName | group-name | Name of the group used when enabling TAP and SSPR |
| VerifiedID | TenantId | guid | Your tenant id (guid) can be same or different as the AzureAd.TenantId. If it is the same, you can leave this setting blank ("") and the sample will use AzureAd.TenantId. |
| | ClientId| guid | Your AppID (client_id) can be same or different as the AzureAd.ClientId. If it is the same, you can leave this setting blank ("") and the sample will use AzureAd.ClientId. |
| | ClientSecret | string | Your client secret can be same or different as the AzureAd.ClientSecret. If it is the same, you can leave this setting blank ("") and the sample will use AzureAd.ClientSecret. |
| | DidAuthority | did | The authority DID that is making the presentation request. You can copy it from your [Organizational settings](https://portal.azure.com/#view/Microsoft_AAD_DecentralizedIdentity/InitialMenuBlade/~/issuerSettingsBlade) in the Entra portal |

If you are deploying the solution to Azure AppServices, the configuration settings needs to have names like `AppSettings__KeyIdentifier`, `AzureAd__ClientId` and `VerifiedId__DidAuthority`, 
ie Section, then double underscore, followed by name. There is a template you can use [here](appservices-advanced-settings.json)

## Compile and running the sample locally

1. Compile & run it using either Visual Studio or VSCode.
```Powershell
dotnet build "OnboardWithTAP.csproj" -c Debug -o .\bin\Debug\net6
dotnet run
```
2. Install [ngrok](https://ngrok.com/download) tool if you don't have it already.
3. Start ngrok
```Powershell
ngrok http 5000
```
4. Copy ngrok's `https` forwarding URL:
    - Update your app's `Redirect URI` to also have `https://56aa-367-420-597-13.ngrok-free.app/signin-oidc` (replace with your ngrok URL).
    - Browse to `https://56aa-367-420-597-13.ngrok-free.app` to use the app.

