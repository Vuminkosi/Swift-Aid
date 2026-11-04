import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const source = await readFile(new URL('../../src/SwiftAid.UI/wwwroot/js/ellai-composer-input.js', import.meta.url), 'utf8');
const input = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function fixture() {
    let handler;
    let release;
    const calls = [];
    const root = {
        addEventListener(_, callback) { handler = callback; },
        removeEventListener(_, callback) { if (handler === callback) handler = null; },
    };
    const receiver = { invokeMethodAsync(...args) { calls.push(args); return new Promise(resolve => { release = resolve; }); } };
    input.attach(root, receiver);
    return {
        calls, root, receiver, release: () => release?.(),
        send(extra = {}) {
            const event = { key: 'Enter', target: { matches: () => true, value: 'Hello Ellai' },
                prevented: false, preventDefault() { this.prevented = true; }, stopPropagation() {}, ...extra };
            handler?.(event);
            return event;
        },
    };
}

test('Enter sends the actual text once without inserting a newline', () => {
    const f = fixture();
    assert.equal(f.send().prevented, true);
    f.send();
    assert.deepEqual(f.calls, [['SubmitKeyboardMessage', 'Hello Ellai']]);
    f.release();
});

test('Shift Enter, composition and other controls retain native keyboard behavior', () => {
    const f = fixture();
    for (const extra of [{ shiftKey: true }, { isComposing: true }, { keyCode: 229 }, { key: 'a' },
        { target: { matches: () => false, value: 'Other control' } }]) {
        assert.equal(f.send(extra).prevented, false);
    }
    assert.equal(f.calls.length, 0);
});

test('Held Enter, empty and disabled input cannot submit', () => {
    const f = fixture();
    f.send({ repeat: true });
    f.send({ target: { matches: () => true, value: ' ' } });
    f.send({ target: { matches: () => true, value: 'Hello', disabled: true } });
    assert.equal(f.calls.length, 0);
});

test('Detach removes the handler when the composer closes', () => {
    const f = fixture();
    input.detach(f.root);
    assert.equal(f.send().prevented, false);
    assert.equal(f.calls.length, 0);
});
