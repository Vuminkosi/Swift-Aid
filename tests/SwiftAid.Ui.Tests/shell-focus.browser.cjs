// Focused shell keyboard check for the TestHost /shell-focus route.
// This verifies skip-link behavior only; it does not exercise product workflows.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

const hostDll = path.resolve("tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll");
const hostContentRoot = path.resolve("tests/SwiftAid.Ui.TestHost");
const routePath = "/shell-focus";
const artifactDirectory = path.join(__dirname, "artifacts", "shell-focus-20261028");
fs.mkdirSync(artifactDirectory, { recursive: true });

async function startHost(child) {
    let output = "";
    child.stdout.on("data", chunk => { output += chunk.toString(); });
    child.stderr.on("data", chunk => { output += chunk.toString(); });
    const deadline = Date.now() + 30000;

    while (Date.now() < deadline) {
        if (child.exitCode !== null) {
            throw new Error(`TestHost child PID ${child.pid} exited before readiness (${child.exitCode}).\n${output}`);
        }

        const listener = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/);
        if (listener) {
            const baseUrl = listener[1];
            try {
                const response = await fetch(`${baseUrl}${routePath}`);
                if (response.ok) {
                    const markup = await response.text();
                    if (!markup.includes("swiftaid-app-shell__skip-link") || !markup.includes("shell-focus-main")) {
                        throw new Error(`TestHost PID ${child.pid} did not serve the shell focus fixture at ${baseUrl}${routePath}`);
                    }
                    return { baseUrl, output, childPid: child.pid };
                }
            } catch (error) {
                if (error.message.includes("did not serve the shell focus fixture")) throw error;
            }
        }

        await new Promise(resolve => setTimeout(resolve, 200));
    }

    throw new Error(`Timed out waiting for TestHost child PID ${child.pid}.\n${output}`);
}

async function focusSkipLink(page) {
    await page.goto(`${page.context()._baseUrl}${routePath}`, { waitUntil: "networkidle" });
    const skipLink = page.getByRole("link", { name: "Skip to main content" });
    const main = page.locator("main#shell-focus-main");
    await skipLink.waitFor({ state: "visible" });
    await main.waitFor({ state: "visible" });
    await page.keyboard.press("Tab");
    assert.equal(await skipLink.evaluate(element => element === document.activeElement), true,
        "Skip link should be the first keyboard focus target");
    return { skipLink, main };
}

