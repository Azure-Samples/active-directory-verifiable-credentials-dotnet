# Azure AD Verifiable Credentials Samples

This repo contains a set of Azure AD Verifiable Credentials samples

## Samples

| Sample | Description |
|------|--------|
| asp-net-core-api | dotnet sample for using the VC Request API to issue and verify verifiable credentials with a credential contract which requires the user to sign in|
| asp-net-core-user-signin | Sample where user signs in to website which uses ID Tokenhint to issue a verifiable credential |

Microsoft provides a simple to use REST API to issue and verify verifiable credentials. You can use the programming language you prefer to the REST API. Instead of needing to understand the different protocols and encryption algoritms for Verifiable Credentials and DIDs you only need to understand how to format a JSON structure as parameter for the VC Request API.

![API Overview](ReadmeFiles/SampleArchitectureOverview.svg)

## Issuance

### Issuance JSON structure

To call the VC Client API to start the issuance process, the VC Request API needs a JSON structure payload like below. 

```JSON
{
  "authority": "did:ion: ...of the Issuer",
  "includeQRCode": true,
  "registration": {
    "clientName": "the verifier's client name"
  },
  "callback": {
    "url": "https://contoso.com/api/issuer/issuanceCallback",
    "state": "you pass your state here to correlate it when you get the callback",
    "headers": {
        "keyname": "any value you want in your callback"
    }
  },
  "issuance": {
    "type": "your credentialType",
    "manifest": "https://beta.did.msidentity.com/v1.0/3c32ed40-8a10-465b-8ba4-0b1e86882668/verifiableCredential/contracts/VerifiedCredentialExpert",
    "pin": {
      "value": "012345",
      "length": 6
    },
    "claims": {
      "mySpecialClaimOne": "mySpecialValueOne",
      "mySpecialClaimTwo": "mySpecialValueTwo"
    }
  }
}
```
 
- **authority** - is the DID identifier for your registered Verifiable Credential from portal.azure.com.
- **includeQRCode** - If you want the VC Client API to return a `data:image/png;base64` string of the QR code to present in the browser. If you select `false`, you must create the QR code yourself (which is not difficult).
- **registration.clientName** - name of your app which will be shown in the Microsoft Authenticator
- **callback.url** - a callback endpoint in your application. The VC Request API will call this endpoint when the issuance is completed.
- **callback.state** - A state value you provide so you can correlate this request when you get callback confirmation
- **callback.headers** - Any HTTP Header values that you would like the VC Request API to pass back in the callbacks. Here you could set your own API key, for instance
- **issuance.type** - the name of your credentialType. This value is configured in the rules file.
- **issuance.manifest** - url of your manifest for your VC. This comes from your defined Verifiable Credential in portal.azure.com
- **issuance.pin** - If you want to require a pin code in the Microsoft Authenticator for this issuance request. This can be useful if it is a self issuing situation where there is no possibility of asking the user to prove their identity via a login. If you don't want to use the pin functionality, you should not have the pin section in the JSON structure. The appsettings.PinCode.json contains a settings for issuing with pin code.
- **issuance.claims** - optional, extra claims you want to include in the VC.

In the response message from the VC Request API, it will include a URL to the request which is hosted at the Microsoft VC request service, which means that once the Microsoft Authenticator has scanned the QR code, it will contact the VC Request service directly and not your application directly. Your application will get a callback from the VC Request service via the callback.

```json
{
    "requestId": "799f23ea-524a-45af-99ad-cf8e5018814e",
    "url": "openid://vc?request_uri=https://beta.did.msidentity.com/v1.0/abc/verifiablecredentials/request/178319f7-20be-4945-80fb-7d52d47ae82e",
    "expiry": 1622227690,
    "qrCode": "data:image/png;base64,iVBORw0KGgoA<SNIP>"
}
```

### Issuance Callback

In your callback endpoint, you will get a callback with the below message when the QR code is scanned. This callback is typically used to modify the UI, hide the QR code to prevent scanning again and show the pincode to use when the user wants to accept the Verifiable Credential.

```JSON
{"code":"request_retrieved","requestId":"9463da82-e397-45b6-a7a2-2c4223b9fdd0", "state": "...what you passed as the state value..."}
```

Once the VC is issued, you get a second callback which contains information if the issuance of the verifiable credential to the user was succesful or not.

This callback is typically used to notify the user on the issuance website the process is completed and continue with whatever the website needs or wants the user to do.

### Succesful Issuance flow response
```JSON
{"code":"issuance_succesful","requestId":"9463da82-e397-45b6-a7a2-2c4223b9fdd0", "state": "...what you passed as the state value..."}
```
### Unuccesful Issuance flow response
```JSON
{"code":"issuance_failed","requestId":"9463da82-e397-45b6-a7a2-2c4223b9fdd0", "state": "...what you passed as the state value...",
"details" : "user_canceled"
}
```
When the issuance fails this can be caused by several reasons. The following details are currently provided in the details part of the response:
| Details | Definition |
|---|---|
| user_canceled | The user has canceled the flow |
| fetch_contract_error | The user has canceled the flow |
| linked_domain_error | Something wrong with linked domain |
| issuance_service_error | VC Issuance service was not able to validate requirements / something went wrong on Microsoft AAD VC Issuance service side. |
| unspecified_error | Something went wrong that doesn’t fall into this bucket |

