import type {
  DeviceStatusList,
  EventPage,
  QueryContext,
  SourceConflict,
  SyncDiagnosticList,
  UsageDayReport,
  UsageWeekReport,
} from "./generated/contract-models.js";

/**
 * Deterministic rendering of API responses into agent-readable lines. This
 * module is presentation only: every number, source, and conflict reference
 * is copied from the server response, never recomputed, and the uncertainty
 * the server declares (data_state, completeness, source_conflicts,
 * pending_confirmation_count) is always carried through verbatim. Missing
 * data must never read as zero, and a partial or conflicted result must
 * never read as certain.
 */

/** Renders the shared query context every ranged read returns. */
export function describeQueryContext(context: QueryContext): string[] {
  const lines = [
    `Time range: ${context.from} to ${context.to} (exclusive), report timezone ${context.timezone}.`,
    `Sources observed: ${context.provenance.length > 0 ? context.provenance.join(", ") : "none"}.`,
    `Completeness: ${context.completeness}${context.completeness === "partial" ? " — the credential's scope or privacy ceiling withheld in-range data, so reported values undercount reality." : "."}`,
  ];
  if (context.data_state === "no_data") {
    lines.push("Data state: no_data — the range holds no observations at all. Treat every value as MISSING, never as zero.");
  } else if (context.data_state === "zero") {
    lines.push("Data state: zero — observations exist in the range and the normalized value is explicitly 0. This is a measured zero, not missing data.");
  } else {
    lines.push("Data state: observed — in-range observations produced the reported values.");
  }
  for (const conflict of context.source_conflicts ?? []) {
    lines.push(...describeSourceConflict(conflict));
  }
  if (context.pending_confirmation_count !== undefined && context.pending_confirmation_count > 0) {
    lines.push(`Payment note: ${context.pending_confirmation_count} transaction candidate(s) in range are pending Owner confirmation; totals include them.`);
  }
  if (context.source_policy_version !== undefined) {
    lines.push(`Normalized results were selected by source policy version ${context.source_policy_version}.`);
  }
  return lines;
}

/** Renders one source conflict with its retained competing evidence. */
export function describeSourceConflict(conflict: SourceConflict): string[] {
  return [
    `Source conflict on ${conflict.metric} (window ${conflict.from} to ${conflict.to}): policy v${conflict.policy_version} selected ${conflict.selected_source} (events ${conflict.selected_event_ids.join(", ") || "none"}); competing observations from ${conflict.competing_sources.join(", ")} (events ${conflict.competing_event_ids.join(", ")}) remain retained but are excluded from the normalized result. Do not present the selected value as the only observation.`,
  ];
}

/** Renders a page of events with correction provenance per item. */
export function describeEventPage(title: string, page: EventPage): string[] {
  const lines = [`${title}: ${page.data.length} event(s) on this page (page size ${page.page.page_size}${page.page.next_cursor ? ", more pages available" : ", last page"}).`];
  lines.push(...describeQueryContext(page.context));
  const corrected = page.data.filter((event) => event.correction !== undefined);
  if (corrected.length > 0) {
    lines.push(`${corrected.length} of these events carry a manual Owner correction (see each event's "correction" provenance); they are human interpretations, not raw device output.`);
  }
  for (const event of page.data) {
    const span = event.end_at ? `${event.start_at} to ${event.end_at}` : `${event.start_at} (open interval, duration unknown)`;
    const mark = event.correction !== undefined
      ? ` [corrected ${event.correction.corrected_at}${event.correction.reason ? `: ${event.correction.reason}` : ""}]`
      : "";
    lines.push(`- ${event.event_type} v${event.schema_version} rev ${event.revision} ${span} privacy=${event.privacy_level} source=${event.source.kind}${mark}`);
  }
  return lines;
}

/**
 * Renders the two usage metrics under their presence semantics: a no-data
 * range is reported as missing (never as 0), an explicit zero is labelled as
 * a measured zero, and only observed data prints the server's numbers. The
 * metric definitions always travel with the numbers.
 */
