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
            await page.goto(`${base}/case-follow-up-fixture?Schedule=true`);
            await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
            await page.getByText('Call reporting worker', { exact: true }).first().click();
            await page.getByLabel('Choose an available action', { exact: true }).click();
            await page.getByRole('option', { name: 'Schedule follow-up', exact: true }).click();
            await page.getByLabel('What this follow-up is for', { exact: true }).fill('Record the next reported observation.');
            await page.getByRole('button', { name: 'Review action', exact: true }).click();
            await page.getByRole('alert').getByText('Choose an eligible staff member before reviewing the schedule.', { exact: true }).waitFor();

            await page.getByLabel('Responsible staff member', { exact: true }).click();
            await page.getByRole('option', { name: 'Synthetic colleague', exact: true }).click();
            await page.getByRole('button', { name: 'Review action', exact: true }).click();
            await page.getByRole('alert').getByText('Select a follow-up date and time.', { exact: true }).waitFor();
            assert(await page.getByLabel('Responsible staff member', { exact: true }).getByText('Synthetic colleague', { exact: true }).isVisible());
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `horizontal overflow at ${width}px`);
            assert.deepEqual(errors, [], `browser errors at ${width}px`);
        }
        console.log('PASS rendered follow-up assignee selection and required-recipient/date validation at 390px and 1280px');
    } finally {
        await browser?.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