async function main() {
    const host = spawn("dotnet", [hostDll, "--urls", "http://127.0.0.1:0"], {
        cwd: hostContentRoot,
        stdio: ["ignore", "pipe", "pipe"],
        windowsHide: true,
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" }
    });

    let browser;
    try {
        const hostIdentity = await startHost(host);
        const { baseUrl } = hostIdentity;
        browser = await chromium.launch({ channel: "chrome", headless: true });

        const activeContext = await browser.newContext({ viewport: { width: 390, height: 844 } });
        activeContext._baseUrl = baseUrl;
        const activePage = await activeContext.newPage();
        const activePageErrors = [];
        const activeConsoleErrors = [];
        const activeConsoleErrorLocations = [];
        const activeFailedRequests = [];
        activePage.on("pageerror", error => activePageErrors.push(error.message));
        activePage.on("console", message => {
            if (message.type() === "error") {
                activeConsoleErrors.push(message.text());
                activeConsoleErrorLocations.push({ text: message.text(), location: message.location() });
            }
        });
        activePage.on("requestfailed", request => activeFailedRequests.push({
            url: request.url(),
            error: request.failure()?.errorText
        }));

        const { skipLink: activeSkipLink, main: activeMain } = await focusSkipLink(activePage);
        const activeBefore = await activePage.evaluate(() => ({
            href: location.href,
            pathname: location.pathname,
            search: location.search,
            hash: location.hash
        }));
        await activePage.keyboard.press("Enter");
        await activePage.waitForFunction(() => location.hash === "#shell-focus-main");
        await activePage.waitForFunction(() => document.activeElement?.id === "shell-focus-main");
        const activeTrace = await activePage.evaluate(() => ({
            href: location.href,
            pathname: location.pathname,
            search: location.search,
            hash: location.hash,
            activeElementTag: document.activeElement?.tagName,
            activeElementId: document.activeElement?.id,
            targetTabIndex: document.querySelector("#shell-focus-main")?.getAttribute("tabindex"),
            targetFocusVisible: document.querySelector("#shell-focus-main")?.matches(":focus-visible"),
            targetOutlineStyle: getComputedStyle(document.querySelector("#shell-focus-main")).outlineStyle,
            targetOutlineWidth: getComputedStyle(document.querySelector("#shell-focus-main")).outlineWidth
        }));
        assert.equal(activeTrace.pathname, new URL(activeBefore.href).pathname);
        assert.equal(activeTrace.search, activeBefore.search);
        assert.equal(activeTrace.hash, "#shell-focus-main");
        assert.equal(activeTrace.activeElementTag, "MAIN");
        assert.equal(activeTrace.activeElementId, "shell-focus-main");
        assert.equal(activeTrace.targetFocusVisible, true, "Skip-link target should retain a visible keyboard focus indicator");
        assert.equal(activeTrace.targetOutlineStyle, "solid");
        assert.ok(parseFloat(activeTrace.targetOutlineWidth) >= 3, `Expected a clear focus outline: ${JSON.stringify(activeTrace)}`);
        assert.deepEqual(activePageErrors, [], `Active-JS page errors: ${JSON.stringify(activePageErrors)}`);
        const fixtureFavicon404 = activeConsoleErrorLocations.some(error =>
            error.location.url.endsWith("/favicon.ico") && /404/i.test(error.text));
        const activeAppConsoleErrors = activeConsoleErrorLocations.filter(error =>
            !(error.location.url.endsWith("/favicon.ico") && /404/i.test(error.text)));
        assert.deepEqual(activeAppConsoleErrors, [], `Active-JS console errors: ${JSON.stringify(activeConsoleErrorLocations)}`);
        assert.deepEqual(activeFailedRequests, [], `Active-JS failed requests: ${JSON.stringify(activeFailedRequests)}`);
        const activeScreenshot = path.join(artifactDirectory, "shell-focus-active-js.png");
        await activePage.screenshot({ path: activeScreenshot, fullPage: true });
        await activeContext.close();

        const fallbackContext = await browser.newContext({ viewport: { width: 390, height: 844 } });
        fallbackContext._baseUrl = baseUrl;
        const fallbackPage = await fallbackContext.newPage();
        const fallbackPageErrors = [];
        const fallbackConsoleErrors = [];
        const fallbackFailedRequests = [];
        fallbackPage.on("pageerror", error => fallbackPageErrors.push(error.message));
        fallbackPage.on("console", message => {
            if (message.type() === "error") fallbackConsoleErrors.push(message.text());
        });
        fallbackPage.on("requestfailed", request => fallbackFailedRequests.push({
            url: request.url(),
            error: request.failure()?.errorText
        }));
        let shellModuleRequestFailed = false;
        await fallbackPage.route("**/_content/SwiftAid.UI/js/swiftaid-shell.js", async route => {
            shellModuleRequestFailed = true;
            await route.abort();
        });

        const { skipLink: fallbackSkipLink } = await focusSkipLink(fallbackPage);
        const firstMainLink = fallbackPage.getByTestId("shell-focus-first-main-link");
        await firstMainLink.waitFor({ state: "visible" });
        const fallbackBefore = await fallbackPage.evaluate(() => ({
            href: location.href,
            pathname: location.pathname,
            search: location.search,
            hash: location.hash
        }));
        await fallbackPage.keyboard.press("Enter");
        await fallbackPage.waitForFunction(() => location.hash === "#shell-focus-main");
        const fallbackImmediateAfterEnter = await fallbackPage.evaluate(() => ({
            href: location.href,
            pathname: location.pathname,
            search: location.search,
            hash: location.hash,
            activeElementTag: document.activeElement?.tagName,
            activeElementId: document.activeElement?.id,
            targetPresent: Boolean(document.querySelector("#shell-focus-main")),
            targetTabIndex: document.querySelector("#shell-focus-main")?.getAttribute("tabindex"),
            firstMainLinkHref: document.querySelector('[data-testid="shell-focus-first-main-link"]')?.href ?? null
        }));
        const immediateScreenshot = path.join(artifactDirectory, "shell-focus-module-blocked-after-enter.png");
        await fallbackPage.screenshot({ path: immediateScreenshot, fullPage: true });
        await fallbackPage.keyboard.press("Tab");
        const fallbackAfterTab = await fallbackPage.evaluate(() => {
            const active = document.activeElement;
            const main = document.querySelector("main#shell-focus-main");
            return {
                href: location.href,
                pathname: location.pathname,
                search: location.search,
                hash: location.hash,
                activeElementTag: active?.tagName,
                activeElementId: active?.id,
                activeElementHref: active instanceof HTMLAnchorElement ? active.href : null,
                activeElementInsideMain: Boolean(main?.contains(active)),
                activeElementInsideNavigation: Boolean(active?.closest("nav")),
                firstMainLinkHref: main?.querySelector('[data-testid="shell-focus-first-main-link"]')?.href ?? null
            };
        });
        const fallbackTrace = {
            immediateAfterEnter: fallbackImmediateAfterEnter,
            afterTab: fallbackAfterTab,
            shellModuleRequestFailed
        };
        fallbackTrace.shellModuleRequestFailed = shellModuleRequestFailed;
        fallbackTrace.pageErrors = fallbackPageErrors;
        fallbackTrace.consoleErrors = fallbackConsoleErrors;
        fallbackTrace.failedRequests = fallbackFailedRequests;
        const fallbackScreenshot = path.join(artifactDirectory, "shell-focus-module-blocked.png");
        await fallbackPage.screenshot({ path: fallbackScreenshot, fullPage: true });

        const evidence = {
            route: `${baseUrl}${routePath}`,
            hostPid: hostIdentity.childPid,
            activeJs: { before: activeBefore, result: activeTrace, pageErrors: activePageErrors, consoleErrors: activeConsoleErrorLocations, fixtureFavicon404, appConsoleErrors: activeAppConsoleErrors, failedRequests: activeFailedRequests },
            blockedModule: { before: fallbackBefore, result: fallbackTrace },
            screenshots: [activeScreenshot, immediateScreenshot, fallbackScreenshot]
        };
        const evidencePath = path.join(artifactDirectory, "shell-focus-evidence.json");
        fs.writeFileSync(evidencePath, JSON.stringify(evidence, null, 2));
        console.log(JSON.stringify({ evidencePath, evidence }, null, 2));

        assert.equal(fallbackTrace.immediateAfterEnter.pathname, new URL(fallbackBefore.href).pathname);
        assert.equal(fallbackTrace.immediateAfterEnter.search, fallbackBefore.search);
        assert.equal(fallbackTrace.immediateAfterEnter.hash, "#shell-focus-main");
        assert.equal(fallbackTrace.immediateAfterEnter.targetPresent, true);
        assert.equal(fallbackTrace.afterTab.pathname, new URL(fallbackBefore.href).pathname);
        assert.equal(fallbackTrace.afterTab.search, fallbackBefore.search);
        assert.equal(fallbackTrace.afterTab.hash, "#shell-focus-main");
        assert.equal(fallbackTrace.afterTab.activeElementInsideMain, true,
            `Tab after skip activation did not reach a control inside main: ${JSON.stringify(fallbackTrace)}`);
        assert.equal(fallbackTrace.afterTab.activeElementInsideNavigation, false,
            `Tab after skip activation landed on navigation: ${JSON.stringify(fallbackTrace)}`);
        assert.equal(fallbackTrace.afterTab.activeElementHref, fallbackTrace.afterTab.firstMainLinkHref,
            `Tab after skip activation did not land on the first main link: ${JSON.stringify(fallbackTrace)}`);
        assert.equal(shellModuleRequestFailed, true, "The failure-path run must block the shell module request");
        assert.equal(fallbackFailedRequests.some(request =>
            request.url.endsWith("/_content/SwiftAid.UI/js/swiftaid-shell.js") && request.error === "net::ERR_FAILED"), true,
            `Expected the shell module request to fail in the fallback case: ${JSON.stringify(fallbackFailedRequests)}`);
        assert.deepEqual(fallbackPageErrors, [], `Blocked-module page errors: ${JSON.stringify(fallbackPageErrors)}`);

        const moduleOnlyContext = await browser.newContext({ viewport: { width: 390, height: 844 } });
        moduleOnlyContext._baseUrl = baseUrl;
        const moduleOnlyPage = await moduleOnlyContext.newPage();
        let fallbackRequestBlocked = false;
        await moduleOnlyPage.route("**/_content/SwiftAid.UI/js/swiftaid-skip-link-fallback.js", async route => {
            fallbackRequestBlocked = true;
            await route.abort();
        });

        const { skipLink: moduleOnlySkipLink, main: moduleOnlyMain } = await focusSkipLink(moduleOnlyPage);
        const moduleOnlyBefore = await moduleOnlyPage.evaluate(() => ({
            pathname: location.pathname,
            search: location.search,
            hash: location.hash
        }));
        await moduleOnlyPage.evaluate(async ({ mainId }) => {
            // Isolate the module handler from the classic fallback and Blazor's render timing.
            const moduleUrl = new URL("_content/SwiftAid.UI/js/swiftaid-shell.js", location.href);
            const shellModule = await import(moduleUrl.href);
            shellModule.attachSkipLinkFocus(document.getElementById(mainId));
        }, { mainId: await moduleOnlyMain.getAttribute("id") });

        await moduleOnlySkipLink.focus();
        let actualModuleClick;
        await moduleOnlyPage.evaluate(() => {
            const main = document.querySelector("#shell-focus-main");
            const nativeFocus = main.focus.bind(main);
            window.__moduleFocusCalls = 0;
            window.__restoreMainFocus = () => { main.focus = nativeFocus; };
            window.__skipClickDefaultPrevented = null;
            window.__skipEventTrace = [];
            window.__skipPreventDefaultTrace = [];
            main.focus = options => {
                window.__moduleFocusCalls += 1;
                nativeFocus(options);
            };
            const originalPreventDefault = Event.prototype.preventDefault;
            Event.prototype.preventDefault = function (...args) {
                if (this.type === "click" && this.target instanceof Element &&
                    this.target.closest(".swiftaid-app-shell__skip-link")) {
                    window.__skipPreventDefaultTrace.push({
                        currentTarget: this.currentTarget?.nodeName ?? null,
                        eventPhase: this.eventPhase,
                        stack: new Error().stack
                    });
                }
                return originalPreventDefault.apply(this, args);
            };
            window.__restorePreventDefault = () => { Event.prototype.preventDefault = originalPreventDefault; };
            const record = label => event => window.__skipEventTrace.push({
                label,
                defaultPrevented: event.defaultPrevented,
                eventPhase: event.eventPhase,
                currentTarget: event.currentTarget?.nodeName ?? null
            });
            window.addEventListener("click", record("window-capture"), true);
            document.addEventListener("click", record("document-capture"), true);
            document.querySelector(".swiftaid-app-shell__skip-link").addEventListener("click", record("link-target"));
            document.body.addEventListener("click", record("body-bubble"));
            document.addEventListener("click", event => {
                if (event.target instanceof Element && event.target.closest(".swiftaid-app-shell__skip-link")) {
                    window.__skipClickDefaultPrevented = event.defaultPrevented;
                }
            }, { once: true });
        });
        await moduleOnlySkipLink.click();
        await moduleOnlyPage.waitForTimeout(75);
        actualModuleClick = await moduleOnlyPage.evaluate(() => {
            window.__restorePreventDefault();
            return {
                pathname: location.pathname,
                search: location.search,
                hash: location.hash,
                activeElementId: document.activeElement?.id,
                moduleFocusCalls: window.__moduleFocusCalls,
                defaultPrevented: window.__skipClickDefaultPrevented,
                eventTrace: window.__skipEventTrace,
                preventDefaultTrace: window.__skipPreventDefaultTrace
            };
        });
        console.log("MODULE_ONLY_REAL_CLICK", JSON.stringify(actualModuleClick));
        assert.equal(actualModuleClick.pathname, moduleOnlyBefore.pathname);
        assert.equal(actualModuleClick.search, moduleOnlyBefore.search);
        assert.equal(actualModuleClick.hash, "#shell-focus-main");
        assert.equal(actualModuleClick.activeElementId, "shell-focus-main");
        assert.equal(actualModuleClick.moduleFocusCalls, 1,
            `The module must focus the main target once for a real Playwright click: ${JSON.stringify(actualModuleClick)}`);

        await moduleOnlyPage.evaluate(() => {
            window.__restoreMainFocus();
            const routeChangeLink = document.createElement("a");
            routeChangeLink.id = "module-route-change-link";
            routeChangeLink.href = `${window.location.pathname}?module-route=changed`;
            routeChangeLink.textContent = "Change route query";
            routeChangeLink.style.cssText = "position: fixed; inset: 1rem auto auto 1rem; z-index: 2000;";
            document.body.append(routeChangeLink);
        });
        await moduleOnlyPage.locator("#module-route-change-link").click();
        await moduleOnlyPage.waitForFunction(() => new URL(window.location.href).searchParams.get("module-route") === "changed");
        await moduleOnlyPage.waitForFunction(() => {
            const link = document.querySelector(".swiftaid-app-shell__skip-link");
            const destination = link ? new URL(link.href) : null;
            return destination?.searchParams.get("module-route") === "changed";
        });
        const routeChangedLink = await moduleOnlySkipLink.evaluate(element => element.href);
        const routeChangedDestination = new URL(routeChangedLink);
        assert.equal(routeChangedDestination.pathname, moduleOnlyBefore.pathname);
        assert.equal(routeChangedDestination.searchParams.get("module-route"), "changed",
            "Blazor should update the persistent shell skip link after a location change");

        await moduleOnlyPage.evaluate(() => {
            document.querySelector("#module-route-change-link")?.remove();
            const main = document.querySelector("#shell-focus-main");
            const nativeFocus = main.focus.bind(main);
            window.__moduleFocusCalls = 0;
            window.__restoreMainFocus = () => { main.focus = nativeFocus; };
            main.focus = options => {
                window.__moduleFocusCalls += 1;
                nativeFocus(options);
            };
        });
        await moduleOnlyPage.evaluate(() => document.activeElement?.blur());
        await moduleOnlyPage.keyboard.press("Tab");
        assert.equal(await moduleOnlySkipLink.evaluate(element => element === document.activeElement), true,
            "After a location change, the skip link should remain the first keyboard focus target");
        await moduleOnlySkipLink.click();
        await moduleOnlyPage.waitForFunction(() => window.__moduleFocusCalls > 0);
        const routeChangedClick = await moduleOnlyPage.evaluate(() => ({
            pathname: location.pathname,
            search: location.search,
            hash: location.hash,
            activeElementId: document.activeElement?.id,
            moduleFocusCalls: window.__moduleFocusCalls
        }));
        evidence.moduleOnlyRouteChange = {
            skipLinkHref: routeChangedLink,
            result: routeChangedClick
        };
        fs.writeFileSync(evidencePath, JSON.stringify(evidence, null, 2));
        console.log("MODULE_ONLY_ROUTE_CHANGED_CLICK", JSON.stringify(evidence.moduleOnlyRouteChange));
        assert.equal(routeChangedClick.pathname, moduleOnlyBefore.pathname);
        assert.equal(routeChangedClick.search, "?module-route=changed");
        assert.equal(routeChangedClick.hash, "#shell-focus-main");
        assert.equal(routeChangedClick.activeElementId, "shell-focus-main");
        assert.equal(routeChangedClick.moduleFocusCalls, 1,
            `The persistent module must use the updated route destination once: ${JSON.stringify(routeChangedClick)}`);

        await moduleOnlyPage.keyboard.press("Tab");
        assert.equal(await moduleOnlyPage.locator('[data-testid="shell-focus-first-main-link"]').evaluate(element => element === document.activeElement), true,
            "The repeated-activation case must move focus away from the main landmark first");
        await moduleOnlySkipLink.focus();
        await moduleOnlySkipLink.click();
        await moduleOnlyPage.waitForFunction(() => window.__moduleFocusCalls === 2);
        const repeatedModuleClick = await moduleOnlyPage.evaluate(() => ({
            pathname: location.pathname,
            search: location.search,
            hash: location.hash,
            activeElementId: document.activeElement?.id,
            moduleFocusCalls: window.__moduleFocusCalls
        }));
        evidence.moduleOnlyRepeatedActivation = repeatedModuleClick;
        fs.writeFileSync(evidencePath, JSON.stringify(evidence, null, 2));
        console.log("MODULE_ONLY_REPEATED_ACTIVATION", JSON.stringify(repeatedModuleClick));
        assert.equal(repeatedModuleClick.pathname, routeChangedClick.pathname);
        assert.equal(repeatedModuleClick.search, routeChangedClick.search);
        assert.equal(repeatedModuleClick.hash, routeChangedClick.hash,
            "Repeated skip-link activation must preserve the already-matching fragment");
        assert.equal(repeatedModuleClick.activeElementId, "shell-focus-main");
        assert.equal(repeatedModuleClick.moduleFocusCalls, 2,
            `Repeated activation must add exactly one main focus call: ${JSON.stringify(repeatedModuleClick)}`);

        await moduleOnlyPage.evaluate(() => {
            const current = new URL(window.location.href);
            current.hash = "";
            history.replaceState({}, "", current);
        });

        await moduleOnlySkipLink.focus();
        await moduleOnlyPage.evaluate(() => {
            const link = document.querySelector(".swiftaid-app-shell__skip-link");
            link.dispatchEvent(new MouseEvent("click", {
                bubbles: false,
                cancelable: true,
                button: 0,
                ctrlKey: true
            }));
        });
        await moduleOnlyPage.waitForTimeout(50);
        assert.equal(await moduleOnlySkipLink.evaluate(element => element === document.activeElement), true,
            "Module-only skip-link handler must ignore modified clicks");

        await moduleOnlySkipLink.focus();
        await moduleOnlyPage.evaluate(() => {
            const link = document.querySelector(".swiftaid-app-shell__skip-link");
            const preventDefault = event => event.preventDefault();
            link.addEventListener("click", preventDefault, { capture: true, once: true });
            link.dispatchEvent(new MouseEvent("click", { bubbles: false, cancelable: true, button: 0 }));
        });
        await moduleOnlyPage.waitForTimeout(50);
        assert.equal(await moduleOnlySkipLink.evaluate(element => element === document.activeElement), true,
            "Module-only skip-link handler must ignore clicks canceled by an earlier listener");

        const focusCallsBeforeLateCancellation = await moduleOnlyPage.evaluate(() => window.__moduleFocusCalls);
        const lateCancellation = await moduleOnlyPage.evaluate(() => {
            const link = document.querySelector(".swiftaid-app-shell__skip-link");
            document.body.addEventListener("click", event => event.preventDefault(), { once: true });
            const event = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
            const notCanceled = link.dispatchEvent(event);
            return { defaultPrevented: event.defaultPrevented, notCanceled };
        });
        assert.equal(lateCancellation.defaultPrevented, true,
            "The later bubbling listener must cancel the synthetic skip-link event");
        assert.equal(lateCancellation.notCanceled, false,
            "A prevented synthetic skip-link event must report canceled dispatch");
        await moduleOnlyPage.waitForTimeout(50);
        assert.equal(await moduleOnlySkipLink.evaluate(element => element === document.activeElement), true,
            "Module-only skip-link handler must ignore clicks canceled by a later bubbling listener");
        assert.equal(await moduleOnlyPage.evaluate(() => window.__moduleFocusCalls), focusCallsBeforeLateCancellation,
            "A later cancellation without destination navigation must not focus main");
        assert.equal(fallbackRequestBlocked, true, "Module-only test must block the classic fallback script");
        assert.equal(actualModuleClick.hash, "#shell-focus-main");
        assert.equal(actualModuleClick.activeElementId, "shell-focus-main");
        assert.equal(actualModuleClick.moduleFocusCalls, 1,
            `The module must focus the main target once for a real Playwright click: ${JSON.stringify(actualModuleClick)}`);
        await moduleOnlyContext.close();
    } finally {
        if (browser) await browser.close();
        if (host.exitCode === null) {
            host.kill();
            await new Promise(resolve => host.once("exit", resolve));
        }
    }
}

main().catch(error => {
    console.error(error.stack || error);
    process.exitCode = 1;
});
