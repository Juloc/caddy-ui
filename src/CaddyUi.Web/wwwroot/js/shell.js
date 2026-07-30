(() => {
    "use strict";

    const shell = document.querySelector("[data-shell]");
    if (!shell) {
        return;
    }

    const themeStorageKey = "caddy-ui-theme";
    const sidebarStorageKey = "caddy-ui-sidebar-collapsed";
    const mobileBreakpoint = window.matchMedia("(max-width: 1024px)");
    const themeButtons = Array.from(document.querySelectorAll("[data-theme-option]"));
    const collapseButton = document.querySelector("[data-sidebar-collapse]");
    const openButton = document.querySelector("[data-mobile-navigation]");
    const closeButton = document.querySelector("[data-mobile-navigation-close]");
    const backdrop = document.querySelector("[data-sidebar-backdrop]");
    const sidebar = document.querySelector("[data-sidebar]");
    let previouslyFocusedElement = null;

    function readPreference(key, fallback) {
        try {
            return localStorage.getItem(key) ?? fallback;
        } catch {
            return fallback;
        }
    }

    function writePreference(key, value) {
        try {
            localStorage.setItem(key, value);
        } catch {
            // The interface remains usable when browser storage is unavailable.
        }
    }

    function applyTheme(preference, persist) {
        const theme = preference === "light" || preference === "dark" ? preference : "system";
        document.documentElement.dataset.theme = theme;
        themeButtons.forEach(button => {
            button.setAttribute("aria-pressed", String(button.dataset.themeOption === theme));
        });

        if (persist) {
            writePreference(themeStorageKey, theme);
        }
    }

    function applyCollapsedState(collapsed, persist) {
        shell.dataset.sidebarCollapsed = String(collapsed);
        collapseButton?.setAttribute("aria-pressed", String(collapsed));
        collapseButton?.setAttribute("aria-label", collapsed ? "Seitenleiste ausklappen" : "Seitenleiste einklappen");

        if (persist) {
            writePreference(sidebarStorageKey, String(collapsed));
        }
    }

    function openMobileNavigation() {
        if (!mobileBreakpoint.matches) {
            return;
        }

        previouslyFocusedElement = document.activeElement;
        shell.classList.add("is-navigation-open");
        document.body.classList.add("navigation-open");
        openButton?.setAttribute("aria-expanded", "true");
        window.requestAnimationFrame(() => {
            sidebar?.querySelector("a, button")?.focus();
        });
    }

    function closeMobileNavigation(restoreFocus = true) {
        shell.classList.remove("is-navigation-open");
        document.body.classList.remove("navigation-open");
        openButton?.setAttribute("aria-expanded", "false");

        if (restoreFocus && previouslyFocusedElement instanceof HTMLElement) {
            previouslyFocusedElement.focus();
        }
        previouslyFocusedElement = null;
    }

    applyTheme(readPreference(themeStorageKey, "system"), false);
    applyCollapsedState(readPreference(sidebarStorageKey, "false") === "true", false);

    themeButtons.forEach(button => {
        button.addEventListener("click", () => applyTheme(button.dataset.themeOption, true));
    });

    collapseButton?.addEventListener("click", () => {
        applyCollapsedState(shell.dataset.sidebarCollapsed !== "true", true);
    });

    openButton?.addEventListener("click", openMobileNavigation);
    closeButton?.addEventListener("click", () => closeMobileNavigation());
    backdrop?.addEventListener("click", () => closeMobileNavigation());

    sidebar?.querySelectorAll("a").forEach(link => {
        link.addEventListener("click", () => {
            if (mobileBreakpoint.matches) {
                closeMobileNavigation(false);
            }
        });
    });

    document.addEventListener("keydown", event => {
        if (event.key === "Escape" && shell.classList.contains("is-navigation-open")) {
            closeMobileNavigation();
        }
    });

    mobileBreakpoint.addEventListener("change", event => {
        if (!event.matches) {
            closeMobileNavigation(false);
        }
    });
})();
