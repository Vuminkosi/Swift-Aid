const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

(async () => {
    const host = spawn('dotnet', [path.resolve('tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll'), '--urls', 'http://127.0.0.1:0'], {
        cwd: path.resolve('tests/SwiftAid.Ui.TestHost'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }
    });
    let output = '', browser;
    host.stdout.on('data', data => { output += data; });
    host.stderr.on('data', data => { output += data; });
    try {
        const deadline = Date.now() + 30000;
        while (!/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/.test(output)) {
            assert(host.exitCode === null && Date.now() < deadline, output);
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        const base = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1];
        browser = await chromium.launch({ headless: true, ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        for (const width of [1280, 390]) {
            await page.setViewportSize({ width, height: 1000 });
            await page.emulateMedia({ colorScheme: width === 390 ? 'dark' : 'light', reducedMotion: 'no-preference' });
            await page.goto(base + '/dashboard-presentation-fixture');
            await page.locator('[data-fixture-ready="true"]').waitFor();
            const summary = page.locator('.operations-dashboard__condition-summary');
            assert((await summary.innerText()).includes('195'));
            const panel = summary.locator('..');
            assert((await panel.boundingBox()).height < 350, 'One category should not stretch into an empty chart');
            assert.equal(await page.getByRole('link', {name:'Open map',exact:true}).getAttribute('href'), '/surveillance');
            assert.equal(await page.locator('.operations-map__selected').count(), 0);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
            await page.screenshot({path: `.codex/dashboard-summary-${width}.png`,fullPage:true});
            await page.getByRole('button',{name:'Toggle condition categories',exact:true}).click();
            await page.locator('.operations-dashboard__bar-fill').first().waitFor();
            assert.equal(await page.locator('.operations-dashboard__bar-fill').count(),2);
            assert(await page.locator('.operations-dashboard__bar-fill').evaluateAll(items => items.every(item =>
                item.getBoundingClientRect().height > 10 && getComputedStyle(item).backgroundColor !== 'rgba(0, 0, 0, 0)')));
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
            await page.screenshot({path: `.codex/dashboard-chart-${width}.png`,fullPage:true});
        }
        assert.deepEqual(errors, []);
        console.log('PASS: compact condition summary, visible category bars, map link and viewport containment at 1280/390px. Synthetic presentation fixture.');
    } finally {
        await browser?.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
