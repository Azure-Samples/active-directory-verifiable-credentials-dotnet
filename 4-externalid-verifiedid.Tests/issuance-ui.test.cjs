const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const sample = path.join(__dirname, '..', '4-externalid-verifiedid');
const page = fs.readFileSync(path.join(sample, 'Views', 'Home', 'Issuer.cshtml'), 'utf8');
const script = page.match(/<script>([\s\S]*?)<\/script>/)[1];

function pageContext() {
    const elements = new Map();
    const get = id => {
        if (!elements.has(id)) {
            elements.set(id, {
                value: 'default', disabled: id === 'issue-credential',
                style: { display: 'none' }, listeners: {},
                addEventListener(event, fn) { this.listeners[event] = fn; }
            });
        }
        return elements.get(id);
    };
    const context = vm.createContext({
        document: { getElementById: get },
        QRCode: function () {},
        requestService: {
            apiCreateIssuanceRequest: '/issue', apiSetPhoto: '/photo',
            apiCreateSelfieRequest: '/selfie'
        },
        window: { location: { assign(url) { context.navigation = url; } } },
        hideShowPhotoElements(value) {
            ['take-selfie', 'imageUpload', 'photo-help'].forEach(id => get(id).style.display = value);
        },
        drawVerifiedIDCard(callback, url) { context.loaded = callback; context.manifestUrl = url; }
    });
    vm.runInContext(script, context);
    return { context, get };
}

test('photo controls start hidden and issuance disabled until manifest loads', () => {
    assert.match(page, /id="user-photo" style="display:none"/);
    assert.match(page, /id="photo-help"[^>]+style="display:none"/);
    const { get } = pageContext();
    assert.equal(get('issue-credential').disabled, true);
});

test('no photo metadata enables issuance without upload or selfie', async () => {
    const { context, get } = pageContext();
    context.loaded({ photoClaimName: null });
    assert.equal(get('user-photo').style.display, 'none');
    assert.equal(get('photo-help').style.display, 'none');
    assert.equal(get('issue-credential').disabled, false);
    let issued = false;
    context.setUserPhoto = () => assert.fail('no photo upload expected');
    context.requestService.createIssuanceRequest = async () => { issued = true; };
    await get('issue-credential').listeners.click();
    assert.equal(issued, true);
});

test('photo manifest reveals controls and issuance awaits completed upload', async () => {
    const { context, get } = pageContext();
    context.loaded({ photoClaimName: 'photo' });
    assert.equal(get('user-photo').style.display, '');
    assert.equal(get('photo-help').style.display, '');
    let completeUpload;
    let issued = false;
    context.setUserPhoto = () => new Promise(resolve => { completeUpload = resolve; });
    context.requestService.createIssuanceRequest = async () => { issued = true; };
    const pending = get('issue-credential').listeners.click();
    assert.equal(issued, false);
    assert.equal(get('credential-select').disabled, true);
    completeUpload();
    await pending;
    assert.equal(issued, true);
});

test('failed photo upload never starts issuance and allows retry', async () => {
    const { context, get } = pageContext();
    context.loaded({ photoClaimName: 'photo' });
    context.setUserPhoto = async () => { throw new Error('Upload rejected'); };
    context.requestService.createIssuanceRequest = () => assert.fail('issuance must not start');
    await get('issue-credential').listeners.click();
    assert.equal(get('message').textContent, 'Upload rejected');
    assert.equal(get('issue-credential').disabled, false);
    assert.equal(get('credential-select').disabled, false);
});

test('selection is carried to every issuance endpoint and navigates to selected page', () => {
    const { context, get } = pageContext();
    assert.equal(context.manifestUrl, '/api/issuer/get-manifest?credentialId=default');
    assert.equal(context.requestService.apiSetPhoto, '/photo?credentialId=default');
    assert.equal(context.requestService.apiCreateIssuanceRequest, '/issue?credentialId=default');
    assert.equal(context.requestService.apiCreateSelfieRequest, '/selfie?credentialId=default');
    get('credential-select').value = 'another card';
    get('credential-select').listeners.change();
    assert.equal(context.navigation, '/Home/Issuer?credentialId=another%20card');
});

