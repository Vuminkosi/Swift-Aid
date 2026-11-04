const assert = require('node:assert/strict');
const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.connectOverCDP(process.env.SWIFTAID_TEST_CDP || 'http://127.0.0.1:19226');
    const page = browser.contexts().flatMap(context => context.pages()).find(page => page.url().startsWith('http://localhost:8080/'));
    assert.ok(page, 'An authenticated local test page is required.');
    page.setDefaultTimeout(60000);
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto('http://localhost:8080/surveillance');
    const legend = page.getByRole('group', { name: 'Map layers', exact: true });
    const human = legend.getByRole('button', { name: 'Human', exact: true });
    const animal = legend.getByRole('button', { name: 'Animal', exact: true });
    const both = legend.getByRole('button', { name: 'Both', exact: true });
    const medical = legend.getByRole('button', { name: 'Medical centers & clinics', exact: true });
    const catalogue = await page.evaluate(async () => (await fetch('/_content/SwiftAid.UI/maps/medical-centers-v1.json')).json());
    const expectedNames = country => catalogue.facilities.filter(item => item.country === country)
        .map(item => item.name + ' — ' + item.type.toLowerCase());
    async function verifyCountry(country) {
        await page.waitForFunction(expected => {
            const markers = [...document.querySelectorAll('.sa-medical-marker')];
            return markers.length && markers.reduce((sum, marker) => sum + (marker.textContent.startsWith('M ') ? Number(marker.textContent.slice(2)) : 1), 0) === expected;
        }, expectedNames(country).length);
        const names = await page.locator('.sa-medical-marker[title]').evaluateAll(markers => markers.map(marker => marker.title));
        assert.ok(names.length > 0);
        for (const name of names) assert.ok(expectedNames(country).includes(name), 'A facility from the wrong country is visible.');
    }
    await verifyCountry('SZ');
    assert.equal(await medical.getAttribute('aria-pressed'), 'true');
    await human.focus(); await page.keyboard.press('Space');
    await page.waitForFunction(() => !document.querySelector('.sa-map-marker--mixed') && document.querySelector('.sa-map-marker--animal'));
    assert.equal(await human.getAttribute('aria-pressed'), 'false');
    await animal.click();
    await page.waitForFunction(() => document.querySelectorAll('.sa-map-marker').length === 0);
    assert.equal(await page.locator('.leaflet-container').count(), 1);
    await verifyCountry('SZ');
    await medical.click();
    await page.waitForFunction(() => document.querySelectorAll('.sa-medical-marker').length === 0);
    // The same map and legend remain usable when every layer is off.
    await both.focus(); await page.keyboard.press('Enter');
    await page.waitForFunction(() => document.querySelectorAll('.sa-map-marker').length > 0);
    assert.equal(await both.getAttribute('aria-pressed'), 'true');
    assert.equal(await human.getAttribute('aria-pressed'), 'true');
    assert.equal(await animal.getAttribute('aria-pressed'), 'true');
    await medical.click(); await verifyCountry('SZ');
    await page.screenshot({ path: 'artifacts/live-integration/medical-map-eswatini.png' });
    await page.getByRole('combobox', { name: 'Country', exact: true }).click();
    await page.getByRole('option', { name: 'Zimbabwe', exact: true }).click();
    await verifyCountry('ZW');
    const clinic = page.locator('.sa-medical-marker[title$=" — clinic"]').first();
    for (let attempt = 0; attempt < 4 && !await clinic.count(); attempt++) {
        const cluster = page.locator('.sa-medical-marker').filter({ hasText: /^M / }).first();
        assert.ok(await cluster.count(), 'A clinic or expandable facility cluster must be visible.');
        await cluster.click();
        await page.waitForTimeout(500);
    }
    await clinic.click();
    await page.locator('.sa-medical-popup').waitFor();
    assert.match(await page.locator('.sa-medical-popup').innerText(), /Clinic/);
    assert.match(await page.locator('.sa-medical-popup a').getAttribute('href'), /^https:\/\/www.openstreetmap.org\/(node|way)\/[0-9]+$/);
    await page.screenshot({ path: 'artifacts/live-integration/medical-map-clinic.png' });
    await page.getByRole('button', { name: 'Reset view', exact: true }).click();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(500);
    const navigation = page.getByRole('button', { name: 'Close navigation', exact: true });
    if (await navigation.isVisible()) await navigation.click();
    await page.waitForTimeout(500);
    await page.waitForFunction(() => document.documentElement.scrollWidth <= innerWidth + 1);
    await legend.scrollIntoViewIfNeeded();
    await page.screenshot({ path: 'artifacts/live-integration/medical-map-mobile.png' });
    assert.deepEqual(errors, []);
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.getByRole('combobox', { name: 'Country', exact: true }).click();
    await page.getByRole('option', { name: 'Eswatini', exact: true }).click();
    console.log(JSON.stringify({ countries: ['SZ', 'ZW'], facilitiesPerCountry: { SZ: 39, ZW: 966 }, keyboardToggles: 'PASS',
        allLayersOffRecovery: 'PASS', clinicPopup: 'PASS', mobileViewport: 'PASS', errors }));
    process.exit(0);
})().catch(error => { console.error(error.stack); process.exit(1); });
