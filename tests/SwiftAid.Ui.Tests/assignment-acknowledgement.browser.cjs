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
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        for (const width of [390, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            await page.goto(`${base}/assignment-acknowledgement-fixture`);
            await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
            await page.getByLabel('Choose an available action').click();
            await page.getByRole('option', { name: 'Acknowledge assignment', exact: true }).click();
            assert.equal(await page.locator('#submitted').textContent(), 'False');
            await page.getByRole('button', { name: 'Review action', exact: true }).press('Enter');
            assert.equal(await page.locator('#submitted').textContent(), 'False');
            await page.getByRole('button', { name: 'Confirm action', exact: true }).press('Enter');
            await page.waitForFunction(() => document.querySelector('#submitted')?.textContent === 'True');
            assert(await page.getByText('The accountable owner acknowledged this assignment.').first().isVisible());
            assert.equal(await page.getByRole('button', { name: 'Confirm action', exact: true }).count(), 0);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
        }
        assert.deepEqual(errors, []);
        console.log('PASS: assignment review/keyboard confirmation, refreshed acknowledgement and no overflow at 390/1280px. Synthetic UI fixture only.');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
