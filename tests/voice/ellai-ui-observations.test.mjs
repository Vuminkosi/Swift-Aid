import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { randomUUID } from 'node:crypto';
import test from 'node:test';

const source = readFileSync('src/SwiftAid.UI/wwwroot/js/ellai-ui-observations.js', 'utf8').replaceAll('export function ', 'function ');
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


test('provider snapshots update by instance and disposal removes only their own state', () => {
    const f = fixture();
    f.add('cases.filter');
    assert.equal(typeof f.context.updateComponent, 'function');
    f.context.updateComponent('instance-a', { componentId: 'cases.form', controls: [{ controlId: 'cases.filter', state: 3 }] });
    assert.equal(f.collect().controlStates[0].state, 3);
    f.context.updateComponent('instance-a', { componentId: 'cases.form', controls: [{ controlId: 'cases.filter', state: 4 }] });
    assert.equal(f.collect().controlStates[0].state, 4);
    f.context.updateComponent('instance-b', { componentId: 'cases.form', controls: [{ controlId: 'cases.filter', state: 4 }] });
    assert.equal(f.collect().controlStates[0].state, 5);
    f.context.removeComponent('instance-a');
    assert.equal(f.collect().controlStates[0].state, 4);
    f.context.removeComponent('instance-b');
    assert.equal(f.collect().controlStates[0].state, 4);
});

test('provider state cannot make a missing or hidden DOM control ready', () => {
    const f = fixture();
    assert.equal(typeof f.context.updateComponent, 'function');
    f.context.updateComponent('instance-a', { componentId: 'cases.form', controls: [{ controlId: 'cases.filter', state: 4 }] });
    assert.equal(f.collect().controlStates.length, 0);
    f.add('cases.filter', { hidden: true });
    assert.equal(f.collect().controlStates[0].state, 2);
});


test('navigation cannot crowd current page controls out of a bounded snapshot', () => {
    const f = fixture();
    for (let i = 0; i < 15; i++) f.add(`navigation.link-${i}`);
    for (let i = 0; i < 9; i++) f.add(`report.field-${i}`);
    const report = f.collect();
    assert.equal(report.controlStates.length, 20);
    assert.equal(report.controlStates.filter(item => item.controlId.startsWith('report.')).length, 9);
});

test('targeted feature observation stays bounded and preserves duplicate and hidden evidence', () => {
    const f = fixture();
    f.add('navigation.cases', { hidden: true });
    f.add('report.entry');
    f.add('report.entry');
    const report = f.collect(['report.entry', 'navigation.cases', 'missing.control']);
    assert.equal(report.controlStates.length, 2);
    assert.equal(report.controlStates.find(item => item.controlId === 'report.entry').state, 5);
    assert.equal(report.controlStates.find(item => item.controlId === 'navigation.cases').state, 2);
    assert.equal(f.collect(['missing.control']).controlStates.length, 0);
});

test('invalid observation filters cannot broaden to every control', () => {
    const f = fixture();
    f.add('report.entry');
    for (const filter of ['report.entry', ['arbitrary#selector'], Array(21).fill('report.entry'), []])
        assert.equal(f.collect(filter).controlStates.length, 0);
});

test('changing observation focus invalidates context while stable focus retains it', () => {
    const f = fixture();
    f.add('report.entry');
    f.add('navigation.cases');
    const report = f.collect(['report.entry']);
    assert.equal(report.contextStamp, f.collect(['report.entry']).contextStamp);
    assert.notEqual(report.contextStamp, f.collect(['navigation.cases']).contextStamp);
});
