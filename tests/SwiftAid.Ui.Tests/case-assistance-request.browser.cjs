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
            await page.goto(`${base}/case-assistance-fixture`);
            await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
            await page.getByRole('tab', { name: 'Assistance', exact: true }).click();
            await page.getByRole('status', { name: 'Referral directory status' }).getByText('Referral contact not configured.', { exact: true }).waitFor();
            await page.getByRole('button', { name: 'Request assistance', exact: true }).press('Enter');
            await page.getByRole('button', { name: 'Submit request', exact: true }).click();
            await page.getByRole('alert').getByText('Select the support type and describe the requested assistance.', { exact: true }).waitFor();
            assert.equal(await page.locator('#request-result').innerText(), 'Pending', 'invalid request must not be submitted');

            await page.getByLabel('Assistance need', { exact: true }).click();
            await page.getByRole('option', { name: 'Transport', exact: true }).click();
            await page.getByLabel('Describe the requested support', { exact: true }).fill('Synthetic transport coordination');
            await page.getByRole('button', { name: 'Submit request', exact: true }).click();
            await page.getByLabel('Selected work details').getByText('Transport: Synthetic transport coordination', { exact: true }).waitFor();
            assert.equal(await page.locator('#request-result').innerText(), 'Accepted');
            assert(await page.getByLabel('Selected work details').getByText('Requested', { exact: true }).isVisible());
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `horizontal overflow at ${width}px`);
            assert.deepEqual(errors, [], `browser errors at ${width}px`);
        }
        for (const width of [390, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            await page.goto(`${base}/case-assistance-receipt-fixture?Receipt=true`);
            await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
            await page.getByRole('tab', { name: 'Assistance', exact: true }).click();
            await page.getByRole('status', { name: 'Referral directory status' }).getByText('Referral contact not configured.', { exact: true }).waitFor();
            await page.getByLabel('Choose an available action', { exact: true }).click();
            await page.getByRole('option', { name: 'Confirm assistance received', exact: true }).click();
            await page.getByLabel('Describe the receipt confirmation', { exact: true }).fill('Recipient confirmed the support arrived');
            await page.getByRole('button', { name: 'Review action', exact: true }).click();
            await page.getByRole('alert').getByText('Confirm that the assistance was received.', { exact: true }).waitFor();
            assert.equal(await page.locator('#request-result').innerText(), 'Provided', 'receipt must remain unconfirmed');

            await page.getByLabel('I confirm the assistance was received', { exact: true }).check();
            await page.getByRole('button', { name: 'Review action', exact: true }).click();
            await page.getByRole('alert').getByText('Record how receipt was confirmed and the context of that confirmation.', { exact: true }).waitFor();
            await page.getByLabel('How was receipt confirmed?', { exact: true }).fill('Direct confirmation');
            await page.getByLabel('Receipt confirmation context', { exact: true }).fill('Synthetic follow-up conversation');
            await page.getByRole('button', { name: 'Review action', exact: true }).click();
            await page.getByRole('button', { name: 'Confirm action', exact: true }).click();
            await page.getByText('ReceiptConfirmed', { exact: true }).waitFor();
            assert.equal(await page.locator('#request-result').innerText(), 'ReceiptConfirmed');
            assert(await page.getByLabel('Selected work details').getByText('Receipt confirmed', { exact: true }).isVisible());
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `receipt horizontal overflow at ${width}px`);
            assert.deepEqual(errors, [], `receipt browser errors at ${width}px`);
        }
        for (const width of [390, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            await page.goto(`${base}/case-assistance-fixture?Unfulfilled=true`);
            await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
            await page.getByRole('tab', { name: 'Assistance', exact: true }).click();
            await page.getByRole('status', { name: 'Referral directory status' }).getByText('Referral contact not configured.', { exact: true }).waitFor();
            const details = page.getByLabel('Selected work details');
            await details.getByText('Assistance not fulfilled', { exact: true }).waitFor();
            assert(await details.getByText('Transport support - Unfulfilled: No eligible provider was available', { exact: true }).isVisible());
            await details.getByText('Actions are unavailable for this item.', { exact: true }).waitFor();
            assert.equal(await page.locator('#request-result').innerText(), 'Unfulfilled');
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `unfulfilled horizontal overflow at ${width}px`);
            assert.deepEqual(errors, [], `unfulfilled browser errors at ${width}px`);
        }
        console.log('PASS synthetic assistance request, receipt, and unmet-need visibility at 390px and 1280px');
    } finally {
        await browser?.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
