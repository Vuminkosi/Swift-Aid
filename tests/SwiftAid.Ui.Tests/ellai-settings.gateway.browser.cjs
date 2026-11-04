const assert = require('node:assert/strict');
const { chromium } = require('playwright');

// Uses an already authenticated test page; never acquires or logs credentials.
async function main() {
    const browser = await chromium.connectOverCDP(process.env.SWIFTAID_TEST_CDP || 'http://127.0.0.1:19226');
    const page = browser.contexts().flatMap(context => context.pages())
        .find(candidate => candidate.url().startsWith('http://localhost:8080/') && !candidate.url().includes('sign-in'));
    assert.ok(page, 'An authenticated localhost test page is required.');
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto('http://localhost:8080/ellai/about');
    await page.getByRole('heading', { name: 'Languages', exact: true }).waitFor();
    const closeComposer = page.getByRole('button', { name: 'Close Ellai composer', exact: true });
    if (await closeComposer.isVisible()) await closeComposer.click();
    const input = page.getByRole('combobox', { name: 'Your input language', exact: true });
    const output = page.getByRole('combobox', { name: "Ellai's reply language", exact: true });
    const previous = await page.evaluate(() => localStorage.getItem('swiftaid.ellai.languages.v1'));
    async function choose(control, label) {
        await control.click();
        await page.getByRole('option', { name: label, exact: true }).click();
    }
    try {
        await choose(input, 'Shona');
        await page.waitForFunction(() => JSON.parse(localStorage.getItem('swiftaid.ellai.languages.v1'))?.inputLanguage === 'sna');
        await choose(output, 'siSwati');
        await page.waitForFunction(() => JSON.parse(localStorage.getItem('swiftaid.ellai.languages.v1'))?.outputLanguage === 'ssw');
        await page.reload();
        await input.waitFor();
        await page.waitForFunction(() => {
            const saved = JSON.parse(localStorage.getItem('swiftaid.ellai.languages.v1'));
            return saved.inputLanguage === 'sna' && saved.outputLanguage === 'ssw'
                && [...document.querySelectorAll('[role="combobox"][aria-label="Your input language"]')].some(node => node.textContent.includes('Shona'))
                && [...document.querySelectorAll('[role="combobox"][aria-label="Ellai\'s reply language"]')].some(node => node.textContent.includes('siSwati'));
        });
        assert.match(await input.innerText(), /Shona/);
        assert.match(await output.innerText(), /siSwati/);
        if (await closeComposer.isVisible()) await closeComposer.click();
        await page.getByRole('tab', { name: 'Session history' }).click();
        await page.getByRole('heading', { name: 'Your conversations', exact: true }).waitFor();
        const session = page.locator('.ellai-settings__session').first();
        await session.locator('.ellai-history__body h4').waitFor();
        assert.ok((await session.locator('h4').innerText()).length > 0);
        assert.ok((await session.locator('time').getAttribute('datetime')).length > 0);
        const savedConversation = page.locator('.ellai-settings__session').filter({
            has: page.locator('.ellai-history__metadata > span').filter({ hasText: /^[1-9]\d* messages? sent$/ })
        }).first()
            .getByRole('button', { name: 'View conversation', exact: true });
        await savedConversation.click();
        const dialog = page.getByRole('dialog');
        await dialog.waitFor();
        assert.match(await dialog.getAttribute('class'), /mud-dialog-width-md/);
        await dialog.locator('.mudx-chat-bubble').first().waitFor();
        assert.ok(await dialog.locator('.mudx-chat-bubble').count() >= 2);
        await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
        await page.setViewportSize({ width: 390, height: 844 });
        await page.waitForTimeout(500);
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
        await savedConversation.click();
        await dialog.waitFor();
        assert.match(await dialog.getAttribute('class'), /mud-dialog-fullscreen/);
        await dialog.getByRole('button', { name: 'Close', exact: true }).last().click();
        console.log(JSON.stringify({ languagesPersistAfterReload: true, desktopMediumChat: true, mobileViewportFullScreenChat: true,
            compactHistory: true, nativeDeviceTested: false }));
    } finally {
        await page.evaluate(value => {
            if (value === null) localStorage.removeItem('swiftaid.ellai.languages.v1');
            else localStorage.setItem('swiftaid.ellai.languages.v1', value);
        }, previous);
        await page.setViewportSize({ width: 1440, height: 1000 });
        await page.reload();
    }
}

main().then(() => process.exit(0)).catch(error => { console.error(error.stack); process.exit(1); });
