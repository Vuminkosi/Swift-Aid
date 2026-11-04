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
        for (const width of [390, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            await page.goto(base + '/case-queue-fixture');
            await page.getByRole('heading', { name: 'Synthetic case queue' }).waitFor();
            await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
            const grid = page.locator('#case-queue-advanced-filters');
            const toggle = page.getByRole('button', { name: 'More filters', exact: true });
            if (width === 390) {
                assert(!(await grid.isVisible()));
                await toggle.press('Enter');
                await grid.waitFor({ state: 'visible' });
                assert.equal(await page.getByRole('button', { name: 'Hide filters', exact: true }).getAttribute('aria-expanded'), 'true');
            } else {
                await grid.waitFor({ state: 'visible' });
                assert(!(await toggle.isVisible()));
            }
            await grid.getByLabel('Human or animal', { exact: true }).click();
            await page.getByRole('option', { name: 'Unknown', exact: true }).click();
            await page.waitForFunction(() => document.querySelector('#selected-sector').textContent === 'Unknown');
            if (width === 390) {
                await page.getByRole('button', { name: 'Hide filters', exact: true }).press('Enter');
                await grid.waitFor({ state: 'hidden' });
            }
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
        }
        assert.deepEqual(errors, []);
        assert(!output.includes('Unhandled exception'), output);
        console.log('PASS: compact filter keyboard toggle, Unknown selection, desktop filter visibility and no horizontal overflow at 390/1280px. Synthetic Chromium fixture only.');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
