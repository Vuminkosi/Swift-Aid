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
        const page = await browser.newPage({ timezoneId: 'America/New_York' });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        for (const width of [390, 1280]) {
            await page.setViewportSize({ width, height: 900 });
            await page.goto(`${base}/collaborator-fixture?Grant=true`);
            await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
            assert.equal(await page.evaluate(async () => {
                const expiry = await import('/_content/SwiftAid.UI/js/collaborator-expiry.js');
                return expiry.toUtc('2030-03-10T02:30');
            }), null, 'A nonexistent daylight-saving local time must be rejected');
            await page.getByRole('button', { name: 'Add collaborator', exact: true }).press('Enter');
            await page.getByLabel('Eligible colleague', { exact: true }).selectOption('candidate-1');
            await page.getByLabel('Manage follow-ups', { exact: true }).check();
            assert(await page.getByLabel('Read case', { exact: true }).isDisabled());
            await page.getByLabel('Access expires (local time)', { exact: true }).fill('2030-12-01T10:30');
            await page.getByLabel('Reason for access', { exact: false }).fill('Synthetic grant reason');
            await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Review grant' && !button.disabled));
            await page.getByRole('button', { name: 'Review grant', exact: true }).press('Enter');
            await page.getByText('Review access for Synthetic colleague', { exact: true }).waitFor();
            assert(await page.getByText('Expires 2030-12-01 15:30:00Z (UTC)', { exact: true }).isVisible());
            await page.getByRole('button', { name: 'Confirm grant', exact: true }).press('Enter');
            await page.getByText('The result is not confirmed. Retry sends the same grant request.', { exact: true }).waitFor();
            assert(await page.getByRole('button', { name: 'Back to edit', exact: true }).isDisabled());
            assert(await page.getByRole('button', { name: 'Refresh access', exact: true }).isDisabled());
            await page.getByRole('button', { name: 'Retry grant', exact: true }).press('Enter');
            await page.getByText('Collaborator access granted.', { exact: true }).waitFor();
            assert(await page.getByText('Synthetic colleague', { exact: true }).isVisible());
            assert(!(await page.locator('body').innerText()).includes('private-recipient-id'));
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
            // Revoke the grant just created, using the refreshed aggregate version.
            assert(await page.getByText('Synthetic colleague', { exact: true }).isVisible());
            assert(!(await page.locator('body').innerText()).includes('private-recipient-id'));
            await page.getByRole('button', { name: 'Review revocation', exact: true }).press('Enter');
            const reason = page.getByLabel('Reason for revocation', { exact: false });
            await reason.waitFor();
            assert(await page.getByRole('button', { name: 'Confirm revocation', exact: true }).isDisabled());
            await reason.fill('Synthetic reason');
            await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Confirm revocation' && !button.disabled));
            await page.getByRole('button', { name: 'Confirm revocation', exact: true }).press('Enter');
            await page.getByText('The result is not confirmed. Retry sends the same revocation request.', { exact: true }).waitFor();
            assert(await reason.isDisabled());
            assert(await page.getByRole('button', { name: 'Cancel', exact: true }).isDisabled());
            await page.getByRole('button', { name: 'Retry revocation', exact: true }).press('Enter');
            await page.getByText('Collaborator grant revoked.', { exact: true }).waitFor();
            assert(await page.getByText('Revoked', { exact: true }).isVisible());
            assert.equal(await page.getByRole('button', { name: 'Review revocation', exact: true }).count(), 0);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
        }
        for (const terminal of ['Conflict', 'Invalid', 'Denied']) {
            await page.goto(`${base}/collaborator-fixture?Grant=true&Terminal=${terminal}`);
            await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
            await page.getByRole('button', { name: 'Add collaborator', exact: true }).press('Enter');
            await page.getByLabel('Eligible colleague', { exact: true }).selectOption('candidate-1');
            await page.getByLabel('Manage follow-ups', { exact: true }).check();
            await page.getByLabel('Access expires (local time)', { exact: true }).fill('2030-12-01T10:30');
            await page.getByLabel('Reason for access', { exact: false }).fill('Synthetic grant reason');
            await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Review grant' && !button.disabled));
            await page.getByRole('button', { name: 'Review grant', exact: true }).press('Enter');
            await page.getByRole('button', { name: 'Confirm grant', exact: true }).press('Enter');
            await page.getByText('The grant was not accepted. Review the refreshed access details before trying again.', { exact: true }).waitFor();
            assert.equal(await page.getByRole('button', { name: 'Retry grant', exact: true }).count(), 0);
            assert.equal(await page.getByText('Collaborator access granted.', { exact: true }).count(), 0);
            assert.equal(await page.getByText('Synthetic colleague', { exact: true }).count(), 0);
            if (terminal === 'Denied') {
                assert.equal(await page.getByRole('button', { name: 'Add collaborator', exact: true }).count(), 0);
            } else {
                assert(await page.getByRole('button', { name: 'Add collaborator', exact: true }).isVisible());
                assert(await page.getByRole('button', { name: 'Refresh access', exact: true }).isEnabled());
                await page.getByRole('button', { name: 'Add collaborator', exact: true }).press('Enter');
                await page.getByLabel('Eligible colleague', { exact: true }).selectOption('candidate-1');
                await page.getByLabel('Manage follow-ups', { exact: true }).check();
                await page.getByLabel('Access expires (local time)', { exact: true }).fill('2030-12-01T10:30');
                await page.getByLabel('Reason for access', { exact: false }).fill('Synthetic grant reason');
                await page.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent.trim() === 'Review grant' && !button.disabled));
                await page.getByRole('button', { name: 'Review grant', exact: true }).press('Enter');
                await page.getByRole('button', { name: 'Confirm grant', exact: true }).press('Enter');
                await page.getByText('The result is not confirmed. Retry sends the same grant request.', { exact: true }).waitFor();
                await page.getByRole('button', { name: 'Retry grant', exact: true }).press('Enter');
                await page.getByText('Collaborator access granted.', { exact: true }).waitFor();
            }
        }
        await page.goto(`${base}/collaborator-fixture?Denied=true`);
        await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
        assert.equal(await page.getByRole('heading', { name: 'Collaborator access', exact: true }).count(), 0);
        assert.equal(await page.getByRole('button', { name: 'Review revocation', exact: true }).count(), 0);
        assert(!(await page.locator('body').innerText()).includes('Synthetic colleague'));
        assert.deepEqual(errors, []);
        console.log('PASS: grant then revoke at refreshed version; explicit permissions, browser-local expiry, frozen uncertain retries and no overflow at 390/1280px; conflict/invalid fresh-review recovery and permission-loss removal. Synthetic UI fixture.');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
