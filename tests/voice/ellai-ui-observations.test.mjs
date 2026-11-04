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

test('invalid replacement removes stale completion and actionability evidence', () => {
    const f = fixture();
    f.add('report.subject');
    const valid = { componentId: 'report.form', controls: [{ controlId: 'report.subject', state: 3, completionAccepted: true }] };
    for (const invalid of [
        { componentId: 'invalid#id', controls: [] },
        { componentId: 'report.form', controls: null },
        { componentId: 'report.form', controls: [null] },
        { componentId: 'report.form', controls: Array(21).fill(valid.controls[0]) },
        { componentId: 'report.form', controls: [{ controlId: 'report.subject', state: 99 }] }
    ]) {
        assert.equal(f.context.updateComponent('form', valid), true);
        assert.equal(f.collect().completedControlIds.join(), 'report.subject');
        assert.equal(f.collect().controlStates[0].state, 3);
        assert.equal(f.context.updateComponent('form', invalid), false);
        const observation = f.collect();
        assert.equal(observation.completedControlIds.length, 0);
        assert.equal(observation.controlStates[0].state, 0);
    }
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


test('validated controls survive wizard unmount but not component disposal or ambiguous ownership', () => {
    const f = fixture();
    const snapshot = { componentId: 'report.form', controls: [{ controlId: 'report.subject', state: 1, completionAccepted: true }] };
    f.context.updateComponent('form', snapshot);
    const report = f.collect();
    assert.equal(report.mountedControlIds.length, 0);
    assert.equal(report.completedControlIds.join(), 'report.subject');
    assert.equal(f.collect(['report.narrative']).completedControlIds.length, 0);
    f.context.updateComponent('duplicate', snapshot);
    assert.equal(f.collect().completedControlIds.length, 0);
    f.context.removeComponent('duplicate');
    f.context.removeComponent('form');
    assert.equal(f.collect().completedControlIds.length, 0);
});

test('completion changes invalidate context and only literal accepted validation is used', () => {
    const f = fixture();
    const snapshot = accepted => ({ componentId: 'report.form', controls: [{ controlId: 'report.subject', state: 4, completionAccepted: accepted }] });
    f.context.updateComponent('form', snapshot('true'));
    const first = f.collect();
    assert.equal(first.completedControlIds.length, 0);
    f.context.updateComponent('form', snapshot(true));
    const accepted = f.collect();
    assert.notEqual(first.contextStamp, accepted.contextStamp);
    f.context.updateComponent('form', snapshot(false));
    assert.equal(f.collect().completedControlIds.length, 0);
});

test('validation observations carry only ordered field identifiers and clear on correction', () => {
    const f = fixture();
    const snapshot = validationIssues => ({ componentId: 'report.form', controls: [], validationIssues });
    f.context.updateComponent('form', snapshot([
        { inputId: 'report.original-statement', message: 'private entered text', anchor: 'untrusted-selector' },
        { inputId: 'report.occurrence-date', message: 'another message' }
    ]));
    const first = f.collect();
    assert.equal(first.validationInputIds.join(), 'report.original-statement,report.occurrence-date');
    assert(!JSON.stringify(first).includes('private'));
    assert(!JSON.stringify(first).includes('untrusted-selector'));
    f.context.updateComponent('form', snapshot([{ inputId: 'report.occurrence-date' }]));
    const second = f.collect();
    assert.equal(second.validationInputIds.join(), 'report.occurrence-date');
    assert.notEqual(first.contextStamp, second.contextStamp);
    f.context.removeComponent('form');
    assert.equal(f.collect().validationInputIds.length, 0);
});

test('targeted observations do not expose validation identifiers from unrelated components', () => {
    const f = fixture();
    f.context.updateComponent('report', { componentId: 'report.form', controls: [{ controlId: 'report.narrative', state: 4 }],
        validationIssues: [{ inputId: 'report.original-statement' }] });
    f.context.updateComponent('cases', { componentId: 'cases.form', controls: [{ controlId: 'cases.filter', state: 4 }],
        validationIssues: [{ inputId: 'cases.filter-value' }] });
    assert.equal(f.collect(['report.narrative']).validationInputIds.join(), 'report.original-statement');
    assert.equal(f.collect(['cases.filter']).validationInputIds.join(), 'cases.filter-value');
    assert.equal(f.collect(['invalid#selector']).validationInputIds.length, 0);
});
