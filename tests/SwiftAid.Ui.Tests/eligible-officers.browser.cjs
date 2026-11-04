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
            await page.goto(base + '/eligible-officer-fixture');
            await page.getByRole('heading', { name: 'Officer candidate fixture' }).waitFor();
            await page.locator('main[data-fixture-ready="true"]').waitFor();
            const lookup = page.getByRole('button', { name: 'Find eligible officers' });
            await lookup.focus();
            await lookup.press('Enter');
            await page.getByText('Synthetic Officer').waitFor();
            assert.match(await page.locator('main').innerText(), /does not assign or notify/i);
            assert.match(await page.locator('main').innerText(), /first 20/i);
            await page.getByRole('button', { name: 'Change case' }).click();
            await page.getByText('Synthetic Officer').waitFor({ state: 'detached' });
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
            assert.deepEqual(errors, []);
            await page.close();
        }
        process.stdout.write('PASS: candidate lookup, case change, keyboard and layout at desktop/light and mobile/dark.\n');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
