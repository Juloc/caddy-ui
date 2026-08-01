(() => {
    "use strict";

    const storageKey = "caddy-ui-theme";

    try {
        const preference = localStorage.getItem(storageKey);
        document.documentElement.dataset.theme = preference === "light" || preference === "dark" ? preference : "system";
    } catch {
        document.documentElement.dataset.theme = "system";
    }
})();
