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
            await page.goto(base + '/officer-escalation-review-fixture');
            await page.locator('main[data-fixture-ready="true"]').waitFor();
            await page.getByRole('button', { name: 'Load available escalations and officers' }).click();
            await page.getByRole('combobox', { name: 'Raised escalation' }).click();
            await page.getByRole('option', { name: /^Raised / }).click();
            await page.getByRole('combobox', { name: 'Eligible officer' }).click();
            await page.getByRole('option', { name: /Synthetic Officer/ }).click();
            await page.getByLabel('Acknowledgment deadline (UTC)').fill('2099-11-04 10:00');
            await page.getByLabel('Follow-up deadline (UTC)').fill('2099-11-05 10:00');
            await page.getByRole('button', { name: 'Review assignment' }).click();
            await page.getByRole('group', { name: 'Review officer escalation assignment' }).waitFor();
            assert.equal(await page.locator('#approvals').innerText(), '0');
            assert.equal(await page.locator('#assignments').innerText(), '0');
            await page.getByRole('button', { name: 'Approve reviewed assignment' }).press('Enter');
            await page.getByRole('button', { name: 'Retry approval' }).waitFor();
            await page.getByRole('button', { name: 'Retry approval' }).click();
            await page.getByText('Approval recorded. It expires after 15 minutes', { exact: false }).waitFor();
            assert.equal(await page.locator('#assignments').innerText(), '0');
            await page.getByRole('button', { name: 'Assign approved officer' }).click();
            await page.getByRole('button', { name: 'Retry assignment' }).waitFor();
            await page.getByRole('button', { name: 'Retry assignment' }).click();
            await page.getByText('Officer assignment and follow-up recorded.', { exact: false }).waitFor();
            assert.equal(await page.locator('#approvals').innerText(), '2');
            assert.equal(await page.locator('#assignments').innerText(), '2');
            assert.equal((await page.locator('#refreshed').innerText()).toLowerCase(), 'true');
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
            assert.deepEqual(errors, []);
            await page.close();
        }
        process.stdout.write('PASS: explicit review, approval, assignment and safe retries at desktop/light and mobile/dark.\n');
    } finally { if (browser) await browser.close(); host.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
