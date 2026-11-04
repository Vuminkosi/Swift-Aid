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
        browser = await chromium.launch({ headless: true });
        for (const width of [1280, 390]) {
            const page = await browser.newPage({ viewport: { width, height: 900 }, colorScheme: width === 390 ? 'dark' : 'light' });
            const errors = [];
            page.on('pageerror', error => errors.push(error.message));
            await page.goto(base + '/officer-administration-fixture');
            await page.locator('main[data-fixture-ready="true"]').waitFor();
            await page.getByLabel('Organization reference').fill('synthetic-org');
            await page.getByLabel('Officer reference').fill('synthetic-officer');
            await page.getByLabel('Country code').fill('SZ');
            await page.getByRole('button', { name: 'Look up designation' }).click();
            await page.getByText('No designation recorded for this scope.').waitFor();
            await page.getByLabel('Valid from (UTC)').fill('2026-11-04 10:00');
            await page.getByLabel('Expires (UTC)').fill('2026-12-04 10:00');
            await page.getByRole('button', { name: 'Review change' }).click();
            await page.getByRole('group', { name: 'Review designation change' }).waitFor();
            assert.equal(await page.locator('#write-count').innerText(), '0');
            await page.getByRole('button', { name: 'Confirm change' }).press('Enter');
            await page.getByText('The result is not yet known.', { exact: false }).waitFor();
            await page.getByRole('button', { name: 'Retry same change' }).click();
            await page.getByText('The earlier result is still unknown.', { exact: false }).waitFor();
            await page.getByRole('button', { name: 'Retry same change' }).click();
            await page.getByText('Designation change recorded.').waitFor();
            assert.equal(await page.locator('#write-count').innerText(), '3');
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
            assert.deepEqual(errors, []);
            await page.close();
        }
        process.stdout.write('PASS: review, confirmation and idempotent retry at desktop/light and mobile/dark.\n');
    } finally { if (browser) await browser.close(); host.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
