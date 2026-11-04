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
        const url = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1] + '/session-editor-working-copy-fixture';
        browser = await chromium.launch({ headless: true });
        const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
        for (const scenario of ['same', 'wrong version', 'wrong actor', 'wrong session']) {
            await page.goto(url);
            await page.waitForSelector('[data-fixture-ready=true]');
            await page.getByRole('button', { name: 'Add material', exact: true }).click();
            await page.getByLabel('Start date and time', { exact: true }).fill('2026-12-31T12:34');
            for (const [label, value] of [['Title', 'Unfinished synthetic title'], ['Public summary', 'Unfinished summary'],
                ['Country code', 'Z'], ['Material', 'source-id'], ['Source version', 'v1'],
                ['Source and reuse-rights evidence', 'Unfinished rights note'], ['Reason for this change', 'Unfinished reason']]) {
                const input = page.getByLabel(label, { exact: true });
                await input.fill(value);
                if (label !== 'Reason for this change') await input.press('Tab');
            }
            // A session-change event can suspend the editor without blurring its focused field.
            await page.getByRole('button', { name: 'Suspend editor' }).evaluate(button => button.click());
            await page.getByRole('button', { name: scenario === 'same' ? 'Restore editor' : `Restore ${scenario}` }).click();
            assert.equal(await page.getByLabel('Title', { exact: true }).inputValue(), scenario === 'same' ? 'Unfinished synthetic title' : '');
            if (scenario === 'same') {
                assert.equal(await page.getByLabel('Country code', { exact: true }).inputValue(), 'Z');
                assert.equal(await page.getByLabel('Reason for this change', { exact: true }).inputValue(), 'Unfinished reason');
                assert.equal(await page.getByLabel('Material', { exact: true }).inputValue(), 'source-id');
                assert.equal(await page.getByLabel('Source and reuse-rights evidence', { exact: true }).inputValue(), 'Unfinished rights note');
                assert((await page.getByLabel('Start date and time', { exact: true }).inputValue()).startsWith('2026-12-31T12:34'));
                await page.getByText('You have unsaved changes.', { exact: false }).waitFor();
            }
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
        }
        console.log('PASS: incomplete session fields restore only for the same actor, session and version');
    } finally {
        await browser?.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
