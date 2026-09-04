<template>
  <div class="page-stack">
    <a-alert v-if="error" type="error" show-icon :message="error" />

    <section class="toolbar-panel">
      <a-space wrap>
        <a-date-picker v-model:value="startDate" />
        <a-date-picker v-model:value="endDate" />
        <a-button type="primary" :loading="loading" @click="load">查询</a-button>
        <a-tag color="blue">报表时区：{{ reportTimezone }}</a-tag>
      </a-space>
    </section>

    <SourcePolicyPanel :context="context" />

    <a-alert
      v-if="completeness === 'partial'"
      type="warning"
      show-icon
      message="查询范围内存在被凭据隐私上限或事件类型限制隐藏的支付数据，当前结果不完整（partial）。"
    />

    <a-alert
      v-if="pendingTransactions.length > 0"
      type="warning"
      show-icon
    >
      <template #message>
        {{ pendingTransactions.length }} 笔交易缺少稳定来源标识且疑似重复（金额、商户、方向与发生时间相近），已标记待确认并作为独立观测保留——系统从不仅凭金额与时间合并交易。可在交易记录中「确认」或通过「修正」改写商户/分类后确认，驳回误报请用「修正」中的作废开关（均记入审计）。
      </template>
    </a-alert>

    <div class="metric-grid">
      <MetricCard
        title="总支出"
        :value="expenseAvailable ? formatMoney(expenseMinor, currency) : '无数据'"
        :note="expenseAvailable ? `${expenseCount} 笔支出` : '范围内没有支出交易；缺失不会显示为 0'"
        :icon="PayCircleOutlined"
      />
      <MetricCard
        title="总收入"
        :value="incomeAvailable ? formatMoney(incomeMinor, currency) : '无数据'"
        :note="incomeAvailable ? `${incomeCount} 笔收入` : '范围内没有收入交易'"
        :icon="DownCircleOutlined"
      />
      <MetricCard
        title="交易笔数"
        :value="String(transactions.length)"
        :note="`其中待确认 ${pendingTransactions.length} 笔，已计入合计`"
        :icon="FileDoneOutlined"
      />
      <MetricCard
        title="商户数"
        :value="String(merchantRows.length)"
        note="分类由设备端规则产生"
        :icon="ShopOutlined"
      />
    </div>

    <div class="dashboard-grid">
      <section class="panel">
        <div class="panel-title">每日支出（仅含存在交易的日期）</div>
        <BaseChart v-if="dailyOption" :option="dailyOption" />
        <EmptyState v-else description="暂无支付交易" />
      </section>
      <section class="panel">
        <div class="panel-title">分类支出</div>
        <BaseChart v-if="categoryOption" :option="categoryOption" />
        <EmptyState v-else description="暂无分类数据" />
      </section>
    </div>

    <section class="panel">
      <div class="panel-title">按分类（设备端规则提取）</div>
      <a-table
        :data-source="categoryRows"
        :columns="categoryColumns"
        :pagination="false"
        size="small"
        row-key="category"
      />
    </section>

    <section class="panel">
      <div class="panel-title">按商户（批准的提取标签）</div>
      <a-table
        :data-source="merchantRows"
        :columns="merchantColumns"
        :pagination="false"
        size="small"
        row-key="merchant"
      />
    </section>

    <section class="panel">
      <div class="panel-title">交易记录</div>
      <a-table
        :data-source="timelineRows"
        :columns="timelineColumns"
        :pagination="false"
        size="small"
        row-key="key"
      >
        <template #bodyCell="{ column, record }">
          <template v-if="column.key === 'status'">
            <a-tag v-if="record.pending" color="orange">待确认</a-tag>
            <a-tag v-else color="green">已确认来源</a-tag>
          </template>
          <template v-else-if="column.key === 'provenanceKind'">
            <a-tag v-if="record.manual" color="purple">人工修正</a-tag>
            <a-tag v-else color="default">自动提取</a-tag>
          </template>
          <template v-else-if="column.key === 'actions'">
            <a-space size="small">
              <a-button size="small" @click="openCorrection(record.raw)">修正</a-button>
              <a-popconfirm
                v-if="record.pending"
                title="确认这笔交易不是重复观测？"
                @confirm="confirmPending(record.raw)"
              >
                <a-button size="small" type="link">确认</a-button>
              </a-popconfirm>
            </a-space>
          </template>
          <template v-else-if="column.key === 'eventId'">
            <a-tooltip :title="record.fullEventId">
              <span>{{ record.eventId }}</span>
            </a-tooltip>
          </template>
          <template v-else>{{ record[column.key] }}</template>
        </template>
      </a-table>
      <a-button
        v-if="nextCursor"
        class="load-more"
        :loading="loading"
        @click="load(nextCursor)"
      >
        加载更多
      </a-button>
      <a-typography-text v-if="provenanceText" type="secondary">
        来源种类：{{ provenanceText }}
      </a-typography-text>
    </section>

    <EventCorrectionModal :event="correctingEvent" @corrected="onCorrected" @closed="correctingEvent = null" />
  </div>