test('manifest load failure reports error without enabling issuance', async () => {
    const { context, get } = pageContext();
    context.fetch = async () => ({ ok: false, json: async () => ({ error_description: 'Wrong issuer' }) });
    vm.runInContext(fs.readFileSync(path.join(sample, 'wwwroot', 'js', 'verifiedid.card.js'), 'utf8'), context);
    await context.drawVerifiedIDCard(context.loaded);
    assert.equal(get('issue-credential').disabled, true);
    assert.equal(get('user-photo').style.display, 'none');
    assert.match(get('message').textContent, /Wrong issuer/);
});

test('request client draws issuance QR with PIN instead of referencing an undefined variable', async () => {
    let drawn;
    const context = vm.createContext({
        navigator: { userAgent: 'Desktop' },
        fetch: async () => ({ ok: true, json: async () => ({ id: 'id', url: 'openid-vc://issue', pin: '1234' }) })
    });
    vm.runInContext(fs.readFileSync(path.join(sample, 'wwwroot', 'js', 'verifiedid.requestservice.client.js'), 'utf8'), context);
    const service = new context.RequestService((...args) => { drawn = args; });
    service.pollRequestStatus = () => {};
    await service.createIssuanceRequest();
    assert.deepEqual(drawn, ['issuance', 'id', 'openid-vc://issue', '1234']);
});

test('Northstar profile mapping defaults agree across local and deployment configuration', () => {
    const local = JSON.parse(fs.readFileSync(path.join(sample, 'appsettings.json'), 'utf8'));
    const template = JSON.parse(fs.readFileSync(path.join(sample, 'ARMTemplate', 'template.json'), 'utf8'));
    const settings = JSON.parse(fs.readFileSync(path.join(sample, 'appservice-config-template.json'), 'utf8'));
    assert.equal(local.VerifiedID.Verification.ProfileValueClaim, 'loyalty');
    assert.equal(template.parameters.ProfileValueClaim.defaultValue, 'loyalty');
    assert.equal(settings.find(setting => setting.name === 'VerifiedID__Verification__ProfileValueClaim').value, 'loyalty');
    assert.equal(new Set(settings.map(setting => setting.name)).size, settings.length);
});

test('configuration uses the TrueIdentity v2 provider without Graph write settings', () => {
    const local = JSON.parse(fs.readFileSync(path.join(sample, 'appsettings.json'), 'utf8'));
    const template = JSON.parse(fs.readFileSync(path.join(sample, 'ARMTemplate', 'template.json'), 'utf8'));
    const settings = JSON.parse(fs.readFileSync(path.join(sample, 'appservice-config-template.json'), 'utf8'));
    const provider = 'https://trueidentityincv2.azurewebsites.net/';
    assert.equal(local.IdentityVerification.ProviderUrl, provider);
    assert.equal(template.parameters.ProviderUrl.defaultValue, provider);
    assert.equal(settings.find(setting => setting.name === 'IdentityVerification__ProviderUrl').value, provider);
    assert.equal(local.Graph, undefined);
    assert.equal(local.VerifiedID.Verification.ProfileAttribute, undefined);
    for (const name of ['IsVerifiedAttribute', 'LoyaltyAttribute', 'ExpirationAttribute']) {
        assert.equal(template.parameters[name], undefined);
    }
    for (const setting of [...settings, ...template.variables.baseAppSettings]) {
        assert.doesNotMatch(setting.name, /^Graph__|__ProfileAttribute$/);
    }
});

