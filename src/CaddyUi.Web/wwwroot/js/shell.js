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
    const appContent = document.querySelector(".app-content");
    const skipLink = document.querySelector(".skip-link");
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
        const label = collapsed
            ? collapseButton?.dataset.labelExpand ?? "Expand sidebar"
            : collapseButton?.dataset.labelCollapse ?? "Collapse sidebar";
        collapseButton?.setAttribute("aria-label", label);

        if (persist) {
            writePreference(sidebarStorageKey, String(collapsed));
        }
    }

    function applyLocalTimes(root = document) {
        const locale = document.documentElement.lang || undefined;
        root.querySelectorAll("time[data-local-time]").forEach(element => {
            const value = element.getAttribute("datetime");
            if (!value) {
                return;
            }

            const date = new Date(value);
            if (Number.isNaN(date.getTime())) {
                return;
            }

            element.textContent = new Intl.DateTimeFormat(locale, {
                dateStyle: "medium",
                timeStyle: "medium"
            }).format(date);
            element.title = date.toISOString();
        });
    }

    function setInert(element, isInert) {
        if (!element) {
            return;
        }

        element.inert = isInert;
        if (isInert) {
            element.setAttribute("aria-hidden", "true");
        } else {
            element.removeAttribute("aria-hidden");
        }
    }

    function setMobileNavigationAccessibility(isOpen) {
        if (!sidebar) {
            return;
        }

        if (!mobileBreakpoint.matches) {
            sidebar.inert = false;
            sidebar.removeAttribute("aria-hidden");
            sidebar.removeAttribute("aria-modal");
            sidebar.removeAttribute("role");
            setInert(appContent, false);
            setInert(skipLink, false);
            setInert(openButton, false);
            return;
        }

        sidebar.inert = !isOpen;
        if (isOpen) {
            sidebar.setAttribute("role", "dialog");
            sidebar.setAttribute("aria-modal", "true");
            sidebar.removeAttribute("aria-hidden");
        } else {
            sidebar.removeAttribute("role");
            sidebar.removeAttribute("aria-modal");
            sidebar.setAttribute("aria-hidden", "true");
        }

        setInert(appContent, isOpen);
        setInert(skipLink, isOpen);
        setInert(openButton, isOpen);
    }

    function drawerFocusableElements() {
        if (!sidebar) {
            return [];
        }

        return Array.from(sidebar.querySelectorAll(
            "a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])"
        )).filter(element => !element.hasAttribute("hidden") && element.getAttribute("aria-hidden") !== "true");
    }

    function openMobileNavigation() {
        if (!mobileBreakpoint.matches || shell.classList.contains("is-navigation-open")) {
            return;
        }

        previouslyFocusedElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
        shell.classList.add("is-navigation-open");
        document.body.classList.add("navigation-open");
        openButton?.setAttribute("aria-expanded", "true");
        setMobileNavigationAccessibility(true);
        window.requestAnimationFrame(() => {
            (closeButton ?? drawerFocusableElements()[0])?.focus();
        });
    }

    function closeMobileNavigation(restoreFocus = true) {
        const wasOpen = shell.classList.contains("is-navigation-open");
        shell.classList.remove("is-navigation-open");
        document.body.classList.remove("navigation-open");
        openButton?.setAttribute("aria-expanded", "false");
        setMobileNavigationAccessibility(false);

        if (wasOpen && restoreFocus && previouslyFocusedElement instanceof HTMLElement) {
            previouslyFocusedElement.focus();
        }
        previouslyFocusedElement = null;
    }

    function trapDrawerFocus(event) {
        if (event.key !== "Tab" || !shell.classList.contains("is-navigation-open")) {
            return;
        }

        const focusableElements = drawerFocusableElements();
        if (focusableElements.length === 0) {
            event.preventDefault();
            return;
        }

        const first = focusableElements[0];
        const last = focusableElements[focusableElements.length - 1];
        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    }

    applyTheme(readPreference(themeStorageKey, "system"), false);
    applyCollapsedState(readPreference(sidebarStorageKey, "false") === "true", false);
    setMobileNavigationAccessibility(false);
    applyLocalTimes();

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
            event.preventDefault();
            closeMobileNavigation();
            return;
        }

        trapDrawerFocus(event);
    });

    mobileBreakpoint.addEventListener("change", event => {
        if (!event.matches) {
            closeMobileNavigation(false);
        }
        setMobileNavigationAccessibility(false);
    });
})();
