<script setup lang="ts">
/**
 * 数据保护（docs/data-protection.md）：发给外部模型前的脱敏。
 * - 开关在安全中心（数据脱敏三项），可以按部门、按电脑设置；
 * - 这里看最近换掉了多少（只有数量，没有原值）；
 * - 「试一试」：贴一段文字，看会被换成什么（不发给任何模型、不记录）。
 */
import { computed, onMounted, ref } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { useRouter } from 'vue-router'
import { get, post } from '@/api/http'

interface Summary {
  days: number
  labels: Record<string, string>
  requests: Record<string, number>
  totals: Record<string, Record<string, number>>
  devices: { device_id: number; machine_name: string; user_name: string; department: string; counts: Record<string, number>; total: number }[]
  recent: { device_id: number; machine_name: string; scene: string; action: string; counts: Record<string, number>; created_at: string }[]
}

const router = useRouter()
const days = ref(30)
const loading = ref(false)
const summary = ref<Summary | null>(null)
const enabled = ref<boolean | null>(null)

async function load() {
  loading.value = true
  try {
    summary.value = await get<Summary>('/dlp/summary', { days: days.value })
    const sec = await get<{ values?: Record<string, unknown> }>('/security')
    enabled.value = Boolean(sec.values?.dlp_enabled)
  } finally {
    loading.value = false
  }
}
onMounted(load)

const label = (k: string) => summary.value?.labels[k] ?? k
const fmtCounts = (c: Record<string, number>) => Object.entries(c).map(([k, n]) => `${label(k)} ${n}`).join('，')
const kinds = computed(() => Object.keys(summary.value?.labels ?? {}))
const total = (action: string) => Object.values(summary.value?.totals[action] ?? {}).reduce((a, b) => a + b, 0)

// ---------- 试一试 ----------
const sample = ref('员工张三，身份证 11010519491231002X，工资卡 6222 0212 3456 7890 128，手机 13812345678，邮箱 zhang.san@corp.cn，系统密码是Abc12345。订单号 202611200000001234。')
const contacts = ref(true)
const result = ref<{ masked: string; counts: Record<string, number> } | null>(null)
async function tryIt() {
  result.value = await post('/dlp/preview', { text: sample.value, contacts: contacts.value })
}
</script>

<template>
  <div class="page">
    <div class="head">
      <div>
        <h2>数据保护</h2>
        <p class="sub">
          发给外部模型（如阿里云百炼）前，服务端把身份证号、银行卡号、手机号、邮箱换成 [身份证_1] 这样的占位符，密码、密钥直接隐藏；
          模型回答回来时再换回真实值。员工看到的、AI 写进文件里的都是真实值，模型服务商见不到原值。
        </p>
      </div>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
    </div>

    <el-alert v-if="enabled === false" type="warning" :closable="false" show-icon class="mb">
      <template #title>
        数据脱敏还没有打开。到「安全中心」打开「数据脱敏：发给模型前把身份证、银行卡、手机号、邮箱换成占位符」；
        可以先对一个部门或几台电脑打开（部门 / 设备详情里的安全设置）。
        <el-button link type="primary" @click="router.push({ name: 'security' })">去安全中心</el-button>
      </template>
    </el-alert>

    <el-row :gutter="16">
      <el-col :xs="24" :md="12">
        <el-card shadow="never" class="mb">
          <template #header>
            <div class="card-head">
              <span>最近 {{ days }} 天</span>
              <el-select v-model="days" size="small" style="width: 110px" @change="load">
                <el-option :value="7" label="7 天" />
                <el-option :value="30" label="30 天" />
                <el-option :value="90" label="90 天" />
              </el-select>
            </div>
          </template>
          <div class="stats">
            <div><b>{{ total('masked') }}</b><span>处换成占位符</span><small>{{ summary?.requests.masked ?? 0 }} 次请求</small></div>
          </div>
          <el-table :data="kinds.map((k) => ({ k, masked: summary?.totals.masked?.[k] ?? 0 }))" size="small">
            <el-table-column label="类型"><template #default="{ row }">{{ label(row.k) }}</template></el-table-column>
            <el-table-column prop="masked" label="已替换" width="100" />
          </el-table>
        </el-card>
      </el-col>
      <el-col :xs="24" :md="12">
        <el-card shadow="never" class="mb">
          <template #header>试一试（不发给任何模型，不记录）</template>
          <el-input v-model="sample" type="textarea" :rows="4" maxlength="20000" />
          <div class="row">
            <el-checkbox v-model="contacts">手机号、邮箱也换</el-checkbox>
            <el-button type="primary" @click="tryIt">看看会换成什么</el-button>
          </div>
          <template v-if="result">
            <pre class="out">{{ result.masked }}</pre>
            <p class="muted">{{ Object.keys(result.counts).length ? fmtCounts(result.counts) : '没有识别到要换的内容' }}</p>
          </template>
        </el-card>
      </el-col>
    </el-row>

    <el-card shadow="never" class="mb">
      <template #header>涉及最多的电脑</template>
      <el-table :data="summary?.devices ?? []" size="small" empty-text="还没有记录">
        <el-table-column prop="machine_name" label="电脑" />
        <el-table-column prop="user_name" label="账号" />
        <el-table-column prop="department" label="部门" />
        <el-table-column label="明细"><template #default="{ row }">{{ fmtCounts(row.counts) }}</template></el-table-column>
        <el-table-column prop="total" label="合计" width="90" />
      </el-table>
    </el-card>

    <el-card shadow="never">
      <template #header>最近记录</template>
      <el-table :data="summary?.recent ?? []" size="small" empty-text="还没有记录" max-height="420">
        <el-table-column label="时间" width="180"><template #default="{ row }">{{ new Date(row.created_at).toLocaleString('zh-CN', { hour12: false }) }}</template></el-table-column>
        <el-table-column prop="machine_name" label="电脑" />
        <el-table-column prop="scene" label="场景" width="110" />
        <el-table-column label="处理" width="100">
          <template #default="{ row }"><el-tag :type="row.action === 'blocked' ? 'danger' : 'success'" size="small">{{ row.action === 'blocked' ? '拦下（旧版）' : '已替换' }}</el-tag></template>
        </el-table-column>
        <el-table-column label="明细"><template #default="{ row }">{{ fmtCounts(row.counts) }}</template></el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<style scoped>
.head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 12px;
}
.head h2 {
  margin: 0 0 4px;
}
.sub,
.muted {
  margin: 0;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.6;
}
.mb {
  margin-bottom: 16px;
}
.card-head,
.row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.row {
  margin-top: 8px;
}
.stats {
  display: flex;
  gap: 24px;
  margin-bottom: 12px;
}
.stats div {
  display: flex;
  flex-direction: column;
}
.stats b {
  font-size: 26px;
  color: var(--el-color-success);
}
.stats small {
  color: var(--el-text-color-secondary);
}
.out {
  margin: 10px 0 4px;
  padding: 10px 12px;
  border-radius: 6px;
  background: var(--el-fill-color-light);
  white-space: pre-wrap;
  word-break: break-all;
  font-size: 13px;
}
</style>
