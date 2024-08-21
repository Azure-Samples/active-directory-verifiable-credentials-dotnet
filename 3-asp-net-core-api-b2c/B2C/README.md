# Entra Verified ID integration with Azure AD B2C

This folder includes what you need to integrate Azure AD B2C with Entra Verified ID. 

**UPDATE 2024-02-20** B2C Custom HTML is now included in dotnet sample and no longer needs to be deployed by itself to Azure Storage. Verified ID Face Check also works with the sample/policies.

**UPDATE 2022-09-16** Added instructions for how make changes if you are using the [SocialAndLocalAccountsWithMfa](#SocialAndLocalAccountsWithMfa-changes) version of the B2C starter pack and get errors when uploading. 

**UPDATE 2022-06-30** Added B2C Policy that presents a QR code on the signin page so you can signin with VC already there.

**UPDATE 2022-05-10** You need to change your existing Rules files since the claim `sub` is now a restricted claim and will no longer work. You need to change it to `oid`. See `oid` in this README file below.

Note - You should consider using the new [Entra ID for customers](https://learn.microsoft.com/en-us/entra/external-id/customers/overview-customers-ciam) instead of Azure AD B2C.

## Scenarios

- Issue Verified ID credentials for accounts in your Azure AD B2C tenant
- [Not recommended] Issue verified ID credentials during signup of new users in your Azure AD B2C tenant (new). 
- Signin to B2C with your Verified ID credentials by scanning a QR code
- Using your Verified ID credentials as MFA
 
The recommended way for issuing Verified ID credentials in B2C/CIAM is to handle it in the application code and not in the B2C custom policy. 
To make use of Verified ID's feature Face Check, the credential needs to be issued with a photo of the user and that is too complicated for a B2C custom policy.

![Scan QR code](ReadmeFiles/b2c-vc-scan-qr-code.png)

## Verified ID B2C Custom Policies

The different signup/signin policy files illustrate different scenarios and you may choose which one meets your requirements. It is not ment as a suite where you use all in your application.

| File   | Description |
| -------- | ----------- |
| TrustFrameworkExtensionsVC.xml | Extensions for VC's to the TrustFrameworkExtensions from the Starter Pack |
| SignUpVCOrSignin.xml | Standard Signup/Signin B2C policy but that issues a VC during user signup |
| SignUpOrSignInVC.xml | Standard Signup/Signin B2C policy but with Verifiable Credentials added as a claims provider option (button) |
| SignUpOrSignInVCQ.xml | Standard Signup/Signin B2C policy but with a QR code on signin page so you can scan it already there. Signup journey ends with issue the new user a VC via the id_token_hint flow. |
| SigninVC.xml | Policy lets you signin to your B2C account via scanning the QR code and present your VC |
| SigninVCMFA.xml | Policy that uses VCs as a MFA after you've signed in with userid/password |

### MFA and FaceCheck

The policy SigninVCMFA.xml enforces the use of Verified ID Face Check even if the sample app isn't configured to use Face Check. This happens via passing a claim from the policy to selfAsserted UI to tell it to force Face Check.
This illustrates the scenario were the app requires a high-assurance 2FA before letting the user continue with a sensitive operation.

## Setup

- Complete [setup on Entra Verified ID](https://learn.microsoft.com/en-us/entra/verified-id/verifiable-credentials-configure-tenant-quick). Note that Verified ID can not be onboarded in the B2C tenant as that is not supported.
- [Create an Azure AD B2C tenant](https://learn.microsoft.com/en-us/azure/active-directory-b2c/tutorial-create-tenant) if you don't have one already.
- Deployed the `B2C Custom Policy Starter Pack` [https://docs.microsoft.com/en-us/azure/active-directory-b2c/custom-policy-get-started?tabs=applications#custom-policy-starter-pack](https://docs.microsoft.com/en-us/azure/active-directory-b2c/custom-policy-get-started?tabs=applications#custom-policy-starter-pack).
- Create a REST API key in the B2C portal named `RestApiKey`, manually set a value and copy the value for later use. You need it in `Deploy to Azure`. Do **not** select `generate`g. You must provide the key here and update the B2C policy files too.
- [Register a web application in B2C](https://learn.microsoft.com/en-us/azure/active-directory-b2c/tutorial-register-applications) if you don't have one already.
- Deploy this dotnet sample using the `Deploy To Azure` button.
- Update the B2C web application's redirect URI to include the new Azure App Service app. Should be something like `https:/your-app-name.azurewebsites.net/signin-oidc`.
- Edit the B2C Custom Policies in the [policies](./policies) folder
    - In all xml files, update all B2C tenant names from `yourtenant.onmicrosoft.com`	to the name of your tenant, like `mydev92.onmicrosoft.com`
    - In [TrustFrameworkExtensionsVC.xml](.\policies\TrustFrameworkExtensionsVC.xml) file:
        - Update all `ServiceUrl` to use the endpoint of your deployed Azure App Service, i.e. `https://your-appname.azurewebsites.net/`
        - Update the `LoadUri` to use the endpoint of your deployed Azure App Service, i.e. `https://your-appname.azurewebsites.net/`
        - [Not needed] Update all `VCServiceUrl` to use the endpoint of your deployed Azure App Service, i.e. `https://your-appname.azurewebsites.net/` - only needed if you plan to use the B2C policies with another sample.
- Upload the B2C Custom Policies in the [policies](./policies) folder, starting with TrustFrameworkExtensionsVC.xml.

### Changes if you are not using SocialAndLocalAccounts as Base Policy

The B2C sample policies in this repo are created using the [SocialAndLocalAccounts](https://github.com/Azure-Samples/active-directory-b2c-custom-policy-starterpack/tree/main/SocialAndLocalAccounts). This means that if you try and use them with the [SocialAndLocalAccountsWithMfa](https://github.com/Azure-Samples/active-directory-b2c-custom-policy-starterpack/tree/main/SocialAndLocalAccountsWithMfa) version, with a different TrustFrameworkBase.xml file, then you will get errors during uploading of the policies. 
If you use other base policies, you need to modify these two files since the orchestration step numbers are different between the two starter pack base files. 
If you don't change the step numbers, uploading will emit errors complaining about multiple steps of type `SendClaims`. 

| Base Policy | File | Changes |
|------|------|--------|
| LocalAccounts | SignupOrSigninVCQ.xml | OrchestrationStep 7-8-9 should be changed to 4-5-6 |
| LocalAccounts | SignUpVCOrSignin.xml | OrchestrationStep 7-8-9 should be changed to 4-5-6 |
| SocialAndLocalAccountsWithMfa | SignupOrSigninVCQ.xml | OrchestrationStep 7-8-9 should be changed to 9-10-11 |
| SocialAndLocalAccountsWithMfa | SignUpVCOrSignin.xml | OrchestrationStep 7-8-9 should be changed to 9-10-11 |

### Deploy the custom html

The custom html is now served from the dotnet application and you no longer need to deploy html-files to Azure Storage. 
However, if you want to use the B2C custom policies without the dotnet sample, you need to follow these instructions.
In this case, make sure to update the LoadUri values to the [TrustFrameworkExtensionsVC.xml](.\policies\TrustFrameworkExtensionsVC.xml).

- Create an Azure Storage and create a new container because you need to CORS enable it as explained [here](https://docs.microsoft.com/en-us/azure/active-directory-b2c/customize-ui-with-html?pivots=b2c-user-flow#2-create-an-azure-blob-storage-account). If you create a new storage account, you should perform step 2 through 3.1. Note that you can select `LRS` for Replication as `RA-GRS` is a bit overkill. Make sure you enable CORS for your B2C tenant.
- Download your copy of [qrcode.min.js](https://raw.githubusercontent.com/davidshimjs/qrcodejs/master/qrcode.min.js) and upload it to the container in the Azure Storage.
- Edit [selfAsserted.html](.\html\selfAsserted.html) and [unifiedquick.html](.\html\unifiedquick.html) and change the `script src` reference to point to your Azure Storage location. 
- Upload the files `selfAsserted.html`, `unified.html` and `unifiedquick.html` to the container in the Azure Storage.
- Copy the full url to the files and test that you can access them in a browser. If it fails, the B2C UX will not work either. If it works, you need to update the [TrustFrameworkExtensionsVC.xml](.\policies\TrustFrameworkExtensionsVC.xml) files with the `LoadUri` references.

Note that you only need to deploy `unifiedquick.html` if you plan to use `SignUpOrSignInVCQ.xml`. Otherwise you can skip this html file.