function describeUsageMetrics(metrics: { device_minutes: number; active_minutes: number }, context: QueryContext): string[] {
  if (context.data_state === "no_data") {
    return [
      "Device minutes: unavailable — the range holds no observations at all. This is missing data, not zero.",
      "Active minutes: unavailable — the range holds no observations at all. This is missing data, not zero.",
    ];
  }
  if (context.data_state === "zero") {
    return [
      "Device minutes: 0 — observations exist in the range and the normalized value is explicitly 0 (a measured zero, not missing data).",
      "Active minutes: 0 — observations exist in the range and the normalized value is explicitly 0 (a measured zero, not missing data).",
    ];
  }
  return [
    `Device minutes: ${metrics.device_minutes} — the sum of every device's qualifying intervals; concurrent device use can legitimately exceed elapsed time.`,
    `Active minutes: ${metrics.active_minutes} — the union of non-AFK intervals; overlapping time counts once.`,
  ];
}

/** Renders day usage metrics, keeping the two time metrics strictly apart. */
export function describeUsageDayReport(report: UsageDayReport): string[] {
  const lines = [
    `Usage report for local day ${report.date} (timezone ${report.timezone}).`,
    ...describeUsageMetrics(report.metrics, report.context),
  ];
  if (report.context.data_state !== "no_data") {
    for (const device of report.devices) {
      lines.push(`- device ${device.device_id} (${device.platform}): device ${device.device_minutes} min, active ${device.active_minutes} min`);
    }
  }
  lines.push(...describeQueryContext(report.context));
  return lines;
}

/** Renders week usage metrics with the per-day breakdown. */
export function describeUsageWeekReport(report: UsageWeekReport): string[] {
  const lines = [
    `Usage report for week ${report.week_start_date} to ${report.week_end_date} (timezone ${report.timezone}).`,
    ...describeUsageMetrics(report.metrics, report.context),
  ];
  if (report.context.data_state !== "no_data") {
    for (const day of report.days) {
      lines.push(`- ${day.date}: device ${day.device_minutes} min, active ${day.active_minutes} min`);
    }
  }
  lines.push(...describeQueryContext(report.context));
  return lines;
}

/**
 * Renders the current-context projection. Concurrent devices stay
 * independent: the renderer never merges them into one global focus, and
 * offline devices are labelled stale instead of being silently dropped.
 */
export function describeDeviceStatus(list: DeviceStatusList): string[] {
  const lines = [`Current device status as of ${list.server_time}: ${list.devices.length} device(s) have reported a heartbeat.`];
  for (const device of list.devices) {
    const activity = device.activity.is_afk
      ? "AFK (no foreground activity)"
      : device.activity.application_label ?? device.activity.application_id ?? "unknown application";
    lines.push(
      `- ${device.device_name ?? device.device_id} (${device.platform}): ${device.online ? "online" : `offline, last heartbeat ${device.age_seconds}s ago`} — ${activity}`,
    );
  }
  lines.push("Heartbeat projections are expirable current states, not historical evidence; they never contribute to duration totals.");
  return lines;
}

/**
 * Renders sync diagnostics. Together with the device status this is what
 * lets an agent explain a coverage gap as absent activity, an uncollected
 * backlog, or a broken sync — instead of guessing.
 */
export function describeSyncDiagnostics(list: SyncDiagnosticList): string[] {
  const lines = [`Sync diagnostics as of ${list.server_time}: ${list.devices.length} device(s) have pushed a snapshot.`];
  for (const device of list.devices) {
    lines.push(
      `- ${device.device_name ?? device.device_id} (${device.platform}): pending ${device.pending_count}, permanent failures ${device.permanent_failure_count}, snapshot age ${device.age_seconds}s`,
    );
    if (device.pending_count > 0) {
      lines.push(`  An uncollected backlog exists${device.oldest_pending_at ? ` (oldest waiting observation captured at ${device.oldest_pending_at})` : ""}; missing data may still arrive.`);
    }
    if (device.permanent_failure_count > 0) {
      lines.push(`  ${device.permanent_failure_count} observation(s) were permanently rejected and are never retried; their data will remain missing.`);
    }
    for (const error of device.recent_errors) {
      lines.push(`  recent error ${error.code} at ${error.occurred_at}: ${error.message}`);
    }
  }
  return lines;
}
