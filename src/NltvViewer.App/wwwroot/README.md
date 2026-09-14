# Producer web UI contract

The static UI uses only relative asset and API URLs so it can be hosted below an ASP.NET `PathBase` (for example `/ratings/`) behind nginx. The proxy should redirect the no-slash form to the slash form and forward that prefix to the app unchanged.

There is intentionally no refresh or configuration action in the public UI. Those operations belong to the desktop app/server.

## Endpoints

### `GET api/v1/days`

```json
{
  "days": ["2026-09-08", "2026-09-09", "2026-09-10"],
  "latestAvailableDate": "2026-09-10"
}
```

Dates are ISO `yyyy-MM-dd`. The UI defaults to `latestAvailableDate`; previous and next move through `days`, while the calendar may request a date between the returned minimum and maximum.

### `GET api/v1/capabilities`

Returns the same `capabilities` object shown in the ratings example below. The UI can also obtain this object from `capabilities` on the days or status response, allowing one fewer startup request.

### `GET api/v1/ratings?date=2026-09-10&demographic=p18-plus&metric=imp`

```json
{
  "selectedDate": "2026-09-10",
  "comparisonDates": {
    "previousWeek": "2026-09-03",
    "twoWeeksAgo": "2026-08-27",
    "previousYear": "2025-09-11"
  },
  "generatedAtUtc": "2026-09-11T18:30:00Z",
  "displayTitle": "News Ratings",
  "capabilities": {
    "demographics": [
      { "id": "p18-plus", "label": "P18+" },
      { "id": "p25-54", "label": "P25-54" }
    ],
    "metrics": [
      { "id": "imp", "label": "IMP", "unit": "impressions", "format": "number", "decimals": 0 },
      { "id": "rtg", "label": "RTG %", "unit": "percent", "format": "number", "decimals": 1 }
    ],
    "availableSelections": [
      { "demographicId": "p18-plus", "metricId": "imp" },
      { "demographicId": "p18-plus", "metricId": "rtg" },
      { "demographicId": "p25-54", "metricId": "imp" }
    ],
    "defaultDemographicId": "p18-plus",
    "defaultMetricId": "imp"
  },
  "selection": { "demographicId": "p18-plus", "metricId": "imp" },
  "isStale": false,
  "shows": [
    {
      "id": "evening-news-at-six",
      "name": "Evening News at 6",
      "channel": "Channel 2",
      "rawTitle": "LOCAL NEWS AT 6",
      "scheduledStart": "6:00 PM",
      "actualStart": "6:01 PM",
      "isShifted": true,
      "isPreempted": false,
      "preemptionNote": null,
      "segments": [
        {
          "label": "6:00 PM",
          "time": "18:00",
          "selected": { "value": 26480, "actualTime": "6:01 PM" },
          "previousWeek": 24120,
          "twoWeeksAgo": 25200,
          "previousYear": { "value": 23310, "rawTitle": "LOCAL NEWS 6PM" },
          "isShifted": true,
          "isPreempted": false
        }
      ]
    }
  ]
}
```

Each series value can be either a JSON number or an object. A value object supports `value`, `delta`, `percentDelta`, `actualTime`, `rawTitle`, and `missingReason`. The browser computes the displayed change as selected minus comparison so results remain consistent. A missing value should be `null` or `{ "value": null, "missingReason": "..." }`.

The UI sends the selected capability IDs as `demographic` and `metric`. It filters the measure dropdown through `availableSelections`, remembers the last valid measure for each audience in local browser storage, and falls back visibly when a selection is not offered for another report date. Capability metadata may alternatively be included on the days or status response. Metrics may also be nested on each demographic or supplied through `metricsByDemographic`.

Compatibility aliases accepted by the UI include `priorWeek`/`d7`, `twoWeeksPrior`/`d14`, `priorYear`/`d364`, and `quarterHours` in place of `segments`.
It also accepts the Core comparison model directly: show fields `displayName`, `viewingSource`, and `programName`; segment fields `current` and `comparisons`; and comparison period IDs `last-week`, `two-weeks-ago`, and `last-year`.

### `GET api/v1/status`

```json
{
  "lastRefreshUtc": "2026-09-11T18:30:00Z",
  "version": "1.1.0",
  "displayTitle": "News Ratings",
  "isStale": false,
  "sourceAvailable": true,
  "sourceDisplayName": "Configured ratings share",
  "errorCode": null,
  "message": null
}
```

`displayTitle` is optional on either status or ratings. The checked-in page uses the neutral `News Ratings`; deployments can supply a branded title from local configuration without committing it to the repository.

Detailed parser/catalog warnings are intentionally excluded from the public web API and view. Administrators can inspect them in the desktop application; refresh summaries remain in the local logs.

When the report location cannot be read, use HTTP `503` and/or return `sourceAvailable: false` with a stable error code such as `SOURCE_UNAVAILABLE`. `sourceDisplayName` may contain the configured path if administrators intend that path to be visible to web viewers. The UI displays: “Cannot access X. Check the desktop app/server for file share permissions.”

For an unsupported report, use HTTP `415` or `422` with a Problem Details JSON response. A missing date may use `404`. Other errors are shown as general availability failures. Do not include stack traces or secrets in public responses.
