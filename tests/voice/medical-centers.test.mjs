import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../../src/SwiftAid.UI/wwwroot/maps/medical-centers.js', import.meta.url), 'utf8');
const { selectMedicalCenters, medicalCenterSource } = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));
const catalogue = JSON.parse(await readFile(new URL('../../src/SwiftAid.UI/wwwroot/maps/medical-centers-v1.json', import.meta.url), 'utf8'));

test('bundled facilities stay within the selected country and carry source provenance', () => {
    assert.equal(selectMedicalCenters(catalogue, 'sz').length, 39);
    assert.equal(selectMedicalCenters(catalogue, 'ZW').length, 966);
    assert.deepEqual(selectMedicalCenters(catalogue, 'ZA'), []);
    assert.equal(new Set(catalogue.facilities.map(item => item.id)).size, 1005);
    for (const country of ['SZ', 'ZW']) {
        assert.ok(selectMedicalCenters(catalogue, country).some(item => item.type === 'Clinic'));
        assert.ok(selectMedicalCenters(catalogue, country).some(item => item.type === 'Hospital'));
    }
    for (const item of catalogue.facilities) {
        assert.match(item.sourceSha256, /^[a-f0-9]{64}$/);
        assert.equal(item.sourceSnapshot, '2026-10-07');
        assert.match(medicalCenterSource(item), /^https:\/\/www.openstreetmap.org\/(node|way|relation)\/[0-9]+$/);
    }
});

test('malformed coordinates and unsafe source identifiers never become markers or links', () => {
    const valid = catalogue.facilities[0];
    const bad = { version: 1, facilities: [
        { ...valid, latitude: NaN }, { ...valid, longitude: 200 }, { ...valid, name: '' },
        { ...valid, id: 'javascript:alert(1)' }, { ...valid, id: 'node/1?redirect=bad' }
    ] };
    assert.deepEqual(selectMedicalCenters(bad, 'SZ'), []);
    assert.equal(medicalCenterSource(bad.facilities[3]), null);
    assert.deepEqual(selectMedicalCenters(null, 'SZ'), []);
    assert.deepEqual(selectMedicalCenters({ ...catalogue, version: 2 }, 'SZ'), []);
});