test('ARM credential array expands to indexed App Service settings, including an empty array', () => {
    const template = JSON.parse(fs.readFileSync(path.join(sample, 'ARMTemplate', 'template.json'), 'utf8'));
    const credentialSets = [[], [
        { Id: 'one', Name: 'One', Type: 'First', Manifest: 'https://example.com/one' },
        { Id: 'two', Name: 'Two', Type: 'Second', Manifest: 'https://example.com/two' }
    ]];
    for (const { credentials, deploySourceControl } of credentialSets.flatMap(credentials =>
        [true, false].map(deploySourceControl => ({ credentials, deploySourceControl })))) {
        const parameters = Object.fromEntries(Object.entries(template.parameters)
            .map(([name, parameter]) => [name, parameter.defaultValue ?? 'test']));
        parameters.AdditionalIssuanceCredentials = credentials;
        parameters.deploySourceControl = deploySourceControl;
        parameters.CredentialVerificationSettings = [{
            name: 'VerifiedID__Credentials__0__Verification__AccountBindingClaim', value: 'workEmail'
        }];
        let index = 0;
        let additional = [];
        const functions = {
            parameters: name => parameters[name],
            variables: name => name === 'additionalCredentialSettings' ? additional : evaluate(template.variables[name]),
            concat: (...values) => Array.isArray(values[0]) ? values.flat() : values.join(''),
            length: value => value.length, mul: (a, b) => a * b, div: (a, b) => Math.trunc(a / b),
            mod: (a, b) => a % b, string: String, copyIndex: () => index,
            armIf: (condition, whenTrue, whenFalse) => condition ? whenTrue : whenFalse
        };
        function evaluate(value) {
            if (Array.isArray(value)) return value.map(evaluate);
            if (value && typeof value === 'object') {
                return Object.fromEntries(Object.entries(value).map(([key, v]) => [key, evaluate(v)]));
            }
            return typeof value === 'string' && value.startsWith('[')
                ? vm.runInNewContext(value.slice(1, -1).replace(/\bif\(/g, 'armIf('), functions) : value;
        }
        const copy = template.variables.copy[0];
        const count = evaluate(copy.count);
        for (index = 0; index < count; index++) additional.push(evaluate(copy.input));
        const site = template.resources.find(resource => resource.type === 'Microsoft.Web/sites');
        const settings = evaluate(site.properties.siteConfig.appSettings);
        assert.equal(settings.find(setting => setting.name === 'VerifiedID__CredentialType').value, 'NorthstarLoyalty');
        assert.equal(settings.find(setting => setting.name === 'PROJECT').value, deploySourceControl ? parameters.Project : '');
        assert.equal(settings.find(setting => setting.name === 'SCM_DO_BUILD_DURING_DEPLOYMENT').value, String(deploySourceControl));
        assert.equal(settings.find(setting => setting.name === 'EEID__ClientId').value, parameters.ClientId);
        assert.equal(settings.find(setting => setting.name === 'VerifiedID__Verification__ProfileValueClaim').value, parameters.ProfileValueClaim);
        assert.equal(settings.filter(setting => setting.name.startsWith('VerifiedID__Credentials__')).length, credentials.length * 4 + 1);
        assert.equal(settings.find(setting => setting.name === 'VerifiedID__Credentials__0__Verification__AccountBindingClaim').value, 'workEmail');
        credentials.forEach((credential, i) => {
            for (const [key, value] of Object.entries(credential)) {
                assert.equal(settings.find(setting => setting.name === `VerifiedID__Credentials__${i}__${key}`).value, value);
            }
        });

    }
});

test('ARM deploys Windows Free F1 with HTTPS and Always On disabled', () => {
    const template = JSON.parse(fs.readFileSync(path.join(sample, 'ARMTemplate', 'template.json'), 'utf8'));
    const plan = template.resources.find(resource => resource.type === 'Microsoft.Web/serverfarms');
    assert.deepEqual(plan.sku, { name: 'F1', tier: 'Free', size: 'F1', family: 'F', capacity: 1 });
    assert.equal(plan.properties.reserved, false);
    const site = template.resources.find(resource => resource.type === 'Microsoft.Web/sites');
    assert.equal(site.properties.httpsOnly, true);
    assert.equal(site.properties.siteConfig.alwaysOn, false);
    const config = template.resources.find(resource => resource.type === 'Microsoft.Web/sites/config');
    assert.equal(config.properties.alwaysOn, false);
    assert.equal(config.properties.netFrameworkVersion, 'v10.0');
});

const identityPage = fs.readFileSync(path.join(sample, 'Views', 'IdentityVerification', 'Index.cshtml'), 'utf8');
const identityScripts = [...identityPage.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(match => match[1]);

test('verification dropdown navigates with selected credential, not a hard-coded loyalty type', () => {
    let changed;
    let navigation;
    const context = vm.createContext({
        document: { getElementById: () => ({ addEventListener(event, handler) { changed = handler; } }) },
        window: { location: { assign(url) { navigation = url; } } }
    });
    vm.runInContext(identityScripts[0], context);
    changed({ target: { value: 'employee card' } });
    assert.equal(navigation, '/identity?credentialId=employee%20card');
    assert.match(identityPage, /name="credentialId" value="@selectedId"/);
    assert.match(identityPage, /name="stage" value="credential"/);
    assert.doesNotMatch(identityPage, /loyalty_verified|name="stage" value="loyalty"/);
});

for (const stage of ['credential', 'identity']) for (const status of ['credential_verified', 'verified', 'failed']) {
test(`${stage} QR stays inside its card and terminal status ${status} clears the wallet request`, async () => {
    let submitted;
    let poll;
    let reloaded = false;
    const elements = new Map();
    const get = id => {
        if (!elements.has(id)) {
            const element = {
                disabled: false, hidden: true, textContent: '', children: [],
                appendChild(child) { this.children.push(child); child.parentNode = this; }
            };
            elements.set(id, element);
        }
        return elements.get(id);
    };
    const form = {
        action: '/identity/presentation',
        addEventListener(event, handler) { submitted = handler; },
        querySelector: selector => selector === 'button' ? get('button') : { value: stage }
    };
    const context = vm.createContext({
        document: { querySelector: () => form, getElementById: get },
        FormData: function () {},
        QRCode: function () {},
        setInterval(fn) { poll = fn; return 1; },
        clearInterval() {},
        window: { location: { reload() { reloaded = true; } } },
        fetch: async url => ({
            ok: true,
            json: async () => url === form.action
                ? { id: 'request', url: 'openid-vc://present', expiry: Date.now() / 1000 + 300 }
                : { status, message: 'Presentation result' }
        })
    });
    vm.runInContext(identityScripts[1], context);
    await submitted({ preventDefault() {} });
    await poll();
    assert.equal(reloaded, true);
    assert.equal(get('flow-message').textContent, 'Presentation result');
    assert.equal(get('verification-qrcode').hidden, true);
    assert.equal(get('verification-link').hidden, true);
    const slot = stage === 'credential' ? 'credential-wallet-slot' : 'identity-wallet-slot';
    assert.equal(get(slot).children.includes(get('wallet-scan-panel')), true);
});
}

test('IDV integration page presents three gated cards without manual verification success', () => {
    assert.match(identityPage, /identity verification provider integration flow/);
    for (const heading of ['credential-step-heading', 'provider-step-heading', 'identity-step-heading']) {
        assert.match(identityPage, new RegExp(`id="${heading}"`));
    }
    assert.match(identityPage, /status == "credential_verified" && !providerReturned && ViewData\["ProviderConfigured"\] is true/);
    assert.match(identityPage, /confirms the return only—not the proofing result/);
    assert.match(identityPage, /Only a validated backend presentation result/);
    assert.match(identityPage, /Continue to TrueIdentity/);
    assert.match(identityPage, /identity-provider-actions/);
    assert.match(identityPage, /Returned — continue to step 3/);
    assert.match(identityPage, /is-returned/);
    assert.match(identityPage, /Scan with Microsoft Authenticator/);
    assert.match(identityPage, /walletSlot\.appendChild\(scanPanel\)/);
    assert.match(identityPage, /credential-wallet-slot/);
    assert.match(identityPage, /identity-wallet-slot/);
    assert.match(identityPage, /Your External ID profile is not changed/);
    assert.doesNotMatch(identityPage, /VerifiedIdentity|Profile updated|Updating profile/);
    assert.doesNotMatch(identityPage, /I have been verified|identity\/verified/);
});

test('site omits the redundant Verified ID sign-in reminder', () => {
    const layout = fs.readFileSync(path.join(sample, 'Views', 'Shared', '_Layout.cshtml'), 'utf8');
    const home = fs.readFileSync(path.join(sample, 'Views', 'Home', 'Index.cshtml'), 'utf8');
    assert.doesNotMatch(layout + home + identityPage, /Verified ID is an (?:app|application) feature, not a sign-in method|Presenting a Verified ID is not an External ID sign-in method/);
    assert.doesNotMatch(layout, /class="sidebar-note"/);
});
