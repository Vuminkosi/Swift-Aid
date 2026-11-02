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

            await page.goto(`${base}/case-follow-up-fixture?Outcome=true`);
            await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
            await page.getByText('Call reporting worker', { exact: true }).first().click();
            await page.getByLabel('Choose an available action', { exact: true }).click();
            await page.getByRole('option', { name: 'Record follow-up outcome', exact: true }).click();
            await page.getByRole('combobox', { name: 'Follow-up outcome' }).click();
            await page.getByRole('option', { name: 'Another follow-up needed', exact: true }).click();
            await page.getByRole('group', { name: 'Follow-up date' }).waitFor();
            await page.getByRole('group', { name: 'Follow-up time' }).waitFor();
            await page.locator('label').filter({ hasText: 'Time zone' }).waitFor();
            await page.getByRole('combobox', { name: 'Follow-up outcome' }).click();
            await page.getByRole('option', { name: 'Unable to reach', exact: true }).waitFor();
            assert.equal(await page.getByRole('option', { name: 'Referred for review', exact: true }).count(), 0);
            await page.keyboard.press('Escape');
            await page.getByLabel('Follow-up details', { exact: true }).fill('No contact; follow-up remains necessary.');
            await page.getByRole('button', { name: 'Review action', exact: true }).click();
            await page.getByRole('alert').getByText('Select a follow-up date and time.', { exact: true }).waitFor();
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `outcome form overflow at ${width}px`);
            assert.deepEqual(errors, [], `outcome form browser errors at ${width}px`);
        }
        console.log('PASS rendered follow-up assignee validation and outcome reschedule controls/validation at 390px and 1280px');
    } finally {
        await browser?.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
