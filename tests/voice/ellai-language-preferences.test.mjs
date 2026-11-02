import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const source = await readFile(new URL('../../src/SwiftAid.UI/wwwroot/js/ellai-language-preferences.js', import.meta.url), 'utf8');
const preferences = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('language defaults survive missing or malformed local storage', () => {
    for (const value of [null, '{', '{"inputLanguage":"unknown","outputLanguage":"eng"}']) {
        globalThis.localStorage = { getItem: () => value };
        assert.deepEqual(preferences.load(), { inputLanguage: 'eng', outputLanguage: 'eng', englishVoiceId: 'af_heart' });
    }
});

test('saved input and output choices restore independently', () => {
    let stored;
    globalThis.localStorage = { getItem: () => stored, setItem: (_, value) => { stored = value; } };
    assert.equal(preferences.save({ inputLanguage: 'sna', outputLanguage: 'ssw' }), true);
    assert.deepEqual(preferences.load(), { inputLanguage: 'sna', outputLanguage: 'ssw', englishVoiceId: 'af_heart' });
    assert.equal(preferences.save({ inputLanguage: 'unsupported', outputLanguage: 'eng' }), false);
    assert.equal(preferences.load().inputLanguage, 'sna');
});

test('storage refusal is observable and never reports a successful save', () => {
    globalThis.localStorage = { getItem() { throw Error('blocked'); }, setItem() { throw Error('quota'); } };
    assert.equal(preferences.load().inputLanguage, 'eng');
    assert.equal(preferences.save({ inputLanguage: 'sna', outputLanguage: 'eng' }), false);
});

test('English voice selection survives reload and rejects unknown voices', () => {
    let stored = JSON.stringify({ inputLanguage: 'sna', outputLanguage: 'eng' });
    globalThis.localStorage = { getItem: () => stored, setItem: (_, value) => { stored = value; } };
    assert.equal(preferences.load().englishVoiceId, 'af_heart');
    assert.equal(preferences.save({ ...preferences.load(), englishVoiceId: 'am_michael' }), true);
    assert.equal(preferences.load().englishVoiceId, 'am_michael');
    assert.equal(preferences.load().inputLanguage, 'sna');
    assert.equal(preferences.save({ ...preferences.load(), englishVoiceId: 'unknown' }), false);
    assert.equal(preferences.load().englishVoiceId, 'am_michael');
});
