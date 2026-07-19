(() => {
  const root = document.documentElement;
  const theme = root.dataset.themePreference || "system";
  if (theme !== "system") root.dataset.theme = theme;

  const versionTarget = document.querySelector(".sidebar-bottom");
  if (versionTarget) {
    fetch("/api/health", { cache: "no-store", credentials: "same-origin" })
      .then((response) => response.ok ? response.json() : Promise.reject(new Error(`HTTP ${response.status}`)))
      .then((health) => {
        if (!health.version) return;
        const version = document.createElement("div");
        version.className = "muted";
        version.style.padding = "8px 10px 2px";
        version.style.fontSize = "11px";
        version.textContent = `Caddy UI v${health.version}`;
        versionTarget.appendChild(version);
      })
      .catch(() => {});
  }

  document.querySelector("[data-menu-toggle]")?.addEventListener("click", () => {
    document.body.classList.toggle("nav-open");
  });

  document.querySelectorAll("[data-dialog-open]").forEach((button) => {
    button.addEventListener("click", () => document.getElementById(button.dataset.dialogOpen)?.showModal());
  });
  document.querySelectorAll("[data-dialog-close]").forEach((button) => {
    button.addEventListener("click", () => button.closest("dialog")?.close());
  });
  document.querySelectorAll("dialog[data-auto-open]").forEach((dialog) => dialog.showModal());

  document.querySelectorAll("form[data-confirm]").forEach((form) => {
    form.addEventListener("submit", (event) => {
      if (!window.confirm(form.dataset.confirm)) event.preventDefault();
    });
  });

  const selectAll = document.querySelector("[data-select-all]");
  selectAll?.addEventListener("change", () => {
    document.querySelectorAll("input[name='route_ids']").forEach((item) => { item.checked = selectAll.checked; });
  });

  document.querySelectorAll("[data-filter-table]").forEach((input) => {
    const table = document.getElementById(input.dataset.filterTable);
    input.addEventListener("input", () => {
      const query = input.value.trim().toLowerCase();
      table?.querySelectorAll("tbody tr").forEach((row) => {
        row.hidden = query && !row.textContent.toLowerCase().includes(query);
      });
    });
  });

  document.querySelectorAll("[data-route-column]").forEach((checkbox) => {
    const key = `caddy-ui-column-${checkbox.dataset.routeColumn}`;
    checkbox.checked = window.localStorage.getItem(key) !== "hidden";
    const update = () => {
      document.querySelectorAll(`[data-column="${checkbox.dataset.routeColumn}"]`).forEach((cell) => {
        cell.hidden = !checkbox.checked;
      });
      window.localStorage.setItem(key, checkbox.checked ? "visible" : "hidden");
    };
    checkbox.addEventListener("change", update);
    update();
  });

  const logFilter = document.querySelector("[data-log-filter]");
  const logHost = document.querySelector("[data-log-host]");
  const logStatus = document.querySelector("[data-log-status]");
  const logSeverity = document.querySelector("[data-log-severity]");
  const filterLogs = () => {
    const query = logFilter?.value.trim().toLowerCase() || "";
    document.querySelectorAll(".log-row").forEach((row) => {
      row.hidden = Boolean(
        (query && !row.textContent.toLowerCase().includes(query)) ||
        (logHost?.value && row.dataset.host !== logHost.value) ||
        (logStatus?.value && row.dataset.status !== logStatus.value) ||
        (logSeverity?.value && row.dataset.severity !== logSeverity.value)
      );
    });
  };
  [logFilter, logHost, logStatus, logSeverity].forEach((control) => control?.addEventListener("input", filterLogs));

  document.querySelector("[data-log-download]")?.addEventListener("click", (event) => {
    const rows = [...document.querySelectorAll(".log-row:not([hidden])")];
    if (!rows.length) return;
    event.preventDefault();
    const blob = new Blob([rows.map((row) => row.textContent.trim()).join("\n") + "\n"], { type: "text/plain" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = "caddy-ui-filtered-logs.txt";
    link.click();
    URL.revokeObjectURL(link.href);
  });

  let logTimer = null;
  const liveButton = document.querySelector("[data-live-logs]");
  if (liveButton) {
    const refresh = () => window.location.reload();
    logTimer = window.setInterval(refresh, 10000);
    liveButton.addEventListener("click", () => {
      if (logTimer) {
        window.clearInterval(logTimer);
        logTimer = null;
        liveButton.textContent = "Resume live";
      } else {
        logTimer = window.setInterval(refresh, 10000);
        liveButton.textContent = "Pause live";
      }
    });
  }
})();
