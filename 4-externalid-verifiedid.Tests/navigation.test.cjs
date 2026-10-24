const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const sample = path.join(__dirname, '..', '4-externalid-verifiedid');

test('mobile navigation toggles, closes with Escape and returns focus', () => {
    const attributes = { 'aria-expanded': 'false' };
    const events = {};
    let open = false;
    let focused = false;
    const toggle = {
        getAttribute: name => attributes[name],
        setAttribute: (name, value) => { attributes[name] = value; },
        addEventListener: (name, handler) => { events['toggle:' + name] = handler; },
        focus: () => { focused = true; }
    };
    const navigation = {
        addEventListener: (name, handler) => { events['nav:' + name] = handler; }
    };
    const document = {
        body: { classList: { toggle: (name, value) => { open = value; } } },
        getElementById: id => id === 'navigation-toggle' ? toggle : navigation,
        addEventListener: (name, handler) => { events[name] = handler; }
    };
    vm.runInNewContext(fs.readFileSync(path.join(sample, 'wwwroot', 'js', 'site.js'), 'utf8'), { document });
    events['toggle:click']();
    assert.equal(open, true);
    assert.equal(attributes['aria-expanded'], 'true');
    assert.equal(attributes['aria-label'], 'Close navigation');
    events.keydown({ key: 'Escape' });
    assert.equal(open, false);
    assert.equal(attributes['aria-expanded'], 'false');
    assert.equal(focused, true);
    events['toggle:click']();
    events['nav:click']({ target: { closest: () => ({}) } });
    assert.equal(open, false);
});

test('shell has accessible navigation and retains local authentication routes', () => {
    const layout = fs.readFileSync(path.join(sample, 'Views', 'Shared', '_Layout.cshtml'), 'utf8');
    const login = fs.readFileSync(path.join(sample, 'Views', 'Shared', '_LoginPartial.cshtml'), 'utf8');
    assert.match(layout, /href="#main-content"/);
    assert.match(layout, /id="main-content" tabindex="-1"/);
    assert.match(layout, /aria-label="Main navigation"/);
    assert.match(layout, /aria-current=/);
    assert.match(layout, /asp-controller="IdentityVerification"/);
    assert.match(login, /asp-area="MicrosoftIdentity" asp-controller="Account" asp-action="SignOut"/);
    assert.doesNotMatch(layout, /https:\/\/viddebugger/);
});

test('sample consistently uses the light Entra shell without a dark mode override', () => {
    const layout = fs.readFileSync(path.join(sample, 'Views', 'Shared', '_Layout.cshtml'), 'utf8');
    const css = fs.readFileSync(path.join(sample, 'wwwroot', 'css', 'sample-layout.css'), 'utf8');
    assert.match(layout, /<html lang="en" data-theme="light">/);
    assert.doesNotMatch(layout, /clawpilotTheme|prefers-color-scheme/);
    assert.doesNotMatch(css, /data-theme="dark"/);
    assert.match(css, /\.app-sidebar \{ background: var\(--cp-bg-elevated\); color: var\(--cp-text\)/);
    assert.match(css, /\.app-workspace main \{[^}]*background: var\(--cp-surface\)/);
});
