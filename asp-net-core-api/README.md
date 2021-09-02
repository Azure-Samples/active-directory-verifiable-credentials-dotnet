---
page_type: sample
languages:
- dotnet
- powershell
products:
- active-directory
- verifiable credentials
description: "A code sample demonstrating issuance and verification of verifiable credentials."
urlFragment: "active-directory-verifiable-credentials-dotnet"
---
# Verifiable Credentials Code Sample

This code sample demonstrates how to use Microsoft's Azure Active Directory Verifiable Credentials preview to issue and consume verifiable credentials.

## About this sample

Welcome to Azure Active Directory Verifiable Credentials. In this sample, we'll teach you to issue your first verifiable credential: a Verified Credential Expert Card. You'll then use this card to prove to a verifier that you are a Verified Credential Expert, mastered in the art of digital credentialing. The sample uses the preview REST API which supports ID Token hints to pass a payload for the verifiable credential.

> **Important**: Azure Active Directory Verifiable Credentials is currently in public preview. This preview version is provided without a service level agreement, and it's not recommended for production workloads. Certain features might not be supported or might have constrained capabilities. For more information, see [Supplemental Terms of Use for Microsoft Azure Previews](https://azure.microsoft.com/support/legal/preview-supplemental-terms/).

## Contents

The project is divided in 2 parts, one for issuance and one for verifying a verifiable credential. Depending on the scenario you need you can remove 1 part. To verify if your environment is completely working you can use both parts to issue a verifiedcredentialexpert VC and verify that as well.

| Issuance | |
|------|--------|
| Pages/Issuer.cshtml|The basic webpage containing the javascript to call the APIs for issuance. |
| IssuerController.cs | This is the controller which contains the API called from the webpage. It calls the REST API after getting an access token through MSAL. |
| issuance_request_config.json | The sample payload send to the server to start issuing a vc. |

| Verification | |
|------|--------|
| Pages/Verifier.cshtml | The website acting as the verifier of the verifiable credential.
| VerifierController.cs | This is the controller which contains the API called from the webpage. It calls the REST API after getting an access token through MSAL and helps verifying the presented verifiable credential.
| verifier_request_config.json | The sample payload send to the server to start issuing a vc.

## Setup

Before you can run this sample make sure your environment is setup correctly. 
Run the powershell script ConfigureVCService.ps1 in the AppCreationScripts directory to create the correct enterprise application for the VC request service in your tenant. And make sure that SP has the correct permissions on your Keyvault. In the future this will be configured automaticall:
### keyvault permissions
1. Go to your issuer key vault's "Access Policies" blade
2. Click "Add Access Policy"
3. Check "Get" and "Sign" for Key Permissions, and "Get" for secret permissions.
4. Select Principal and enter "Verifiable Credential Request Service"
5. Click "Add", then Click "Save"

### create application registration
Run the [Configure1.ps1](./AppCreationScripts/AppCreationScripts.md) powershell script in the AppCreationScripts directory or follow these manual steps to create an application registrations, give the application the correct permissions so it can access the Verifiable Credentials Request REST API:

Register an application in Azure Active Directory: 
1. Sign in to the Azure portal using either a work or school account or a personal Microsoft account.
2. Navigate to the Microsoft identity platform for developers App registrations page.
3.	Select New registration
    -  In the Name section, enter a meaningful application name for your issuance and/or verification application
    - In the supported account types section, select Accounts in this organizational directory only ({tenant name})
    - Select Register to create the application
4.	On the app overview page, find the Application (client) ID value and record it for later.
5.	From the Certificates & secrets page, in the Client secrets section, choose New client secret:
    - Type a key description (for instance app secret)
    - Select a key duration.
    - When you press the Add button, the key value will be displayed, copy and save the value in a safe location.
    - You’ll need this key later to configure the sample application. This key value will not be displayed again, nor retrievable by any other means, so record it as soon as it is visible from the Azure portal.
6.	In the list of pages for the app, select API permissions
    - Click the Add a permission button
    - Search for APIs in my organization for bbb94529-53a3-4be5-a069-7eaf2712b826 and click the “Verifiable Credential Request Service”
    - Click the “Application Permission” and expand “VerifiableCredential.Create.All”
    - Click Grant admin consent for {tenant name} on top of the API/Permission list and click YES. This allows the application to get the correct permissions

Store the recorded values in the appsettings.json file

### API Payloads
Make sure you modify the payloads json files. The Authority in the payloads should be your own DID, you can copy that from the verifiable credential page in the azure portal.

## Credential defintion
In the project directory CredentialFiles you will find the VerifiedCredentialExpertDisplay.json file and the VerifiedCredentialExpertRules.json file. When you are using these 2 files to create your own credential make sure you modify the "iss:" value in the rules file and replace the DID with your own DID.

## About the code
Since the API is now a multi-tenant API it needs to receive an access token when it's called. 
The endpoint of the API is https://beta.did.msidentity.com/v1.0/{YOURTENANTID}/verifiablecredentials/request 

To get an access token we are using MSAL as library. MSAL supports the creation and caching of access token which are used when calling Azure Active Directory protected resources like the verifiable credential request API.
Typicall calling the libary looks something like this:
```C#
app = ConfidentialClientApplicationBuilder.Create(AppSettings.ClientId)
    .WithClientSecret(AppSettings.ClientSecret)
    .WithAuthority(new Uri(AppSettings.Authority))
    .Build();
```
And creating an access token:
```C#
result = await app.AcquireTokenForClient(scopes)
                  .ExecuteAsync();
```
> **Important**: At this moment the scope needs to be: **bbb94529-53a3-4be5-a069-7eaf2712b826/.default** This might change in the future

Calling the API looks like this:
```C#
HttpClient client = new HttpClient();
var defaultRequestHeaders = client.DefaultRequestHeaders;
defaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);

HttpResponseMessage res = client.PostAsync(AppSettings.ApiEndpoint, new StringContent(jsonString, Encoding.UTF8, "application/json")).Result;
response = res.Content.ReadAsStringAsync().Result;
```

## Troubleshooting

### Did you forget to provide admin consent? This is needed for confidential apps
If you get an error when calling the API `Insufficient privileges to complete the operation.`, this is because the tenant administrator has not granted permissions
to the application. See step 6 of 'Register the client app' above.

You will typically see, on the output window, something like the following:

```Json
Failed to call the Web Api: Forbidden
Content: {
  "error": {
    "code": "Authorization_RequestDenied",
    "message": "Insufficient privileges to complete the operation.",
    "innerError": {
      "request-id": "<a guid>",
      "date": "<date>"
    }
  }
}
```


## Best practices
When deploying applications which need client credentials and use secrets or certificates the more secure practice is to use certificates. If you are hosting your application on azure make sure you check how to deploy managed identities. This takes away the management and risks of secrets in your application.
You can find more information here:
- [Integrate a daemon app with Key Vault and MSI](https://github.com/Azure-Samples/active-directory-dotnetcore-daemon-v2/tree/master/3-Using-KeyVault)


## More information

For more information, see MSAL.NET's conceptual documentation:

- [Quickstart: Register an application with the Microsoft identity platform](https://docs.microsoft.com/azure/active-directory/develop/quickstart-register-app)
- [Quickstart: Configure a client application to access web APIs](https://docs.microsoft.com/azure/active-directory/develop/quickstart-configure-app-access-web-apis)
- [Acquiring a token for an application with client credential flows](https://aka.ms/msal-net-client-credentials)