</template>

<script setup lang="ts">
import type { EChartsOption } from "echarts";
import { computed, ref } from "vue";
import { DownCircleOutlined, FileDoneOutlined, PayCircleOutlined, ShopOutlined } from "@ant-design/icons-vue";
import dayjs, { type Dayjs } from "dayjs";
import { fetchPaymentEvents } from "../api/payment";
import { submitCorrection } from "../api/corrections";
import { fetchOwnerSettings } from "../api/settings";
import type { QueryContext, VersionedEvent } from "../generated/contract-models";
import { message } from "ant-design-vue";
import BaseChart from "../components/charts/BaseChart.vue";
import EmptyState from "../components/common/EmptyState.vue";
import EventCorrectionModal from "../components/common/EventCorrectionModal.vue";
import MetricCard from "../components/common/MetricCard.vue";
import SourcePolicyPanel from "../components/common/SourcePolicyPanel.vue";
import {
  addDaysIso,
  dateInTimezone,
  formatCaptureZone,
  formatUtcText,
  todayInTimezone,
  zonedDayRangeUtc,
} from "../utils/date";

const reportTimezone = ref("UTC");
const startDate = ref<Dayjs | null>(null);
const endDate = ref<Dayjs | null>(null);
const events = ref<VersionedEvent[]>([]);
const context = ref<QueryContext | null>(null);
const nextCursor = ref<string | null>(null);
const loading = ref(false);
const error = ref("");

const completeness = computed(() => context.value?.completeness ?? "");
const provenance = computed(() => context.value?.provenance ?? []);

const rangeText = computed(() => ({
  start: startDate.value ? startDate.value.format("YYYY-MM-DD") : "",
  end: endDate.value ? endDate.value.format("YYYY-MM-DD") : "",
}));

const transactions = computed(() => events.value.filter((event) => event.event_type === "payment.transaction"));
const pendingTransactions = computed(() => transactions.value.filter((event) => event.payload.pending_confirmation === true));
const expenseTransactions = computed(() => transactions.value.filter((event) => event.payload.direction === "expense"));
const incomeTransactions = computed(() => transactions.value.filter((event) => event.payload.direction === "income"));

const expenseMinor = computed(() => sumMinor(expenseTransactions.value));
const incomeMinor = computed(() => sumMinor(incomeTransactions.value));
const expenseCount = computed(() => expenseTransactions.value.length);
const incomeCount = computed(() => incomeTransactions.value.length);
const expenseAvailable = computed(() => expenseTransactions.value.length > 0);
const incomeAvailable = computed(() => incomeTransactions.value.length > 0);

/** All amounts share the ISO 4217 code carried by the transactions (WeChat Pay uploads CNY). */
const currency = computed(() => transactions.value[0]?.payload.amount?.currency ?? "CNY");

function sumMinor(list: VersionedEvent[]): number {
  return list.reduce((total, event) => total + (event.payload.amount?.value ?? 0), 0);
}

/** Renders exact minor units as a decimal string with integer math, never floats. */
function formatMoney(minor: number, currencyCode: string): string {
  const sign = minor < 0 ? "-" : "";
  const abs = Math.abs(minor);
  return `${sign}¥${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, "0")} ${currencyCode}`;
}

