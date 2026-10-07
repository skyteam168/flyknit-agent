<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Download, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { AuditLog, Device } from '@/api/types'
import { DECISION_LABELS, dateTime, sceneLabel } from '@/utils/format'
import { auth } from '@/store/auth'

// 工具参数里是员工让 AI 读写的文件内容，属于聊天内容那一档：服务端只发给
// 有查看权限的具名账号。这里据此说明「为什么是空的」，而不是留一片空白让人以为出了错
const canSeeContent = computed(() => !!auth.user?.can_read_chats && !auth.user?.must_change_password)

const PAGE_SIZE = 50
const rows = ref<AuditLog[]>([])
const devices = ref<Device[]>([])
const loading = ref(false)
const decision = ref('')
const deviceId = ref<number | null>(null)
const page = ref(1)
const hasMore = ref(false)

async function load() {
  loading.value = true
  try {
    // 多取一条，用来判断后面还有没有
    const list = await api.audit({
      decision: decision.value || undefined,
      device_id: deviceId.value,
      limit: PAGE_SIZE + 1,
      offset: (page.value - 1) * PAGE_SIZE,
    })
    hasMore.value = list.length > PAGE_SIZE
    rows.value = list.slice(0, PAGE_SIZE)
  } finally {
    loading.value = false
  }
}
function search() {
  page.value = 1
  void load()
}
function go(delta: number) {
  page.value += delta
  void load()
}
onMounted(async () => {
  void load()
  devices.value = await api.devices()
})

const RISK_LABELS: Record<string, { label: string; type: 'danger' | 'warning' | 'info' }> = {
  high: { label: '高', type: 'danger' },
  medium: { label: '中', type: 'warning' },
  low: { label: '低', type: 'info' },
}
const decisionOf = (d: string) => DECISION_LABELS[d] ?? { label: d || '—', type: 'info' as const }

function pretty(args: string) {
  try {
    return JSON.stringify(JSON.parse(args), null, 2)
  } catch {
    return args
  }
}

// ---------- 导出 ----------
const exportDialog = ref(false)
const range = ref<[string, string] | null>(null)
const exporting = ref(false)
async function doExport() {
  exporting.value = true
  try {
    await api.exportAudit({
      decision: decision.value || undefined,
      device_id: deviceId.value,
      since: range.value?.[0],
      until: range.value?.[1],
    })
    exportDialog.value = false
    ElMessage.success('已开始下载')
  } finally {
    exporting.value = false
  }
}
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>审计日志</h1>
        <p>员工端执行过的高风险操作：被拦截的、员工确认放行或拒绝的、按规则自动放行的。</p>
      </div>
      <div class="toolbar">
        <el-select v-model="decision" placeholder="全部判定" clearable style="width: 140px" @change="search">
          <el-option v-for="(v, k) in DECISION_LABELS" :key="k" :value="k" :label="v.label" />
        </el-select>
        <el-select v-model="deviceId" placeholder="全部设备" clearable filterable style="width: 200px" @change="search">
          <el-option v-for="d in devices" :key="d.id" :value="d.id" :label="`${d.machine_name}${d.user_name ? ` · ${d.user_name}` : ''}`" />
        </el-select>
        <el-button :icon="Refresh" :loading="loading" @click="load" />
        <el-button type="primary" :icon="Download" @click="exportDialog = true">导出 CSV</el-button>
      </div>
    </div>

    <div class="panel">
      <el-table :data="rows" v-loading="loading" row-key="id" empty-text="没有审计记录">
        <el-table-column type="expand">
          <template #default="{ row }">
            <div class="detail">
              <div v-if="row.arguments">
                <label>参数</label>
                <pre>{{ pretty(row.arguments) }}</pre>
              </div>
              <div v-else-if="!canSeeContent">
                <label>参数</label>
                <p class="muted">参数里是员工让 AI 读写的文件内容，需要「可查看聊天记录」权限才能看。</p>
              </div>
              <div v-if="row.summary">
                <label>摘要</label>
                <pre>{{ row.summary }}</pre>
              </div>
              <div v-if="row.conversation_id" class="muted">会话 <code>{{ row.conversation_id }}</code></div>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="时间" width="150">
          <template #default="{ row }">{{ dateTime(row.occurred_at) }}</template>
        </el-table-column>
        <el-table-column label="电脑 / 用户" min-width="160">
          <template #default="{ row }">
            <div class="who">
              <strong>{{ row.machine_name || '—' }}</strong>
              <small>{{ row.user_name }}</small>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="模式" width="90">
          <template #default="{ row }">{{ row.scene ? sceneLabel(row.scene) : '—' }}</template>
        </el-table-column>
        <el-table-column label="工具" width="150">
          <template #default="{ row }"><code>{{ row.tool_name }}</code></template>
        </el-table-column>
        <el-table-column label="参数" min-width="260" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="row.arguments" class="mono muted">{{ row.arguments }}</span>
            <span v-else-if="!canSeeContent" class="muted">需「可查看聊天记录」权限</span>
          </template>
        </el-table-column>
        <el-table-column label="风险" width="70" align="center">
          <template #default="{ row }">
            <el-tag v-if="RISK_LABELS[row.risk]" :type="RISK_LABELS[row.risk].type" size="small" effect="plain">{{ RISK_LABELS[row.risk].label }}</el-tag>
            <span v-else class="muted">{{ row.risk || '—' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="判定" width="110">
          <template #default="{ row }">
            <el-tag :type="decisionOf(row.decision).type" size="small">{{ decisionOf(row.decision).label }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="结果" width="90">
          <template #default="{ row }"><span class="muted">{{ row.status || '—' }}</span></template>
        </el-table-column>
      </el-table>
      <div class="pager">
        <span class="muted">第 {{ page }} 页 · 每页 {{ PAGE_SIZE }} 条</span>
        <el-button-group>
          <el-button :disabled="page <= 1 || loading" @click="go(-1)">上一页</el-button>
          <el-button :disabled="!hasMore || loading" @click="go(1)">下一页</el-button>
        </el-button-group>
      </div>
    </div>

    <el-dialog v-model="exportDialog" title="导出审计日志" width="460px">
      <el-form label-width="80px">
        <el-form-item label="日期范围">
          <el-date-picker
            v-model="range"
            type="daterange"
            value-format="YYYY-MM-DD"
            start-placeholder="开始"
            end-placeholder="结束"
            unlink-panels
            style="width: 100%"
          />
        </el-form-item>
      </el-form>
      <p class="hint">不选日期则导出全部。当前的判定和设备筛选也会一并生效。时间为 UTC。</p>
      <template #footer>
        <el-button @click="exportDialog = false">取消</el-button>
        <el-button type="primary" :loading="exporting" @click="doExport">导出</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.who {
  display: flex;
  flex-direction: column;
  line-height: 1.35;
}
.who strong {
  font-weight: 500;
}
.who small {
  color: var(--ink-faint);
  font-size: 12px;
}
.detail {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 4px 48px 8px;
}
.detail label {
  display: block;
  margin-bottom: 4px;
  color: var(--ink-faint);
  font-size: 12px;
}
.detail pre {
  max-height: 280px;
  margin: 0;
  padding: 10px 12px;
  overflow: auto;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
  font-family: var(--font-code);
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-all;
}
.pager {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 14px;
}
</style>
