<template>
  <div class="credentials-page">
    <section class="panel">
      <div class="panel-title">凭据管理</div>
      <a-alert
        class="credentials-intro"
        type="info"
        show-icon
        message="设备令牌仅供采集器上报事件，查询令牌仅供 AI 代理只读读取。令牌明文只在创建时显示一次。"
      />
      <div class="credentials-toolbar">
        <a-button type="primary" @click="openCreate">新建凭据</a-button>
        <a-button :loading="loading" @click="refresh">刷新</a-button>
      </div>
      <a-table
        :data-source="credentials"
        :columns="columns"
        :loading="loading"
        row-key="credential_id"
        :pagination="false"
      >
        <template #bodyCell="{ column, record }">
          <template v-if="column.key === 'name'">
            <div>{{ record.name }}</div>
            <a-typography-text type="secondary" class="credential-prefix">{{ record.token_prefix }}…</a-typography-text>
          </template>
          <template v-else-if="column.key === 'kind'">
            <a-tag :color="record.kind === 'device_token' ? 'blue' : 'purple'">
              {{ record.kind === "device_token" ? "设备令牌" : "查询令牌" }}
            </a-tag>
          </template>
          <template v-else-if="column.key === 'scopes'">
            <a-tag v-for="scope in record.scopes" :key="scope">{{ scope }}</a-tag>
          </template>
          <template v-else-if="column.key === 'allowed_event_types'">
            <a-tag v-if="record.allowed_event_types.length === 0">全部已注册类型</a-tag>
            <a-tag v-for="eventType in record.allowed_event_types" :key="eventType">{{ eventType }}</a-tag>
          </template>
          <template v-else-if="column.key === 'privacy_ceiling'">
            {{ record.privacy_ceiling }}
          </template>
          <template v-else-if="column.key === 'created_at'">{{ formatDate(record.created_at) }}</template>
          <template v-else-if="column.key === 'expires_at'">
            {{ record.expires_at ? formatDate(record.expires_at) : "永不过期" }}
          </template>
          <template v-else-if="column.key === 'last_used_at'">
            {{ record.last_used_at ? formatDate(record.last_used_at) : "从未使用" }}
          </template>
          <template v-else-if="column.key === 'status'">
            <a-tag v-if="record.revoked_at" color="red">已撤销</a-tag>
            <a-tag v-else-if="isExpired(record)" color="orange">已过期</a-tag>
            <a-tag v-else color="green">有效</a-tag>
          </template>
          <template v-else-if="column.key === 'actions'">
            <a-popconfirm
              title="撤销后该凭据立即失效，确认撤销？"
              ok-text="撤销"
              cancel-text="取消"
              :disabled="record.revoked_at !== null"
              @confirm="revoke(record.credential_id)"
            >
              <a-button danger size="small" :disabled="record.revoked_at !== null">撤销</a-button>
            </a-popconfirm>
          </template>
        </template>
      </a-table>
      <a-alert
        v-if="message"
        class="credentials-message"
        :type="messageType"
        show-icon
        :message="message"
      />
    </section>

    <a-modal
      v-model:open="createOpen"
      title="新建凭据"
      :confirm-loading="creating"
      ok-text="创建"
      cancel-text="取消"
      @ok="submitCreate"
    >
      <a-form layout="vertical">
        <a-form-item label="类型">
          <a-segmented
            v-model:value="form.kind"
            :options="[
              { label: '设备令牌（上报事件）', value: 'device_token' },
              { label: '查询令牌（只读读取）', value: 'query_token' },
            ]"
          />
        </a-form-item>
        <a-form-item label="名称" required>
          <a-input v-model:value="form.name" :maxlength="100" placeholder="例如：Windows 桌面机" />
        </a-form-item>
        <a-form-item label="Scopes">
          <a-checkbox-group v-model:value="form.scopes" :options="scopeOptions" />
          <a-typography-text type="secondary" class="scope-hint">
            {{ scopeHint }}
          </a-typography-text>
        </a-form-item>
        <a-form-item label="允许事件类型（留空表示全部已注册类型）">
          <a-select
            v-model:value="form.allowed_event_types"
            mode="tags"
            :open="false"
            placeholder="例如 activity.interval"
            :token-separators="[',', ' ']"
          />
        </a-form-item>
        <a-form-item label="隐私上限">
          <a-select v-model:value="form.privacy_ceiling">
            <a-select-option value="normal">normal（仅一般数据）</a-select-option>
            <a-select-option value="sensitive">sensitive（含敏感数据）</a-select-option>
            <a-select-option value="private">private（private 不会进入服务端）</a-select-option>
          </a-select>
        </a-form-item>
        <a-form-item label="到期时间（可选）">
          <a-date-picker
            v-model:value="form.expiresAt"
            show-time
            value-format="YYYY-MM-DDTHH:mm:ssZ"
            placeholder="永不过期"
          />
        </a-form-item>
      </a-form>
    </a-modal>

    <a-modal
      v-model:open="tokenOpen"
      title="凭据已创建"
      :footer="null"
      :mask-closable="false"
      :closable="false"
    >
      <a-alert
        type="warning"
        show-icon
        message="令牌明文只显示这一次，请立即保存。关闭后无法再次查看。"
      />
      <a-typography-paragraph class="token-display" copyable>
        {{ createdToken }}
      </a-typography-paragraph>
      <div class="token-actions">
        <a-button type="primary" @click="closeTokenModal">我已保存</a-button>
      </div>
    </a-modal>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from "vue";
