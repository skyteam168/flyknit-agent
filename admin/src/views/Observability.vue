<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Delete, Plus } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { ObservabilityConfig } from '@/api/types'
import { dateTime } from '@/utils/format'

/**
 * OpenTelemetry 导出（docs/observability.md）：模型调用、工具调用变成标准的追踪数据，发到公司自己的采集器。
 * 不带对话内容、参数和结果；默认也不带电脑名和使用者。看：管理员；改、测试：超级管理员。
 */
interface HeaderRow {
  name: string
  value: string
  hasValue: boolean
  changed: boolean
}

const loading = ref(false)
const saving = ref(false)
const testing = ref(false)
const config = ref<ObservabilityConfig | null>(null)
const form = ref({ enabled: false, endpoint: '', service_name: 'flyknitbuddy', include_identity: false, timeout_ms: 5000 })
const headers = ref<HeaderRow[]>([])

function fill(c: ObservabilityConfig) {
  config.value = c
  form.value = { enabled: c.enabled, endpoint: c.endpoint, service_name: c.service_name, include_identity: c.include_identity, timeout_ms: c.timeout_ms }
  headers.value = c.headers.map((h) => ({ name: h.name, value: '', hasValue: h.has_value, changed: false }))
}

async function load() {
  loading.value = true
  try {
    fill(await api.observability())
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function save() {
  if (form.value.enabled && !/^https?:\/\/\S+/.test(form.value.endpoint.trim())) {
    ElMessage.error('打开之前请先填采集器地址（http:// 或 https:// 开头）')
    return
  }
  saving.value = true
  try {
    const body = {
      ...form.value,
      endpoint: form.value.endpoint.trim(),
      headers: headers.value.filter((h) => h.name.trim()).map((h) => (h.changed || !h.hasValue ? { name: h.name.trim(), value: h.value } : { name: h.name.trim() })),
    }
    fill(await api.saveObservability(body))
    ElMessage.success(form.value.enabled ? '已保存，半分钟内开始导出' : '已保存')
  } finally {
    saving.value = false
  }
}

async function test() {
  testing.value = true
  try {
    const r = await api.testObservability()
    if (r.ok) ElMessage.success(`采集器收到了测试数据（${r.latency_ms} 毫秒）`)
    else ElMessage.error(`没发成功：${r.error}`)
  } finally {
    testing.value = false
  }
}
</script>

<template>
  <div v-loading="loading" class="page">
    <div>
      <h2>可观测性（OpenTelemetry）</h2>
      <p class="sub">
        把每一次模型调用、每一次工具调用变成标准的 OpenTelemetry 追踪数据，发到公司自己的采集器（OpenTelemetry Collector、Grafana、Jaeger、SigNoz、
        云厂商的可观测平台等，支持 OTLP/HTTP 的都行）。同一个任务的调用在同一条追踪里，可以看到一个任务调了几次模型、用了哪些工具、每一步多久、花了多少 token。
        <b>不发</b>对话内容、工具参数和结果；默认也不带电脑名和使用者。
      </p>
    </div>

    <el-card shadow="never">
      <el-form label-width="130px">
        <el-form-item label="导出">
          <el-switch v-model="form.enabled" />
        </el-form-item>
        <el-form-item label="采集器地址">
          <el-input v-model="form.endpoint" placeholder="例如 http://otel-collector:4318（会自动加上 /v1/traces）" />
          <small v-if="config?.traces_url" class="muted">实际发往：{{ config.traces_url }}</small>
        </el-form-item>
        <el-form-item label="请求头">
          <div class="headers">
            <div v-for="(h, i) in headers" :key="i" class="header-row">
              <el-input v-model="h.name" placeholder="名字，例如 Authorization" class="h-name" />
              <el-input v-if="h.changed || !h.hasValue" v-model="h.value" type="password" show-password placeholder="值，例如 Bearer xxx" class="h-value" />
              <span v-else class="h-value muted">已设置 <el-button link type="primary" @click="h.changed = true">修改</el-button></span>
              <el-button :icon="Delete" text type="danger" @click="headers.splice(i, 1)" />
            </div>
            <el-button :icon="Plus" size="small" @click="headers.push({ name: '', value: '', hasValue: false, changed: true })">加一个请求头</el-button>
            <small class="muted">采集器要认证时用（比如 API Key）。值加密保存，保存后这里不再显示。</small>
          </div>
        </el-form-item>
        <el-form-item label="服务名">
          <el-input v-model="form.service_name" maxlength="100" style="width: 260px" />
          <small class="muted gap-left">追踪里 service.name 的值</small>
        </el-form-item>
        <el-form-item label="带上电脑和使用者">
          <el-switch v-model="form.include_identity" />
          <small class="muted gap-left">打开后带上电脑名（host.name）和使用者（enduser.id）；只带部门和电脑编号时请保持关闭</small>
        </el-form-item>
        <el-form-item label="超时">
          <el-input-number v-model="form.timeout_ms" :min="1000" :max="30000" :step="1000" /> <span class="muted gap-left">毫秒</span>
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :loading="saving" @click="save">保存</el-button>
          <el-button :loading="testing" :disabled="!config?.endpoint" @click="test">发一条测试数据</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card v-if="config" shadow="never">
      <template #header>导出情况（服务端启动以来）</template>
      <el-descriptions :column="3" size="small">
        <el-descriptions-item label="已发出">{{ config.stats.sent }} 条</el-descriptions-item>
        <el-descriptions-item label="排队中">{{ config.stats.queued }} 条</el-descriptions-item>
        <el-descriptions-item label="丢弃">{{ config.stats.dropped }} 条</el-descriptions-item>
        <el-descriptions-item label="最近一次发出">{{ config.stats.last_sent_at ? dateTime(config.stats.last_sent_at) : '—' }}</el-descriptions-item>
        <el-descriptions-item label="最近一次出错" :span="2">
          <span v-if="config.stats.last_error" class="warn">{{ dateTime(config.stats.last_error_at!) }} {{ config.stats.last_error }}</span>
          <span v-else>—</span>
        </el-descriptions-item>
      </el-descriptions>
    </el-card>
  </div>
</template>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  gap: 12px;
  max-width: 980px;
}
h2 {
  margin: 0 0 4px;
}
.sub,
.muted {
  margin: 0;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.6;
}
.warn {
  color: var(--el-color-warning);
}
.gap-left {
  margin-left: 8px;
}
.headers {
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: 100%;
}
.header-row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.h-name {
  width: 220px;
}
.h-value {
  flex: 1;
}
</style>
