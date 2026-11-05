(() => {
  "use strict";

  const API = Object.freeze({
    days: "./api/v1/days",
    ratings: "./api/v1/ratings",
    status: "./api/v1/status"
  });

  const SELECTED_SERIES = Object.freeze({ key: "selected", label: "Selected", seriesClass: "series-selected", className: "" });
  const MAX_COMPARISONS = 4;
  const LEGACY_COMPARISONS = Object.freeze(["last-week", "two-weeks-ago", "last-year"]);
  const COMPARISON_PRESETS = Object.freeze([
    { id: "previous-day", label: "Previous day" },
    { id: "last-week", label: "Last week" },
    { id: "two-weeks-ago", label: "Two weeks ago" },
    { id: "last-month", label: "Last month" },
    { id: "last-year", label: "Last year" },
    { id: "specific-date", label: "Specific date" }
  ]);

  const SVG_NS = "http://www.w3.org/2000/svg";
  const DEFAULT_TITLE = "News Ratings";
  const STORAGE_KEY = `nltv-viewer:selection:${location.pathname.replace(/\/[^/]*$/, "/")}`;
  const DEFAULT_DEMOGRAPHIC = Object.freeze({ id: "p18-plus", label: "P18+", available: true });
  const DEFAULT_METRIC = Object.freeze({ id: "imp", label: "IMP", unit: "impressions", format: "number", decimals: 0, available: true });
  const state = {
    availableDays: [],
    selectedDate: null,
    data: null,
    status: null,
    requestId: 0,
    isLoading: false,
    selectionRestored: false,
    capabilityDefaultsApplied: false,
    demographicId: DEFAULT_DEMOGRAPHIC.id,
    metricId: DEFAULT_METRIC.id,
    metricByDemographic: Object.create(null),
    comparisonSlots: LEGACY_COMPARISONS.map(spec => ({ preset: spec, date: "" })),
    comparisonExplicit: false,
    comparisonConfigured: false,
    capabilityNotice: "",
    capabilities: {
      demographics: [DEFAULT_DEMOGRAPHIC],
      metrics: [DEFAULT_METRIC],
      availableSelections: null,
      metricsByDemographic: Object.create(null)
    }
  };
  const dom = {};
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
      appVersion: document.querySelector("#app-version"),
      pageTitle: document.querySelector("#page-title"),
      measureSummary: document.querySelector("#measure-summary"),
      demographic: document.querySelector("#demographic-select"),
      metric: document.querySelector("#metric-select"),
      selectorStatus: document.querySelector("#selector-status"),
      comparisonPanel: document.querySelector("#comparison-panel"),
      comparisonSummary: document.querySelector("#comparison-summary"),
      comparisonSlots: document.querySelector("#comparison-slots"),
      addComparison: document.querySelector("#add-comparison"),
      comparisonStatus: document.querySelector("#comparison-status")
    });

    restoreSelection();

    dom.date.addEventListener("change", () => {
      if (isIsoDate(dom.date.value)) loadRatings(dom.date.value);
    });
    dom.previous.addEventListener("click", () => moveAvailableDay(-1));
    dom.next.addEventListener("click", () => moveAvailableDay(1));
    dom.demographic.addEventListener("change", () => changeSelection("demographic", dom.demographic.value));
    dom.metric.addEventListener("change", () => changeSelection("metric", dom.metric.value));
    dom.addComparison.addEventListener("click", addComparisonSlot);
    dom.tryAgain.addEventListener("click", () => state.selectedDate ? loadRatings(state.selectedDate) : bootstrap());

    renderSelectors();
    renderComparisonControls();
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
      if (!state.comparisonExplicit && state.status.defaultComparisons.length) {
        state.comparisonSlots = state.status.defaultComparisons.map(specToSlot);
        state.comparisonConfigured = true;
        renderComparisonControls();
      }
      renderStatus();
    } else {
      renderStatusError();
    }

    if (daysResult.status === "rejected") {
      showRequestError(daysResult.reason, "Could not load available report dates.");
      return;
    }

    updateCapabilities(daysResult.value, statusResult.status === "fulfilled" ? statusResult.value : null);
    renderSelectors();
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

  async function loadRatings(date, preserveCapabilityNotice = false) {
    const requestId = ++state.requestId;
    if (!preserveCapabilityNotice) state.capabilityNotice = "";
    state.selectedDate = date;
    dom.date.value = date;
    updateNavigation();
    showLoading(true);
    clearNotice();

    try {
      const parameters = new URLSearchParams({ date });
      if (state.demographicId) parameters.set("demographic", state.demographicId);
      if (state.metricId) parameters.set("metric", state.metricId);
      if (state.comparisonExplicit || state.comparisonConfigured) {
        for (const spec of comparisonSpecs()) parameters.append("compare", spec);
      }
      const url = `${API.ratings}?${parameters}`;
      const raw = await fetchJson(url);
      if (requestId !== state.requestId) return;
      updateCapabilities(raw);
      applyResponseSelections(raw);
      state.data = normalizeRatings(raw, date);
      if (!state.comparisonExplicit) syncComparisonSlots(state.data.comparisonDescriptors);
      renderSelectors();
      renderRatings();
    } catch (error) {
      if (requestId !== state.requestId) return;
      if (isSelectionError(error)) {
        const previousDemographic = state.demographicId;
        const previousMetric = state.metricId;
        updateCapabilities(error.body);
        renderSelectors();
        const changed = !sameId(previousDemographic, state.demographicId) || !sameId(previousMetric, state.metricId);
        if (changed && selectedDemographic()?.available && selectedMetric()?.available) {
          state.capabilityNotice = `The requested audience or measure is unavailable for ${formatDate(date)}. Showing ${selectedDemographic().label} · ${metricContextLabel(selectedMetric())}.`;
          await loadRatings(date, true);
          return;
        }
        showMessage("Measure unavailable", error.body?.message || "The selected audience and measure are not available for this report date.", false);
        return;
      }
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
      displayTitle: safeString(raw?.displayTitle || raw?.siteTitle),
      version: safeString(raw?.version || raw?.appVersion || raw?.applicationVersion || raw?.serverVersion || raw?.informationalVersion),
      defaultComparisons: normalizeComparisonSpecList(raw?.defaultComparisons || raw?.comparisons || raw?.comparisonDefaults)
    };
  }

  function updateCapabilities(...sources) {
    const demographics = findCapabilityList(sources, ["demographics", "availableDemographics", "audiences"]);
    const metrics = findCapabilityList(sources, ["metrics", "availableMetrics", "measures"]);
    const selections = findCapabilityList(sources, ["availableSelections", "selections", "validSelections"]);
    const metricMap = findCapabilityMap(sources, ["metricsByDemographic", "metricsByAudience", "measuresByDemographic"]);
    const defaultDemographicId = findCapabilityValue(sources, ["defaultDemographicId", "defaultAudienceId"]);
    const defaultMetricId = findCapabilityValue(sources, ["defaultMetricId", "defaultMeasureId"]);
    const previousDemographic = state.demographicId;
    const previousMetric = state.metricId;

    if (demographics !== null) {
      state.capabilities.demographics = uniqueOptions(demographics.map(item => normalizeCapability(item, "demographic")).filter(Boolean));
    }
    if (metrics !== null) {
      state.capabilities.metrics = uniqueOptions(metrics.map(item => normalizeCapability(item, "metric")).filter(Boolean));
    }
    if (selections !== null) {
      state.capabilities.availableSelections = selections.map(normalizeSelectionPair).filter(Boolean);
    }
    if (metricMap !== null) state.capabilities.metricsByDemographic = normalizeMetricMap(metricMap);

    const useCapabilityDefaults = !state.capabilityDefaultsApplied && !state.selectionRestored;
    const requestedDemographicId = useCapabilityDefaults
      ? defaultDemographicId || state.demographicId
      : state.demographicId;
    state.demographicId = chooseOptionId(demographicsForSelection(), requestedDemographicId, ["p18", "p18plus"]);
    const rememberedMetric = state.metricByDemographic[normalizeToken(state.demographicId)]
      || (useCapabilityDefaults ? defaultMetricId : null)
      || state.metricId;
    state.metricId = chooseOptionId(metricsForDemographic(), rememberedMetric, ["imp", "impressions"]);
    if (state.demographicId && state.metricId) state.metricByDemographic[normalizeToken(state.demographicId)] = state.metricId;
    if (demographics !== null || metrics !== null || selections !== null) state.capabilityDefaultsApplied = true;

    if (state.selectedDate && ((previousDemographic && !sameId(previousDemographic, state.demographicId)) || (previousMetric && !sameId(previousMetric, state.metricId)))) {
      const demographicLabel = selectedDemographic()?.label || "available audience";
      const metricLabel = selectedMetric()?.label || "available measure";
      state.capabilityNotice = `The previous audience or measure is unavailable for this report date. Showing ${demographicLabel} · ${metricLabel}.`;
    }
  }

  function findCapabilityList(sources, keys) {
    for (const source of sources) {
      if (!source || typeof source !== "object") continue;
      const containers = [
        source.capabilities,
        source.capabilityMetadata,
        source.metadata?.capabilities,
        source.options,
        source
      ].filter(Boolean);
      for (const container of containers) {
        for (const key of keys) {
          if (Object.prototype.hasOwnProperty.call(container, key) && Array.isArray(container[key])) {
            return container[key];
          }
        }
      }
    }
    return null;
  }

  function findCapabilityMap(sources, keys) {
    for (const source of sources) {
      if (!source || typeof source !== "object") continue;
      const containers = [source.capabilities, source.capabilityMetadata, source.metadata?.capabilities, source.options, source].filter(Boolean);
      for (const container of containers) {
        for (const key of keys) {
          if (container[key] && typeof container[key] === "object" && !Array.isArray(container[key])) return container[key];
        }
      }
    }
    return null;
  }

  function findCapabilityValue(sources, keys) {
    for (const source of sources) {
      if (!source || typeof source !== "object") continue;
      const containers = [
        source.capabilities,
        source.capabilityMetadata,
        source.metadata?.capabilities,
        source.options,
        source
      ].filter(Boolean);
      for (const container of containers) {
        for (const key of keys) {
          const value = extractSelectionId(container[key]);
          if (value) return value;
        }
      }
    }
    return null;
  }

  function normalizeCapability(item, kind) {
    if (typeof item === "string") {
      const value = safeString(item);
      return value ? { id: value, label: value, available: true } : null;
    }
    if (!item || typeof item !== "object") return null;
    const id = safeString(item.id || item.key || item.value || item.code || item.name);
    if (!id) return null;
    const option = {
      id,
      label: safeString(item.label || item.displayName || item.name) || id,
      available: item.available !== false && item.isAvailable !== false && item.enabled !== false,
      unavailableReason: safeString(item.unavailableReason || item.reason || item.message)
    };
    if (kind === "metric") {
      option.unit = safeString(item.unit || item.units || item.unitLabel);
      option.format = safeString(item.format || item.valueFormat || item.displayFormat).toLowerCase();
      option.decimals = clampDecimals(item.decimals ?? item.decimalPlaces ?? item.precision);
      option.prefix = safeString(item.prefix);
      option.suffix = safeString(item.suffix);
      option.demographicIds = normalizeIdList(item.demographics || item.demographicIds || item.audiences || item.audienceIds);
    } else {
      const nestedMetrics = firstArrayProperty(item, ["metrics", "availableMetrics", "measures"]);
      option.metrics = nestedMetrics === null
        ? null
        : uniqueOptions(nestedMetrics.map(metric => normalizeCapability(metric, "metric")).filter(Boolean));
    }
    return option;
  }

  function normalizeMetricMap(map) {
    const result = Object.create(null);
    for (const [demographic, metrics] of Object.entries(map)) {
      if (!Array.isArray(metrics)) continue;
      result[normalizeToken(demographic)] = uniqueOptions(metrics.map(metric => normalizeCapability(metric, "metric")).filter(Boolean));
    }
    return result;
  }

  function uniqueOptions(options) {
    const seen = new Set();
    return options.filter(option => {
      const key = option.id.toLowerCase();
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  }

  function chooseOptionId(options, currentId, preferredTokens) {
    const current = findOption(options, currentId);
    if (current?.available) return current.id;
    const available = options.filter(option => option.available);
    const preferred = available.find(option => preferredTokens.includes(normalizeToken(option.id)) || preferredTokens.includes(normalizeToken(option.label)));
    return preferred?.id || available[0]?.id || options[0]?.id || null;
  }

  function applyResponseSelections(raw) {
    const selection = raw?.selection || raw?.selected || {};
    const demographic = extractSelectionId(raw?.selectedDemographic ?? raw?.demographic ?? selection.demographicId ?? selection.demographic ?? selection.audienceId ?? selection.audience);
    const metric = extractSelectionId(raw?.selectedMetric ?? raw?.metric ?? selection.metricId ?? selection.metric ?? selection.measureId ?? selection.measure);
    const demographicOption = findOption(demographicsForSelection(), demographic);
    if (demographicOption?.available) state.demographicId = demographicOption.id;
    const metricOption = findOption(metricsForDemographic(), metric);
    const responseMetricId = metricOption?.available ? metricOption.id : null;
    state.metricId = chooseOptionId(
      metricsForDemographic(),
      responseMetricId || state.metricByDemographic[normalizeToken(state.demographicId)] || state.metricId,
      ["imp", "impressions"]);
    if (state.demographicId && state.metricId) state.metricByDemographic[normalizeToken(state.demographicId)] = state.metricId;
    saveSelection();
  }

  function extractSelectionId(value) {
    return typeof value === "string"
      ? safeString(value)
      : safeString(value?.id || value?.key || value?.value || value?.code || value?.name);
  }

  function renderSelectors() {
    renderSelector(dom.demographic, demographicsForSelection(), state.demographicId, "No audiences available");
    renderSelector(dom.metric, metricsForDemographic(), state.metricId, "No measures available", true);
    const demographic = selectedDemographic();
    const metric = selectedMetric();
    const demographicLabel = demographic?.label || "Audience unavailable";
    const metricLabel = metricContextLabel(metric);
    dom.measureSummary.textContent = `${demographicLabel} · ${metricLabel}`;
    dom.selectorStatus.textContent = `${demographicLabel}, ${metricLabel}${state.isLoading ? ", loading" : " selected"}.`;
  }

  function renderSelector(select, options, selectedId, emptyLabel, includeUnit = false) {
    select.replaceChildren();
    if (!options.length) {
      const option = document.createElement("option");
      option.textContent = emptyLabel;
      option.value = "";
      select.append(option);
      select.disabled = true;
      return;
    }
    for (const descriptor of options) {
      const option = document.createElement("option");
      option.value = descriptor.id;
      option.textContent = `${includeUnit ? metricContextLabel(descriptor) : descriptor.label}${descriptor.available ? "" : " (unavailable)"}`;
      option.disabled = !descriptor.available;
      option.title = descriptor.unavailableReason;
      select.append(option);
    }
    select.value = findOption(options, selectedId)?.id || "";
    const selectableCount = options.filter(option => option.available).length;
    select.disabled = state.isLoading || selectableCount === 0 || (selectableCount === 1 && options.find(option => option.available)?.id === select.value);
  }

  function changeSelection(kind, value) {
    const options = kind === "demographic" ? demographicsForSelection() : metricsForDemographic();
    const selected = findOption(options, value);
    if (!selected?.available) {
      renderSelectors();
      return;
    }
    state.capabilityNotice = "";
    if (kind === "demographic") {
      if (state.demographicId && state.metricId) state.metricByDemographic[normalizeToken(state.demographicId)] = state.metricId;
      state.demographicId = selected.id;
      state.metricId = chooseOptionId(
        metricsForDemographic(),
        state.metricByDemographic[normalizeToken(state.demographicId)] || state.metricId,
        ["imp", "impressions"]);
    } else {
      state.metricId = selected.id;
      state.metricByDemographic[normalizeToken(state.demographicId)] = selected.id;
    }
    saveSelection();
    renderSelectors();
    if (state.selectedDate) loadRatings(state.selectedDate);
  }

  function renderComparisonControls() {
    dom.comparisonSlots.replaceChildren();
    state.comparisonSlots.forEach((slot, index) => {
      const fieldset = element("fieldset", "comparison-slot");
      fieldset.append(element("legend", "sr-only", `Comparison ${index + 1}`));

      const periodLabel = element("label", "comparison-slot__period");
      periodLabel.append(element("span", "comparison-slot__label", `Period ${index + 1}`));
      const select = document.createElement("select");
      select.setAttribute("aria-label", `Comparison ${index + 1} period`);
      for (const preset of COMPARISON_PRESETS) {
        const option = document.createElement("option");
        option.value = preset.id;
        option.textContent = preset.label;
        select.append(option);
      }
      select.value = slot.preset;
      select.disabled = state.isLoading;
      select.addEventListener("change", () => {
        slot.preset = select.value;
        if (slot.preset !== "specific-date") slot.date = "";
        state.comparisonExplicit = true;
        renderComparisonControls();
        if (slot.preset === "specific-date") {
          dom.comparisonSlots.querySelector(`[data-comparison-date="${index}"]`)?.focus();
        } else {
          applyComparisonChange(`Comparison ${index + 1} changed to ${comparisonSpecLabel(slot.preset)}.`);
        }
      });
      periodLabel.append(select);
      fieldset.append(periodLabel);

      const dateLabel = element("label", "comparison-slot__date");
      dateLabel.append(element("span", "comparison-slot__label", "Date"));
      const date = document.createElement("input");
      date.type = "date";
      date.value = slot.date;
      date.dataset.comparisonDate = String(index);
      date.setAttribute("aria-label", `Comparison ${index + 1} date; choosing a date selects Specific date`);
      date.setAttribute("aria-describedby", "comparison-help");
      date.disabled = state.isLoading;
      date.addEventListener("change", () => {
        slot.date = date.value;
        if (isIsoDate(slot.date)) {
          slot.preset = "specific-date";
          state.comparisonExplicit = true;
          applyComparisonChange(`Comparison ${index + 1} changed to ${formatDate(slot.date)}.`);
        } else {
          dom.comparisonStatus.textContent = `Choose a valid date for comparison ${index + 1}.`;
        }
      });
      dateLabel.append(date);
      fieldset.append(dateLabel);

      const remove = element("button", "comparison-slot__remove", "Remove");
      remove.type = "button";
      remove.setAttribute("aria-label", `Remove comparison ${index + 1}`);
      remove.disabled = state.isLoading || state.comparisonSlots.length <= 1;
      remove.addEventListener("click", () => {
        state.comparisonSlots.splice(index, 1);
        state.comparisonExplicit = true;
        applyComparisonChange(`Comparison ${index + 1} removed.`);
      });
      fieldset.append(remove);
      dom.comparisonSlots.append(fieldset);
    });

    dom.addComparison.disabled = state.isLoading || state.comparisonSlots.length >= MAX_COMPARISONS;
    dom.comparisonSummary.textContent = comparisonSummaryText();
  }

  function addComparisonSlot() {
    if (state.comparisonSlots.length >= MAX_COMPARISONS) return;
    const used = new Set(state.comparisonSlots.map(slot => slot.preset));
    const preset = COMPARISON_PRESETS.find(item => item.id !== "specific-date" && !used.has(item.id))?.id || "last-week";
    state.comparisonSlots.push({ preset, date: "" });
    state.comparisonExplicit = true;
    applyComparisonChange(`${comparisonSpecLabel(preset)} comparison added.`);
  }

  function applyComparisonChange(message) {
    saveSelection();
    renderComparisonControls();
    dom.comparisonStatus.textContent = message;
    if (state.selectedDate && comparisonSpecs().length === state.comparisonSlots.length) loadRatings(state.selectedDate);
  }

  function comparisonSpecs() {
    return state.comparisonSlots.map(slotToSpec).filter(Boolean).slice(0, MAX_COMPARISONS);
  }

  function slotToSpec(slot) {
    return slot?.preset === "specific-date" ? (isIsoDate(slot.date) ? `date:${slot.date}` : null) : normalizeComparisonSpec(slot?.preset);
  }

  function specToSlot(spec) {
    return parseComparisonSpec(spec) || { preset: "last-week", date: "" };
  }

  function syncComparisonSlots(descriptors) {
    const specs = descriptors.map(descriptor => normalizeComparisonSpec(descriptor.spec)).filter(Boolean).slice(0, MAX_COMPARISONS);
    if (specs.length) state.comparisonSlots = specs.map(specToSlot);
    renderComparisonControls();
  }

  function comparisonSummaryText() {
    const descriptors = state.data?.comparisonDescriptors || [];
    const descriptorSpecs = descriptors.map(descriptor => normalizeComparisonSpec(descriptor.spec)).filter(Boolean);
    const requestedSpecs = comparisonSpecs();
    const descriptorsAreCurrent = !state.comparisonExplicit
      || (descriptorSpecs.length === requestedSpecs.length && descriptorSpecs.every((spec, index) => spec === requestedSpecs[index]));
    if (descriptorsAreCurrent && descriptors.length) {
      return descriptors.map(descriptor => `${descriptor.label}${descriptor.available === false ? " unavailable" : ""}`).join(", ");
    }
    return state.comparisonSlots.map(slot => slot.preset === "specific-date"
      ? (isIsoDate(slot.date) ? formatShortDate(slot.date) : "Choose a date")
      : comparisonSpecLabel(slot.preset)).join(", ");
  }

  function normalizeComparisonSpecList(value) {
    if (!Array.isArray(value)) return [];
    return value.map(item => normalizeComparisonSpec(typeof item === "string" ? item : item?.spec)).filter(Boolean).slice(0, MAX_COMPARISONS);
  }

  function normalizeComparisonSpec(value) {
    const spec = safeString(value).toLowerCase();
    if (["previous-day", "last-week", "two-weeks-ago", "last-month", "last-year"].includes(spec)) return spec;
    if (spec.startsWith("date:") && isIsoDate(spec.slice(5))) return `date:${spec.slice(5)}`;
    return null;
  }

  function parseComparisonSpec(value) {
    const spec = normalizeComparisonSpec(value);
    if (!spec) return null;
    return spec.startsWith("date:") ? { preset: "specific-date", date: spec.slice(5) } : { preset: spec, date: "" };
  }

  function comparisonSpecLabel(value) {
    const parsed = parseComparisonSpec(value);
    if (!parsed) return "";
    if (parsed.preset === "specific-date") return formatShortDate(parsed.date);
    return COMPARISON_PRESETS.find(preset => preset.id === parsed.preset)?.label || parsed.preset;
  }

  function findOption(options, id) {
    const key = safeString(id).toLowerCase();
    return key ? options.find(option => option.id.toLowerCase() === key) || null : null;
  }

  function selectedDemographic() { return findOption(demographicsForSelection(), state.demographicId); }
  function selectedMetric() { return findOption(metricsForDemographic(), state.metricId); }

  function demographicsForSelection() {
    if (state.capabilities.availableSelections === null) return state.capabilities.demographics;
    return state.capabilities.demographics.map(demographic => {
      const hasMetric = state.capabilities.availableSelections.some(selection => sameId(selection.demographicId, demographic.id));
      return hasMetric
        ? demographic
        : { ...demographic, available: false, unavailableReason: demographic.unavailableReason || "No measures are available for this audience." };
    });
  }

  function metricsForDemographic() {
    const demographic = selectedDemographic();
    if (!demographic) return [];
    if (state.capabilities.availableSelections !== null) {
      const metricIds = state.capabilities.availableSelections
        .filter(selection => sameId(selection.demographicId, demographic.id))
        .map(selection => selection.metricId);
      return state.capabilities.metrics.filter(metric => metricIds.some(id => sameId(id, metric.id)));
    }
    if (demographic.metrics !== null && demographic.metrics !== undefined) return enrichMetrics(demographic.metrics);
    const mapped = state.capabilities.metricsByDemographic[normalizeToken(demographic.id)];
    if (mapped) return enrichMetrics(mapped);
    return state.capabilities.metrics.filter(metric => !metric.demographicIds?.length || metric.demographicIds.some(id => sameId(id, demographic.id)));
  }

  function enrichMetrics(metrics) {
    return metrics.map(metric => {
      const global = findOption(state.capabilities.metrics, metric.id);
      if (!global) return metric;
      return {
        ...global,
        ...metric,
        label: metric.label === metric.id ? global.label : metric.label,
        available: global.available && metric.available
      };
    });
  }

  function normalizeRatings(raw, requestedDate) {
    const comparisons = raw?.comparisonDates || raw?.dates || {};
    const shows = Array.isArray(raw?.shows) ? raw.shows : (Array.isArray(raw?.programs) ? raw.programs : []);
    const inferredDates = inferComparisonDates(shows);
    const comparisonDescriptors = normalizeComparisonDescriptors(raw?.comparisonDescriptors, comparisons, inferredDates);
    return {
      selectedDate: normalizeIsoDate(raw?.selectedDate || raw?.date) || requestedDate,
      generatedAtUtc: raw?.generatedAtUtc || raw?.generatedAt || raw?.sourceLastModifiedUtc || null,
      isStale: Boolean(raw?.isStale),
      displayTitle: safeString(raw?.displayTitle || raw?.siteTitle),
      version: safeString(raw?.version || raw?.appVersion || raw?.applicationVersion || raw?.serverVersion || raw?.informationalVersion),
      comparisonDescriptors,
      shows: shows.map((show, index) => normalizeShow(show, index, comparisonDescriptors)).filter(show => show.name)
    };
  }

  function normalizeComparisonDescriptors(rawDescriptors, comparisons, inferredDates) {
    if (Array.isArray(rawDescriptors)) {
      const seen = new Set();
      return rawDescriptors.slice(0, MAX_COMPARISONS).map((descriptor, index) => {
        const spec = normalizeComparisonSpec(descriptor?.spec || descriptor?.preset);
        let id = safeString(descriptor?.id || descriptor?.slotId || descriptor?.comparisonId || spec) || `comparison-${index + 1}`;
        while (seen.has(id)) id = `${id}-${index + 1}`;
        seen.add(id);
        return {
          id,
          label: safeString(descriptor?.label || descriptor?.displayName) || comparisonSpecLabel(spec) || `Comparison ${index + 1}`,
          targetDate: normalizeIsoDate(descriptor?.targetDate || descriptor?.date),
          spec,
          available: descriptor?.available !== false && descriptor?.isAvailable !== false,
          missingReason: safeString(descriptor?.missingReason || descriptor?.message)
        };
      });
    }

    return [
      { id: "previousWeek", label: "Prior week", targetDate: normalizeIsoDate(comparisons.previousWeek || comparisons.priorWeek || comparisons.d7 || inferredDates.previousWeek), spec: "last-week" },
      { id: "twoWeeksAgo", label: "Two weeks prior", targetDate: normalizeIsoDate(comparisons.twoWeeksAgo || comparisons.twoWeeksPrior || comparisons.d14 || inferredDates.twoWeeksAgo), spec: "two-weeks-ago" },
      { id: "previousYear", label: "Prior year", targetDate: normalizeIsoDate(comparisons.previousYear || comparisons.priorYear || comparisons.d364 || inferredDates.previousYear), spec: "last-year" }
    ].map(descriptor => ({ ...descriptor, available: true, missingReason: descriptor.targetDate ? "" : "Comparison date unavailable" }));
  }

  function normalizeShow(show, index, comparisonDescriptors) {
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
      segments: rawSegments.map((segment, segmentIndex) => normalizeSegment(segment, segmentIndex, comparisonDescriptors))
    };
  }

  function normalizeSegment(segment, index, comparisonDescriptors) {
    const current = segment?.current || {};
    const values = segment?.values || {};
    const normalized = {
      label: safeString(segment?.label || segment?.time || segment?.quarterHour || current?.timeLabel) || `QH ${segment?.segmentNumber || index + 1}`,
      time: safeString(segment?.time || segment?.actualTime || current?.timeLabel),
      rawTitle: safeString(segment?.rawTitle || current?.programName),
      isShifted: Boolean(segment?.isShifted || segment?.shifted),
      isPreempted: Boolean(segment?.isPreempted || segment?.preempted),
      selected: normalizeValue(firstDefined(segment?.selected, segment?.value, values?.selected, values?.value, segment?.current, current?.p18PlusImpressions)),
      comparisonValues: Object.create(null)
    };
    comparisonDescriptors.forEach((descriptor, descriptorIndex) => {
      const aliases = comparisonAliases(descriptor.spec);
      let candidate = firstDefined(segment?.[descriptor.id], values?.[descriptor.id]);
      for (const key of aliases) candidate = firstDefined(candidate, segment?.[key], values?.[key]);
      if (candidate === undefined) {
        const period = findDescriptorComparison(segment?.comparisons, descriptor, descriptorIndex);
        if (period) candidate = comparisonCandidate(period);
      }
      normalized.comparisonValues[descriptor.id] = descriptor.available
        ? normalizeValue(candidate)
        : { value: null, missingReason: descriptor.missingReason || "Comparison report unavailable" };
    });
    return normalized;
  }

  function comparisonAliases(spec) {
    const preset = parseComparisonSpec(spec)?.preset;
    if (preset === "previous-day") return ["previousDay", "previous-day", "priorDay", "prior-day", "d1"];
    if (preset === "last-week") return ["previousWeek", "previous-week", "priorWeek", "prior-week", "d7"];
    if (preset === "two-weeks-ago") return ["twoWeeksAgo", "twoWeeksPrior", "two-weeks-prior", "d14"];
    if (preset === "last-month") return ["previousMonth", "previous-month", "lastMonth", "d28"];
    if (preset === "last-year") return ["previousYear", "previous-year", "priorYear", "prior-year", "d364"];
    return [];
  }

  function comparisonCandidate(period) {
    return {
      value: firstPresent(period, ["value", "metricValue", "impressions"]),
      delta: period.delta?.absolute ?? period.delta,
      percentDelta: period.delta?.percentage ?? period.percentDelta,
      actualTime: period.actualTimeLabel || period.actualTime,
      rawTitle: period.actualProgramName || period.rawTitle,
      missingReason: period.missingReason
    };
  }

  function normalizeValue(raw) {
    if (typeof raw === "number") return Number.isFinite(raw) ? { value: raw } : { value: null };
    if (raw && typeof raw === "object") {
      const number = finiteOrNull(firstPresent(raw, ["value", "metricValue", "p18PlusImpressions", "impressions"]));
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
        for (const [key, spec] of [["previousWeek", "last-week"], ["twoWeeksAgo", "two-weeks-ago"], ["previousYear", "last-year"]]) {
          const comparison = findDescriptorComparison(segment?.comparisons, { id: key, spec }, -1);
          if (!result[key] && comparison?.targetDate) result[key] = comparison.targetDate;
        }
      }
    }
    return result;
  }

  function findDescriptorComparison(comparisons, descriptor, fallbackIndex) {
    if (!Array.isArray(comparisons)) return null;
    const descriptorIds = [descriptor.id, descriptor.spec].map(value => safeString(value).toLowerCase()).filter(Boolean);
    const matched = comparisons.find(item => {
      const ids = [item?.comparisonDescriptorId, item?.descriptorId, item?.slotId, item?.id, item?.period?.id, item?.periodId, item?.target?.id, item?.target?.spec, item?.spec]
        .map(value => safeString(value).toLowerCase()).filter(Boolean);
      return ids.some(id => descriptorIds.includes(id)) || comparisonAliases(descriptor.spec).map(value => value.toLowerCase()).some(alias => ids.includes(alias));
    });
    return matched || (fallbackIndex >= 0 ? comparisons[fallbackIndex] : null) || null;
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
    renderVersion(state.data.version || state.status?.version || "");
    renderSelectors();
    renderComparisonControls();

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

  function currentSeries() {
    const comparisons = (state.data?.comparisonDescriptors || []).slice(0, MAX_COMPARISONS).map((descriptor, index) => ({
      key: descriptor.id,
      label: descriptor.label,
      date: descriptor.targetDate,
      available: descriptor.available,
      seriesClass: `series-comparison-${index + 1}`,
      className: `chart-line--secondary${index > 0 ? " chart-line--dashed" : ""}`,
      dashed: index > 0
    }));
    return [{ ...SELECTED_SERIES, date: state.data?.selectedDate, available: true }, ...comparisons];
  }

  function seriesValue(segment, series) {
    return series.key === "selected" ? segment.selected : segment.comparisonValues?.[series.key];
  }

  function createLegend() {
    const legend = element("div", "legend");
    for (const series of currentSeries()) {
      const item = element("span", `legend-item ${series.seriesClass}${series.dashed ? " legend-item--dashed" : ""}`);
      item.append(element("span", "legend-swatch"));
      item.append(element("span", "", series.label));
      item.append(element("span", "legend-date", series.available === false ? "Unavailable" : formatShortDate(series.date)));
      legend.append(item);
    }
    return legend;
  }

  function createChart(show) {
    const wrap = element("div", "chart-wrap");
    const seriesList = currentSeries();
    const allValues = show.segments.flatMap(segment => seriesList.map(series => seriesValue(segment, series)?.value)).filter(Number.isFinite);
    if (!show.segments.length || !allValues.length) {
      wrap.append(element("div", "chart-empty", `No ${metricContextLabel(selectedMetric()).toLowerCase()} values are available for this show.`));
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
    svg.setAttribute("aria-label", `${show.name}, ${selectedDemographic()?.label || "selected audience"} ${metricContextLabel(selectedMetric())} by quarter hour for ${seriesList.map(series => `${series.label} ${series.available === false ? "unavailable" : formatShortDate(series.date)}`).join(", ")}`);

    for (let tick = 0; tick <= 4; tick++) {
      const value = niceMax * tick / 4;
      const tickY = y(value);
      svg.append(svgElement("line", { x1: margin.left, y1: tickY, x2: width - margin.right, y2: tickY, class: "chart-grid" }));
      const label = svgElement("text", { x: margin.left - 9, y: tickY + 4, class: "chart-axis-label", "text-anchor": "end" });
      label.textContent = formatMetricValue(value, true);
      svg.append(label);
    }

    const labelEvery = Math.max(1, Math.ceil(show.segments.length / 7));
    show.segments.forEach((segment, index) => {
      if (index % labelEvery !== 0 && index !== show.segments.length - 1) return;
      const label = svgElement("text", { x: x(index), y: height - 15, class: "chart-axis-label", "text-anchor": "middle" });
      label.textContent = segment.label;
      svg.append(label);
    });

    for (const series of [...seriesList].reverse()) {
      const runs = contiguousRuns(show.segments.map(segment => seriesValue(segment, series)?.value));
      for (const run of runs) {
        const points = run.map(point => `${x(point.index)},${y(point.value)}`).join(" ");
        if (run.length > 1) {
          const line = svgElement("polyline", { points, class: `chart-line ${series.seriesClass} ${series.className}`.trim() });
          svg.append(line);
        }
        for (const point of run) {
          const circle = svgElement("circle", { cx: x(point.index), cy: y(point.value), r: series.key === "selected" ? 3.5 : 2.6, class: `chart-point ${series.seriesClass}` });
          const title = svgElement("title");
          title.textContent = `${show.segments[point.index].label} — ${series.label}: ${formatMetricValue(point.value)}${metricUnitText()}`;
          circle.append(title);
          svg.append(circle);
        }
      }
    }

    wrap.append(svg);
    return wrap;
  }

  function createTable(show) {
    const seriesList = currentSeries();
    const panel = element("section", "table-panel");
    panel.append(element("h3", "", `Exact quarter-hour ${metricContextLabel(selectedMetric()).toLowerCase()}`));
    const scroll = element("div", "table-scroll");
    scroll.setAttribute("tabindex", "0");
    scroll.setAttribute("role", "region");
    scroll.setAttribute("aria-label", `${show.name} exact ratings table`);
    const table = element("table", "stat-table");
    const thead = document.createElement("thead");
    const headerRow = document.createElement("tr");
    headerRow.append(tableHeader("Quarter hour"));
    headerRow.append(tableHeader(`Selected · ${formatShortDate(state.data.selectedDate)}`));
    for (const series of seriesList.slice(1)) {
      headerRow.append(tableHeader(`${series.label} · ${series.available === false ? "Unavailable" : formatShortDate(series.date)}`));
    }
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
      for (const series of seriesList.slice(1)) row.append(comparisonCell(segment.selected, seriesValue(segment, series)));
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
    cell.append(element("span", "stat-main", formatMetricValue(item.value)));
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
    cell.append(element("span", className, `${sign}${formatMetricValue(change)}${percentText}`));
    return cell;
  }

  function appendValueMetadata(cell, item) {
    const details = [item.actualTime ? `Aired ${item.actualTime}` : "", item.rawTitle ? `Report: ${item.rawTitle}` : ""].filter(Boolean);
    if (details.length) cell.append(element("span", "stat-change", details.join(" · ")));
  }

  function renderWarnings() {
    const messages = [];
    const demographic = selectedDemographic();
    const metric = selectedMetric();
    if (!demographic) messages.push("No audience options are available for this report date.");
    else if (!demographic.available) messages.push(`${demographic.label} is unavailable for this report date.`);
    if (!metric) messages.push(`No measures are available for ${demographic?.label || "the selected audience"} on this report date.`);
    else if (!metric.available) messages.push(`${metric.label} is unavailable for ${demographic?.label || "the selected audience"} on this report date.`);
    if (state.capabilityNotice) messages.push(state.capabilityNotice);
    if (state.status?.sourceAvailable === false) messages.push(shareErrorText(state.status));
    if (state.data.isStale || state.status?.isStale) messages.push("Data may be stale. The desktop app/server has not completed a recent report refresh.");
    if (messages.length) showNotice([...new Set(messages)].join(" "), state.status?.sourceAvailable === false);
  }

  function renderStatus() {
    const status = state.status;
    dom.statusDot.className = `status-dot ${status.sourceAvailable ? "status-dot--ready" : "status-dot--error"}`;
    dom.statusLabel.textContent = status.sourceAvailable ? (status.isStale ? "Reports stale" : "Reports available") : "Report source unavailable";
    dom.lastRefresh.textContent = status.lastRefreshUtc ? `Last refresh ${formatTimestamp(status.lastRefreshUtc)}` : "Refresh time unavailable";
    renderVersion(status.version);
    applyDisplayTitle(status.displayTitle);
  }

  function renderStatusError() {
    dom.statusDot.className = "status-dot status-dot--error";
    dom.statusLabel.textContent = "Server status unavailable";
    dom.lastRefresh.textContent = "";
    renderVersion("");
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

  function isSelectionError(error) {
    const code = safeString(error?.body?.errorCode || error?.body?.code).toLowerCase();
    return code === "invalid_rating_selection" || code === "rating_selection_unavailable";
  }

  function shareErrorText(info) {
    const location = safeString(info?.sourceDisplayName);
    return `Cannot access ${location || "the configured report location"}. Check the desktop app/server for file share permissions.${info?.message ? ` ${safeString(info.message)}` : ""}`;
  }

  function showLoading(visible) {
    state.isLoading = visible;
    dom.loading.hidden = !visible;
    if (visible) {
      dom.message.hidden = true;
      dom.showList.hidden = true;
    }
    renderSelectors();
    renderComparisonControls();
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

  function firstPresent(source, keys) {
    if (!source || typeof source !== "object") return undefined;
    for (const key of keys) {
      if (Object.prototype.hasOwnProperty.call(source, key)) return source[key];
    }
    return undefined;
  }

  function firstDefined(...values) {
    return values.find(value => value !== undefined);
  }

  function firstArrayProperty(source, keys) {
    if (!source || typeof source !== "object") return null;
    for (const key of keys) {
      if (Object.prototype.hasOwnProperty.call(source, key) && Array.isArray(source[key])) return source[key];
    }
    return null;
  }

  function normalizeIdList(value) {
    if (!Array.isArray(value)) return [];
    return value.map(extractSelectionId).filter(Boolean);
  }

  function normalizeSelectionPair(value) {
    if (!value || typeof value !== "object") return null;
    const demographicId = extractSelectionId(value.demographicId ?? value.demographic ?? value.audienceId ?? value.audience);
    const metricId = extractSelectionId(value.metricId ?? value.metric ?? value.measureId ?? value.measure);
    return demographicId && metricId ? { demographicId, metricId } : null;
  }

  function clampDecimals(value) {
    const number = finiteOrNull(value);
    return Number.isInteger(number) ? Math.min(6, Math.max(0, number)) : null;
  }

  function normalizeToken(value) { return safeString(value).toLowerCase().replace(/[^a-z0-9]+/g, ""); }
  function sameId(left, right) { return Boolean(left && right) && safeString(left).toLowerCase() === safeString(right).toLowerCase(); }

  function formatMetricValue(value, compact = false) {
    if (!Number.isFinite(value)) return "—";
    const metric = selectedMetric() || DEFAULT_METRIC;
    const format = normalizeToken(metric.format);
    const unit = normalizeToken(metric.unit);
    const isPercent = format === "percent" || format === "percentage" || unit === "percent" || metric.unit === "%" || metric.suffix === "%";
    const decimals = metric.decimals ?? (isPercent || format === "decimal" ? 1 : 0);
    let formatted;
    if (compact && !isPercent && !metric.prefix && !metric.suffix) {
      formatted = compactFormatter.format(value);
    } else {
      formatted = new Intl.NumberFormat(undefined, {
        minimumFractionDigits: decimals,
        maximumFractionDigits: decimals
      }).format(value);
    }
    const suffix = metric.suffix || (isPercent ? "%" : "");
    return `${metric.prefix || ""}${formatted}${suffix}`;
  }

  function metricContextLabel(metric) {
    if (!metric) return "Measure unavailable";
    const label = metric.label || metric.id;
    const unit = safeString(metric.unit);
    const labelToken = normalizeToken(label);
    const unitToken = normalizeToken(unit);
    if (!unit || unit === "%" || unitToken === "percent" || labelToken.includes(unitToken)) return label;
    return `${label} (${unit})`;
  }

  function metricUnitText() {
    const metric = selectedMetric() || DEFAULT_METRIC;
    if (!metric.unit || metric.unit === "%" || normalizeToken(metric.unit) === "percent" || metric.suffix === "%") return "";
    return ` ${metric.unit}`;
  }

  function restoreSelection() {
    try {
      const saved = JSON.parse(localStorage.getItem(STORAGE_KEY) || "null");
      if (!saved || typeof saved !== "object") return;
      if (safeString(saved.demographicId)) state.demographicId = safeString(saved.demographicId);
      if (safeString(saved.metricId)) state.metricId = safeString(saved.metricId);
      state.selectionRestored = Boolean(safeString(saved.demographicId) && safeString(saved.metricId));
      if (saved.metricByDemographic && typeof saved.metricByDemographic === "object") {
        for (const [key, value] of Object.entries(saved.metricByDemographic)) {
          if (normalizeToken(key) && safeString(value)) state.metricByDemographic[normalizeToken(key)] = safeString(value);
        }
      }
      const comparisons = normalizeComparisonSpecList(saved.comparisonSpecs);
      if (comparisons.length) {
        state.comparisonSlots = comparisons.map(specToSlot);
        state.comparisonExplicit = true;
      }
    } catch {
      // Storage can be unavailable in locked-down browsers; selection remains session-local.
    }
  }

  function saveSelection() {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify({
        demographicId: state.demographicId,
        metricId: state.metricId,
        metricByDemographic: state.metricByDemographic,
        comparisonSpecs: state.comparisonExplicit ? comparisonSpecs() : undefined
      }));
    } catch {
      // The view continues to work when storage is unavailable or full.
    }
  }

  function safeString(value) { return typeof value === "string" ? value.trim() : ""; }
  function slug(value) { return value.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 48) || "ratings"; }
  function applyDisplayTitle(value) {
    const title = safeString(value) || DEFAULT_TITLE;
    dom.pageTitle.textContent = title;
    document.title = title;
  }
  function renderVersion(value) {
    const version = safeString(value);
    dom.appVersion.hidden = !version;
    dom.appVersion.textContent = version ? `v${version.replace(/^v/i, "")}` : "";
    if (version) dom.appVersion.setAttribute("aria-label", `Application version ${version}`);
    else dom.appVersion.removeAttribute("aria-label");
  }
  function isIsoDate(value) { return /^\d{4}-\d{2}-\d{2}$/.test(value || "") && !Number.isNaN(Date.parse(`${value}T00:00:00Z`)); }
  function normalizeIsoDate(value) { return typeof value === "string" && isIsoDate(value.slice(0, 10)) ? value.slice(0, 10) : null; }
  function formatDate(value) { return isIsoDate(value) ? dateFormatter.format(new Date(`${value}T00:00:00Z`)) : "Unknown date"; }
  function formatShortDate(value) { return isIsoDate(value) ? shortDateFormatter.format(new Date(`${value}T00:00:00Z`)) : "Unavailable"; }
  function formatTimestamp(value) {
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? "time unavailable" : new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(date);
  }
})();