import dayjs from "dayjs";
import { createCredential, listCredentials, revokeCredential } from "../api/credentials";
import type { CredentialView } from "../generated/contract-models";

const credentials = ref<CredentialView[]>([]);
const loading = ref(false);
const creating = ref(false);
const createOpen = ref(false);
const tokenOpen = ref(false);
const createdToken = ref("");
const message = ref("");
const messageType = ref<"success" | "error" | "info">("info");

const form = reactive<{
  kind: "device_token" | "query_token";
  name: string;
  scopes: string[];
  allowed_event_types: string[];
  privacy_ceiling: "normal" | "sensitive" | "private";
  expiresAt: string | null;
}>({
  kind: "device_token",
  name: "",
  scopes: ["events:write"],
  allowed_event_types: [],
  privacy_ceiling: "normal",
  expiresAt: null,
});

const KIND_SCOPES: Record<"device_token" | "query_token", Array<{ label: string; value: string }>> = {
  device_token: [
    { label: "events:write（上传活动事件）", value: "events:write" },
    { label: "health:write（上传健康观测）", value: "health:write" },
  ],
  query_token: [
    { label: "events:read（读取活动事件）", value: "events:read" },
    { label: "health:read（读取健康观测）", value: "health:read" },
  ],
};

const scopeOptions = computed(() => KIND_SCOPES[form.kind]);

const scopeHint = computed(() =>
  form.kind === "device_token"
    ? "设备令牌只能持有写 scopes。采集 Health Connect 数据需要 health:write，且隐私上限需为 sensitive。"
    : "查询令牌只能持有读 scopes。读取健康数据需要 health:read，且隐私上限需为 sensitive。",
);

function defaultScopesFor(kind: "device_token" | "query_token"): string[] {
  return [KIND_SCOPES[kind][0].value];
}

const columns = [
  { title: "名称", key: "name" },
  { title: "类型", key: "kind", width: 110 },
  { title: "Scopes", key: "scopes", width: 130 },
  { title: "允许事件类型", key: "allowed_event_types" },
  { title: "隐私上限", key: "privacy_ceiling", width: 100 },
  { title: "创建时间", key: "created_at", width: 170 },
  { title: "到期时间", key: "expires_at", width: 170 },
  { title: "最后使用", key: "last_used_at", width: 170 },
  { title: "状态", key: "status", width: 90 },
  { title: "操作", key: "actions", width: 90 },
];

onMounted(refresh);

async function refresh(): Promise<void> {
  loading.value = true;
  try {
    const result = await listCredentials();
    credentials.value = result.credentials;
  } catch (err) {
    show(err, "加载凭据失败");
  } finally {
    loading.value = false;
  }
}

function openCreate(): void {
  form.kind = "device_token";
  form.name = "";
  form.scopes = defaultScopesFor("device_token");
  form.allowed_event_types = [];
  form.privacy_ceiling = "normal";
  form.expiresAt = null;
  createOpen.value = true;
}

// Switching the actor type resets the scopes to that kind's default subset.
watch(() => form.kind, (kind) => {
  form.scopes = defaultScopesFor(kind);
});

async function submitCreate(): Promise<void> {
  if (!form.name.trim()) {
    showMessage("请填写凭据名称", "error");
    return;
  }
  if (form.scopes.length === 0) {
    showMessage("请至少选择一个 scope", "error");
    return;
  }
  creating.value = true;
  try {
    const created = await createCredential({
      kind: form.kind,
      name: form.name.trim(),
      scopes: [...form.scopes],
      allowed_event_types: form.allowed_event_types,
      privacy_ceiling: form.privacy_ceiling,
      expires_at: form.expiresAt ? dayjs(form.expiresAt).toISOString() : null,
    });
    createdToken.value = created.token;
    createOpen.value = false;
    tokenOpen.value = true;
    await refresh();
  } catch (err) {
    show(err, "创建凭据失败");
  } finally {
    creating.value = false;
  }
}

function closeTokenModal(): void {
  tokenOpen.value = false;
  createdToken.value = "";
}

async function revoke(credentialId: string): Promise<void> {
  try {
    await revokeCredential(credentialId);
    showMessage("凭据已撤销", "success");
    await refresh();
  } catch (err) {
    show(err, "撤销失败");
  }
}

function isExpired(record: CredentialView): boolean {
  return record.expires_at !== null && dayjs(record.expires_at).isBefore(dayjs());
}

function formatDate(value: string): string {
  return dayjs(value).format("YYYY-MM-DD HH:mm");
}

function show(err: unknown, fallback: string): void {
  message.value = err instanceof Error ? err.message : fallback;
  messageType.value = "error";
}

function showMessage(text: string, type: "success" | "error" | "info"): void {
  message.value = text;
  messageType.value = type;
}
</script>

<style scoped>
.credentials-page {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.credentials-intro {
  margin-bottom: 12px;
}

.credentials-toolbar {
  display: flex;
  gap: 8px;
  margin-bottom: 12px;
}

.credential-prefix {
  font-size: 12px;
}

.scope-hint {
  display: block;
  font-size: 12px;
}

.credentials-message {
  margin-top: 12px;
}

.token-display {
  margin-top: 12px;
  padding: 8px 12px;
  border-radius: 6px;
  background: rgba(0, 0, 0, 0.04);
  word-break: break-all;
}

.token-actions {
  display: flex;
  justify-content: flex-end;
}
</style>
