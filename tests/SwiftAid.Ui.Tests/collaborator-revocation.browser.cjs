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
            await page.goto(`${base}/collaborator-fixture`);
            await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
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
        await page.goto(`${base}/collaborator-fixture?Denied=true`);
        await page.waitForFunction(() => document.querySelector('#fixture-ready')?.textContent === 'True');
        assert.equal(await page.getByRole('heading', { name: 'Collaborator access', exact: true }).count(), 0);
        assert.equal(await page.getByRole('button', { name: 'Review revocation', exact: true }).count(), 0);
        assert(!(await page.locator('body').innerText()).includes('Synthetic colleague'));
        assert.deepEqual(errors, []);
        console.log('PASS: collaborator review, required reason, keyboard confirmation, frozen uncertain retry, refreshed revocation and no overflow at 390/1280px. Synthetic UI fixture.');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
