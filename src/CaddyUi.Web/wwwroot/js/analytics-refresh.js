(() => {
    const labels = document.currentScript.dataset;
    const input = document.querySelector('[name="RefreshSeconds"]');
    const state = document.querySelector('[data-analytics-refresh-state]');
    if (!input || !state) return;
    let timer;
    let controller;
    let failures = 0;
    let pageActive = true;
    const key = 'caddy-ui.analytics-refresh-seconds';
    try {
        const saved = localStorage.getItem(key);
        if (saved !== null && !new URLSearchParams(location.search).has('RefreshSeconds')) input.value = saved;
    } catch { }

    function schedule() {
        clearTimeout(timer);
        const seconds = Number(input.value);
        if (!pageActive || document.hidden || seconds === 0) {
            state.textContent = labels.pausedLabel;
            return;
        }
        state.textContent = failures ? labels.errorLabel : labels.activeLabel;
        timer = setTimeout(refresh, Math.min(300, Math.max(5, seconds || 30) * 2 ** failures) * 1000);
    }

    async function refresh() {
        const current = document.querySelector('[data-analytics-snapshot]');
        if (!current || document.hidden || current.contains(document.activeElement)) { schedule(); return; }
        controller = new AbortController();
        const timeout = setTimeout(() => controller?.abort(), 10000);
        try {
            const response = await fetch(location.href, { credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
            if (!response.ok || response.redirected) throw new Error('Refresh unavailable');
            const replacement = new DOMParser().parseFromString(await response.text(), 'text/html').querySelector('[data-analytics-snapshot]');
            if (!replacement) throw new Error('Snapshot unavailable');
            if (!document.hidden && !current.contains(document.activeElement)) current.replaceChildren(...replacement.childNodes);
            failures = 0;
        } catch (error) {
            if (error.name !== 'AbortError') failures = Math.min(6, failures + 1);
        } finally {
            clearTimeout(timeout);
            controller = null;
            schedule();
        }
    }

    input.addEventListener('change', () => {
        input.value = Number(input.value) === 0 ? '0' : String(Math.min(300, Math.max(5, Number(input.value) || 30)));
        try { localStorage.setItem(key, input.value); } catch { }
        controller?.abort();
        failures = 0;
        schedule();
    });
    document.addEventListener('visibilitychange', () => { if (document.hidden) controller?.abort(); schedule(); });
    window.addEventListener('pagehide', () => { pageActive = false; clearTimeout(timer); controller?.abort(); });
    window.addEventListener('pageshow', () => { pageActive = true; schedule(); });
    schedule();
})();
