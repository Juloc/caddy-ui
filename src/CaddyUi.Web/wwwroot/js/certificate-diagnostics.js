(() => {
    const localFormatter = new Intl.DateTimeFormat(undefined, {
        dateStyle: "medium",
        timeStyle: "medium",
        timeZoneName: "short",
    });
    const utcFormatter = new Intl.DateTimeFormat("de-DE", {
        dateStyle: "medium",
        timeStyle: "medium",
        timeZone: "UTC",
        timeZoneName: "short",
    });

    document.querySelectorAll("time[data-local-time]").forEach(element => {
        if (!element.dateTime) return;
        const value = new Date(element.dateTime);
        if (Number.isNaN(value.getTime())) return;
        element.textContent = localFormatter.format(value);
        element.title = `UTC: ${utcFormatter.format(value)}`;
    });

    const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone || "lokale Gerätezeit";
    document.querySelectorAll("[data-local-time-zone]").forEach(element => {
        element.textContent = timeZone;
    });

    const domainForm = document.querySelector("[data-domain-form]");
    if (domainForm) {
        const choices = [...domainForm.querySelectorAll("[data-certificate-choice]")];
        const warning = domainForm.querySelector("[data-certificate-warning]");
        const update = () => {
            if (warning) warning.hidden = choices.some(choice => choice.checked);
        };
        choices.forEach(choice => choice.addEventListener("change", update));
        update();
    }

    document.querySelectorAll("[data-confirm-certificate-retry]").forEach(form => {
        form.addEventListener("submit", event => {
            const confirmed = window.confirm(
                "Aktive Caddy-Konfiguration validieren und mit --force neu laden? Zertifikate und Container werden nicht gelöscht."
            );
            if (!confirmed) event.preventDefault();
        });
    });

    const refreshHost = document.querySelector("[data-certificate-auto-refresh='true']");
    if (refreshHost) {
        window.setTimeout(() => {
            if (!document.hidden) window.location.reload();
        }, 15_000);
    }
})();
