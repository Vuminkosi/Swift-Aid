const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

(async () => {
    const browser = await chromium.launch({ headless: true });
    try {
        const page = await browser.newPage();
        const script = fs.readFileSync('src/SwiftAid.UI/wwwroot/js/ellai-ui-observations.js', 'utf8');
        // Synthetic DOM fixture exercising the actual shipped browser module.
        await page.route('http://localhost/**', route => route.fulfill(route.request().url().endsWith('/observe.js')
            ? { contentType: 'text/javascript', body: script }
            : { contentType: 'text/html', body: '<h1 id="guided-report-title">Report</h1><button id="report-submit">Submit</button><input value="sensitive text must not be collected">' }));
        await page.goto('http://localhost/');
        const collect = () => page.evaluate(async () => (await import('/observe.js')).collect());
        const first = await collect();
        assert(first.controlStates.every(c => c.state === 4));
        assert(!JSON.stringify(first).includes('sensitive'));
        assert.equal(first.contextStamp, (await collect()).contextStamp);
        await page.evaluate(() => history.pushState({}, '', '/report'));
        assert.notEqual(first.viewInstanceId, (await collect()).viewInstanceId);
        await page.locator('#report-submit').evaluate(e => e.disabled = true);
        const disabled = await collect();
        assert.equal(disabled.controlStates.find(c => c.controlId === 'report.submit').state, 3);
        assert.notEqual(first.contextStamp, disabled.contextStamp);
        await page.locator('#report-submit').evaluate(e => e.style.display = 'none');
        assert.equal((await collect()).controlStates.find(c => c.controlId === 'report.submit').state, 2);
        await page.locator('#guided-report-title').evaluate(e => e.after(e.cloneNode(true)));
        assert.equal((await collect()).controlStates.find(c => c.controlId === 'report.entry').state, 5);
        await page.locator('body').evaluate(e => e.replaceChildren());
        assert.deepEqual((await collect()).mountedControlIds, []);
        console.log('PASS: ready, disabled, hidden, ambiguous, empty, stable/change context; no input values collected. Synthetic Chromium fixture.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
