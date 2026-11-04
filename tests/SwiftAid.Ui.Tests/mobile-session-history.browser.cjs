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
        await page.goto(output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1] + '/mobile-session-history-fixture');
        await page.waitForSelector('[data-fixture-ready=true]');
        await page.getByRole('button', { name: 'View activity' }).click();
        await page.getByText('synthetic review').waitFor();
        await page.getByRole('button', { name: 'Switch to another account' }).click();
        await page.getByText('synthetic review').waitFor({ state: 'detached' });
        assert.equal(await page.getByRole('button', { name: 'View activity' }).count(), 0);
        await page.getByRole('button', { name: 'Allow owner' }).click();
        await page.getByRole('button', { name: 'View activity' }).click();
        await page.getByText('synthetic review').waitFor();
        await page.getByRole('button', { name: 'Sign out' }).click();
        await page.getByText('synthetic review').waitFor({ state: 'detached' });
        assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
        console.log('PASS: synthetic authorized history appears and clears on account change and sign-out');
    } finally {
        await browser?.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
