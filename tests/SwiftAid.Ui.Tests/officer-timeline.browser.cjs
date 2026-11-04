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
            await page.goto(base + '/officer-timeline-fixture');
            await page.locator('main[data-fixture-ready="true"]').waitFor();
            await page.getByText('Officer escalation follow-up: Synthetic Officer.', { exact: true }).waitFor();
            assert.match(await page.locator('main').innerText(), /Follow-up acknowledged/);
            assert.match(await page.locator('main').innerText(), /Historical assignment/);
            assert.match(await page.locator('main').innerText(), /Originally approved follow-up due/);
            assert.match(await page.locator('main').innerText(), /Overdue officer acknowledgment review recommended/);
            assert.doesNotMatch(await page.locator('main').innerText(), /private-officer/);
            await page.getByRole('combobox', { name: 'Filter history' }).click();
            await page.getByRole('option', { name: 'Escalations', exact: true }).click();
            assert.equal(await page.locator('.case-timeline__officer-assignment').count(), 2);
            await page.getByRole('combobox', { name: 'Filter history' }).click();
            await page.getByRole('option', { name: 'Ellai follow-ups', exact: true }).click();
            await page.getByText('Overdue officer acknowledgment review recommended', { exact: false }).waitFor();
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
            assert.deepEqual(errors, []);
            await page.close();
        }
        process.stdout.write('PASS: authorized officer history, status and escalation filtering at desktop/light and mobile/dark.\n');
    } finally { if (browser) await browser.close(); host.kill(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
