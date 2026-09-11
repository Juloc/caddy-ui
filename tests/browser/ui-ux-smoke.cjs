const { chromium } = require("playwright");

const baseURL = process.env.CADDY_UI_TEST_BASE_URL ?? "http://127.0.0.1:18098";
const username = process.env.CADDY_UI_TEST_USERNAME ?? "ci-admin";
const password = process.env.CADDY_UI_TEST_PASSWORD;

if (!password) {
    throw new Error("CADDY_UI_TEST_PASSWORD is required.");
}

function fail(message) {
    throw new Error(message);
}

async function assertNoPageOverflow(page, label) {
    const dimensions = await page.evaluate(() => ({
        scrollWidth: document.documentElement.scrollWidth,
        clientWidth: document.documentElement.clientWidth,
    }));
    if (dimensions.scrollWidth > dimensions.clientWidth + 1) {
        fail(`${label}: page overflows horizontally (${dimensions.scrollWidth}px > ${dimensions.clientWidth}px).`);
    }
}

async function login(page) {
    await page.goto("/Login");
    await page.locator('input[name="Input.Username"]').fill(username);
    await page.locator('input[name="Input.Password"]').fill(password);
    await Promise.all([
        page.waitForURL(url => !url.pathname.endsWith("/Login")),
        page.locator('button[type="submit"]').click(),
    ]);
}

async function verifyThemeSwitching(page) {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto("/Routing");

    async function selectTheme(theme) {
        const button = page.locator(`[data-theme-option="${theme}"]`);
        await button.click();
        const state = await page.evaluate(() => ({
            theme: document.documentElement.dataset.theme,
            background: getComputedStyle(document.body).backgroundColor,
            text: getComputedStyle(document.body).color,
        }));
        if (state.theme !== theme) {
            fail(`Theme ${theme}: data-theme was ${state.theme}.`);
        }
        if (await button.getAttribute("aria-pressed") !== "true") {
            fail(`Theme ${theme}: selected button is not aria-pressed.`);
        }
        return state;
    }

    const light = await selectTheme("light");
    const dark = await selectTheme("dark");
    if (light.background === dark.background || light.text === dark.text) {
        fail("Light and dark themes do not produce distinct computed page colors.");
    }

    await page.emulateMedia({ colorScheme: "dark" });
    const systemDark = await selectTheme("system");
    if (systemDark.background !== dark.background || systemDark.text !== dark.text) {
        fail("System theme does not follow a dark prefers-color-scheme environment.");
    }

    await page.emulateMedia({ colorScheme: "light" });
    const systemLight = await page.evaluate(() => ({
        background: getComputedStyle(document.body).backgroundColor,
        text: getComputedStyle(document.body).color,
    }));
    if (systemLight.background !== light.background || systemLight.text !== light.text) {
        fail("System theme does not follow a light prefers-color-scheme environment.");
    }
}

async function verifyReflow(page) {
    const paths = [
        "/Routing",
        "/Administration/Domains",
        "/Administration/Providers",
        "/Access",
        "/Operations/Dns",
        "/Operations/Cutover",
    ];

    // 1280 CSS px at 400% browser zoom exposes roughly a 320 CSS px viewport.
    await page.setViewportSize({ width: 320, height: 900 });
    for (const path of paths) {
        await page.goto(path);
        await assertNoPageOverflow(page, `${path} at 320px reflow`);
    }
}

async function verifyTwoHundredPercentTextScale(page) {
    const paths = [
        "/Routing",
        "/Administration/Domains",
        "/Administration/Providers",
        "/Access",
        "/Operations/Dns",
    ];

    await page.setViewportSize({ width: 1280, height: 900 });
    for (const path of paths) {
        await page.goto(path);
        await page.evaluate(() => {
            const elements = Array.from(document.querySelectorAll("*")).map(element => ({
                element,
                fontSize: Number.parseFloat(getComputedStyle(element).fontSize),
            }));
            for (const item of elements) {
                if (Number.isFinite(item.fontSize) && item.fontSize > 0) {
                    item.element.style.setProperty("font-size", `${item.fontSize * 2}px`, "important");
                }
            }
        });
        await assertNoPageOverflow(page, `${path} at 200% text scale`);
    }
}

