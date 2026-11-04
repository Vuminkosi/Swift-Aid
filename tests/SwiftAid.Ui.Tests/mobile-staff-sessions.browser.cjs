const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

(async () => {
    const host = spawn('dotnet', [path.resolve('tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll'), '--urls', 'http://127.0.0.1:0'], {
        cwd: path.resolve('tests/SwiftAid.Ui.TestHost'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }
    });
    let output = '', browser, page;
    host.stdout.on('data', data => { output += data; });
    host.stderr.on('data', data => { output += data; });
    try {
        const deadline = Date.now() + 30000;
        while (!/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/.test(output)) {
            assert(host.exitCode === null && Date.now() < deadline, output);
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        browser = await chromium.launch({ headless: true });
        page = await browser.newPage({ viewport: { width: 390, height: 844 } });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.goto(output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1] + '/staff-session-manager-fixture');
        await page.waitForSelector('[data-fixture-ready=true]');
        await page.getByRole('button', { name: 'Test mismatched reminder' }).click();
        await page.waitForFunction(() => document.querySelector('#reminder-result').textContent === 'error');
        assert.equal(await page.locator('#reminder-result').textContent(), 'error');
        assert.equal(await page.locator('#reminder-mutations').textContent(), '0');
        await page.getByRole('button', { name: 'Test valid reminder' }).click();
        await page.waitForFunction(() => document.querySelector('#reminder-result').textContent === 'Active');
        assert.equal(await page.locator('#reminder-result').textContent(), 'Active');
        assert.equal(await page.locator('#reminder-mutations').textContent(), '1');
        if (process.env.SWIFTAID_REMINDER_ONLY === '1') {
            console.log('PASS: staff reminder rejects mismatched consent and accepts current matching options');
            return;
        }
        assert.equal(await page.getByRole('link', { name: 'Manage sessions' }).getAttribute('href'), '/sessions/manage');
        await page.getByRole('button', { name: 'Schedule Session', exact: true }).click();
        const title = page.getByLabel('Title', { exact: true });
        await title.fill('Unfinished owner session');
        await page.getByLabel('Reason for this change', { exact: true }).fill('Unfinished owner reason');
        await page.getByRole('button', { name: 'Switch account' }).evaluate(button => button.click());
        await page.locator('input[value="Unfinished owner session"]').waitFor({ state: 'detached' });
        assert.equal(await page.locator('input[value="Unfinished owner session"]').count(), 0);
        await page.getByRole('button', { name: 'Schedule Session', exact: true }).click();
        await page.getByText('Restore or explicitly discard the retained session entries', { exact: false }).waitFor();
        assert.equal(await page.getByRole('region', { name: 'Community session editor' }).count(), 0);
        await page.getByRole('button', { name: 'Return owner' }).click();
        await page.getByRole('button', { name: 'Check and restore', exact: true }).waitFor();
        await page.getByRole('button', { name: 'Schedule Session', exact: true }).click();
        await page.getByText('Restore or explicitly discard the retained session entries', { exact: false }).waitFor();
        assert.equal(await page.getByRole('region', { name: 'Community session editor' }).count(), 0);
        await page.getByRole('button', { name: 'Check and restore', exact: true }).click();
        await page.waitForFunction(() => [...document.querySelectorAll('input')].some(input => input.value === 'Unfinished owner session'));
        assert.equal(await title.inputValue(), 'Unfinished owner session');
        assert.equal(await page.getByLabel('Reason for this change', { exact: true }).inputValue(), 'Unfinished owner reason');
        const editor = page.getByRole('region', { name: 'Community session editor' });
        const fillDraft = async (target, scopedEditor) => {
            await scopedEditor.getByLabel('Organization', { exact: true }).click();
            await target.getByRole('option', { name: 'fixture-org', exact: true }).click();
            for (const [label, value] of [['Presenter', 'Synthetic presenter'], ['Public summary', 'Synthetic public summary'],
                ['Who should attend', 'Community members'], ['Country code', 'SZ'], ['Region', 'Example region'],
                ['Sector', 'Human'], ['Language', 'English'], ['Time zone', 'UTC']]) {
                const input = scopedEditor.getByLabel(label, { exact: true });
                await input.fill(value);
                await input.press('Tab');
                await target.waitForTimeout(200);
            }
            await scopedEditor.getByLabel('Start date and time', { exact: true }).fill('2030-12-31T12:34');
            await scopedEditor.getByRole('button', { name: 'Add material', exact: true }).click();
            await scopedEditor.getByLabel('Material', { exact: true }).fill('synthetic-card');
            await scopedEditor.getByLabel('Material', { exact: true }).press('Tab');
            await target.waitForTimeout(200);
            await scopedEditor.getByLabel('Source version', { exact: true }).fill('v1');
            await scopedEditor.getByLabel('Source version', { exact: true }).press('Tab');
            await target.waitForTimeout(200);
            await scopedEditor.getByLabel('Source and reuse-rights evidence', { exact: true }).fill('Synthetic rights');
            await scopedEditor.getByLabel('Source and reuse-rights evidence', { exact: true }).press('Tab');
            await target.waitForTimeout(200);
        };
        await fillDraft(page, editor);
        await page.getByRole('button', { name: 'Lose next create response' }).click();
        await editor.getByRole('button', { name: 'Save draft', exact: true }).click();
        await page.getByText('The new session may have been saved', { exact: false }).waitFor();
        await page.getByRole('button', { name: 'Check session', exact: true }).click();
        await page.getByText('The session was found.', { exact: false }).waitFor();
        await page.getByRole('button', { name: 'Refresh create count' }).click();
        await page.waitForFunction(() => document.querySelector('#fixture-create-count').textContent === '1');
        await page.getByRole('button', { name: 'Discard retained entries…' }).click();
        await page.getByRole('button', { name: 'Yes, discard' }).click();
        const editing = page.getByRole('region', { name: 'Community session editor' });
        await editing.getByLabel('Title', { exact: true }).fill('<Proposal>');
        await editing.getByLabel('Reason for this change', { exact: true }).fill('Synthetic update reason');
        await page.getByRole('button', { name: 'Conflict next update' }).click();
        await editing.getByRole('button', { name: 'Save changes', exact: true }).click();
        await page.getByText('Your proposal: <Proposal>', { exact: true }).waitFor();
        await page.getByText('Server revision', { exact: false }).waitFor();
        assert.equal(await page.locator('proposal').count(), 0, 'proposal content must render as text');
        for (const width of [320, 390, 768, 1024]) {
            await page.setViewportSize({ width, height: 844 });
            await page.emulateMedia({ colorScheme: width === 390 ? 'dark' : 'light' });
            await page.waitForFunction(() => ![...document.querySelectorAll('table')].some(table => table.getClientRects().length > 0));
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `overflow at ${width}px`);
        }
        await page.close();
        const second = await browser.newPage({ viewport: { width: 390, height: 844 } });
        page = second;
        second.on('pageerror', error => errors.push(error.message));
        await second.goto(output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1] + '/staff-session-manager-fixture');
        await second.waitForSelector('[data-fixture-ready=true]');
        await second.getByRole('button', { name: 'Schedule Session', exact: true }).click();
        const secondEditor = second.getByRole('region', { name: 'Community session editor' });
        await secondEditor.getByLabel('Title', { exact: true }).fill('Synthetic scheduled session');
        await secondEditor.getByLabel('Title', { exact: true }).press('Tab');
        await secondEditor.getByLabel('Reason for this change', { exact: true }).fill('Plan session');
        await secondEditor.getByLabel('Reason for this change', { exact: true }).press('Tab');
        await fillDraft(second, secondEditor);
        await secondEditor.getByRole('button', { name: 'Save draft', exact: true }).click();
        await second.getByText('Status: Draft', { exact: false }).waitFor();
        await secondEditor.getByLabel('Reason for this change', { exact: true }).fill('Review session');
        await secondEditor.getByRole('checkbox', { name: 'I planned this draft and I have reviewed it for publication.' }).check();
        await secondEditor.getByRole('button', { name: 'Approve materials and record review' }).click();
        await second.getByText('Status: Reviewed', { exact: false }).waitFor();
        await secondEditor.getByLabel('Reason for this change', { exact: true }).fill('Publish session');
        await secondEditor.getByRole('button', { name: 'Publish schedule' }).click();
        await second.getByText('Status: Scheduled', { exact: false }).waitFor();
        await second.close();
        const third = await browser.newPage({ viewport: { width: 390, height: 844 } });
        page = third;
        third.on('pageerror', error => errors.push(error.message));
        await third.goto(output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1] + '/staff-session-manager-fixture');
        await third.waitForSelector('[data-fixture-ready=true]');
        await third.getByRole('button', { name: 'Schedule Session', exact: true }).click();
        const pendingEditor = third.getByRole('region', { name: 'Community session editor' });
        await pendingEditor.getByLabel('Title', { exact: true }).fill('In-flight synthetic session');
        await pendingEditor.getByLabel('Title', { exact: true }).press('Tab');
        await pendingEditor.getByLabel('Reason for this change', { exact: true }).fill('Plan session');
        await pendingEditor.getByLabel('Reason for this change', { exact: true }).press('Tab');
        await fillDraft(third, pendingEditor);
        await third.getByRole('button', { name: 'Pause next create' }).click();
        await pendingEditor.getByRole('button', { name: 'Save draft', exact: true }).click();
        await third.waitForFunction(() => [...document.querySelectorAll('button')].some(button => button.textContent.includes('Save draft') && button.disabled));
        await third.getByRole('button', { name: 'Switch account' }).click();
        await third.locator('input[value="In-flight synthetic session"]').waitFor({ state: 'detached' });
        await third.getByRole('button', { name: 'Release create' }).click();
        await third.getByRole('button', { name: 'Return owner' }).click();
        await third.getByText('The new session may have been saved', { exact: false }).waitFor();
        await third.getByRole('button', { name: 'Check session', exact: true }).click();
        await third.getByText('The session was found.', { exact: false }).waitFor();
        await third.getByRole('button', { name: 'Refresh create count' }).click();
        await third.waitForFunction(() => document.querySelector('#fixture-create-count').textContent === '1');
        await third.close();
        assert.deepEqual(errors, []);
        console.log('PASS: mobile editor protects retained entries, recovers draft outcomes, shows conflicts, and reviews and schedules a synthetic session');
    } catch (error) {
        if (page) console.error('Fixture body:', await page.locator('main').innerText());
        console.error('Test host output:', output.slice(-4000));
        throw error;
    } finally {
        await browser?.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
