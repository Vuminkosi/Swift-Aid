const assert = require('node:assert/strict');
const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.connectOverCDP(process.env.SWIFTAID_TEST_CDP || 'http://127.0.0.1:19226');
    const page = browser.contexts().flatMap(context => context.pages()).find(page => page.url().startsWith('http://localhost:8080/'));
    assert.ok(page, 'An authenticated local test page is required.');
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error' && /classList|Unhandled exception|CircuitHost/.test(message.text())) errors.push(message.text()); });
    page.setDefaultTimeout(60000);
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto('http://localhost:8080/surveillance');
    const markers = page.locator('.sa-map-marker[title]');
    await markers.first().waitFor();
    await markers.first().click();
    const popup = page.locator('.sa-map-popup');
    const expected = Number((await popup.locator('p').innerText()).replace(/[^0-9]/g, ''));
    await popup.getByRole('button', { name: 'View cases >>', exact: true }).click();
    const dialog = page.getByRole('dialog');
    await dialog.waitFor();
    assert.equal(await dialog.locator('.map-cases__row').count(), expected);
    const link = dialog.locator('a[href^="/case/"]').first();
    const href = await link.getAttribute('href');
    await page.screenshot({ path: 'artifacts/live-integration/map-case-list.png' });
    await link.click();
    await page.waitForURL('**' + href);
    await page.getByRole('heading', { name: 'Reported observations', exact: true }).waitFor();

    // Exercise real Blazor component disposal without sending a provider request.
    await page.goto('http://localhost:8080/ellai/about');
    const input = page.getByRole('combobox', { name: 'Your input language', exact: true });
    await input.waitFor();
    await page.waitForFunction(() => !document.querySelector('[aria-label="Your input language"]')?.disabled);
    const close = page.getByRole('button', { name: 'Close Ellai composer', exact: true });
    if (await close.isVisible()) await close.click();
    await page.getByRole('tab', { name: 'Session history' }).click();
    await page.getByRole('heading', { name: 'Your conversations', exact: true }).waitFor();
    if (await close.isVisible()) { await close.click(); await close.waitFor({ state: 'hidden' }); }
    assert.equal(await page.locator('.ellai-settings__session').first().evaluate(element => getComputedStyle(element).display), 'grid');
    await page.screenshot({ path: 'artifacts/live-integration/ellai-history-desktop.png' });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForFunction(() => document.documentElement.scrollWidth <= innerWidth + 1);
    const navigation = page.getByRole('button', { name: 'Close navigation', exact: true });
    if (await navigation.isVisible()) await navigation.click();
    await page.locator('.ellai-history__header').scrollIntoViewIfNeeded();
    await page.screenshot({ path: 'artifacts/live-integration/ellai-history-mobile.png' });
    assert.deepEqual(errors, []);
    await page.setViewportSize({ width: 1440, height: 1000 });
    console.log(JSON.stringify({ popupCaseCount: expected, caseDetailNavigation: 'PASS', historyResponsive: 'PASS', composerDisposeErrors: errors }));
    process.exit(0);
})().catch(error => { console.error(error.stack); process.exit(1); });
