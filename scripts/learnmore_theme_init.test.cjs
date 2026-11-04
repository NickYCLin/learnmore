const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('LearnMore/wwwroot/js/theme-init.js', 'utf8');

function boot(saved, storageBlocked = false) {
    function element() {
        const classes = new Set();
        return { classes, style: {}, classList: { toggle(name, enabled) { enabled ? classes.add(name) : classes.delete(name); } } };
    }
    const document = { documentElement: element(), body: null };
    const listeners = {};
    const storage = { value: saved, getItem() { if (storageBlocked) throw new Error('blocked'); return this.value; }, setItem(key, value) { if (storageBlocked) throw new Error('blocked'); this.value = value; } };
    const window = { addEventListener(name, handler) { listeners[name] = handler; } };
    vm.runInNewContext(source, { document, window, localStorage: storage });
    return { document, window, storage, listeners, element };
}

test('saved dark theme applies to root before body and before stylesheets', () => {
    const fixture = boot('dark-theme');
    assert.equal(fixture.document.documentElement.classes.has('dark-theme'), true);
    assert.equal(fixture.document.documentElement.style.colorScheme, 'dark');
    fixture.document.body = fixture.element();
    fixture.window.LearnMoreTheme.applyToBody();
    assert.equal(fixture.document.body.classes.has('dark-theme'), true);
    const layout = fs.readFileSync('LearnMore/Views/Shared/_LayoutHeader.cshtml', 'utf8');
    assert.ok(layout.indexOf('theme-init.js') < layout.indexOf('rel="stylesheet"'));
    assert.ok(layout.indexOf('</head>') < layout.indexOf('<nav'));
    assert.ok(layout.indexOf('applyToBody()') < layout.indexOf('<nav'));
});

test('switch updates root and body immediately and reload retains both themes', () => {
    const fixture = boot('');
    fixture.document.body = fixture.element();
    fixture.window.LearnMoreTheme.toggle();
    assert.equal(fixture.document.body.classes.has('dark-theme'), true);
    assert.equal(boot(fixture.storage.value).document.documentElement.style.colorScheme, 'dark');
    fixture.window.LearnMoreTheme.toggle();
    assert.equal(fixture.document.body.classes.has('dark-theme'), false);
    assert.equal(boot(fixture.storage.value).document.documentElement.style.colorScheme, 'light');
});

test('storage refusal does not break rendering or toggling', () => {
    const fixture = boot(null, true);
    fixture.window.LearnMoreTheme.toggle();
    assert.equal(fixture.document.documentElement.style.colorScheme, 'dark');
});

test('another tab theme change updates without reloading', () => {
    const fixture = boot('');
    fixture.listeners.storage({ key: 'theme', newValue: 'dark-theme' });
    assert.equal(fixture.document.documentElement.style.colorScheme, 'dark');
});
