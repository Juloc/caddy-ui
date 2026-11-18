(() => {
    const labels = document.currentScript.dataset;
    const rows = document.getElementById('live-rows');
    const state = document.getElementById('live-state');
    let stream = null;
    let pageActive = true;
    let lastSequence = 0;
    const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[character]));

    function updateStream() {
        if (document.hidden || !pageActive) {
            if (stream) stream.close();
            stream = null;
            state.textContent = labels.connectingLabel;
            state.className = 'status-badge status-badge--neutral';
            return;
        }
        if (stream) return;

        stream = new EventSource(lastSequence ? `${labels.streamUrl}&after=${lastSequence}` : labels.streamUrl);
        stream.onopen = () => {
            state.textContent = labels.liveLabel;
            state.className = 'status-badge status-badge--ok';
        };
        stream.addEventListener('request', event => {
            const item = JSON.parse(event.data);
            if (event.lastEventId) lastSequence = Number(event.lastEventId);
            const row = document.createElement('tr');
            const duration = Number(item.durationMilliseconds || 0).toFixed(1);
            row.innerHTML = `<td>${escapeHtml(new Date(item.occurredAt).toLocaleString(document.documentElement.lang || undefined))}</td><td><strong>${escapeHtml(item.host)}</strong><small><code>${escapeHtml(item.method)} ${escapeHtml(item.path)}</code></small></td><td>${escapeHtml(item.status)}</td><td>${escapeHtml(item.requestType)}</td><td>${escapeHtml(item.actorType)}</td><td>${escapeHtml(duration)} ms</td>`;
            rows.prepend(row);
            document.querySelector('[data-live-empty]')?.remove();
            while (rows.children.length > 100) rows.lastElementChild.remove();
        });
        stream.onerror = () => {
            state.textContent = labels.reconnectLabel;
            state.className = 'status-badge status-badge--warning';
        };
    }

    document.addEventListener('visibilitychange', updateStream);
    window.addEventListener('pagehide', () => {
        pageActive = false;
        updateStream();
    });
    window.addEventListener('pageshow', () => {
        pageActive = true;
        updateStream();
    });
    updateStream();
})();