async function verifyDialogFocus(page) {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto("/Administration/Domains");

    const opener = page.locator('[data-dialog-open="create-domain-dialog"]');
    await opener.focus();
    await opener.click();
    const dialog = page.locator("#create-domain-dialog");
    await dialog.waitFor({ state: "visible" });

    const focusIsInsideDialog = await page.evaluate(() => {
        const currentDialog = document.getElementById("create-domain-dialog");
        return Boolean(currentDialog && document.activeElement && currentDialog.contains(document.activeElement));
    });
    if (!focusIsInsideDialog) {
        fail("Create-domain dialog did not move focus into the dialog.");
    }

    await page.keyboard.press("Escape");
    await page.waitForFunction(() => !document.getElementById("create-domain-dialog")?.hasAttribute("open"));
    const focusReturned = await page.evaluate(() =>
        document.activeElement?.getAttribute("data-dialog-open") === "create-domain-dialog");
    if (!focusReturned) {
        fail("Closing the create-domain dialog did not return focus to its opener.");
    }
}

async function verifyMobileNavigationFocus(page) {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/");

    const opener = page.locator("[data-mobile-navigation]");
    await opener.focus();
    await opener.click();
    await page.waitForFunction(() => document.querySelector("[data-shell]")?.classList.contains("is-navigation-open"));

    const openState = await page.evaluate(() => {
        const sidebar = document.querySelector("[data-sidebar]");
        const appContent = document.querySelector(".app-content");
        return {
            role: sidebar?.getAttribute("role"),
            modal: sidebar?.getAttribute("aria-modal"),
            contentInert: Boolean(appContent?.inert),
            focusInside: Boolean(sidebar && document.activeElement && sidebar.contains(document.activeElement)),
        };
    });
    if (openState.role !== "dialog" || openState.modal !== "true" || !openState.contentInert || !openState.focusInside) {
        fail(`Mobile navigation accessibility state is invalid: ${JSON.stringify(openState)}`);
    }

    await page.keyboard.press("Escape");
    await page.waitForFunction(() => !document.querySelector("[data-shell]")?.classList.contains("is-navigation-open"));
    if (await opener.getAttribute("aria-expanded") !== "false") {
        fail("Mobile navigation opener remained expanded after Escape.");
    }
    const focusReturned = await page.evaluate(() =>
        document.activeElement?.hasAttribute("data-mobile-navigation"));
    if (!focusReturned) {
        fail("Closing mobile navigation did not return focus to the opener.");
    }
}

async function saveLanguage(page, language) {
    await page.goto("/Settings");
    const selector = page.locator('select[name="Input.Language"]');
    await selector.selectOption(language);
    await Promise.all([
        page.waitForNavigation(),
        page.locator('.settings-form button[type="submit"]').click(),
    ]);
}

async function verifyLocalizationPreference(page) {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.goto("/Settings");

    const initialLanguage = await page.evaluate(() => document.documentElement.lang);
    const initialSelection = await page.locator('select[name="Input.Language"]').inputValue();
    if (initialLanguage !== "de" || initialSelection !== "de") {
        fail(`Default culture mismatch: html=${initialLanguage}, settings=${initialSelection}.`);
    }

    await page.goto("/Routing");
    const germanDescription = (await page.locator(".page-description").textContent())?.trim();
    if (germanDescription !== "Dienste nach Domain verwalten. Neue Standardrouten brauchen nur einen Namen und ein Upstream-Ziel.") {
        fail(`German routing copy was not rendered: ${germanDescription}`);
    }

    await saveLanguage(page, "en");
    await page.goto("/Routing");
    const englishLanguage = await page.evaluate(() => document.documentElement.lang);
    const englishDescription = (await page.locator(".page-description").textContent())?.trim();
    if (englishLanguage !== "en") {
        fail(`English preference did not set html lang: ${englishLanguage}`);
    }
    if (englishDescription !== "Manage services by domain. New standard routes only need a name and an upstream target.") {
        fail(`English routing copy was not rendered: ${englishDescription}`);
    }

    await saveLanguage(page, "de");
    await page.goto("/Routing");
    const restoredLanguage = await page.evaluate(() => document.documentElement.lang);
    const restoredDescription = (await page.locator(".page-description").textContent())?.trim();
    if (restoredLanguage !== "de" || restoredDescription !== germanDescription) {
        fail(`German preference was not restored: html=${restoredLanguage}, copy=${restoredDescription}`);
    }
}

(async () => {
    const browser = await chromium.launch();
    try {
        const context = await browser.newContext({
            baseURL,
            viewport: { width: 1280, height: 900 },
            colorScheme: "light",
        });
        const page = await context.newPage();
        await login(page);

        await verifyThemeSwitching(page);
        await verifyReflow(page);
        await verifyTwoHundredPercentTextScale(page);
        await verifyDialogFocus(page);
        await verifyMobileNavigationFocus(page);
        await verifyLocalizationPreference(page);

        console.log("UI/UX and localization browser acceptance passed.");
    } finally {
        await browser.close();
    }
})().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
