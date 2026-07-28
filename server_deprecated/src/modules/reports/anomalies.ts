import type { DaySummary, LifestyleAnomalyReport } from "@ai-life/shared";

function round(value: number): number {
  return Math.round(value * 100) / 100;
}

function average(values: number[]): number | null {
  if (values.length === 0) return null;
  return round(values.reduce((sum, value) => sum + value, 0) / values.length);
}

function focusRatio(activeMinutes: number, focusMinutes: number): number | null {
  if (activeMinutes <= 0) return null;
  return round(focusMinutes / activeMinutes);
}

function anomaly(
  anomalies: LifestyleAnomalyReport["anomalies"],
  input: LifestyleAnomalyReport["anomalies"][number],
) {
  anomalies.push({
    ...input,
    value: round(input.value),
    baseline: round(input.baseline),
  });
}

export function buildLifestyleAnomalyReport(
  date: string,
  timezone: string,
  today: DaySummary,
  baselineDays: DaySummary[],
): LifestyleAnomalyReport {
  const baselineSteps = average(baselineDays.map((day) => day.health.steps));
  const baselineSleep = average(baselineDays.map((day) => day.health.sleep_minutes));
  const baselineScreen = average(baselineDays.map((day) => day.screen.active_screen_minutes));
  const baselineFocus = average(baselineDays.map((day) => day.screen.focus_minutes));
  const baselineSpending = average(baselineDays.map((day) => day.spending.total_expense));
  const todayFocusRatio = focusRatio(today.screen.active_screen_minutes, today.screen.focus_minutes);
  const baselineFocusRatio =
    baselineScreen !== null && baselineFocus !== null ? focusRatio(baselineScreen, baselineFocus) : null;

  const anomalies: LifestyleAnomalyReport["anomalies"] = [];

  if (baselineSteps !== null && baselineSteps >= 2000 && today.health.steps < baselineSteps * 0.6) {
    anomaly(anomalies, {
      type: "low_steps",
      severity: today.health.steps < baselineSteps * 0.35 ? "critical" : "warning",
      metric: "steps",
      value: today.health.steps,
      baseline: baselineSteps,
      unit: "count",
      message: "Step count is materially below the recent baseline.",
    });
  }

  if (baselineSleep !== null && baselineSleep >= 240 && today.health.sleep_minutes < Math.min(360, baselineSleep * 0.75)) {
    anomaly(anomalies, {
      type: "short_sleep",
      severity: today.health.sleep_minutes < 300 ? "critical" : "warning",
      metric: "sleep_minutes",
      value: today.health.sleep_minutes,
      baseline: baselineSleep,
      unit: "minutes",
      message: "Sleep duration is below the recent baseline.",
    });
  }

  if (baselineScreen !== null && baselineScreen >= 60 && today.screen.active_screen_minutes > baselineScreen * 1.4) {
    anomaly(anomalies, {
      type: "high_screen_time",
      severity: today.screen.active_screen_minutes > baselineScreen * 2 ? "critical" : "warning",
      metric: "active_screen_minutes",
      value: today.screen.active_screen_minutes,
      baseline: baselineScreen,
      unit: "minutes",
      message: "Active screen time is above the recent baseline.",
    });
  }

  if (
    todayFocusRatio !== null &&
    baselineFocusRatio !== null &&
    today.screen.active_screen_minutes >= 120 &&
    baselineFocusRatio >= 0.35 &&
    todayFocusRatio < 0.25
  ) {
    anomaly(anomalies, {
      type: "low_focus_ratio",
      severity: todayFocusRatio < 0.15 ? "critical" : "warning",
      metric: "focus_ratio",
      value: todayFocusRatio,
      baseline: baselineFocusRatio,
      unit: "ratio",
      message: "Focused work ratio is below the recent baseline.",
    });
  }

  if (baselineSpending !== null && baselineSpending >= 20 && today.spending.total_expense > baselineSpending * 1.8) {
    anomaly(anomalies, {
      type: "high_spending",
      severity: today.spending.total_expense > baselineSpending * 3 ? "critical" : "warning",
      metric: "spending_expense",
      value: today.spending.total_expense,
      baseline: baselineSpending,
      unit: today.spending.currency,
      message: "Spending is above the recent baseline.",
    });
  }

  if (anomalies.length === 0) {
    anomaly(anomalies, {
      type: "no_major_anomaly",
      severity: "info",
      metric: "overall",
      value: 0,
      baseline: 0,
      unit: "none",
      message: "No major lifestyle anomaly was detected against the recent baseline.",
    });
  }

  const firstBaselineDay = baselineDays[0];
  const lastBaselineDay = baselineDays[baselineDays.length - 1];

  return {
    date,
    timezone,
    baseline: {
      start_date: firstBaselineDay?.date ?? null,
      end_date: lastBaselineDay?.date ?? null,
      days: baselineDays.length,
      average_steps: baselineSteps,
      average_sleep_minutes: baselineSleep,
      average_active_screen_minutes: baselineScreen,
      average_focus_minutes: baselineFocus,
      average_spending_expense: baselineSpending,
    },
    metrics: {
      steps: today.health.steps,
      sleep_minutes: today.health.sleep_minutes,
      active_screen_minutes: today.screen.active_screen_minutes,
      focus_minutes: today.screen.focus_minutes,
      focus_ratio: todayFocusRatio,
      spending_expense: today.spending.total_expense,
      currency: today.spending.currency,
    },
    anomalies,
  };
}
