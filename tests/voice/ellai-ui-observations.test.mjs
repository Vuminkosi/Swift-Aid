import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { randomUUID } from 'node:crypto';
import test from 'node:test';

const source = readFileSync('src/SwiftAid.UI/wwwroot/js/ellai-ui-observations.js', 'utf8').replace('export function collect()', 'function collect()');
function fixture() {
    const nodes = [];
    const context = { crypto: { randomUUID }, location: { pathname: '/' },
        document: { querySelectorAll: selector => selector === '[data-ellai-control]' ? nodes : [] },
        getComputedStyle: element => ({ visibility: element.hidden ? 'hidden' : 'visible' }) };
    runInNewContext(source + '\nglobalThis.collect = collect;', context);
    const add = (id, properties = {}) => {
        const node = { getAttribute: () => id, getClientRects: () => [1],
            checkVisibility: () => true, matches: () => !!node.disabled,
            closest: () => null, ...properties };
        nodes.push(node);
        return node;
    };
    return { nodes, add, collect: context.collect, context };
}

test('new component identities are discovered without editing a central binding list', () => {
    const f = fixture();
    f.add('cases.filter');
    assert.equal(f.collect().controlStates[0]?.controlId, 'cases.filter');
    assert.equal(f.collect().controlStates[0].state, 4);
});
test('disposal, duplicates, visibility and disabled state follow current elements', () => {
    const f = fixture();
    const node = f.add('cases.filter', { disabled: true });
    assert.equal(f.collect().controlStates[0]?.state, 3);
    node.hidden = true;
    assert.equal(f.collect().controlStates[0].state, 2);
    f.add('cases.filter');
    assert.equal(f.collect().controlStates[0].state, 5);
    f.nodes.length = 0;
    assert.equal(f.collect().controlStates.length, 0);
});
test('reports are bounded and contain no input values or arbitrary identities', () => {
    const f = fixture();
    f.add('bad\"selector');
    for (let i = 0; i < 25; i++) f.add(`case.control-${i}`, { value: 'private narrative' });
    const report = f.collect();
    assert.equal(report.controlStates.length, 20);
    assert.equal(JSON.stringify(report).includes('private'), false);
    assert.equal(JSON.stringify(report).includes('selector'), false);
});
test('stable observations retain stamps; navigation and changed state invalidate them', () => {
    const f = fixture();
    const node = f.add('cases.filter');
    const first = f.collect();
    assert.equal(first.contextStamp, f.collect().contextStamp);
    node.disabled = true;
    assert.notEqual(first.contextStamp, f.collect().contextStamp);
    f.context.location.pathname = '/cases';
    assert.notEqual(first.viewInstanceId, f.collect().viewInstanceId);
});
