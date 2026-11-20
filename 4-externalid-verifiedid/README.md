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

1. **Issue credential** — Choose `NorthstarLoyalty` to keep the existing
   account-claims issuance flow, or choose `VerifiedIdentity` to enter first
   and last name, gender, address, nationality, document number and type, date
   of birth, and expiry date. The VerifiedIdentity option requires its own
   published credential manifest; without it, the page displays the required
   configuration and does not issue the Northstar credential under a different
   name. Upload a portrait or take a selfie when the selected manifest supports
   a JPEG photo claim. Values are demo inputs, not verified evidence. Photos are
   held temporarily in memory for issuance and are not written to persistent
   storage.
2. **Verify credential and IDV verification** — This page guides the identity flow:
   present the account credential so the app can validate its issuer, type,
   validity, and account binding; continue to TrueIdentity, the sample's dummy
   IDV service, for its government document and selfie checks; then present
   the provider-issued `TrueIdentity` credential. The app validates that
   presentation's issuer, type, revocation status, expiration, and required
   Face Check result (confidence score at least 70) before
   completing the in-memory flow. Returning from TrueIdentity only confirms
   navigation, not proofing success. A compact step indicator shows progress;
   only the active step is expanded, with its QR alongside it on desktop.
   Existing flows completed without Face Check are labeled as older results,
   not Face Check successes; start a new flow to use the required check.
3. **Present credential** — Select `NorthstarLoyalty` to use the existing
   verifier flow, including optional Face Check. Select `VerifiedIdentity` to
   make one request using the common `VerifiableCredential` type. The request
   accepts a credential from any issuer. After the callback, the app requires
   exactly one credential whose returned type list contains
   `VerifiableCredential`, `TrueIdentity`, or `VerifiedIdentity`, and checks
   callback state, status, revocation when reported, and expiration when
   reported. Authenticator's credential choice matches the request's common
   `VerifiableCredential` type; it does not mean the app requested the
   `TrueIdentity` type specifically. Because this demo accepts any issuer, a
   successful presentation is not an issuer-trusted identity assurance
   decision.

   Face Check is available for either profile when enabled for the tenant. It
   compares a live selfie with the credential's photo claim; it requires a
   suitable credential and a supported wallet. For VerifiedIdentity the option
   is off by default and uses the `photo` claim unless
   `IdentityVerification:PresentationPhotoClaimName` is configured. A face match
   does not establish that the issuer or the photo is trustworthy.
   The verifier shows the option only when the selected profile's configured
   issuance manifest declares a JPEG claim matching the presentation photo claim.
   A manifest describes a contract, not the contents of a wallet credential:
   photos may be absent and VerifiedIdentity can be presented by other issuers
   with different contracts. If the manifest cannot be loaded, Face Check is
   hidden and presentation without it remains available. Scan the QR code and
   approve the request in your wallet. This standalone verifier is separate
   from the gated TrueIdentity flow.

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
   For VerifiedIdentity issuance, publish a separate credential manifest in
   the same Verified ID tenant and configure
   `IdentityVerification:IssuanceManifest`. Set
   `IdentityVerification:IssuanceCredentialType` to the type in that manifest;
   `IdentityVerification:IssuanceIssuer` may set its issuer DID and otherwise
   defaults to `VerifiedID:DidAuthority`. Its manifest must contain display
   claims that map to first name (`givenName`/`firstName`), last name
   (`surname`/`lastName`/`familyName`), `gender`, `address`, `nationality`,
   `documentNumber`, document type (`documentType` or `documentCode`),
   `dateOfBirth`, and expiry date (`expiryDate` or `dateOfExpiry`). To enable
   photo upload/selfie, publish a JPEG photo claim and configure
   `IdentityVerification:IssuancePhotoClaimName`.
4. Set the provider's actual issuer DID in
   `IdentityVerification:IdentityIssuer`. The identity credential presented
   through the identity-verification flow must include the `TrueIdentity` type
   and come from that issuer. The standalone verifier does not restrict issuers
   for its `VerifiedIdentity` profile. The Request Service API accepts one type
   string per requested credential and doesn't define an OR list. The generic
   base-type request therefore depends on `VerifiableCredential` being present
   in the credential's type list. The standalone presentation allows any
   issuer, so do not treat it as proof of government identity or an IDV
   decision.
   Step 3 requires the tenant's Face Check add-on, a compatible wallet, and a
   provider-issued credential containing a suitable photo in
   `IdentityVerification:PresentationPhotoClaimName` (default `photo`).
   Missing/failed Face Check does not fall back to credential-only success.
   The dummy provider and a face match are not production identity assurance.

Set configuration with user secrets, environment variables, or App Service
settings—never commit credentials. Key environment variables include:

| Setting | Purpose |
| --- | --- |
| `EEID__TenantId`, `EEID__TenantName`, `EEID__Authority` | External ID tenant |
| `EEID__ClientId`, `EEID__ClientSecret` | Web app registration |
| `VerifiedID__DidAuthority` | Verified ID issuer DID |
| `VerifiedID__CredentialType`, `VerifiedID__CredentialManifest` | Issuance contract |
| `IdentityVerification__ProviderUrl` | TrueIdentity enrollment URL; defaults to `https://trueidentity-vcdemo01.azurewebsites.net/`. The provider handoff appends the URL-encoded, flow-specific `returnUrl` pointing to this app's `/identity/return/{flow}` route. |
| `IdentityVerification__IdentityIssuer` | Trusted `TrueIdentity` issuer DID |
| `IdentityVerification__PresentationPhotoClaimName` | Photo claim for required step-3 Face Check and optional standalone identity Face Check; defaults to `photo`. Step 3 requires a score of at least 70 and disables receipts. |
| `IdentityVerification__IssuanceManifest` | Published manifest for the separate VerifiedIdentity issuance profile; required to enable that profile |
| `IdentityVerification__IssuanceCredentialType` | Credential type in the VerifiedIdentity issuance manifest; defaults to `VerifiedIdentity` |
| `IdentityVerification__IssuanceIssuer` | Optional issuer DID for that manifest; defaults to `VerifiedID__DidAuthority` |
| `IdentityVerification__IssuancePhotoClaimName` | JPEG photo claim name in that manifest; defaults to `photo` |
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
