(() => {
    "use strict";

    const supportsModalDialog = typeof HTMLDialogElement !== "undefined" &&
        typeof HTMLDialogElement.prototype.showModal === "function";
    const openers = new WeakMap();

    function focusDialog(dialog) {
        window.requestAnimationFrame(() => {
            const target = dialog.querySelector(
                "[autofocus], input:not([type='hidden']):not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]), a[href]"
            );
            target?.focus();
        });
    }

    function openDialog(dialog, opener) {
        if (!dialog) {
            return false;
        }

        if (opener instanceof HTMLElement) {
            openers.set(dialog, opener);
        }

        if (supportsModalDialog) {
            if (dialog.hasAttribute("open")) {
                dialog.close();
            }
            dialog.showModal();
        } else {
            dialog.setAttribute("open", "");
        }
        focusDialog(dialog);
        return true;
    }

    document.querySelectorAll("[data-dialog-open]").forEach(opener => {
        opener.addEventListener("click", event => {
            const id = opener.getAttribute("data-dialog-open");
            const dialog = id ? document.getElementById(id) : null;
            if (openDialog(dialog, opener)) {
                event.preventDefault();
            }
        });
    });

    document.querySelectorAll("dialog[data-dialog]").forEach(dialog => {
        if (dialog.hasAttribute("open") && supportsModalDialog) {
            openDialog(dialog, null);
        }

        dialog.querySelectorAll("[data-dialog-close]").forEach(closer => {
            closer.addEventListener("click", event => {
                if (supportsModalDialog) {
                    event.preventDefault();
                    dialog.close("cancel");
                }
            });
        });

        dialog.addEventListener("click", event => {
            if (event.target === dialog && supportsModalDialog) {
                dialog.close("cancel");
            }
        });

        dialog.addEventListener("close", () => {
            const opener = openers.get(dialog);
            if (opener?.isConnected) {
                opener.focus();
            }
            openers.delete(dialog);
        });
    });

    const confirmDialog = document.querySelector("[data-confirm-dialog]");
    const confirmMessage = confirmDialog?.querySelector("[data-confirm-message]");
    let pendingForm = null;
    let pendingSubmitter = null;

    document.querySelectorAll("form[data-confirm]").forEach(form => {
        form.addEventListener("submit", event => {
            if (form.dataset.confirmed === "true") {
                delete form.dataset.confirmed;
                return;
            }

            const message = form.dataset.confirm;
            if (!message) {
                return;
            }

            event.preventDefault();
            if (!confirmDialog || !supportsModalDialog) {
                if (window.confirm(message)) {
                    form.dataset.confirmed = "true";
                    form.requestSubmit(event.submitter ?? undefined);
                }
                return;
            }

            pendingForm = form;
            pendingSubmitter = event.submitter;
            if (confirmMessage) {
                confirmMessage.textContent = message;
            }
            openDialog(confirmDialog, event.submitter);
        });
    });

    confirmDialog?.addEventListener("close", () => {
        if (confirmDialog.returnValue === "confirm" && pendingForm) {
            pendingForm.dataset.confirmed = "true";
            pendingForm.requestSubmit(pendingSubmitter ?? undefined);
        }
        pendingForm = null;
        pendingSubmitter = null;
    });
})();
