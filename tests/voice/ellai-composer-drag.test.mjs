import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../../src/SwiftAid.UI/wwwroot/js/ellai-composer-drag.js', import.meta.url), 'utf8');
const moduleUrl = 'data:text/javascript;base64,' + Buffer.from(source).toString('base64');

test('teardown tolerates a missing Blazor element and repeated disposal', async () => {
    const drag = await import(moduleUrl);
    assert.doesNotThrow(() => drag.detach(null, true));
    assert.doesNotThrow(() => drag.detach(undefined));
});

test('removing a composer releases interact and resize ownership without a DOM reference from .NET', async () => {
    let observerCallback, unsets = 0, removed = 0, disconnects = 0;
    globalThis.MutationObserver = class {
        constructor(callback) { observerCallback = callback; }
        observe() { }
        disconnect() { disconnects++; }
    };
    globalThis.document = { documentElement: {} };
    globalThis.window = { addEventListener() { }, removeEventListener() { removed++; } };
    globalThis.interact = Object.assign(() => ({ draggable() { return this; }, unset() { unsets++; } }),
        { modifiers: { restrictRect() { return {}; } } });
    const root = { isConnected: true, classList: { remove() { } }, style: { removeProperty() { } } };
    const drag = await import(moduleUrl);
    await drag.attach(root, {});
    root.isConnected = false;
    observerCallback();
    drag.detach(null, true);
    drag.detach(root, true);
    assert.equal(unsets, 1);
    assert.equal(removed, 1);
    assert.equal(disconnects, 1);
});

test('detach during asynchronous attachment cannot resurrect capture handlers', async () => {
    let attachments = 0;
    globalThis.interact = Object.assign(() => { attachments++; return { draggable() { return this; } }; },
        { modifiers: { restrictRect() { return {}; } } });
    const root = { isConnected: true, classList: { remove() { } }, style: {} };
    const drag = await import(moduleUrl);
    const pending = drag.attach(root, {});
    drag.detach(root);
    await pending;
    assert.equal(attachments, 0);
});
