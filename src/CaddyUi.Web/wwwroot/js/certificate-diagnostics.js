(() => {
    "use strict";

    const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    if (timeZone) {
        document.querySelectorAll("[data-local-time-zone]").forEach(element => {
            element.textContent = timeZone;
        });
    }

    const domainForm = document.querySelector("[data-domain-form]");
    if (!domainForm) {
        return;
    }

    const choices = Array.from(domainForm.querySelectorAll("[data-certificate-choice]"));
    const warning = domainForm.querySelector("[data-certificate-warning]");
    const updateWarning = () => {
        if (warning) {
            warning.hidden = choices.some(choice => choice.checked);
        }
    };

    choices.forEach(choice => choice.addEventListener("change", updateWarning));
    updateWarning();
})();