These 5 specific details generically bucket most of the errors that could occur during issuance.


## Verification

### Verification JSON structure

To call the VC Request API to start the verification process, the application creates a JSON structure like below. Since the WebApp asks the user to present a VC, the request is also called `presentation request`.

```JSON
{
  "authority": "did:ion: did-of-the-Verifier",
  "includeQRCode": true,
  "registration": {
    "clientName": "the verifier's client name"
  },
  "callback": {
    "url": "https://contoso.com/api/verifier/presentationCallback",
    "state": "you pass your state here to correlate it when you get the callback",
    "headers": {
        "keyname": "any value you want in your callback"
    }
  },
  "presentation": {
    "includeReceipt": true,
    "requestedCredentials": [
      {
        "type": "your credentialType",
        "manifest": "https://portableidentitycards.azure-api.net/dev/536279f6-15cc-45f2-be2d-61e352b51eef/portableIdentities/contracts/MyCredentialTypeName",
        "purpose": "the purpose why the verifier asks for a VC",
        "trustedIssuers": [ "did:ion: ...of the Issuer" ]
      }
    ]
  }
}
```

Much of the data is the same in this JSON structure, but some differences needs explaining.

- **authority** vs **trustedIssuers** - The Verifier and the Issuer may be two different entities. For example, the Verifier might be a online service, like a car rental service, while the DID it is asking for is the issuing entity for drivers licenses. Note that `trustedIssuers` is a collection of DIDs, which means you can ask for multiple VCs from the user coming from different trusted issuers.
- **presentation** - required for a Verification request. Note that `issuance` and `presentation` are mutually exclusive. You can't send both.
- **requestedCredentials** - please also note that the `requestedCredentials` is a collection too, which means you can ask to create a presentation request that contains multiple DIDs.
- **includeReceipt** - if set to true, the `presentation_verified` callback will contain the `receipt` element.

### Verification Callback

In your callback endpoint, you will get a callback with the below message when the QR code is scanned.

When the QR code is scanned, you get a short callback like this.
```JSON
{"code":"request_retrieved","requestId":"c18d8035-3fc8-4c27-a5db-9801e6232569", "state": "...what you passed as the state value..."}
```

Once the VC is verified, you get a second, more complete, callback which contains all the details on what whas presented by the user.

```JSON
{
    "code":"presentation_verified",
    "requestId":"c18d8035-3fc8-4c27-a5db-9801e6232569",
    "state": "...what you passed as the state value...",
    "subject": "did:ion: ... of the VC holder...",
    "issuers": [
      {
        "authority": "did:ion of the issuer of this verifiable credential ",
        "type": [ "VerifiableCredential", "your credentialType" ],
        "claims": {
            "displayName":"Alice Contoso",
            "lastName":"Contoso",
            "firstName":"alice" 
        },
        "domain":"https://did.woodgrovedemo.com",
        "verified": "DNS"
      }
    ],
    "receipt":{
        "id_token": "...JWT Token of VC...",
        "state": "
        }
    }
}
```
Some notable attributes in the message:
- **claims** - parsed claims from the VC
- **receipt.id_token** - the DID of the presentation


## Setup

Before you can run any of these samples make sure your environment is setup correctly. 

### VC Client API Service Principle
For the public preview of this API you need to manually create the Service Principal for the API `Verifiable Credential Request Service`, which is the Microsoft service that will access your Key Vault.
You do this via the following Powershell command

```Powershell
Connect-AzureAD -TenantId <your-tenantid-guid>
New-AzureADServicePrincipal -AppId "bbb94529-53a3-4be5-a069-7eaf2712b826" -DisplayName "Verifiable Credential Request Service" 
```

### App Registration for Client Credentials
Your app needs a way to get an access token and this is done via the client credentials flow. You can register a Web app, accept the defaults, and set the redirect_uri `https://localhost`.
The important thing is to add an `API Permission` for API `Verifiable Credential Request Service` and permission `VerifiableCredential.Create.All`.

You can run the  powershell script `Configure1.ps1` in the `AppCreationScripts` directory of the sample or follow these manual steps:

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

Store the recorded values since they need to be used in the configuration of the samples later.

### Update your Access Policy for Azure Key Vault
The VC Client API needs to have access to your Azure Key Vault. You need to add a new `Access Policy` in your Azure Key Vault for the API `Verifiable Credential Request Service` with "Get" and "Sign" for Key Permissions and "Get" for Secret Permissions.

1. Go to your issuer key vault's "Access Policies" blade
2. Click "Add Access Policy"
3. Check "Get" and "Sign" for Key Permissions, and "Get" for secret permissions.
4. Select Principal and enter "Verifiable Credential Request Service"
5. Click "Add", then Click "Save"

## Resources

For more information, see MSAL.NET's conceptual documentation:

- [Quickstart: Register an application with the Microsoft identity platform](https://docs.microsoft.com/azure/active-directory/develop/quickstart-register-app)
- [Quickstart: Configure a client application to access web APIs](https://docs.microsoft.com/azure/active-directory/develop/quickstart-configure-app-access-web-apis)
- [Acquiring a token for an application with client credential flows](https://aka.ms/msal-net-client-credentials)
