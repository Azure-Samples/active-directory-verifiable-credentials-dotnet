# Microsoft Entra Verified ID Samples

This repo contains a set of Microsoft Entra Verified ID samples

## Samples

| Sample | Description |
|------|--------|
| 1-asp-net-core-api-idtokenhint | dotnet sample for using the VC Request Service API to issue and verify verifiable credentials with a credential contract which allows the VC Request API to pass in a payload for the Verifiable Credentials|
| 2-asp-net-core-api-user-signin | dotnet sample for a developer who wants to provide the signed-in users an option to get and present Verifiable credentials using the VC Request Service API. **Note:** This is different from 1-asp-net-core-api-idtokenhint sample as follows : User sign-in is a requirement to issue credentials since the credentials have claims (first name, last name) based on the signed-in user's idToken.'|
| 3-asp-net-core-api-b2c | dotnet sample for using the VC Request Service API to issue and verify verifiable credentials in a B2C policy|
| 5-onboard-with-tap | dotnet sample for onboarding new hire employees and guest users. |

Microsoft provides a simple to use REST API to issue and verify verifiable credentials. You can use the programming language you prefer to the REST API. Instead of needing to understand the different protocols and encryption algorithms for Verifiable Credentials and DIDs you only need to understand how to format a JSON structure as parameter for the VC Request API.

![API Overview](ReadmeFiles/SampleArchitectureOverview.svg)

## Issuance

The documentation for calling the issuance API is available [here](https://learn.microsoft.com/en-us/entra/verified-id/get-started-request-api?tabs=http%2Cissuancerequest%2Cpresentationrequest#issuance-request-example).

## Verification

The documentation for calling the verification API is available [here](https://learn.microsoft.com/en-us/entra/verified-id/get-started-request-api?tabs=http%2Cissuancerequest%2Cpresentationrequest#presentation-request-example).

## Setup

Before you can run any of these samples make sure your environment is setup correctly. You can follow the setup instructions [here](https://aka.ms/vcsetup)

## Troubleshooting

If you are deploying this sample to Azure App Services, then you can view app logging information in the `Log stream` if you do the following:

- Go to Development Tools, then Extensions
- Select `+ Add` and add `ASP.NET Core Logging Integration` extension
- Go to `Log stream` and set `Log level` drop down filter to `verbose`
- 
The Log stream console will now contain traces from the deployed.

## Resources

For more information, see MSAL.NET's conceptual documentation:

- [Quickstart: Register an application with the Microsoft identity platform](https://docs.microsoft.com/azure/active-directory/develop/quickstart-register-app)
- [Quickstart: Configure a client application to access web APIs](https://docs.microsoft.com/azure/active-directory/develop/quickstart-configure-app-access-web-apis)
- [Acquiring a token for an application with client credential flows](https://aka.ms/msal-net-client-credentials)
