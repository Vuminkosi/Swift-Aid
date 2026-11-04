const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

async function main() {
    const host = spawn('dotnet', [path.resolve('tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll'), '--urls', 'http://127.0.0.1:0'], {
        cwd: path.resolve('tests/SwiftAid.Ui.TestHost'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }
    });
    let output = '', browser;
    host.stdout.on('data', d => { output += d; }); host.stderr.on('data', d => { output += d; });
    try {
        const deadline = Date.now() + 30000;
        while (!output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)) {
            assert(host.exitCode === null && Date.now() < deadline, output);
            await new Promise(r => setTimeout(r, 100));
        }
        const base = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1];
        browser = await chromium.launch({ headless: true, ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', e => errors.push(e.message));
        for (const width of [1280, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await page.emulateMedia({ colorScheme: width === 390 ? 'dark' : 'light' });
            await page.goto(base + '/case-translation-fixture');
            await page.locator('main[data-interactive="true"]').waitFor();
            await page.getByText('2 synthetic reports.', { exact: true }).waitFor();
            const accept = page.getByRole('button', { name: 'Accept translation', exact: true });
            assert(await accept.isDisabled());
            await page.getByRole('checkbox').check();
            await page.waitForFunction(() => [...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Accept translation' && !b.disabled));
            await accept.focus(); await page.keyboard.press('Enter');
            await page.getByText('Review saved.', { exact: true }).waitFor();
            await page.getByText('Translation accepted by a reviewer.', { exact: true }).waitFor();
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
            await page.getByRole('button', { name: 'readonly', exact: true }).click();
            await page.getByText('Not yet reviewed. Compare all details with the original.', { exact: true }).waitFor();
            assert.equal(await accept.count(), 0);
            await page.getByRole('button', { name: 'conflict', exact: true }).click();
            await page.getByRole('checkbox').check();
            await accept.click();
            await page.getByText('This translation changed. Review the latest version.', { exact: true }).waitFor();
            await page.getByRole('button', { name: 'slow', exact: true }).click();
            await page.getByRole('button', { name: 'denied', exact: true }).click();
            await page.waitForTimeout(1500); // Exceeds this fixture's deliberately stale read.
            assert.equal(await page.getByText('2 synthetic reports.', { exact: true }).count(), 0);
        }
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ passed: true, syntheticFixture: true, widths: [1280, 390], keyboardReview: true, staleReadRejected: true, pageErrors: errors }));
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