/** [rangeStart, rangeEnd) instants of the selected local days in the report timezone. */
function currentRangeUtc(): { start: Date; end: Date } | null {
  const start = rangeText.value.start;
  const endExclusive = rangeText.value.end ? addDaysIso(rangeText.value.end, 1) : "";
  if (!start || !endExclusive) return null;
  const startRange = zonedDayRangeUtc(start, reportTimezone.value);
  const endRange = zonedDayRangeUtc(endExclusive, reportTimezone.value);
  return { start: startRange.start, end: endRange.end };
}

const categoryLabels: Record<string, string> = {
  food: "餐饮",
  transport: "交通",
  shopping: "购物",
  bills: "生活缴费",
  health: "医疗健康",
  education: "教育",
  entertainment: "娱乐",
  transfer: "转账红包",
  uncategorized: "未分类",
};

interface CategoryTotals {
  category: string;
  expenseMinor: number;
  incomeMinor: number;
  count: number;
}

const categoryTotals = computed<CategoryTotals[]>(() => {
  const groups = new Map<string, CategoryTotals>();
  for (const event of transactions.value) {
    const category = event.payload.category ?? "uncategorized";
    const row = groups.get(category) ?? { category, expenseMinor: 0, incomeMinor: 0, count: 0 };
    row.count += 1;
    if (event.payload.direction === "expense") row.expenseMinor += event.payload.amount?.value ?? 0;
    else row.incomeMinor += event.payload.amount?.value ?? 0;
    groups.set(category, row);
  }
  return [...groups.values()].sort((a, b) => b.count - a.count);
});

const categoryRows = computed(() => categoryTotals.value.map((row) => ({
  category: categoryLabels[row.category] ?? row.category,
  expense: row.expenseMinor > 0 ? formatMoney(row.expenseMinor, currency.value) : "无数据",
  income: row.incomeMinor > 0 ? formatMoney(row.incomeMinor, currency.value) : "无数据",
  count: row.count,
})));

const categoryColumns = [
  { title: "分类", dataIndex: "category" },
  { title: "支出", dataIndex: "expense" },
  { title: "收入", dataIndex: "income" },
  { title: "笔数", dataIndex: "count" },
];

interface MerchantTotals {
  merchant: string;
  expenseMinor: number;
  incomeMinor: number;
  count: number;
}

const merchantTotals = computed<MerchantTotals[]>(() => {
  const groups = new Map<string, MerchantTotals>();
  for (const event of transactions.value) {
    const merchant = event.payload.merchant;
    if (!merchant) continue;
    const row = groups.get(merchant) ?? { merchant, expenseMinor: 0, incomeMinor: 0, count: 0 };
    row.count += 1;
    if (event.payload.direction === "expense") row.expenseMinor += event.payload.amount?.value ?? 0;
    else row.incomeMinor += event.payload.amount?.value ?? 0;
    groups.set(merchant, row);
  }
  return [...groups.values()].sort((a, b) => b.count - a.count);
});

const merchantRows = computed(() => merchantTotals.value.map((row) => ({
  merchant: row.merchant,
  expense: row.expenseMinor > 0 ? formatMoney(row.expenseMinor, currency.value) : "无数据",
  income: row.incomeMinor > 0 ? formatMoney(row.incomeMinor, currency.value) : "无数据",
  count: row.count,
})));

const merchantColumns = [
  { title: "商户", dataIndex: "merchant" },
  { title: "支出", dataIndex: "expense" },
  { title: "收入", dataIndex: "income" },
  { title: "笔数", dataIndex: "count" },
];

const dailyExpense = computed(() => {
  const result = new Map<string, number>();
  for (const event of expenseTransactions.value) {
    const key = dateInTimezone(event.start_at, reportTimezone.value);
    result.set(key, (result.get(key) ?? 0) + (event.payload.amount?.value ?? 0));
  }
  return [...result.entries()].sort((a, b) => a[0].localeCompare(b[0]));
});

const dailyOption = computed<EChartsOption | null>(() => {
  if (!dailyExpense.value.length) return null;
  return {
    grid: { left: 72, right: 24, top: 24, bottom: 32 },
    tooltip: { trigger: "axis" },
    xAxis: { type: "category", data: dailyExpense.value.map(([date]) => date.slice(5)) },
    yAxis: { type: "value", name: "元" },
    series: [
      {
        type: "bar",
        data: dailyExpense.value.map(([, minor]) => minor / 100),
        color: "#fa8c16",
        barMaxWidth: 20,
      },
    ],
  };
});

