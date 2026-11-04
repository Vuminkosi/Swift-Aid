// UI-QA component run only: this reuses the synthetic TestHost component fixture
// and writes screenshots to a task-specific evidence folder. It is not a
// Gateway route test and does not claim acceptance of journeys A-H.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

const hostDll = path.resolve("tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll");
const hostContentRoot = path.resolve("tests/SwiftAid.Ui.TestHost");
const artifactDirectory = path.join(__dirname, "artifacts", "frontend-journeys-20261028-shared-surfaces");
fs.mkdirSync(artifactDirectory, { recursive: true });

async function startHost(child) {
    let output = "";
    child.stdout.on("data", chunk => { output += chunk.toString(); });
    child.stderr.on("data", chunk => { output += chunk.toString(); });
    const deadline = Date.now() + 30000;
    let baseUrl;
    let identityVerified = false;

    while (Date.now() < deadline) {
        if (child.exitCode !== null) {
            throw new Error(`Test host child PID ${child.pid} exited before readiness (${child.exitCode}).\n${output}`);
        }

        const listener = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/);
        if (listener) {
            baseUrl = listener[1];
            try {
                const response = await fetch(baseUrl);
                if (response.ok) {
                    const markup = await response.text();
                    if (!markup.includes("Shared component verification fixture")) {
                        throw new Error(`Child PID ${child.pid} listener did not serve the test fixture identity at ${baseUrl}`);
                    }
                    if (child.exitCode !== null) throw new Error(`Test host child PID ${child.pid} exited during identity verification`);
                    identityVerified = true;
                    return { baseUrl, output, identityVerified, childPid: child.pid };
                }
            } catch (error) {
                if (error.message.includes("did not serve the test fixture identity")) throw error;
                // Kestrel has announced the port but may not have completed its first request yet.
            }
        }

        await new Promise(resolve => setTimeout(resolve, 200));
    }

    throw new Error(`Timed out waiting for child PID ${child.pid} to announce and serve an ephemeral loopback listener.\n${output}`);
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
        const { baseUrl, output: hostOutput } = hostIdentity;
        let liveHostOutput = hostOutput;
        host.stdout.on("data", chunk => { liveHostOutput += chunk.toString(); });
        host.stderr.on("data", chunk => { liveHostOutput += chunk.toString(); });
        const directAssetChecks = {};
        for (const path of [
            "/_content/Blazor.Lottie.Player/lottie.min.js",
            "/_content/Blazor.Lottie.Player/lottiePlayerModule.js",
            "/_content/SwiftAid.UI/assets/loading/swiftaid-loader.lottie.json",
            "/_content/SwiftAid.UI/assets/loading/swiftaid-loader.gif",
            "/_content/SwiftAid.UI/assets/loading/swiftaid-loader-still.png"
        ]) {
            const response = await fetch(`${baseUrl}${path}`);
            directAssetChecks[path] = response.status;
        }
        assert(Object.values(directAssetChecks).every(status => status === 200),
            `A required local UI asset did not load: ${JSON.stringify(directAssetChecks)}`);
        browser = await chromium.launch({ channel: "chrome", headless: true });
        const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
        const page = await context.newPage();
        const errors = [];
        const expectedSyntheticRuntimeErrors = [];
        const candidateLateSyntheticRuntimeErrors = [];
        let browserEventSequence = 0;
        let lateMalformedRequestSeen = false;
        let lateMalformedResponseCompletedSequence;
        let browserPhase = "page-startup";
        const consoleErrors = [];
        const consoleErrorLocations = [];
        const requests = [];
        const failedResponses = [];
        const failedRequests = [];
        page.on("pageerror", error => {
            const record = { phase: browserPhase, sequence: ++browserEventSequence, message: error.message, stack: error.stack };
            if (browserPhase === "lottie-second-load-synthetic-error"
                && lottieJsonRequests === 2
                && lateMalformedRequestSeen
                && error.message === "Cannot read properties of null (reading '__complete')"
                && error.stack.includes("lottie.min.js")
                && error.stack.includes("completeData")) {
                candidateLateSyntheticRuntimeErrors.push(record);
            } else {
                errors.push(record);
            }
        });
        page.on("console", message => {
            if (message.type() === "error") {
                consoleErrors.push(message.text());
                consoleErrorLocations.push({ text: message.text(), location: message.location() });
            }
        });
        page.on("request", request => requests.push(request.url()));
        page.on("response", response => { if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`); });
        page.on("requestfailed", request => failedRequests.push(`${request.failure()?.errorText ?? "failed"} ${request.url()}`));
        let lottieJsonRequests = 0;
        let resolveLateMalformedResponse;
        const lateMalformedResponse = new Promise(resolve => { resolveLateMalformedResponse = resolve; });
        await page.route("**/_content/SwiftAid.UI/assets/loading/swiftaid-loader.lottie.json", async route => {
            lottieJsonRequests++;
            if (lottieJsonRequests === 2) {
                lateMalformedRequestSeen = true;
                await route.fulfill({ status: 200, contentType: "application/json", body: "{ synthetic invalid Lottie JSON" });
                lateMalformedResponseCompletedSequence = ++browserEventSequence;
                resolveLateMalformedResponse();
                return;
            }

            const response = await route.fetch();
            await route.fulfill({
                response,
                headers: { ...response.headers(), "cache-control": "no-store" }
            });
        });

        await page.goto(baseUrl, { waitUntil: "networkidle" });
        assert.equal(await page.getByRole("heading", { name: "Shared component verification fixture" }).count(), 1);
        assert.equal(await page.locator(".swiftaid-loader").count(), 0);

        const deniedRegion = page.getByRole("region", { name: "Coordinator access unavailable" });
        const errorRegion = page.getByRole("region", { name: "Reports could not be loaded" });
        assert.equal(await deniedRegion.count(), 1, "Denied state panel needs an accessible region name");
        assert.equal(await errorRegion.count(), 1, "Error state panel needs an accessible region name");
        const panelLabels = await page.locator("[data-testid='state-panel-fixture'] .swiftaid-status-panel")
            .evaluateAll(panels => panels.map(panel => {
                const id = panel.getAttribute("aria-labelledby");
                return { id, text: id ? document.getElementById(id)?.textContent.trim() : null };
            }));
        assert.equal(panelLabels.length, 2, "Expected both rendered state panels");
        assert(panelLabels.every(label => label.id && label.text),
            "Each state panel must reference a rendered heading");
        assert.notEqual(panelLabels[0].id, panelLabels[1].id, "Rendered state panels must not reuse heading IDs");

        const report = page.getByTestId("guided-report-fixture");
        const continueToDetails = report.getByRole("button", { name: "Continue to details" });
        assert.equal(await continueToDetails.isDisabled(), true,
            "The guided report must prevent continuing until a subject is selected");
        assert.match(await report.getByRole("status").filter({ hasText: /Choose Human health or Animal health to continue/i }).innerText(),
            /Choose Human health or Animal health to continue/i,
            "The guided report must explain why continuing is unavailable");
        await report.getByRole("radio", { name: "Human health — a person or people" }).check();
        await continueToDetails.click();
        await report.getByRole("heading", { name: "What did you notice?" }).waitFor({ state: "visible" });
        await report.getByRole("button", { name: "Review report" }).click();
        const emptyReviewAlert = await report.getByRole("alert").innerText();
        const emptyReviewValidation = await report.locator(".guided-report__validation").innerText();
        assert.match(emptyReviewAlert, /Describe what happened or add an observation/i);
        assert.match(emptyReviewValidation, /Describe what happened or add an observation/i,
            "The guided report must require an observation before opening review");
        const validationFocus = await report.locator("[data-ellai-control='report.narrative']")
            .evaluate(element => ({ focused: document.activeElement === element, tabIndex: element.getAttribute("tabindex") }));
        assert.deepEqual(validationFocus, { focused: true, tabIndex: "-1" },
            "Invalid review must focus and highlight the first invalid input");
        await report.getByLabel("Describe what happened").fill("What I saw this morning");
        await report.getByLabel("Add other observations (optional)").fill("Two animals were nearby");
        assert.match(await report.getByText("This saved country is no longer on the list. Choose a listed country before sending.").innerText(),
            /choose a listed country before sending/i, "A saved country absent from the current list must be identified");
        assert.equal(await report.locator("input[type='date']").count(), 0,
            "Unknown occurrence time must not require a date");
        await report.getByRole("button", { name: "Review report" }).click();
        const reportSummary = report.locator(".guided-report__summary");
        await report.getByRole("heading", { name: "Review your report" }).waitFor({ state: "visible" });
        const staleCountrySummary = await reportSummary.innerText();
        assert.match(staleCountrySummary, /What I saw this morning[\s\S]*Unknown/i,
            "Review should preserve reported details and label unknown time as unknown");
        assert.match(staleCountrySummary, /Country choice unavailable/i,
            "Review must not treat a country absent from the current catalogue as valid");
        assert.equal(await report.getByRole("button", { name: "Submit report" }).isDisabled(), true,
            "Submission must remain disabled for a country code outside the current catalogue");
        await report.getByRole("button", { name: "Back" }).click();
        await report.getByLabel("Describe what happened").waitFor({ state: "visible" });
        assert.equal(await report.getByLabel("Describe what happened").inputValue(), "What I saw this morning",
            "Going back from review must preserve typed narrative");
        await report.getByRole("combobox", { name: "Country where the event happened" }).click();
        await page.getByRole("option", { name: "Zimbabwe" }).click();
        await report.getByRole("button", { name: "Save draft" }).click();
        await page.getByTestId("report-callback-status").getByText("Synthetic draft callback: What I saw this morning").waitFor();
        await report.getByRole("button", { name: "Back" }).click();
        await report.getByRole("button", { name: "Continue to details" }).click();
        assert.equal(await report.getByLabel("Describe what happened").inputValue(), "What I saw this morning",
            "Going back to type and continuing again must preserve typed narrative");
        await report.getByRole("button", { name: "Review report" }).click();
        await report.getByRole("checkbox", { name: /I reviewed the original statement and the known or unknown details above\./ }).check();
        await report.getByRole("checkbox", { name: "I confirm the selected event country." }).check();
        await report.getByRole("checkbox", { name: /I have reviewed the report-handling information shown here\.?/ }).check();
        await report.getByRole("button", { name: "Submit report" }).click();
        await page.getByTestId("report-callback-status").getByText("Synthetic submit callback: What I saw this morning").waitFor();
        const localSubmitStatus = await report.locator(".mud-alert[role='status']").innerText();
        assert.match(localSubmitStatus, /Check the status shown on this page/i,
            "The local submit status should direct the user to the displayed callback outcome");
        assert.doesNotMatch(localSubmitStatus, /receipt number|server record|persisted/i,
            "The local callback fixture must not claim a server receipt or persistence");
        console.log(JSON.stringify({
            scenarioResults: {
                statePanelAccessibleRegionNames: true,
                statePanelHeadingIdsUnique: true,
                guidedReportRequiresSubjectAndObservation: true,
                guidedReportPreservesDraftAcrossBackAndSave: true,
                guidedReportAllowsUnknownOccurrenceTime: true,
                guidedReportBlocksStaleCountryUntilCurrentChoice: true,
                guidedReportUsesSyntheticSubmitCallbackAndHonestReceiptCopy: true
            }
        }, null, 2));

        const stillImage = "./_content/SwiftAid.UI/assets/loading/swiftaid-loader-still.png";
        await page.emulateMedia({ reducedMotion: "reduce" });
        await page.getByTestId("toggle-loading").click();
        try {
            await page.locator(".swiftaid-loader").getByText("Synthetic fixture loading status").waitFor({ state: "visible", timeout: 10000 });
        } catch {
            throw new Error(`Loader did not appear. Body: ${await page.locator("body").innerText()}\nConsole errors: ${consoleErrors.join(" | ")}\nFailed responses: ${failedResponses.join(" | ")}\nPage errors: ${JSON.stringify(errors)}\nHost output: ${hostOutput}`);
        }
        assert.equal(await page.locator(".swiftaid-loader__visual").getAttribute("aria-hidden"), "true");
        assert.equal(await page.locator(".swiftaid-loader__image").getAttribute("src"), stillImage);

        const screenshots = [];
        const saveScreenshot = async name => {
            const outputPath = path.join(artifactDirectory, name);
            await page.screenshot({ path: outputPath, fullPage: true });
            screenshots.push(path.relative(process.cwd(), outputPath));
        };
        await saveScreenshot("fixture-390-light-reduced-motion.png");
        await page.emulateMedia({ colorScheme: "dark" });
        await page.waitForFunction(() => document.documentElement.dataset.theme === "agentic" && document.documentElement.dataset.colorMode === "dark");
        await saveScreenshot("fixture-390-dark-reduced-motion.png");
        await page.setViewportSize({ width: 1280, height: 900 });
        await saveScreenshot("fixture-1280-dark-reduced-motion.png");
        await page.emulateMedia({ colorScheme: "light" });
        await page.waitForFunction(() => document.documentElement.dataset.theme === "agentic" && document.documentElement.dataset.colorMode === "light");
        await saveScreenshot("fixture-1280-light-reduced-motion.png");

        const stillResponse = await page.request.get(new URL(stillImage.replace(/^\.\//, "/"), baseUrl).toString());
        assert.equal(stillResponse.status(), 200);

        await page.emulateMedia({ reducedMotion: "no-preference" });
        await page.locator(".swiftaid-loader__fallback").waitFor({ state: "visible" });
        await page.setViewportSize({ width: 390, height: 844 });
        await saveScreenshot("fixture-390-light-gif-fallback.png");
        await page.getByTestId("toggle-lottie").click();
        browserPhase = "lottie-first-load-ready";
        await page.waitForFunction(() => performance.getEntriesByType("resource")
            .some(entry => entry.name.includes("_content/Blazor.Lottie.Player/lottie.min.js")));
        try {
            await page.locator(".swiftaid-loader__lottie.is-ready").waitFor({ state: "visible", timeout: 10000 });
        } catch {
            await saveScreenshot("fixture-390-light-lottie-error-boundary.png");
            const resources = await page.evaluate(() => performance.getEntriesByType("resource")
                .map(item => item.name)
                .filter(name => name.includes("Lottie") || name.includes("lottie")));
            const runtimeState = await page.evaluate(() => ({
                hasLottie: Boolean(window.lottie),
                loaderMarkup: document.querySelector(".swiftaid-loader__visual")?.innerHTML,
                script: document.querySelector("script[data-swiftaid-lottie-runtime]")?.outerHTML
            }));
            throw new Error(`Lottie failed to become ready. Asset status: ${JSON.stringify(directAssetChecks)}\nRuntime: ${JSON.stringify(runtimeState)}\nFailed responses: ${failedResponses.join(" | ")}\nFailed requests: ${failedRequests.join(" | ")}\nBrowser console: ${JSON.stringify(consoleErrorLocations)}\nPage errors: ${JSON.stringify(errors)}\nResources: ${JSON.stringify(resources)}\nScreenshots: ${screenshots.join(", ")}\nHost logs: ${liveHostOutput}`);
        }
        assert(await page.evaluate(() => Boolean(window.lottie && typeof window.lottie === "object")));
        assert(requests.some(url => url.includes("swiftaid-loader.lottie.json")), "Lottie data asset was not requested");
        assert(await page.locator(".swiftaid-loader__lottie svg, .swiftaid-loader__lottie canvas").count() > 0,
            "Lottie did not render an SVG or canvas");
        const lottieSvgOrCanvas = await page.locator(".swiftaid-loader__lottie svg, .swiftaid-loader__lottie canvas").count();
        await saveScreenshot("fixture-390-light-lottie-ready.png");

        const statusText = await page.locator(".swiftaid-loader").innerText();
        browserPhase = "lottie-second-load-synthetic-error";
        await page.getByTestId("toggle-lottie").click();
        await page.locator(".swiftaid-loader__lottie").waitFor({ state: "detached" });
        await page.getByTestId("toggle-lottie").click();
        await Promise.race([
            lateMalformedResponse,
            new Promise((_, reject) => setTimeout(() => reject(new Error("Second malformed Lottie response was not completed within 10 seconds")), 10000))
        ]);
        assert.equal(lateMalformedRequestSeen, true, "Second Lottie JSON request did not reach its route handler");
        assert.equal(lateMalformedResponseCompletedSequence !== undefined, true, "Second malformed Lottie response was not completed");
        await page.locator(".swiftaid-loader__fallback").waitFor({ state: "visible", timeout: 10000 });
        assert.equal(lottieJsonRequests, 2, "Expected exactly two Lottie JSON requests before the late-failure assertion");
        const latePlayerCount = await page.locator(".swiftaid-loader__lottie").count();
        let latePlayerState = null;
        if (latePlayerCount === 1) {
            latePlayerState = await page.locator(".swiftaid-loader__lottie")
                .evaluate(element => ({ className: element.className, opacity: getComputedStyle(element).opacity }));
        }
        assert(latePlayerCount === 0 || (latePlayerCount === 1
            && latePlayerState.className.includes("is-pending")
            && latePlayerState.opacity === "0"),
        `Late failure must remove the player or leave it hidden and pending: ${JSON.stringify({ latePlayerCount, latePlayerState })}`);
        assert.equal(await page.locator(".swiftaid-loader").innerText(), statusText,
            "Late Lottie data failure changed or removed the loading status");
        await saveScreenshot("fixture-390-light-late-lottie-fallback.png");
        for (const error of candidateLateSyntheticRuntimeErrors) {
            if (error.sequence > lateMalformedResponseCompletedSequence) {
                expectedSyntheticRuntimeErrors.push(error);
            } else {
                errors.push(error);
            }
        }

        const widths = [320, 390, 768, 1280];
        const layout = [];
        const overflowByWidth = [];
        for (const width of widths) {
            await page.setViewportSize({ width, height: 844 });
            const current = await page.evaluate(() => ({
                width: document.documentElement.clientWidth,
                scrollWidth: document.documentElement.scrollWidth,
                statusVisible: Boolean(document.querySelector('[role="status"]')),
                overflow: [...document.querySelectorAll("body *")]
                    .map(element => ({
                        tag: element.tagName,
                        className: typeof element.className === "string" ? element.className : "",
                        testId: element.getAttribute("data-testid"),
                        right: Math.round(element.getBoundingClientRect().right),
                        width: Math.round(element.getBoundingClientRect().width)
                    }))
                    .filter(element => element.right > document.documentElement.clientWidth + 1)
                    .slice(0, 20)
            }));
            layout.push({ width: current.width, scrollWidth: current.scrollWidth, statusVisible: current.statusVisible });
            if (current.width !== current.scrollWidth) overflowByWidth.push({ testedWidth: width, elements: current.overflow });
        }
        if (!layout.every(item => item.width === item.scrollWidth)) {
            throw new Error(`Horizontal overflow: ${JSON.stringify({ layout, overflowByWidth })}`);
        }

        await page.emulateMedia({ colorScheme: "dark" });
        await page.waitForFunction(() => document.documentElement.dataset.theme === "agentic" && document.documentElement.dataset.colorMode === "dark");
        await page.emulateMedia({ colorScheme: "light" });
        await page.waitForFunction(() => document.documentElement.dataset.theme === "agentic" && document.documentElement.dataset.colorMode === "light");

        const status = await page.locator(".swiftaid-loader").getAttribute("aria-live");
        assert.equal(status, "polite");
        await page.getByTestId("toggle-loading").click();
        await page.locator(".swiftaid-loader").waitFor({ state: "detached" });
        assert.deepEqual(errors, [], `Unexpected browser errors: ${JSON.stringify(errors)}; console: ${JSON.stringify(consoleErrorLocations)}; requests: ${JSON.stringify(failedRequests)}; host: ${liveHostOutput}`);

        for (const url of requests) {
            assert(!/^https?:\/\/(?!127\.0\.0\.1)/i.test(url), `Unexpected external request: ${url}`);
        }
        assert.deepEqual(errors, [], `Browser errors: ${JSON.stringify(errors)}; console: ${JSON.stringify(consoleErrorLocations)}; requests: ${JSON.stringify(failedRequests)}; host: ${liveHostOutput}`);

        console.log(JSON.stringify({
            result: "PASS",
            viewportWidths: widths,
            layout,
            localLottieRequested: requests.some(url => url.includes("_content/Blazor.Lottie.Player/lottie.min.js")),
            sharedAssetsRequested: requests.filter(url => url.includes("_content/SwiftAid.UI/assets/loading/")),
            failedResponses,
            failedRequests,
            consoleErrorLocations,
            page404Requests: requests.filter(url => url.includes("favicon") || failedResponses.some(failure => failure.endsWith(url))),
            lottieJsonRequests,
            expectedSyntheticRuntimeErrors,
            latePlayerState,
            latePlayerCount,
            hostIdentity,
            lottieSvgOrCanvas,
            pageErrors: errors,
            browserConsoleErrors: consoleErrors,
            hostReadyOutput: hostOutput.split(/\r?\n/).filter(line => line.includes("Now listening"))
        }, null, 2));

        const lottieFallbackPage = await context.newPage();
        await lottieFallbackPage.route("**/_content/Blazor.Lottie.Player/lottie.min.js", route => route.abort());
        await lottieFallbackPage.goto(baseUrl, { waitUntil: "networkidle" });
        await lottieFallbackPage.getByTestId("toggle-loading").click();
        await lottieFallbackPage.locator(".swiftaid-loader").waitFor({ state: "visible" });
        await lottieFallbackPage.getByTestId("toggle-lottie").click();
        await lottieFallbackPage.locator(".swiftaid-loader__fallback").waitFor({ state: "visible" });
        assert.equal(await lottieFallbackPage.locator(".swiftaid-loader__lottie").count(), 0,
            "Lottie component rendered after local runtime load failure");
        await lottieFallbackPage.close();

        const earlyFailurePage = await context.newPage();
        const earlyFailureErrors = [];
        const expectedEarlySyntheticRuntimeErrors = [];
        const candidateEarlySyntheticRuntimeErrors = [];
        let earlyEventSequence = 0;
        let earlyMalformedRequestSeen = false;
        let earlyMalformedResponseCompletedSequence;
        let resolveEarlyMalformedResponse;
        const earlyMalformedResponse = new Promise(resolve => { resolveEarlyMalformedResponse = resolve; });
        earlyFailurePage.on("pageerror", error => {
            const record = { sequence: ++earlyEventSequence, message: error.message, stack: error.stack };
            if (earlyMalformedRequestSeen
                && error.message === "Cannot read properties of null (reading '__complete')"
                && error.stack.includes("lottie.min.js")
                && error.stack.includes("completeData")) {
                candidateEarlySyntheticRuntimeErrors.push(record);
            } else {
                earlyFailureErrors.push(record);
            }
        });
        await earlyFailurePage.route("**/_content/SwiftAid.UI/assets/loading/swiftaid-loader.lottie.json", async route => {
            earlyMalformedRequestSeen = true;
            await route.fulfill({ status: 200, contentType: "application/json", body: "{ synthetic invalid Lottie JSON" });
            earlyMalformedResponseCompletedSequence = ++earlyEventSequence;
            resolveEarlyMalformedResponse();
        });
        await earlyFailurePage.emulateMedia({ reducedMotion: "no-preference" });
        await earlyFailurePage.goto(baseUrl, { waitUntil: "networkidle" });
        await earlyFailurePage.getByTestId("toggle-loading").click();
        await earlyFailurePage.locator(".swiftaid-loader").waitFor({ state: "visible" });
        await earlyFailurePage.waitForFunction(() => performance.getEntriesByType("resource")
            .some(entry => entry.name.includes("SwiftAidLoader.razor.js")), { timeout: 10000 });
        assert.equal(await earlyFailurePage.evaluate(() => window.matchMedia("(prefers-reduced-motion: reduce)").matches), false,
            "Pre-ready fault scenario requires the no-preference animation path");
        await earlyFailurePage.getByTestId("toggle-lottie").click();
        await Promise.race([
            earlyMalformedResponse,
            new Promise((_, reject) => setTimeout(() => reject(new Error("Pre-ready synthetic animation request was not observed within 10 seconds")), 10000))
        ]);
        assert.equal(earlyMalformedRequestSeen, true, "Pre-ready malformed animation response did not reach its route handler");
        assert.equal(earlyMalformedResponseCompletedSequence !== undefined, true, "Pre-ready malformed animation response was not completed");
        for (const error of candidateEarlySyntheticRuntimeErrors) {
            if (error.sequence > earlyMalformedResponseCompletedSequence) {
                expectedEarlySyntheticRuntimeErrors.push(error);
            } else {
                earlyFailureErrors.push(error);
            }
        }
        await earlyFailurePage.locator(".swiftaid-loader__fallback").waitFor({ state: "visible", timeout: 10000 });
        const earlyFailureState = await earlyFailurePage.locator(".swiftaid-loader__lottie")
            .evaluate(element => ({ className: element.className, opacity: getComputedStyle(element).opacity }));
        assert(earlyFailureState.className.includes("is-pending"),
            `Pre-ready failure unexpectedly marked Lottie ready: ${JSON.stringify(earlyFailureState)}`);
        assert.equal(earlyFailureState.opacity, "0",
            `Pre-ready failure did not hide the pending Lottie player behind the GIF: ${JSON.stringify(earlyFailureState)}`);
        assert.equal(await earlyFailurePage.locator(".swiftaid-loader").innerText(), "Synthetic fixture loading status");
        assert.equal(earlyMalformedRequestSeen, true, "Pre-ready malformed animation response was not injected");
        assert.deepEqual(earlyFailureErrors, [], `Pre-ready failure produced an unexpected browser error: ${JSON.stringify(earlyFailureErrors)}`);
        await earlyFailurePage.screenshot({
            path: path.join(artifactDirectory, "fixture-390-light-early-lottie-fallback.png"),
            fullPage: true
        });
        await earlyFailurePage.close();

        const sharedPage = await context.newPage();
        const sharedPageErrors = [];
        const sharedConsoleErrors = [];
        const sharedConsoleErrorLocations = [];
        const sharedRequests = [];
        const sharedFailedResponses = [];
        const sharedFailedRequests = [];
        sharedPage.on("pageerror", error => sharedPageErrors.push(error.message));
        sharedPage.on("console", message => {
            if (message.type() === "error") {
                sharedConsoleErrors.push(message.text());
                sharedConsoleErrorLocations.push({ text: message.text(), location: message.location() });
            }
        });
        sharedPage.on("request", request => sharedRequests.push(request.url()));
        sharedPage.on("response", response => {
            if (response.status() >= 400) sharedFailedResponses.push({ status: response.status(), url: response.url() });
        });
        sharedPage.on("requestfailed", request => sharedFailedRequests.push({
            url: request.url(), error: request.failure()?.errorText ?? "failed"
        }));
        await sharedPage.emulateMedia({ colorScheme: "light", reducedMotion: "reduce" });
        await sharedPage.goto(`${baseUrl}/shared-surfaces`, { waitUntil: "networkidle" });
        await sharedPage.getByRole("heading", { name: "Shared surfaces verification" }).waitFor();
        assert.equal(await sharedPage.evaluate(() => window.matchMedia("(prefers-reduced-motion: reduce)").matches), true,
            "Shared-surface fixture should honor reduced-motion emulation");
        assert.equal(await sharedPage.getByTestId("public-home-surface").count(), 1);
        assert.equal(await sharedPage.getByTestId("public-report-surface").count(), 1);
        assert.equal(await sharedPage.getByTestId("public-sessions-surface").count(), 1);
        assert.equal(await sharedPage.getByRole("heading", { name: "Support that follows through." }).count(), 1);
        await sharedPage.getByTestId("public-sessions-surface").getByRole("heading", { name: "Synthetic community session", exact: true }).waitFor();
        const syntheticSessionLinks = await sharedPage.getByTestId("public-sessions-surface").locator("a[href]")
            .evaluateAll(links => links.map(link => ({ text: link.textContent.trim(), href: link.href })));
        assert.equal(syntheticSessionLinks.length, 1,
            `Synthetic session listing should expose only its local calendar route: ${JSON.stringify(syntheticSessionLinks)}`);
        const [calendarLink] = syntheticSessionLinks;
        const calendarUrl = new URL(calendarLink.href);
        assert.equal(calendarLink.text, "Add to calendar");
        assert.equal(calendarUrl.origin, new URL(sharedPage.url()).origin,
            "Synthetic session calendar link must remain on the TestHost origin");
        assert.equal(calendarUrl.pathname, "/public/v1/sessions/f7663b33-aee7-47a8-9b9d-5b1d3ab8fc01/calendar",
            "Synthetic session link must target the expected local calendar route");
        assert.equal(calendarUrl.search, "", "Synthetic calendar link must not carry invitation data");

        const screenshotsByWidth = [];
        const sharedLayout = [];
        const duplicateIdsByWidth = [];
        let skipLinkFallbackTrace;
        const sharedWidths = [320, 360, 390, 430, 768, 1024, 1280, 1440, 1920];
        for (const width of sharedWidths) {
            await sharedPage.setViewportSize({ width, height: 900 });
            const layout = await sharedPage.evaluate(() => {
                const visible = element => {
                    const style = getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.display !== "none" && style.visibility !== "hidden" && rect.width > 0 && rect.height > 0;
                };
                const navs = [...document.querySelectorAll("nav[aria-label='Primary navigation']")]
                .filter(visible).map(nav => {
                    const navRect = nav.getBoundingClientRect();
                    return { top: Math.round(navRect.top), bottom: Math.round(navRect.bottom), height: Math.round(navRect.height),
                    links: [...nav.querySelectorAll("a")].filter(visible).map(link => {
                        const rect = link.getBoundingClientRect();
                        return {
                            text: link.textContent.trim(),
                            fontSize: Number.parseFloat(getComputedStyle(link).fontSize),
                            left: Math.round(rect.left), top: Math.round(rect.top), right: Math.round(rect.right),
                            bottom: Math.round(rect.bottom), height: Math.round(rect.height)
                        };
                    }) };
                });
                const allIds = [...document.querySelectorAll("[id]")].map(element => element.id);
                const duplicates = [...new Set(allIds.filter((id, index) => allIds.indexOf(id) !== index))];
                const brandImages = [...document.querySelectorAll(".swiftaid-app-shell__brand-mark")]
                    .map(image => ({ source: image.currentSrc, loaded: image.complete && image.naturalWidth > 0 }));
                return {
                    viewport: document.documentElement.clientWidth,
                    scrollWidth: document.documentElement.scrollWidth,
                    navs,
                    duplicates,
                    brandImages,
                    theme: document.documentElement.dataset.theme,
                    colorMode: document.documentElement.dataset.colorMode
                };
            });
            assert.equal(layout.viewport, layout.scrollWidth, `Horizontal overflow at ${width}px: ${JSON.stringify(layout)}`);
            assert.equal(layout.navs.length, 1, `Expected one visible primary nav at ${width}px`);
            assert(layout.navs[0].links.every(link => link.fontSize >= 12),
                `Navigation label below 12px at ${width}px: ${JSON.stringify(layout.navs[0])}`);
            assert.deepEqual(layout.duplicates, [], `Duplicate DOM IDs at ${width}px: ${layout.duplicates.join(", ")}`);
            assert(layout.brandImages.length > 0 && layout.brandImages.every(image => image.loaded),
                `Brand mark failed to load at ${width}px: ${JSON.stringify(layout.brandImages)}`);
            const expectedNavCount = width <= 960 ? 6 : 7;
            assert.equal(layout.navs[0].links.length, expectedNavCount,
                `Unexpected visible nav link count at ${width}px`);
            if (width <= 960) {
                const links = layout.navs[0].links;
                assert(links.every(link => Math.abs(link.top - links[0].top) <= 1
                    && Math.abs(link.bottom - links[0].bottom) <= 1 && link.height >= 44),
                `Mobile navigation links do not share a single full-height row at ${width}px: ${JSON.stringify(links)}`);
                assert(links.every((link, index) => index === 0 || link.left >= links[index - 1].right),
                    `Mobile navigation links overlap at ${width}px: ${JSON.stringify(links)}`);
            }
            sharedLayout.push({ width, scrollWidth: layout.scrollWidth, navLinkCount: layout.navs[0].links.length,
                navTop: layout.navs[0].top, navBottom: layout.navs[0].bottom, navHeight: layout.navs[0].height,
                linkBounds: layout.navs[0].links.map(({ text, left, top, right, bottom, height, fontSize }) => ({
                    text, left, top, right, bottom, height, fontSize
                })), theme: layout.theme });

            if (width === 320 || width === 1024) {
                const name = width === 320 ? "shared-320-light.png" : "shared-1024-light.png";
                const outputPath = path.join(artifactDirectory, name);
                await sharedPage.screenshot({ path: outputPath, fullPage: true });
                screenshotsByWidth.push(path.relative(process.cwd(), outputPath));
            }
        }
        const viewportEvidencePath = path.join(artifactDirectory, "shared-surface-viewport-measurements.json");
        fs.writeFileSync(viewportEvidencePath, JSON.stringify(sharedLayout, null, 2));
        console.log(JSON.stringify({ sharedViewportMeasurements: path.relative(process.cwd(), viewportEvidencePath) }));

        await sharedPage.setViewportSize({ width: 390, height: 844 });
        await sharedPage.keyboard.press("Tab");
        assert.equal(await sharedPage.evaluate(() => document.activeElement?.classList.contains("swiftaid-app-shell__skip-link")), true,
            "First keyboard stop should be the shell skip link");
        const mainTargetBeforeActivation = await sharedPage.evaluate(() => ({
            present: Boolean(document.getElementById("shared-surfaces-main")),
            tag: document.getElementById("shared-surfaces-main")?.tagName ?? "",
            tabIndex: document.getElementById("shared-surfaces-main")?.getAttribute("tabindex")
        }));
        const locationBeforeSkip = await sharedPage.evaluate(() => ({ href: location.href, pathname: location.pathname, search: location.search }));
        await sharedPage.evaluate(() => {
            window.__swiftAidFocusEvents = [];
            document.addEventListener("focusin", event => window.__swiftAidFocusEvents.push({
                tag: event.target?.tagName ?? "",
                id: event.target?.id ?? "",
                className: typeof event.target?.className === "string" ? event.target.className : ""
            }), true);
            window.__swiftAidFocusEvents.length = 0;
        });
        const shellModuleRequest = sharedPage.waitForRequest(request => request.url().includes("/_content/SwiftAid.UI/js/swiftaid-shell.js"), { timeout: 2000 }).catch(() => null);
        const shellModuleResponse = sharedPage.waitForResponse(response => response.url().includes("/_content/SwiftAid.UI/js/swiftaid-shell.js"), { timeout: 2000 }).catch(() => null);
        await sharedPage.keyboard.press("Enter");
        const shellModuleLoaded = await shellModuleRequest;
        const shellModuleAssetResponse = await shellModuleResponse;
        await sharedPage.waitForFunction(() => document.activeElement?.id === "shared-surfaces-main", { timeout: 1500 }).catch(() => {});
        const skipLinkFocus = await sharedPage.evaluate(() => ({
            href: location.href,
            pathname: location.pathname,
            search: location.search,
            hash: window.location.hash,
            targetBeforeActivation: null,
            activeElementId: document.activeElement?.id ?? "",
            activeElementTag: document.activeElement?.tagName ?? "",
            targetTabIndex: document.getElementById("shared-surfaces-main")?.getAttribute("tabindex"),
            targetPresent: Boolean(document.getElementById("shared-surfaces-main")),
            focusEvents: window.__swiftAidFocusEvents
        }));
        skipLinkFocus.targetBeforeActivation = mainTargetBeforeActivation;
        skipLinkFocus.locationBefore = locationBeforeSkip;
        skipLinkFocus.shellModuleRequestObserved = Boolean(shellModuleLoaded);
        skipLinkFocus.shellModuleHttpStatus = shellModuleAssetResponse?.status() ?? null;
        console.log(JSON.stringify({ skipLinkFocusTrace: skipLinkFocus }));
        assert.equal(skipLinkFocus.pathname, locationBeforeSkip.pathname,
            `Skip link changed pathname: ${JSON.stringify(skipLinkFocus)}`);
        assert.equal(skipLinkFocus.search, locationBeforeSkip.search,
            `Skip link changed query string: ${JSON.stringify(skipLinkFocus)}`);
        assert.equal(skipLinkFocus.hash, "#shared-surfaces-main",
            `Skip link did not set the main-content hash: ${JSON.stringify(skipLinkFocus)}`);
        assert.equal(skipLinkFocus.activeElementId, "shared-surfaces-main",
            `Skip link did not move focus to main: ${JSON.stringify(skipLinkFocus)}`);
        assert.equal(skipLinkFocus.targetPresent, true, `Skip link target disappeared: ${JSON.stringify(skipLinkFocus)}`);

        const shellFallbackPage = await context.newPage();
        const shellFallbackPageErrors = [];
        const shellFallbackConsoleErrors = [];
        const fallbackFailedRequests = [];
        shellFallbackPage.on("pageerror", error => shellFallbackPageErrors.push(error.message));
        shellFallbackPage.on("console", message => {
            if (message.type() === "error") shellFallbackConsoleErrors.push(message.text());
        });
        shellFallbackPage.on("requestfailed", request => fallbackFailedRequests.push({
            url: request.url(), error: request.failure()?.errorText ?? "failed"
        }));
        await shellFallbackPage.route("**/_content/SwiftAid.UI/js/swiftaid-shell.js", route => route.abort());
        await shellFallbackPage.setViewportSize({ width: 390, height: 844 });
        await shellFallbackPage.goto(`${baseUrl}/shared-surfaces`, { waitUntil: "networkidle" });
        await shellFallbackPage.getByRole("heading", { name: "Shared surfaces verification" }).waitFor();
        await shellFallbackPage.keyboard.press("Tab");
        assert.equal(await shellFallbackPage.evaluate(() => document.activeElement?.classList.contains("swiftaid-app-shell__skip-link")), true,
            "Fallback test should begin with keyboard focus on the skip link");
        const fallbackLocationBefore = await shellFallbackPage.evaluate(() => ({ pathname: location.pathname, search: location.search }));
        await shellFallbackPage.evaluate(() => {
            window.__swiftAidFallbackFocusEvents = [];
            document.addEventListener("focusin", event => window.__swiftAidFallbackFocusEvents.push({
                tag: event.target?.tagName ?? "", id: event.target?.id ?? ""
            }), true);
        });
        await shellFallbackPage.keyboard.press("Enter");
        await shellFallbackPage.waitForFunction(() => location.hash === "#shared-surfaces-main", { timeout: 1500 }).catch(() => {});
        const fallbackRequestSeen = fallbackFailedRequests.some(request => request.url.includes("swiftaid-shell.js"));
        const fallbackActiveState = await shellFallbackPage.evaluate(() => ({
            href: location.href,
            pathname: location.pathname,
            search: location.search,
            hash: location.hash,
            activeElementId: document.activeElement?.id ?? "",
            activeElementTag: document.activeElement?.tagName ?? "",
            targetPresent: Boolean(document.getElementById("shared-surfaces-main")),
            focusEvents: window.__swiftAidFallbackFocusEvents
        }));
        skipLinkFallbackTrace = {
            locationBefore: fallbackLocationBefore,
            moduleRequestFailed: fallbackRequestSeen,
            pageErrors: shellFallbackPageErrors,
            consoleErrors: shellFallbackConsoleErrors,
            ...fallbackActiveState,
            fallbackWorked: fallbackRequestSeen
                && fallbackActiveState.pathname === fallbackLocationBefore.pathname
                && fallbackActiveState.search === fallbackLocationBefore.search
                && fallbackActiveState.hash === "#shared-surfaces-main"
                && fallbackActiveState.activeElementId === "shared-surfaces-main"
                && fallbackActiveState.targetPresent
        };
        const fallbackScreenshot = path.join(artifactDirectory, "shared-390-skip-link-module-failure.png");
        await shellFallbackPage.screenshot({ path: fallbackScreenshot, fullPage: true });
        screenshotsByWidth.push(path.relative(process.cwd(), fallbackScreenshot));
        console.log(JSON.stringify({ skipLinkFallbackTrace }));
        await shellFallbackPage.close();
        await sharedPage.emulateMedia({ colorScheme: "dark" });
        await sharedPage.waitForFunction(() => document.documentElement.dataset.theme === "agentic" && document.documentElement.dataset.colorMode === "dark");
        const darkTheme = await sharedPage.evaluate(() => ({ theme: document.documentElement.dataset.theme, colorMode: document.documentElement.dataset.colorMode }));
        const darkScreenshot = path.join(artifactDirectory, "shared-390-dark.png");
        await sharedPage.screenshot({ path: darkScreenshot, fullPage: true });
        screenshotsByWidth.push(path.relative(process.cwd(), darkScreenshot));
        await sharedPage.emulateMedia({ colorScheme: "light" });
        await sharedPage.waitForFunction(() => document.documentElement.dataset.theme === "agentic" && document.documentElement.dataset.colorMode === "light");

        const reportDomSnapshot = await sharedPage.evaluate(() => ({
            viewport: window.innerWidth,
            url: window.location.href,
            reportSurfaceCount: document.querySelectorAll("[data-testid='public-report-surface']").length,
            reportSection: document.querySelector("#shared-report")?.outerHTML.slice(0, 500) ?? null,
            buttons: [...document.querySelectorAll("[data-testid='public-report-surface'] button")]
                .map(button => ({ text: button.textContent.trim(), aria: button.getAttribute("aria-label"), disabled: button.disabled,
                    visible: button.getBoundingClientRect().width > 0 && getComputedStyle(button).display !== "none" }))
        }));
        console.log(JSON.stringify({ sharedReportDomSnapshot: reportDomSnapshot }));
        const mobileNarrative = sharedPage.getByLabel("Describe what happened").filter({ visible: true });
        await sharedPage.locator("[data-testid='public-report-surface'] button:visible")
            .filter({ hasText: /continue to details/i }).first().click({ timeout: 5000 });
        await mobileNarrative.waitFor({ state: "visible" });
        await mobileNarrative.evaluate(element => {
            window.__swiftAidReportInputEvents = 0;
            element.addEventListener("input", () => window.__swiftAidReportInputEvents++, { once: false });
        });
        await mobileNarrative.fill("Synthetic shared draft updated on mobile.");
        assert.equal(await mobileNarrative.inputValue(), "Synthetic shared draft updated on mobile.");
        assert.equal(await sharedPage.evaluate(() => window.__swiftAidReportInputEvents), 1,
            "One mobile input action should raise one input event");
        await sharedPage.getByTestId("capture-shared-draft").click();
        await sharedPage.waitForFunction(() => document.querySelector("[data-testid='shared-draft-value']")?.textContent === "Synthetic shared draft updated on mobile.");
        assert.equal(await sharedPage.getByTestId("shared-draft-value").innerText(), "Synthetic shared draft updated on mobile.");

        await sharedPage.setViewportSize({ width: 1024, height: 900 });
        const desktopNarrative = sharedPage.getByTestId("public-report-surface")
            .getByLabel("Describe what happened").filter({ visible: true });
        await desktopNarrative.waitFor({ state: "visible" });
        assert.equal(await desktopNarrative.inputValue(), "Synthetic shared draft updated on mobile.",
            "The shared report draft value changed after resizing to desktop width");
        await desktopNarrative.fill("Synthetic shared draft updated on desktop.");
        await sharedPage.getByTestId("capture-shared-draft").click();
        await sharedPage.waitForFunction(() => document.querySelector("[data-testid='shared-draft-value']")?.textContent === "Synthetic shared draft updated on desktop.");
        assert.equal(await sharedPage.getByTestId("shared-draft-value").innerText(), "Synthetic shared draft updated on desktop.");
        await sharedPage.setViewportSize({ width: 768, height: 900 });
        if (await mobileNarrative.count() === 0 || !(await mobileNarrative.isVisible().catch(() => false))) {
            await sharedPage.locator("[data-testid='public-report-surface'] button:visible")
                .filter({ hasText: /continue to details/i }).first().click({ timeout: 5000 });
        }
        await mobileNarrative.waitFor({ state: "visible" });
        assert.equal(await mobileNarrative.inputValue(), "Synthetic shared draft updated on desktop.",
            "The shared report draft value changed after resizing back to mobile width");
        const duplicateIdsAfterDraftTransition = await sharedPage.evaluate(() => {
            const ids = [...document.querySelectorAll("[id]")].map(element => element.id);
            return [...new Set(ids.filter((id, index) => ids.indexOf(id) !== index))];
        });
        assert.deepEqual(duplicateIdsAfterDraftTransition, [],
            `Duplicate IDs appeared after viewport/draft transitions: ${duplicateIdsAfterDraftTransition.join(", ")}`);

        await sharedPage.getByTestId("loading-to-empty").click();
        const emptyPanel = sharedPage.getByTestId("loading-transition-panel").locator(".swiftaid-state-panel__announcer");
        await sharedPage.waitForFunction(() => document.querySelector("[data-testid='loading-transition-panel'] .swiftaid-state-panel__announcer")?.textContent.includes("Synthetic reports loading"));
        assert.equal(await emptyPanel.getAttribute("aria-live"), "polite");
        await sharedPage.getByTestId("reset-loading-state").click();
        await sharedPage.getByTestId("loading-to-information").click();
        const infoPanel = sharedPage.getByTestId("loading-transition-panel").locator(".swiftaid-state-panel__announcer");
        await sharedPage.waitForFunction(() => document.querySelector("[data-testid='loading-transition-panel'] .swiftaid-state-panel__announcer")?.textContent.includes("Synthetic reports loading"));
        assert.equal(await infoPanel.getAttribute("aria-live"), "polite");
        assert.equal(await sharedPage.locator(".swiftaid-state-panel__announcer").count(), 2,
            "Each mounted state panel should retain a polite live region in the DOM");
        await sharedPage.getByTestId("populated-to-empty").click();
        await sharedPage.getByText("No live queue was queried.", { exact: true }).waitFor({ state: "visible" });

        const sessionsSurface = sharedPage.getByTestId("public-sessions-surface");
        await sessionsSurface.getByRole("tab", { name: "Reminders" }).click();
        await sessionsSurface.getByText("Reminder choices are not available right now.", { exact: true }).waitFor();
        assert.equal(await sessionsSurface.getByRole("button", { name: /Turn on reminders|Turn off reminders/i }).count(), 0,
            "The synthetic session fixture must not expose reminder actions without a real capability");

        const educationSurface = sharedPage.getByTestId("synthetic-education-surface");
        const externalEducationLinks = await educationSurface.locator("a[href]")
            .evaluateAll(links => links.map(link => link.href).filter(href => /^https?:/i.test(href)));
        assert.deepEqual(externalEducationLinks, [],
            `Synthetic education fixture must not expose external source links: ${JSON.stringify(externalEducationLinks)}`);
        assert.equal(await educationSurface.getByRole("button", { name: /download|save for offline/i }).count(), 0,
            "The local education fixture must not claim download or device-save capability");
        await educationSurface.getByLabel("Search materials").fill("Privacy notice");
        await educationSurface.getByRole("heading", { name: "Privacy notice review process" }).waitFor({ state: "visible" });
        await sharedPage.getByTestId("education-fixture-state")
            .getByText(/Search filters a static synthetic list in page memory.*not approved for public use/i)
            .waitFor({ state: "visible" });
        await educationSurface.getByRole("heading", { name: "Evidence handling process" }).waitFor({ state: "detached" });
        assert.equal(await educationSurface.getByRole("heading", { name: "Evidence handling process" }).count(), 0,
            "Synthetic education search should filter the fixture list");
        await educationSurface.getByText("Review needed", { exact: true }).waitFor({ state: "visible" });
        assert.equal(await educationSurface.getByRole("button", { name: "View material" }).count(), 0,
            "The read-only synthetic education fixture must not claim a content-view action");

        await sharedPage.setViewportSize({ width: 1024, height: 900 });
        const reportSurface = sharedPage.getByTestId("public-report-surface");
        await reportSurface.getByTestId("report-capability-state")
            .getByText("Report submission and draft saving are unavailable in this read-only synthetic fixture.", { exact: true })
            .waitFor();
        await reportSurface.getByRole("button", { name: "Review report" }).click();
        assert.equal(await reportSurface.getByRole("button", { name: "Send report" }).isDisabled(), true,
            "The synthetic fixture must not enable report submission without a connected service");
        assert.equal(await sharedPage.getByText(/DEMO-TESTHOST|Practice reference/i).count(), 0,
            "The synthetic fixture must not show a fake receipt or practice-success page");

        const externalSharedRequests = sharedRequests.filter(url => /^https?:\/\/(?!127\.0\.0\.1)/i.test(url));
        const apiRequests = sharedRequests.filter(url => /\/api(?:\/|\?|$)/i.test(new URL(url).pathname));
        const fixtureFavicon404 = sharedConsoleErrorLocations.some(error =>
            error.location.url.endsWith("/favicon.ico") && /404 \(Not Found\)/i.test(error.text));
        const appConsoleErrors = sharedConsoleErrors.filter((_, index) =>
            !sharedConsoleErrorLocations[index].location.url.endsWith("/favicon.ico"));
        const sharedNetworkDiagnostics = {
            consoleErrors: sharedConsoleErrors,
            appConsoleErrors,
            testHostFavicon404: fixtureFavicon404,
            consoleErrorLocations: sharedConsoleErrorLocations,
            pageErrors: sharedPageErrors,
            failedResponses: sharedFailedResponses,
            failedRequests: sharedFailedRequests,
            externalRequests: externalSharedRequests,
            apiRequests
        };
        sharedNetworkDiagnostics.requests = sharedRequests;
        console.log(JSON.stringify({ sharedNetworkDiagnostics }));
        assert.deepEqual(sharedPageErrors, [], `Shared surface page errors: ${JSON.stringify(sharedNetworkDiagnostics)}`);
        assert.deepEqual(appConsoleErrors, [], `Shared surface app console errors: ${JSON.stringify(sharedNetworkDiagnostics)}`);
        assert.deepEqual(externalSharedRequests, [], `Shared surface attempted external requests: ${JSON.stringify(sharedNetworkDiagnostics)}`);
        assert.deepEqual(apiRequests, [], `Shared surface called API routes: ${JSON.stringify(apiRequests)}`);
        await sharedPage.close();
        await context.close();

        const externalRequests = requests.filter(url => /^https?:\/\/(?!127\.0\.0\.1)/i.test(url));
        assert.deepEqual(externalRequests, [], `Unexpected external requests: ${JSON.stringify(externalRequests)}`);
        console.log(JSON.stringify({
            scenarioResults: {
                statePanelAccessibleRegionNames: true,
                statePanelHeadingIdsUnique: true,
                guidedReportRequiresSubjectAndPreservesDraft: true,
                guidedReportAllowsUnknownTime: true,
                guidedReportBlocksStaleCountryUntilCurrentCountrySelected: true,
                guidedReportSubmissionUsesSyntheticCallbackAndHonestReceiptCopy: true,
                initialGifFallback: true,
                lottieReady: lottieSvgOrCanvas > 0,
                postReadySyntheticErrorRestoredFallbackAndStatus: true,
                preReadySyntheticErrorRestoredFallbackAndStatus: earlyMalformedRequestSeen
            },
            expectedSyntheticRuntimeErrors,
            expectedEarlySyntheticRuntimeErrors,
            earlyFailureUnhandledErrors: earlyFailureErrors,
            externalRequests,
            retainedScreenshots: screenshots.concat(path.relative(process.cwd(), path.join(artifactDirectory, "fixture-390-light-early-lottie-fallback.png"))),
            sharedSurfaceResults: {
                route: "/shared-surfaces",
                viewportWidths: sharedWidths,
                layout: sharedLayout,
                darkTheme,
                draftSharedAcrossMudHiddenBreakpoints: true,
                liveRegionDomSemantics: true,
                sessionReminderChoicesUnavailable: true,
                educationSearchReviewNeededAndReadOnly: true,
                syntheticSessionCalendarLinkIsLocal: calendarUrl.origin === new URL(sharedPage.url()).origin,
                syntheticReportSubmissionUnavailable: true,
                keyboardSkipLinkHashNavigation: true,
                keyboardSkipLinkFocus: skipLinkFocus,
                keyboardSkipLinkNativeFallback: skipLinkFallbackTrace,
                sharedPageErrors,
                sharedConsoleErrors,
                externalRequests: externalSharedRequests,
                apiRequests,
                screenshots: screenshotsByWidth
            }
        }, null, 2));
        assert.equal(skipLinkFocus.activeElementId, "shared-surfaces-main",
            `Skip link did not move focus to the main content: ${JSON.stringify(skipLinkFocus)}`);
        assert.equal(skipLinkFallbackTrace.fallbackWorked, true,
            `Native skip-link fallback failed when the module was unavailable: ${JSON.stringify(skipLinkFallbackTrace)}`);
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

