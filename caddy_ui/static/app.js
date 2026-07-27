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
        version.className = "muted app-version";
        version.textContent = `Caddy UI v${health.version}`;
        versionTarget.appendChild(version);
      })
      .catch(() => {});
  }

  document.querySelector("[data-menu-toggle]")?.addEventListener("click", () => {
    document.body.classList.toggle("nav-open");
  });
  document.querySelectorAll(".sidebar a").forEach((link) => link.addEventListener("click", () => {
    document.body.classList.remove("nav-open");
  }));

  const problemsPanel = [...document.querySelectorAll(".panel")].find(
    (panel) => panel.querySelector(".panel-header h2")?.textContent.trim() === "Problems",
  );
  const problemBody = problemsPanel?.querySelector("tbody");
  if (problemBody) {
    const rows = [...problemBody.children].filter((element) => element.tagName === "TR");
    const claimed = new Set();
    const groups = [
      {
        key: "upstream", singular: "Upstream unavailable", plural: "Upstreams unavailable",
        matches: (text) => text.includes("upstream is unavailable") || text.includes("upstream unavailable"),
        identify: (text) => (text.match(/([A-Za-z0-9*._-]+)\s+upstream is unavailable/i)?.[1]
          || text.match(/upstream unavailable\s*([A-Za-z0-9*._-]+)/i)?.[1] || "").split(".")[0],
      },
      {
        key: "public", singular: "Public route problem", plural: "Public route problems",
        matches: (text) => text.includes("not publicly reachable") || text.includes("public route unavailable") || text.includes("public dns unavailable"),
        identify: (text) => text.match(/([A-Za-z0-9*._-]+)\s+is not publicly reachable/i)?.[1]
          || text.match(/public (?:route|dns) unavailable\s*([A-Za-z0-9*._-]+)/i)?.[1] || "",
      },
      {
        key: "certificate", singular: "Certificate warning", plural: "Certificate warnings",
        matches: (text) => text.includes("certificate") && (text.includes("expires") || text.includes("expiring")),
        identify: (text) => text.match(/certificate\s*([A-Za-z0-9*._-]+)\s+(?:expires|expiring)/i)?.[1] || "",
      },
    ];
    groups.forEach((group) => {
      const members = rows.filter((row) => !claimed.has(row) && group.matches(row.textContent.toLowerCase().replace(/\s+/g, " ")));
      if (members.length < 2) return;
      members.forEach((row) => claimed.add(row));
      const names = [...new Set(members.map((row) => group.identify(row.textContent.replace(/\s+/g, " ").trim())).filter(Boolean))];
      const count = names.length || members.length;
      const preview = names.length ? `${names.slice(0, 4).join(", ")}${names.length > 4 ? ` +${names.length - 4}` : ""}` : `${members.length} entries`;
      const groupRow = document.createElement("tr");
      const statusCell = document.createElement("td");
      statusCell.innerHTML = '<span class="status bad">Issue</span>';
      const contentCell = document.createElement("td");
      contentCell.colSpan = 3;
      const details = document.createElement("details");
      details.dataset.problemGroup = group.key;
      const summary = document.createElement("summary");
      summary.className = "problem-group-summary";
      const title = document.createElement("strong");
      title.textContent = count === 1 ? group.singular : group.plural;
      const badge = document.createElement("span");
      badge.className = "badge";
      badge.textContent = String(count);
      const previewNode = document.createElement("span");
      previewNode.className = "muted";
      previewNode.textContent = preview;
      summary.append(title, badge, previewNode);
      details.appendChild(summary);
      contentCell.appendChild(details);
      groupRow.append(statusCell, contentCell);
      problemBody.insertBefore(groupRow, members[0]);
      let anchor = groupRow;
      members.forEach((row) => {
        row.hidden = true;
        problemBody.insertBefore(row, anchor.nextSibling);
        anchor = row;
      });
      details.addEventListener("toggle", () => members.forEach((row) => { row.hidden = !details.open; }));
    });
  }

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
      table?.querySelectorAll("tbody tr").forEach((row) => { row.hidden = Boolean(query && !row.textContent.toLowerCase().includes(query)); });
    });
  });
  document.querySelectorAll("[data-route-column]").forEach((checkbox) => {
    const key = `caddy-ui-column-${checkbox.dataset.routeColumn}`;
    checkbox.checked = window.localStorage.getItem(key) !== "hidden";
    const update = () => {
      document.querySelectorAll(`[data-column="${checkbox.dataset.routeColumn}"]`).forEach((cell) => { cell.hidden = !checkbox.checked; });
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
        (query && !row.textContent.toLowerCase().includes(query))
        || (logHost?.value && row.dataset.host !== logHost.value)
        || (logStatus?.value && row.dataset.status !== logStatus.value)
        || (logSeverity?.value && row.dataset.severity !== logSeverity.value)
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
        window.clearInterval(logTimer); logTimer = null; liveButton.textContent = "Resume live";
      } else {
        logTimer = window.setInterval(refresh, 10000); liveButton.textContent = "Pause live";
      }
    });
  }

  document.querySelectorAll("[data-saved-view]").forEach((select) => {
    select.addEventListener("change", () => {
      if (!select.value) return;
      try {
        const query = JSON.parse(select.value);
        const params = new URLSearchParams();
        Object.entries(query).forEach(([key, value]) => { if (value !== "") params.set(key, value); });
        window.location.search = params.toString();
      } catch (_) {
        select.value = "";
      }
    });
  });

  const customButton = document.querySelector("[data-custom-range]");
  const customPanel = document.querySelector("[data-custom-range-panel]");
  customButton?.addEventListener("click", () => {
    if (!customPanel) return;
    customPanel.hidden = !customPanel.hidden;
    if (!customPanel.hidden) customPanel.querySelector("input")?.focus();
  });

  const svgNamespace = "http://www.w3.org/2000/svg";
  const renderChart = (container) => {
    let series = [];
    try { series = JSON.parse(container.dataset.chartSeries || "[]"); } catch (_) { return; }
    const metric = container.dataset.chartMetric || "requests";
    if (!series.length) return;
    const width = Math.max(280, container.clientWidth || 640);
    const height = 230;
    const padding = { top: 16, right: 18, bottom: 34, left: 54 };
    const values = series.map((item) => Number(item[metric]) || 0);
    const maximum = Math.max(...values, 1);
    const minimum = Math.min(...values, 0);
    const span = Math.max(1, maximum - minimum);
    const x = (index) => padding.left + (series.length === 1 ? (width - padding.left - padding.right) / 2 : index / (series.length - 1) * (width - padding.left - padding.right));
    const y = (value) => padding.top + (maximum - value) / span * (height - padding.top - padding.bottom);
    const svg = document.createElementNS(svgNamespace, "svg");
    svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
    svg.setAttribute("role", "img");
    svg.setAttribute("aria-label", `${container.closest("section")?.querySelector("h2")?.textContent || "Analytics"} chart`);
    svg.classList.add("analytics-svg");
    [0, 0.5, 1].forEach((fraction) => {
      const lineY = padding.top + fraction * (height - padding.top - padding.bottom);
      const line = document.createElementNS(svgNamespace, "line");
      line.setAttribute("x1", String(padding.left)); line.setAttribute("x2", String(width - padding.right));
      line.setAttribute("y1", String(lineY)); line.setAttribute("y2", String(lineY)); line.classList.add("chart-grid-line");
      svg.appendChild(line);
    });
    const path = document.createElementNS(svgNamespace, "path");
    path.setAttribute("d", series.map((item, index) => `${index ? "L" : "M"}${x(index).toFixed(1)} ${y(Number(item[metric]) || 0).toFixed(1)}`).join(" "));
    path.classList.add("chart-line");
    svg.appendChild(path);
    series.forEach((item, index) => {
      const group = document.createElementNS(svgNamespace, "a");
      const href = container.dataset.chartDrill || "/logs";
      group.setAttribute("href", href);
      const point = document.createElementNS(svgNamespace, "circle");
      point.setAttribute("cx", String(x(index))); point.setAttribute("cy", String(y(Number(item[metric]) || 0))); point.setAttribute("r", "4");
      point.classList.add("chart-point");
      const title = document.createElementNS(svgNamespace, "title");
      title.textContent = `${item.bucket}: ${Number(item[metric]) || 0} ${container.dataset.chartUnit || ""}`;
      point.appendChild(title); group.appendChild(point); svg.appendChild(group);
    });
    const first = document.createElementNS(svgNamespace, "text");
    first.textContent = String(series[0].bucket || ""); first.setAttribute("x", String(padding.left)); first.setAttribute("y", String(height - 10)); first.classList.add("chart-axis-label");
    const last = document.createElementNS(svgNamespace, "text");
    last.textContent = String(series.at(-1)?.bucket || ""); last.setAttribute("x", String(width - padding.right)); last.setAttribute("y", String(height - 10)); last.setAttribute("text-anchor", "end"); last.classList.add("chart-axis-label");
    const maxLabel = document.createElementNS(svgNamespace, "text");
    maxLabel.textContent = maximum.toFixed(metric === "avg_ms" ? 0 : 0); maxLabel.setAttribute("x", String(padding.left - 8)); maxLabel.setAttribute("y", String(padding.top + 4)); maxLabel.setAttribute("text-anchor", "end"); maxLabel.classList.add("chart-axis-label");
    svg.append(first, last, maxLabel);
    container.replaceChildren(svg);
  };
  const chartContainers = [...document.querySelectorAll("[data-chart-series]")];
  chartContainers.forEach(renderChart);
  if (chartContainers.length && "ResizeObserver" in window) {
    let resizeTimer = null;
    const observer = new ResizeObserver(() => {
      window.clearTimeout(resizeTimer);
      resizeTimer = window.setTimeout(() => chartContainers.forEach(renderChart), 80);
    });
    chartContainers.forEach((container) => observer.observe(container));
  }

  const liveRequests = document.querySelector("[data-live-requests]");
  const liveBody = document.querySelector("[data-live-request-body]");
  const liveStatus = document.querySelector("[data-live-status]");
  let requestStream = null;
  const textCell = (value, className = "") => {
    const cell = document.createElement("td");
    if (className) cell.className = className;
    cell.textContent = value == null || value === "" ? "—" : String(value);
    return cell;
  };
  const addLiveRow = (item) => {
    if (!liveBody) return;
    const empty = liveBody.querySelector(".empty")?.closest("tr");
    empty?.remove();
    const row = document.createElement("tr"); row.className = "request-row live-new-row";
    row.append(
      textCell(item.occurred_at, "nowrap"), textCell(item.host), textCell(item.method), textCell(item.uri, "request-path"),
      textCell(item.status), textCell(`${Math.round(Number(item.duration_ms) || 0)} ms`), textCell(item.bytes_sent), textCell(item.remote_ip),
      textCell(`${item.client_type || ""} ${item.category || ""}`.trim()), textCell(item.user_agent, "ua-cell")
    );
    liveBody.prepend(row);
    while (liveBody.children.length > 300) liveBody.lastElementChild?.remove();
    window.setTimeout(() => row.classList.remove("live-new-row"), 1500);
  };
  const stopLive = () => {
    requestStream?.close(); requestStream = null;
    if (liveRequests) liveRequests.textContent = "Live";
    if (liveStatus) { liveStatus.hidden = true; liveStatus.textContent = ""; }
  };
  liveRequests?.addEventListener("click", () => {
    if (requestStream) { stopLive(); return; }
    const params = new URLSearchParams(window.location.search);
    params.delete("page"); params.delete("tab");
    requestStream = new EventSource(`/api/live/logs?${params.toString()}`);
    liveRequests.textContent = "Pause live";
    if (liveStatus) { liveStatus.hidden = false; liveStatus.textContent = "Live stream connected"; }
    requestStream.onmessage = (event) => { try { addLiveRow(JSON.parse(event.data)); } catch (_) {} };
    requestStream.onerror = () => { if (liveStatus) liveStatus.textContent = "Live stream reconnecting…"; };
  });
  window.addEventListener("pagehide", stopLive);
})();
