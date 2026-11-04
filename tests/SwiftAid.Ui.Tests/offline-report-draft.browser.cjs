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
        browser = await chromium.launch({ headless: true });
        const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
        await page.goto(output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1] + '/offline-report-draft-fixture');
        await page.getByRole('button', { name: 'Continue to details', exact: true }).click();
        await page.getByLabel('Describe what happened').fill('Edited while offline');
        await page.getByRole('button', { name: 'Save draft', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('#saved-country').textContent === 'SZ');
        assert.equal(await page.locator('#saved-version').textContent(), 'countries-v1');
        assert.equal(await page.locator('#saved-narrative').textContent(), 'Edited while offline');
        assert.equal((await page.locator('#saved-confirmations').textContent()).toLowerCase(), 'false,false');
        console.log('PASS: offline draft save preserves the saved country and clears stale confirmations');
    } finally {
        await browser?.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