const categoryOption = computed<EChartsOption | null>(() => {
  const slices = categoryTotals.value.filter((row) => row.expenseMinor > 0);
  if (!slices.length) return null;
  return {
    tooltip: { trigger: "item" },
    legend: { orient: "vertical", left: "left", top: "middle" },
    series: [
      {
        type: "pie",
        radius: ["38%", "68%"],
        center: ["58%", "50%"],
        data: slices.map((row) => ({
          name: categoryLabels[row.category] ?? row.category,
          value: row.expenseMinor / 100,
        })),
      },
    ],
  };
});

const timelineColumns = [
  { title: "发生时间（UTC）", dataIndex: "start" },
  { title: "金额", dataIndex: "amount" },
  { title: "方向", dataIndex: "direction" },
  { title: "商户", dataIndex: "merchant" },
  { title: "分类", dataIndex: "category" },
  { title: "状态", key: "status" },
  { title: "解释", key: "provenanceKind" },
  { title: "设备", dataIndex: "device" },
  { title: "事件", dataIndex: "eventId", key: "eventId" },
  { title: "采集时区", dataIndex: "captureZone" },
  { title: "修订", dataIndex: "revision" },
  { title: "同步时间（UTC）", dataIndex: "synced" },
  { title: "操作", key: "actions" },
];

const timelineRows = computed(() => transactions.value.map((event, index) => ({
  key: `${event.event_id}-${event.revision}-${index}`,
  start: formatUtcText(event.start_at),
  amount: formatMoney(event.payload.amount?.value ?? 0, event.payload.amount?.currency ?? "CNY"),
  direction: event.payload.direction === "income" ? "收入" : "支出",
  merchant: event.payload.merchant,
  category: categoryLabels[event.payload.category ?? "uncategorized"] ?? event.payload.category,
  pending: event.payload.pending_confirmation === true,
  manual: event.correction !== undefined,
  raw: event,
  device: `${event.device.platform} · ${event.device.id}`,
  eventId: event.event_id.slice(0, 8),
  fullEventId: event.event_id,
  captureZone: formatCaptureZone(event.capture_timezone, event.capture_offset_minutes),
  revision: `修订 ${event.revision}`,
  synced: formatUtcText(event.provenance.observed_at),
})));

const correctingEvent = ref<VersionedEvent | null>(null);

function openCorrection(event: VersionedEvent) {
  correctingEvent.value = event;
}

/** One-click confirmation of a suspected-duplicate transaction via a higher revision. */
async function confirmPending(event: VersionedEvent) {
  try {
    await submitCorrection(event.event_id, {
      fields: [{ path: "payload.pending_confirmation", value: false }],
      reason: "确认非重复观测",
    });
    message.success("已确认来源，待确认标记解除。");
    await load();
  } catch (err) {
    message.error(err instanceof Error ? err.message : String(err));
  }
}

async function onCorrected() {
  correctingEvent.value = null;
  await load();
}

const provenanceText = computed(() => provenance.value.join("、"));

async function load(cursor?: string) {
  const range = currentRangeUtc();
  if (!range) {
    error.value = "请选择查询日期范围。";
    return;
  }
  loading.value = true;
  error.value = "";
  try {
    const page = await fetchPaymentEvents(range.start, range.end, reportTimezone.value, cursor ? { cursor } : undefined);
    events.value = cursor ? [...events.value, ...page.data] : page.data;
    context.value = page.context;
    nextCursor.value = page.page.next_cursor;
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err);
  } finally {
    loading.value = false;
  }
}

async function initialize() {
  try {
    // The Owner report timezone defines report boundaries; the browser timezone is never used.
    reportTimezone.value = (await fetchOwnerSettings()).report_timezone;
  } catch {
    // Settings unavailable; the server still defaults to UTC.
  }
  const today = todayInTimezone(reportTimezone.value);
  endDate.value = dayjs(today);
  startDate.value = dayjs(addDaysIso(today, -29));
  await load();
}

initialize();
</script>

<style scoped>
.toolbar-panel {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.load-more {
  margin-top: 12px;
}
</style>
