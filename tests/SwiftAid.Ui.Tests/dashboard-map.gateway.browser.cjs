// Uses an already authenticated local test browser; never creates or imports records.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

(async () => {
    const browser = await chromium.connectOverCDP(process.env.SWIFTAID_BROWSER_CDP || 'http://127.0.0.1:19226');
    const context = browser.contexts()[0];
    const page = context.pages().find(candidate => /\/(dashboard|surveillance|my-cases|ellai\/about)(\?|$)/.test(candidate.url()));
    assert.ok(page, 'Keep an authenticated operations page open in the test browser.');
    const base = process.env.SWIFTAID_GATEWAY_URL || 'http://localhost:8080';
    page.setDefaultTimeout(90000);
    page.setDefaultNavigationTimeout(90000);
    const results = [];
    // Avoid automated tile traffic during gesture/viewport checks.
    await page.route('https://tile.openstreetmap.org/**', route => route.abort());
    try {
        await page.setViewportSize({ width: 1440, height: 1000 });
        for (const route of ['/dashboard', '/surveillance']) {
            console.log('Checking', route);
            await page.goto(base + route, { waitUntil: 'domcontentloaded' });
            await page.locator('.leaflet-overlay-pane path').first().waitFor();
            const map = page.locator('.leaflet-container');
            await page.locator('.street-map__reset').filter({hasText: '100%'}).waitFor();
            await map.focus();
            await map.press('+');
            await page.waitForFunction(() => !document.querySelector('.street-map__reset').textContent.includes('100%'), null, { timeout: 15000 });
            await page.getByRole('button', { name: 'Reset view', exact: true }).click();
            await page.locator('.street-map__reset').filter({hasText: '100%'}).waitFor();
            await map.hover();
            await page.mouse.wheel(0, -200);
            await page.waitForFunction(() => !document.querySelector('.street-map__reset').textContent.includes('100%'), null, { timeout: 15000 });
            await page.getByRole('button', { name: 'Reset view', exact: true }).click();
            results.push({ route, map: 'PASS', keyboardAndWheelZoom: 'PASS', countryReset: 'PASS' });
        }
        const api = await page.evaluate(async () => {
            const response = await fetch('/api/v1/surveillance/area-aggregates');
            return { status: response.status, areas: response.ok ? (await response.json()).areas.length : 0 };
        });
        assert.equal(api.status, 200);
        assert.ok(api.areas > 0);
        results.push({ mapApi: 'PASS' });

        for (const route of ['/dashboard', '/my-cases']) {
            console.log('Checking', route);
            await page.goto(base + route, { waitUntil: 'domcontentloaded' });
            const link = page.locator(route === '/dashboard'
                ? '.operations-dashboard__case-table a[href^="/case/"]'
                : '.operations-case-queue__table a[href^="/case/"]').first();
            await link.waitFor();
            const location = page.locator('a[href^="/surveillance?country="]').first();
            await location.waitFor();
            assert.ok((await location.innerText()).includes('|'));
            const placeHref = await location.getAttribute('href');
            assert.ok(new URL(placeHref, base).searchParams.get('country'));
            const href = await link.getAttribute('href');
            assert.match(href, /^\/case\/[a-f0-9-]{36}$/i);
            await link.click();
            await page.waitForURL(base + href);
            await page.getByText('Report analysis', { exact: true }).waitFor();
            await page.locator('.case-report-evidence li').first().waitFor();
            await page.goto(base + placeHref, { waitUntil: 'domcontentloaded' });
            await page.locator('.leaflet-overlay-pane path').first().waitFor();
            assert.ok(await page.getByLabel('Country', { exact: true }).count() > 0);
            results.push({ route, caseLink: 'PASS', observations: 'PASS', placeLink: 'PASS' });
        }
        await page.setViewportSize({ width: 390, height: 844 });
        for (const route of ['/dashboard', '/my-cases', '/surveillance']) {
            console.log('Checking', route);
            await page.goto(base + route, { waitUntil: 'domcontentloaded' });
            await page.locator(route === '/my-cases' ? '.operations-case-queue__row' : '.leaflet-container').first().waitFor();
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), route + ' overflows');
            results.push({ route, mobileViewport: 'PASS' });
        }

        const anonymous = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
        try {
            const home = await anonymous.newPage();
            await home.goto(base, { waitUntil: 'domcontentloaded' });
            await home.locator('.platform-flow').waitFor();
            assert.equal(await home.locator('.platform-flow__node').count(), 27);
            assert.equal(await home.locator('.platform-flow__edge').count(), 28);
            await home.getByRole('button', { name: 'Pause flow', exact: true }).click();
            await home.locator('.platform-flow--paused').waitFor();
            await home.emulateMedia({ reducedMotion: 'reduce' });
            assert.equal(await home.locator('.platform-flow__pulse').first().evaluate(el => getComputedStyle(el).animationName), 'none');
            await home.setViewportSize({ width: 390, height: 844 });
            await home.waitForFunction(() => document.documentElement.scrollWidth <= innerWidth + 1, null, { timeout: 5000 });
            for (const model of ['NLLB-200', 'MMS-ASR']) await home.getByRole('heading', { name: model, exact: true }).waitFor();
            results.push({ homepage: 'PASS', pause: 'PASS', reducedMotion: 'PASS', mobileViewport: 'PASS' });
        } finally {
            await anonymous.close();
        }
        console.log(JSON.stringify(results, null, 2));
    } finally {
        await page.unroute('https://tile.openstreetmap.org/**');
        await page.setViewportSize({ width: 1440, height: 1000 });
    }
    process.exit(0);
})().catch(error => { console.error(error.message); process.exit(1); });
