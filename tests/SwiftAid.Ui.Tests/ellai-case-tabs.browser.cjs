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
        browser = await chromium.launch({ headless: true, ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        for (const width of [1280, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await page.emulateMedia({ colorScheme: width === 390 ? "dark" : "light" });
            await page.goto(base + '/case/6d8cbf50-47f6-4b39-9ad8-7c2324401679?keep=value');
            await page.getByRole('heading', { name: 'Case tab navigation synthetic fixture' }).waitFor();
            await page.getByRole('tab', { name: 'Overview', exact: true }).waitFor();
            await page.waitForFunction(() => document.querySelector('[role=tab][aria-selected=true]')?.textContent.includes('Overview'));
            for (const [key, label] of [['overview', 'Overview'], ['history', 'History'], ['follow-ups', 'Follow-ups'], ['assistance', 'Assistance'], ['discussion', 'Discussion'], ['attachments', 'Attachments'], ['observations', 'Observations']]) {
                console.log(`Checking ${width}px ${key}`);
                const trigger = page.getByRole('button', { name: `Select ${key} through route`, exact: true });
                if (key === 'observations') { await trigger.press('Enter'); }
                else await trigger.click();
                await page.waitForFunction(key => new URL(location.href).searchParams.get('tab') === key, key);
                await page.waitForFunction(label => Array.from(document.querySelectorAll('[role=tab]')).some(tab => tab.textContent.includes(label) && tab.getAttribute('aria-selected') === 'true'), label);
                assert(new URL(page.url()).searchParams.get('keep') === 'value');
                assert(new URL(page.url()).searchParams.get('tab') === key);
                if (key === 'overview') {
                    for (const label of ['Clinical classification', 'Administrative workflow', 'Operational priority']) {
                        await page.locator('dt').filter({ hasText: label }).waitFor();
                    }
                }
                await page.waitForFunction(async key => {
                    const report = (await import('/_content/SwiftAid.UI/js/ellai-ui-observations.js')).collect();
                    return report.controlStates.some(control => control.controlId === 'case-detail.' + key && control.state === 4);
                }, key);
                const report = await page.evaluate(async () => (await import('/_content/SwiftAid.UI/js/ellai-ui-observations.js')).collect());
                assert(!JSON.stringify(report).includes('SYNTHETIC-PRIVATE'));
                if (key !== 'overview') assert(!report.controlStates.some(control => control.controlId === 'case-detail.summary' && control.state === 4));
            }
            await page.reload();
            await page.waitForFunction(() => Array.from(document.querySelectorAll('button')).some(button => button.textContent.includes('Select overview through route') && !button.disabled));
            await page.waitForFunction(() => Array.from(document.querySelectorAll('[role=tab]')).some(tab => tab.textContent.includes('Observations') && tab.getAttribute('aria-selected') === 'true'));
            await page.getByRole('tab', { name: 'Overview', exact: true }).press('Enter');
            await page.waitForFunction(() => document.querySelector('[role=tab][aria-selected=true]')?.textContent.includes('Overview'));
            assert(new URL(page.url()).searchParams.get('keep') === 'value');
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
        }
        assert.deepEqual(errors, []);
        assert(!output.includes('Unhandled exception'), output);
        console.log('PASS: all seven shared case tabs, route activation, observation readiness, reload, keyboard tab activation, no horizontal overflow and value exclusion at 1280/390px. Synthetic Chromium fixture; no Gateway/provider/device verification.');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
