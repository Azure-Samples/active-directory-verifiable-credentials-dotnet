---
page_type: sample
languages:
- dotnet
- powershell
products:
- Entra
- Verified ID
description: "A .NET sample combining External ID sign-in with Verified ID issuance, presentation, and an identity-provider integration."
urlFragment: "active-directory-verifiable-credentials-dotnet"
---
# External ID and Verified ID sample

## About this sample

A .NET 10 web app showing how to combine External ID sign-in with Verified ID
credential issuance and presentation, including a TrueIdentity provider handoff.
Users sign in with the normal hosted External ID experience. Verified ID is used
after sign-in, not as an authentication method.

TrueIdentity is a **dummy IDV service** for this sample. To use an actual IDV
provider, work directly with an IDV partner and follow the
[Verified ID integration guidance](https://learn.microsoft.com/en-gb/entra/verified-id/integration-guidance).

## Deploy to Azure

Complete the [configuration](#configure) first so you have the required tenant,
app registration, issuer, credential manifest, and provider values.

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2FAzure-Samples%2Factive-directory-verifiable-credentials-dotnet%2Fmain%2F4-externalid-verifiedid%2FARMTemplate%2Ftemplate.json)

The deployment prompts for the app and Verified ID settings. It deploys from
the repository's `main` branch; it does not include uncommitted local changes.
For Northstar, `ProfileValueClaim` defaults to `loyalty`; use `membershipNo`
for the included sample rules. The template creates a Windows App Service on
Free F1 by default. It does not create the External ID tenant, app registration,
issuer, or credential contract.

## User flow

Sign in with the hosted External ID experience, then use the three pages under
**Credential journey** in the left navigation:

1. **Issue credential** — Choose a configured credential, such as
   `NorthstarLoyalty`, and issue it to Microsoft Authenticator or another
   compatible wallet. Issuance uses the signed-in account's configured claims;
   it does not perform identity proofing.
2. **Verify credential and identity** — This page guides the identity flow:
   present the account credential so the app can validate its issuer, type,
   validity, and account binding; continue to TrueIdentity, the sample's dummy
   IDV service, for its government document and selfie checks; then present
   the provider-issued `TrueIdentity` credential. The app validates that
   presentation's issuer, type, revocation status, and expiration before
   completing the in-memory flow. Returning from TrueIdentity only confirms
   navigation, not proofing success.
3. **Present credential** — Use the standalone verifier page to request a
   presentation of a credential from your wallet. Scan its QR code and respond
   in your wallet; this demonstrates credential presentation separately from
   the gated TrueIdentity integration flow.

Presentation QR codes appear on the relevant page, with instructions to scan
using Microsoft Authenticator or a compatible wallet. **The app does not
update the user's profile or write to Microsoft Graph.**

## Configure

1. Create an External ID app registration and hosted sign-up/sign-in user flow.
   Add the app's `/signin-oidc` redirect URI.
2. Configure ID-token claims `email`, `given_name`, and `family_name` where
   available. Users whose credential contract requires names must have given
   name and surname in their profile. Sign out and back in after changing claims.
3. Configure a Verified ID issuer and publish a credential contract. The
   default Northstar mapping expects `email` and `loyalty`. The included sample
   rules use `membershipNo`; set `VerifiedID:Verification:ProfileValueClaim`
   (or the ARM `ProfileValueClaim` parameter) to `membershipNo` when using them.
4. Set the provider's actual issuer DID in
   `IdentityVerification:IdentityIssuer`. The presented identity credential
   must include the `TrueIdentity` type and come from that issuer.

Set configuration with user secrets, environment variables, or App Service
settings—never commit credentials. Key environment variables include:

| Setting | Purpose |
| --- | --- |
| `EEID__TenantId`, `EEID__TenantName`, `EEID__Authority` | External ID tenant |
| `EEID__ClientId`, `EEID__ClientSecret` | Web app registration |
| `VerifiedID__DidAuthority` | Verified ID issuer DID |
| `VerifiedID__CredentialType`, `VerifiedID__CredentialManifest` | Issuance contract |
| `IdentityVerification__ProviderUrl` | TrueIdentity enrollment URL; defaults to `https://trueidentityincv2.azurewebsites.net/` |
| `IdentityVerification__IdentityIssuer` | Trusted `TrueIdentity` issuer DID |
| `IdentityVerification__PublicBaseUrl` | Public HTTPS origin for callbacks |
| `IdentityVerification__CallbackApiKey` | Shared secret used to authenticate Verified ID callbacks |

The checked-in `appsettings.json` contains placeholders. The
`appservice-config-template.json` lists App Service settings without usable
secrets. No Graph permission or directory extension is required.

## Run locally

From the repository root, with the .NET 10 SDK installed:

```powershell
dotnet run --project .\4-externalid-verifiedid\ExternalIDVerifiedID.csproj --launch-profile https
```

Open `https://localhost:5001/`. A live issuance/presentation requires a
configured tenant and issuer. Verified ID callbacks require a publicly
reachable HTTPS URL; use a trusted HTTPS tunnel for local end-to-end testing.

## Deploy local changes

The ARM template configures the app. F1 is a shared demo tier and can unload
the app.

To deploy uncommitted local code, publish the app and ZIP the contents of the
publish directory, then use `az webapp deploy --type zip`. ARM source deployment
uses the selected remote branch and does not include unpushed local changes.

After deployment, add the site's `/signin-oidc` URL to the app registration.
Start a new verification after any app restart or deployment; in-progress
flows are held in memory.

## Additional credentials

Add credentials to `VerifiedID:Credentials` in `appsettings.json` or through
indexed App Service settings. Each entry needs an `Id`, `Name`, `Type`,
`Manifest`, and a `Verification` mapping:

```json
{
  "Id": "employee-card",
  "Name": "Employee card",
  "Type": "Employee",
  "Manifest": "https://verifiedid.did.msidentity.com/v1.0/tenants/<tenant-id>/verifiableCredentials/contracts/<contract-id>/manifest",
  "Verification": {
    "AccountBindingClaim": "email",
    "AccountBindingSource": "email",
    "ProfileValueClaim": "employeeNumber"
  }
}
```

`AccountBindingSource` is `email` or `oid`, and claim names must match the
published credential exactly. Only configured credentials are available for
issuance. The sample's issuance claim builder is designed for its existing
Northstar claims; other credentials may require code changes.

## Limitations

- Provider return is not proof of successful document checks. The provider
  must issue a `TrueIdentity` credential; the app completes only after its
  validated wallet presentation callback.
- Verification state is in memory and can be lost on restart or unload.
- The callback uses a shared API key. This sample is for demonstration, not
  production identity assurance; it does not durably bind the provider's
  proofed identity to the signed-in account.
- Verified ID and provider services may have separate costs; Free F1 does not
  make the full solution free.
