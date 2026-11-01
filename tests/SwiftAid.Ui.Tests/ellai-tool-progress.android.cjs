const assert = require("node:assert/strict");
const { chromium } = require("playwright");

async function main() {
    assert.equal(process.env.SWIFTAID_RUN_LIVE_ELLAI, "1");
    const browser = await chromium.connectOverCDP(process.env.SWIFTAID_ANDROID_CDP ?? "http://127.0.0.1:9223");
    const page = browser.contexts().flatMap(context => context.pages()).find(item => item.url().startsWith("https://0.0.0.1"));
    assert.ok(page, "An installed MAUI WebView must be running.");
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    try {
        if (!await page.locator(".ellai-composer").count()) {
            await page.locator(".ellai-launcher-menu__button").click();
            await page.getByRole("menuitem", { name: "Ask", exact: true }).click();
        }
        await page.waitForFunction(() => document.querySelector(".ellai-composer .ellai-status__text")?.textContent.trim() === "Ready");
        const listen = page.getByRole("button", { name: "Listen to Ellai", exact: true });
        const enabled = page.getByRole("button", { name: "Turn off Ellai voice", exact: true });
        if (!await listen.count() && !await enabled.count()) {
            await page.getByRole("textbox", { name: "Message Ellai" }).fill("Hello. Reply with one short greeting.");
            await page.getByRole("button", { name: "Send to Ellai", exact: true }).click();
            await listen.waitFor({ timeout: 120000 });
        }
        if (await listen.count()) await listen.click();
        assert.equal(await enabled.getAttribute("aria-pressed"), "true");
        await page.evaluate(() => {
            window.ellaiMobileStatuses = [];
            const status = document.querySelector(".ellai-composer .ellai-status__text");
            window.ellaiMobileObserver = new MutationObserver(() => window.ellaiMobileStatuses.push(status.textContent.trim()));
            window.ellaiMobileObserver.observe(status, { subtree: true, childList: true, characterData: true });
        });
        const question = "Check the verified SwiftAid product facts and tell me how a guest can submit a report. Keep the answer short.";
        await page.getByRole("textbox", { name: "Message Ellai" }).fill(question);
        const start = Date.now();
        await page.getByRole("button", { name: "Send to Ellai", exact: true }).click();
        const thinking = page.locator(".ellai-status__thinking");
        await thinking.waitFor();
        const style = await thinking.evaluate(node => ({ color: getComputedStyle(node).color,
            weight: getComputedStyle(node.querySelector(".ellai-status__dot")).fontWeight }));
        assert.ok(["rgb(124, 58, 237)", "rgb(196, 161, 255)"].includes(style.color));
        assert.equal(style.weight, "900");
        await page.waitForFunction(() => document.querySelector(".ellai-composer__response")?.textContent.trim()
            && document.querySelector(".ellai-composer .ellai-status__text")?.textContent.trim() === "Ready",
            null, { timeout: 150000 });
        const statuses = await page.evaluate(() => [...new Set(window.ellaiMobileStatuses)]);
        assert.ok(statuses.some(status => /Checking|Reviewing/.test(status)), "Real operation progress must appear.");
        assert.deepEqual(errors, []);
        assert.equal(await enabled.getAttribute("aria-pressed"), "true");
        console.log(JSON.stringify({ result: "PASS", client: "Android emulator", elapsedMs: Date.now() - start,
            statuses, thinking: style, listeningEnabled: true,
            responseVisible: true, pageErrors: errors, physicalAudio: "NOT VERIFIED" }));
    } finally {
        await page.evaluate(() => window.ellaiMobileObserver?.disconnect()).catch(() => {});
        await browser.close();
    }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
