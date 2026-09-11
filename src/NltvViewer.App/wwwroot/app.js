(() => {
  "use strict";

  const API = Object.freeze({
    days: "./api/v1/days",
    ratings: "./api/v1/ratings",
    status: "./api/v1/status"
  });

  const SERIES = Object.freeze([
    { key: "selected", label: "Selected", seriesClass: "series-selected", className: "" },
    { key: "previousWeek", label: "Prior week", seriesClass: "series-previous-week", className: "chart-line--secondary" },
    { key: "twoWeeksAgo", label: "Two weeks prior", seriesClass: "series-two-weeks-ago", className: "chart-line--secondary chart-line--dashed", dashed: true },
    { key: "previousYear", label: "Prior year", seriesClass: "series-previous-year", className: "chart-line--secondary chart-line--dashed", dashed: true }
  ]);

  const SVG_NS = "http://www.w3.org/2000/svg";
  const DEFAULT_TITLE = "News Ratings";
  const state = { availableDays: [], selectedDate: null, data: null, status: null, requestId: 0 };
  const dom = {};
  const integerFormatter = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });
  const compactFormatter = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 });
  const dateFormatter = new Intl.DateTimeFormat(undefined, { weekday: "long", month: "long", day: "numeric", year: "numeric", timeZone: "UTC" });
  const shortDateFormatter = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", year: "numeric", timeZone: "UTC" });

  document.addEventListener("DOMContentLoaded", initialize);

  function initialize() {
    Object.assign(dom, {
      date: document.querySelector("#report-date"),
      dateSummary: document.querySelector("#date-summary"),
      previous: document.querySelector("#previous-day"),
      next: document.querySelector("#next-day"),
      loading: document.querySelector("#loading-state"),
      message: document.querySelector("#message-state"),
      messageTitle: document.querySelector("#message-title"),
      messageBody: document.querySelector("#message-body"),
      tryAgain: document.querySelector("#try-again"),
      showList: document.querySelector("#show-list"),
      notice: document.querySelector("#notice"),
      statusDot: document.querySelector("#status-dot"),
      statusLabel: document.querySelector("#status-label"),
      lastRefresh: document.querySelector("#last-refresh"),
      pageTitle: document.querySelector("#page-title")
    });

    dom.date.addEventListener("change", () => {
      if (isIsoDate(dom.date.value)) loadRatings(dom.date.value);
    });
    dom.previous.addEventListener("click", () => moveAvailableDay(-1));
    dom.next.addEventListener("click", () => moveAvailableDay(1));
    dom.tryAgain.addEventListener("click", () => state.selectedDate ? loadRatings(state.selectedDate) : bootstrap());

    bootstrap();
  }

  async function bootstrap() {
    showLoading(true);
    const [daysResult, statusResult] = await Promise.allSettled([
      fetchJson(API.days),
      fetchJson(API.status)
    ]);

    if (statusResult.status === "fulfilled") {
      state.status = normalizeStatus(statusResult.value);
      renderStatus();
    } else {
      renderStatusError();
    }

    if (daysResult.status === "rejected") {
      showRequestError(daysResult.reason, "Could not load available report dates.");
      return;
    }

    const daysInfo = normalizeDays(daysResult.value);
    state.availableDays = daysInfo.days;
    configureDateBounds();

    if (!daysInfo.latestAvailableDate) {
      if (state.status?.sourceAvailable === false) {
        showMessage("Report location unavailable", shareErrorText(state.status), true);
        dom.dateSummary.textContent = "Report location unavailable";
        return;
      }
      showMessage("No reports found", "There are no matching reports available yet. Check the desktop app's report location and show filters.", false);
      dom.dateSummary.textContent = "No report dates available";
      return;
    }

    await loadRatings(daysInfo.latestAvailableDate);
  }

  async function loadRatings(date) {
    const requestId = ++state.requestId;
    state.selectedDate = date;
    dom.date.value = date;
    updateNavigation();
    showLoading(true);
    clearNotice();

    try {
      const url = `${API.ratings}?date=${encodeURIComponent(date)}`;
      const raw = await fetchJson(url);
      if (requestId !== state.requestId) return;
      state.data = normalizeRatings(raw, date);
      renderRatings();
    } catch (error) {
      if (requestId !== state.requestId) return;
      showRequestError(error, `Could not load ratings for ${formatDate(date)}.`);
    }
  }

  async function fetchJson(url) {
    const response = await fetch(url, { headers: { Accept: "application/json" }, cache: "no-store" });
    const contentType = response.headers.get("content-type") || "";
    let body = null;
    if (contentType.includes("json")) {
      try { body = await response.json(); } catch { body = null; }
    }
    if (!response.ok) {
      const error = new Error(body?.detail || body?.message || `Request failed (${response.status})`);
      error.status = response.status;
      error.body = body;
      throw error;
    }
    if (body === null) throw new Error("The server returned an unsupported response.");
    return body;
  }

  function normalizeDays(raw) {
    const candidates = Array.isArray(raw) ? raw : (raw?.days || raw?.availableDates || []);
    const days = [...new Set(candidates.map(value => typeof value === "string" ? value.slice(0, 10) : "").filter(isIsoDate))].sort();
    const suppliedLatest = raw?.latestAvailableDate || raw?.latestDate || raw?.latest;
    const latestAvailableDate = isIsoDate(suppliedLatest) && days.includes(suppliedLatest) ? suppliedLatest : (days.at(-1) || null);
    return { days, latestAvailableDate };
  }

  function normalizeStatus(raw) {
    return {
      lastRefreshUtc: raw?.lastRefreshUtc || raw?.lastRefresh || raw?.refreshedAtUtc || raw?.lastSuccessfulRefresh || null,
      isStale: Boolean(raw?.isStale || raw?.stale),
      sourceAvailable: raw?.sourceAvailable !== false && raw?.sourceAccessible !== false && raw?.shareAvailable !== false,
      sourceDisplayName: safeString(raw?.sourceDisplayName || raw?.sourceLocation || raw?.location),
      message: safeString(raw?.message || raw?.detail),
      errorCode: safeString(raw?.errorCode || raw?.code),
      displayTitle: safeString(raw?.displayTitle || raw?.siteTitle)
    };
  }

  function normalizeRatings(raw, requestedDate) {
    const comparisons = raw?.comparisonDates || raw?.dates || {};
    const shows = Array.isArray(raw?.shows) ? raw.shows : (Array.isArray(raw?.programs) ? raw.programs : []);
    const inferredDates = inferComparisonDates(shows);
    return {
      selectedDate: normalizeIsoDate(raw?.selectedDate || raw?.date) || requestedDate,
      comparisonDates: {
        selected: normalizeIsoDate(raw?.selectedDate || raw?.date) || requestedDate,
        previousWeek: normalizeIsoDate(comparisons.previousWeek || comparisons.priorWeek || comparisons.d7 || inferredDates.previousWeek),
        twoWeeksAgo: normalizeIsoDate(comparisons.twoWeeksAgo || comparisons.twoWeeksPrior || comparisons.d14 || inferredDates.twoWeeksAgo),
        previousYear: normalizeIsoDate(comparisons.previousYear || comparisons.priorYear || comparisons.d364 || inferredDates.previousYear)
      },
      generatedAtUtc: raw?.generatedAtUtc || raw?.generatedAt || raw?.sourceLastModifiedUtc || null,
      isStale: Boolean(raw?.isStale),
      warnings: Array.isArray(raw?.warnings) ? raw.warnings.map(warning => safeString(warning?.message || warning)).filter(Boolean) : [],
      displayTitle: safeString(raw?.displayTitle || raw?.siteTitle),
      shows: shows.map(normalizeShow).filter(show => show.name)
    };
  }

  function normalizeShow(show, index) {
    const rawSegments = Array.isArray(show?.segments) ? show.segments : (Array.isArray(show?.quarterHours) ? show.quarterHours : []);
    const showState = safeString(show?.state).replace(/[^a-z]/gi, "").toLowerCase();
    const numericState = typeof show?.state === "number" ? show.state : null;
    return {
      id: `show-${index}-${slug(safeString(show?.id || show?.groupId || show?.name || show?.displayName || show?.title))}`,
      name: safeString(show?.name || show?.displayName || show?.title),
      channel: safeString(show?.channel || show?.station || show?.viewingSource),
      rawTitle: safeString(show?.rawTitle || show?.sourceTitle || show?.programName),
      scheduledStart: safeString(show?.scheduledStart || show?.scheduledTime),
      actualStart: safeString(show?.actualStart || show?.actualTime),
      isShifted: Boolean(show?.isShifted || show?.shifted),
      isPreempted: Boolean(show?.isPreempted || show?.preempted || showState === "missingorpreempted" || numericState === 1),
      isInvalidConfiguration: Boolean(showState === "invalidconfiguration" || numericState === 2),
      preemptionNote: safeString(show?.preemptionNote || show?.statusNote || show?.stateMessage),
      segments: rawSegments.map(normalizeSegment)
    };
  }

  function normalizeSegment(segment, index) {
    const current = segment?.current || {};
    const values = segment?.values || {};
    const normalized = {
      label: safeString(segment?.label || segment?.time || segment?.quarterHour || current?.timeLabel) || `QH ${segment?.segmentNumber || index + 1}`,
      time: safeString(segment?.time || segment?.actualTime || current?.timeLabel),
      rawTitle: safeString(segment?.rawTitle || current?.programName),
      isShifted: Boolean(segment?.isShifted || segment?.shifted),
      isPreempted: Boolean(segment?.isPreempted || segment?.preempted)
    };
    for (const series of SERIES) {
      const aliases = series.key === "previousWeek" ? ["previousWeek", "priorWeek", "d7"]
        : series.key === "twoWeeksAgo" ? ["twoWeeksAgo", "twoWeeksPrior", "d14"]
          : series.key === "previousYear" ? ["previousYear", "priorYear", "d364"]
            : ["selected", "current", "value"];
      let candidate;
      for (const key of aliases) {
        if (segment?.[key] !== undefined) { candidate = segment[key]; break; }
        if (values?.[key] !== undefined) { candidate = values[key]; break; }
      }
      if (candidate === undefined && series.key === "selected") {
        candidate = current?.p18PlusImpressions;
      }
      if (candidate === undefined && series.key !== "selected") {
        const period = findPeriodComparison(segment?.comparisons, series.key);
        if (period) {
          candidate = {
            value: period.impressions,
            delta: period.delta?.absolute,
            percentDelta: period.delta?.percentage,
            actualTime: period.actualTimeLabel,
            rawTitle: period.actualProgramName,
            missingReason: period.missingReason
          };
        }
      }
      normalized[series.key] = normalizeValue(candidate);
    }
    return normalized;
  }

  function normalizeValue(raw) {
    if (typeof raw === "number") return Number.isFinite(raw) ? { value: raw } : { value: null };
    if (raw && typeof raw === "object") {
      const number = finiteOrNull(raw.value ?? raw.p18PlusImpressions ?? raw.impressions);
      return {
        value: number,
        delta: finiteOrNull(raw.delta),
        percentDelta: finiteOrNull(raw.percentDelta),
        actualTime: safeString(raw.actualTime),
        rawTitle: safeString(raw.rawTitle),
        missingReason: safeString(raw.missingReason)
      };
    }
    return { value: null, missingReason: safeString(raw) };
  }

  function inferComparisonDates(shows) {
    const result = {};
    for (const show of shows) {
      const segments = show?.segments || show?.quarterHours || [];
      for (const segment of segments) {
        for (const series of SERIES.slice(1)) {
          const comparison = findPeriodComparison(segment?.comparisons, series.key);
          if (!result[series.key] && comparison?.targetDate) result[series.key] = comparison.targetDate;
        }
      }
    }
    return result;
  }

  function findPeriodComparison(comparisons, seriesKey) {
    if (!Array.isArray(comparisons)) return null;
    const expected = seriesKey === "previousWeek" ? ["last-week", "previous-week", "d7"]
      : seriesKey === "twoWeeksAgo" ? ["two-weeks-ago", "two-weeks-prior", "d14"]
        : ["last-year", "previous-year", "d364"];
    return comparisons.find(item => expected.includes(safeString(item?.period?.id || item?.periodId || item?.id).toLowerCase())) || null;
  }

  function renderRatings() {
    showLoading(false);
    dom.message.hidden = true;
    dom.showList.replaceChildren();
    dom.date.value = state.data.selectedDate;
    state.selectedDate = state.data.selectedDate;
    updateNavigation();
    dom.dateSummary.textContent = formatDate(state.data.selectedDate);
    applyDisplayTitle(state.data.displayTitle || state.status?.displayTitle);

    renderWarnings();
    if (state.data.shows.length === 0) {
      showMessage("No matching shows", `No configured show matched the report for ${formatDate(state.data.selectedDate)}.`, false);
      return;
    }

    for (const show of state.data.shows) dom.showList.append(createShowCard(show));
    dom.showList.hidden = false;
  }

  function createShowCard(show) {
    const article = element("article", "show-card");
    article.setAttribute("aria-labelledby", `${show.id}-title`);

    const header = element("header", "show-card__header");
    const headingBlock = document.createElement("div");
    const heading = element("h2", "", show.name);
    heading.id = `${show.id}-title`;
    headingBlock.append(heading);

    const meta = element("div", "show-meta");
    if (show.channel) meta.append(element("span", "", `Channel ${show.channel}`));
    if (show.scheduledStart) meta.append(element("span", "", `Scheduled ${show.scheduledStart}`));
    if (show.actualStart) meta.append(element("span", "", `Aired ${show.actualStart}`));
    if (show.rawTitle) meta.append(element("span", "raw-title", `Report: ${show.rawTitle}`));
    if (meta.childElementCount) headingBlock.append(meta);
    header.append(headingBlock);

    const flags = element("div", "show-flags");
    if (show.isShifted) flags.append(element("span", "flag flag--shifted", "Shifted time"));
    if (show.isPreempted) flags.append(element("span", "flag flag--preempted", show.preemptionNote || "Preempted"));
    if (show.isInvalidConfiguration) flags.append(element("span", "flag flag--preempted", show.preemptionNote || "Configuration issue"));
    if (flags.childElementCount) header.append(flags);
    article.append(header);

    const body = element("div", "show-card__body");
    const chartPanel = element("section", "chart-panel");
    chartPanel.setAttribute("aria-label", `${show.name} trend chart`);
    chartPanel.append(createLegend());
    chartPanel.append(createChart(show));
    body.append(chartPanel);
    body.append(createTable(show));
    article.append(body);
    return article;
  }

  function createLegend() {
    const legend = element("div", "legend");
    for (const series of SERIES) {
      const item = element("span", `legend-item ${series.seriesClass}${series.dashed ? " legend-item--dashed" : ""}`);
      item.append(element("span", "legend-swatch"));
      item.append(element("span", "", series.label));
      item.append(element("span", "legend-date", formatShortDate(state.data.comparisonDates[series.key])));
      legend.append(item);
    }
    return legend;
  }

  function createChart(show) {
    const wrap = element("div", "chart-wrap");
    const allValues = show.segments.flatMap(segment => SERIES.map(series => segment[series.key]?.value)).filter(Number.isFinite);
    if (!show.segments.length || !allValues.length) {
      wrap.append(element("div", "chart-empty", "No impression values are available for this show."));
      return wrap;
    }

    const width = 760;
    const height = 270;
    const margin = { top: 12, right: 18, bottom: 44, left: 58 };
    const plotWidth = width - margin.left - margin.right;
    const plotHeight = height - margin.top - margin.bottom;
    const maxValue = Math.max(...allValues, 0);
    const niceMax = niceCeiling(maxValue);
    const x = index => margin.left + (show.segments.length <= 1 ? plotWidth / 2 : index * plotWidth / (show.segments.length - 1));
    const y = value => margin.top + plotHeight - (value / niceMax) * plotHeight;
    const svg = svgElement("svg", { viewBox: `0 0 ${width} ${height}`, class: "ratings-chart", role: "img" });
    svg.setAttribute("aria-label", `${show.name}, P18+ impressions by quarter hour for ${SERIES.map(s => `${s.label} ${formatShortDate(state.data.comparisonDates[s.key])}`).join(", ")}`);

    for (let tick = 0; tick <= 4; tick++) {
      const value = niceMax * tick / 4;
      const tickY = y(value);
      svg.append(svgElement("line", { x1: margin.left, y1: tickY, x2: width - margin.right, y2: tickY, class: "chart-grid" }));
      const label = svgElement("text", { x: margin.left - 9, y: tickY + 4, class: "chart-axis-label", "text-anchor": "end" });
      label.textContent = compactFormatter.format(value);
      svg.append(label);
    }

    const labelEvery = Math.max(1, Math.ceil(show.segments.length / 7));
    show.segments.forEach((segment, index) => {
      if (index % labelEvery !== 0 && index !== show.segments.length - 1) return;
      const label = svgElement("text", { x: x(index), y: height - 15, class: "chart-axis-label", "text-anchor": "middle" });
      label.textContent = segment.label;
      svg.append(label);
    });

    for (const series of [...SERIES].reverse()) {
      const runs = contiguousRuns(show.segments.map(segment => segment[series.key]?.value));
      for (const run of runs) {
        const points = run.map(point => `${x(point.index)},${y(point.value)}`).join(" ");
        if (run.length > 1) {
          const line = svgElement("polyline", { points, class: `chart-line ${series.seriesClass} ${series.className}`.trim() });
          svg.append(line);
        }
        for (const point of run) {
          const circle = svgElement("circle", { cx: x(point.index), cy: y(point.value), r: series.key === "selected" ? 3.5 : 2.6, class: `chart-point ${series.seriesClass}` });
          const title = svgElement("title");
          title.textContent = `${show.segments[point.index].label} — ${series.label}: ${formatNumber(point.value)} impressions`;
          circle.append(title);
          svg.append(circle);
        }
      }
    }

    wrap.append(svg);
    return wrap;
  }

  function createTable(show) {
    const panel = element("section", "table-panel");
    panel.append(element("h3", "", "Exact quarter-hour results"));
    const scroll = element("div", "table-scroll");
    scroll.setAttribute("tabindex", "0");
    scroll.setAttribute("role", "region");
    scroll.setAttribute("aria-label", `${show.name} exact ratings table`);
    const table = element("table", "stat-table");
    const thead = document.createElement("thead");
    const headerRow = document.createElement("tr");
    headerRow.append(tableHeader("Quarter hour"));
    headerRow.append(tableHeader(`Selected · ${formatShortDate(state.data.comparisonDates.selected)}`));
    for (const series of SERIES.slice(1)) headerRow.append(tableHeader(`${series.label} · ${formatShortDate(state.data.comparisonDates[series.key])}`));
    thead.append(headerRow);
    table.append(thead);

    const tbody = document.createElement("tbody");
    for (const segment of show.segments) {
      const row = document.createElement("tr");
      const labelCell = document.createElement("td");
      labelCell.append(element("span", "stat-main", segment.label));
      const segmentNotes = [segment.isShifted ? "Shifted" : "", segment.isPreempted ? "Preempted" : "", segment.rawTitle ? `Report: ${segment.rawTitle}` : ""].filter(Boolean);
      if (segmentNotes.length) labelCell.append(element("span", "stat-change", segmentNotes.join(" · ")));
      row.append(labelCell);
      row.append(valueCell(segment.selected));
      for (const series of SERIES.slice(1)) row.append(comparisonCell(segment.selected, segment[series.key]));
      tbody.append(row);
    }
    table.append(tbody);
    scroll.append(table);
    panel.append(scroll);
    panel.append(element("p", "card-footnote", "Change compares the selected date with the value shown in that column. Dash indicates unavailable data."));
    return panel;
  }

  function valueCell(item) {
    const cell = document.createElement("td");
    if (!Number.isFinite(item?.value)) {
      const missing = element("span", "stat-missing", "—");
      if (item?.missingReason) missing.title = item.missingReason;
      cell.append(missing);
      return cell;
    }
    cell.append(element("span", "stat-main", formatNumber(item.value)));
    appendValueMetadata(cell, item);
    return cell;
  }

  function comparisonCell(selected, comparison) {
    const cell = valueCell(comparison);
    if (!Number.isFinite(selected?.value) || !Number.isFinite(comparison?.value)) return cell;
    const change = selected.value - comparison.value;
    const percent = comparison.value === 0 ? null : (change / Math.abs(comparison.value)) * 100;
    const className = change > 0 ? "stat-change stat-change--positive" : change < 0 ? "stat-change stat-change--negative" : "stat-change";
    const sign = change > 0 ? "+" : "";
    const percentText = Number.isFinite(percent) ? ` (${sign}${percent.toFixed(1)}%)` : "";
    cell.append(element("span", className, `${sign}${formatNumber(change)}${percentText}`));
    return cell;
  }

  function appendValueMetadata(cell, item) {
    const details = [item.actualTime ? `Aired ${item.actualTime}` : "", item.rawTitle ? `Report: ${item.rawTitle}` : ""].filter(Boolean);
    if (details.length) cell.append(element("span", "stat-change", details.join(" · ")));
  }

  function renderWarnings() {
    const messages = [];
    if (state.status?.sourceAvailable === false) messages.push(shareErrorText(state.status));
    if (state.data.isStale || state.status?.isStale) messages.push("Data may be stale. The desktop app/server has not completed a recent report refresh.");
    if (state.data.warnings.length) {
      messages.push(`${state.data.warnings.length} report file warnings are available in the desktop app/server log.`);
    }
    if (messages.length) showNotice([...new Set(messages)].join(" "), state.status?.sourceAvailable === false);
  }

  function renderStatus() {
    const status = state.status;
    dom.statusDot.className = `status-dot ${status.sourceAvailable ? "status-dot--ready" : "status-dot--error"}`;
    dom.statusLabel.textContent = status.sourceAvailable ? (status.isStale ? "Reports stale" : "Reports available") : "Report source unavailable";
    dom.lastRefresh.textContent = status.lastRefreshUtc ? `Last refresh ${formatTimestamp(status.lastRefreshUtc)}` : "Refresh time unavailable";
    applyDisplayTitle(status.displayTitle);
  }

  function renderStatusError() {
    dom.statusDot.className = "status-dot status-dot--error";
    dom.statusLabel.textContent = "Server status unavailable";
    dom.lastRefresh.textContent = "";
  }

  function showRequestError(error, fallback) {
    showLoading(false);
    const details = error?.body || {};
    const code = safeString(details.errorCode || details.code).toUpperCase();
    const source = safeString(details.sourceDisplayName || details.sourceLocation || details.location);
    const isShareError = code.includes("SHARE") || code.includes("SOURCE_UNAVAILABLE") || error?.status === 503 || details.sourceAvailable === false;
    if (isShareError) {
      showMessage("Report location unavailable", shareErrorText({ sourceDisplayName: source, message: details.detail || details.message }), true);
    } else if (error?.status === 404) {
      showMessage("No report for this date", fallback, false);
    } else if (error?.status === 422 || error?.status === 415) {
      showMessage("Report format unsupported", details.detail || details.message || "A report was found, but its format is not supported. Check the desktop app/server for details.", false);
    } else {
      showMessage("Ratings unavailable", details.detail || details.message || error?.message || fallback, true);
    }
  }

  function shareErrorText(info) {
    const location = safeString(info?.sourceDisplayName);
    return `Cannot access ${location || "the configured report location"}. Check the desktop app/server for file share permissions.${info?.message ? ` ${safeString(info.message)}` : ""}`;
  }

  function showLoading(visible) {
    dom.loading.hidden = !visible;
    if (visible) {
      dom.message.hidden = true;
      dom.showList.hidden = true;
    }
  }

  function showMessage(title, body, retry) {
    showLoading(false);
    dom.showList.hidden = true;
    dom.messageTitle.textContent = title;
    dom.messageBody.textContent = body;
    dom.tryAgain.hidden = !retry;
    dom.message.hidden = false;
  }

  function showNotice(message, isError = false) {
    dom.notice.textContent = message;
    dom.notice.className = `notice${isError ? " notice--error" : ""}`;
    dom.notice.hidden = false;
  }

  function clearNotice() {
    dom.notice.hidden = true;
    dom.notice.textContent = "";
  }

  function configureDateBounds() {
    dom.date.disabled = false;
    if (state.availableDays.length) {
      dom.date.min = state.availableDays[0];
      dom.date.max = state.availableDays.at(-1);
    }
  }

  function moveAvailableDay(direction) {
    if (!state.availableDays.length) return;
    const current = state.selectedDate || state.availableDays.at(-1);
    let target;
    if (direction < 0) target = [...state.availableDays].reverse().find(day => day < current);
    else target = state.availableDays.find(day => day > current);
    if (target) loadRatings(target);
  }

  function updateNavigation() {
    const date = state.selectedDate;
    dom.previous.disabled = !date || !state.availableDays.some(day => day < date);
    dom.next.disabled = !date || !state.availableDays.some(day => day > date);
  }

  function contiguousRuns(values) {
    const runs = [];
    let run = [];
    values.forEach((value, index) => {
      if (Number.isFinite(value)) run.push({ value, index });
      else if (run.length) { runs.push(run); run = []; }
    });
    if (run.length) runs.push(run);
    return runs;
  }

  function niceCeiling(value) {
    if (!Number.isFinite(value) || value <= 0) return 1;
    const power = 10 ** Math.floor(Math.log10(value));
    const normalized = value / power;
    const nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
    return nice * power;
  }

  function tableHeader(text) {
    const header = document.createElement("th");
    header.scope = "col";
    header.textContent = text;
    return header;
  }

  function element(tag, className = "", text = null) {
    const item = document.createElement(tag);
    if (className) item.className = className;
    if (text !== null) item.textContent = text;
    return item;
  }

  function svgElement(tag, attributes = {}) {
    const item = document.createElementNS(SVG_NS, tag);
    for (const [key, value] of Object.entries(attributes)) item.setAttribute(key, String(value));
    return item;
  }

  function finiteOrNull(value) {
    if (value === null || value === undefined || (typeof value === "string" && value.trim() === "")) {
      return null;
    }

    const number = typeof value === "number" ? value : Number(value);
    return Number.isFinite(number) ? number : null;
  }

  function safeString(value) { return typeof value === "string" ? value.trim() : ""; }
  function slug(value) { return value.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 48) || "ratings"; }
  function applyDisplayTitle(value) {
    const title = safeString(value) || DEFAULT_TITLE;
    dom.pageTitle.textContent = title;
    document.title = title;
  }
  function isIsoDate(value) { return /^\d{4}-\d{2}-\d{2}$/.test(value || "") && !Number.isNaN(Date.parse(`${value}T00:00:00Z`)); }
  function normalizeIsoDate(value) { return typeof value === "string" && isIsoDate(value.slice(0, 10)) ? value.slice(0, 10) : null; }
  function formatDate(value) { return isIsoDate(value) ? dateFormatter.format(new Date(`${value}T00:00:00Z`)) : "Unknown date"; }
  function formatShortDate(value) { return isIsoDate(value) ? shortDateFormatter.format(new Date(`${value}T00:00:00Z`)) : "Unavailable"; }
  function formatNumber(value) { return Number.isFinite(value) ? integerFormatter.format(Math.round(value)) : "—"; }
  function formatTimestamp(value) {
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? "time unavailable" : new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(date);
  }
})();
