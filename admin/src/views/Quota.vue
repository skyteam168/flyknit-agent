<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Refresh, Search } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { DeviceUsage } from '@/api/types'
import { num, short } from '@/utils/format'

const quota = reactive({ daily_tokens: 0, contact_name: '', contact_email: '', contact_phone: '' })
const saving = ref(false)
const days = ref(7)
const usage = ref<DeviceUsage[]>([])
const loading = ref(false)
const keyword = ref('')

const PRESETS = [0, 100_000, 200_000, 500_000, 1_000_000]

async function loadQuota() {
  Object.assign(quota, await api.quota())
}
async function loadUsage() {
  loading.value = true
  try {
    usage.value = await api.usage(days.value)
  } finally {
    loading.value = false
  }
}
onMounted(() => Promise.all([loadQuota(), loadUsage()]))

async function save() {
  saving.value = true
  try {
    Object.assign(quota, await api.setQuota({ ...quota }))
    ElMessage.success('配额已保存，立即生效')
  } finally {
    saving.value = false
  }
}

const filtered = computed(() => {
  const q = keyword.value.trim().toLowerCase()
  return q ? usage.value.filter((u) => `${u.machine_name} ${u.user_name}`.toLowerCase().includes(q)) : usage.value
})
const total = computed(() => usage.value.reduce((s, u) => s + u.tokens, 0))
const todayTotal = computed(() => usage.value.reduce((s, u) => s + u.today_tokens, 0))
const overCount = computed(() => (quota.daily_tokens ? usage.value.filter((u) => u.today_tokens >= quota.daily_tokens).length : 0))

function todayPercent(u: DeviceUsage) {
  return quota.daily_tokens ? Math.min(100, Math.round((u.today_tokens / quota.daily_tokens) * 100)) : 0
}
function status(p: number) {
  return p >= 100 ? 'exception' : p >= 80 ? 'warning' : 'success'
}
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>配额与用量</h1>
        <p>每台电脑每天能用多少 token。用完后网关直接拒绝，员工端提示联系下面填写的 IT 联系人。</p>
      </div>
    </div>

    <div class="layout">
      <section class="panel">
        <div class="panel-title"><h3>每日配额</h3></div>
        <el-form :model="quota" label-position="top">
          <el-form-item label="每台电脑每天上限（token）">
            <el-input-number v-model="quota.daily_tokens" :min="0" :step="50000" controls-position="right" style="width: 100%" />
            <div class="presets">
              <el-check-tag
                v-for="p in PRESETS"
                :key="p"
                :checked="quota.daily_tokens === p"
                @change="quota.daily_tokens = p"
              >{{ p ? short(p) : '不限制' }}</el-check-tag>
            </div>
            <p class="hint">0 表示不限制。按工厂所在时区（UTC+8）每天零点重置。</p>
          </el-form-item>
          <el-divider content-position="left">额度用完时显示的联系人</el-divider>
          <el-form-item label="联系人"><el-input v-model="quota.contact_name" placeholder="IT 管理员" /></el-form-item>
          <el-form-item label="邮箱"><el-input v-model="quota.contact_email" /></el-form-item>
          <el-form-item label="电话 / 分机"><el-input v-model="quota.contact_phone" /></el-form-item>
          <el-button type="primary" :loading="saving" @click="save">保存配额</el-button>
        </el-form>
      </section>

      <section class="panel">
        <div class="panel-title">
          <h3>
            各电脑用量
            <small>
              今日合计 {{ short(todayTotal) }} · 近 {{ days }} 天合计 {{ short(total) }}
              <span v-if="overCount" class="over"> · {{ overCount }} 台今日已超额</span>
            </small>
          </h3>
          <div class="toolbar">
            <el-input v-model="keyword" :prefix-icon="Search" placeholder="搜索电脑或用户" clearable style="width: 180px" />
            <el-select v-model="days" style="width: 110px" @change="loadUsage">
              <el-option :value="1" label="今天" />
              <el-option :value="7" label="近 7 天" />
              <el-option :value="30" label="近 30 天" />
              <el-option :value="90" label="近 90 天" />
            </el-select>
            <el-button :icon="Refresh" :loading="loading" @click="loadUsage" />
          </div>
        </div>
        <el-table :data="filtered" v-loading="loading" empty-text="这段时间没有用量" :default-sort="{ prop: 'tokens', order: 'descending' }">
          <el-table-column label="电脑" min-width="140" prop="machine_name" sortable />
          <el-table-column label="用户" min-width="110" prop="user_name" sortable />
          <el-table-column label="今日用量" min-width="200" prop="today_tokens" sortable>
            <template #default="{ row }">
              <div v-if="quota.daily_tokens" class="bar">
                <el-progress
                  :percentage="todayPercent(row as DeviceUsage)"
                  :status="status(todayPercent(row as DeviceUsage))"
                  :stroke-width="6"
                  :show-text="false"
                />
                <span>{{ short(row.today_tokens) }} / {{ short(quota.daily_tokens) }}</span>
              </div>
              <span v-else>{{ short(row.today_tokens) }}</span>
            </template>
          </el-table-column>
          <el-table-column :label="`近 ${days} 天`" width="120" prop="tokens" sortable align="right">
            <template #default="{ row }">{{ short(row.tokens) }}</template>
          </el-table-column>
          <el-table-column label="请求次数" width="110" prop="requests" sortable align="right">
            <template #default="{ row }">{{ num(row.requests) }}</template>
          </el-table-column>
        </el-table>
      </section>
    </div>
  </div>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: 340px minmax(0, 1fr);
  gap: 16px;
  align-items: start;
}
@media (max-width: 1100px) {
  .layout {
    grid-template-columns: 1fr;
  }
}
.presets {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}
.presets .el-check-tag {
  font-weight: 500;
}
.hint {
  margin-top: 6px;
}
.bar {
  display: flex;
  gap: 10px;
  align-items: center;
}
.bar .el-progress {
  flex: 1;
}
.bar span {
  color: var(--ink-soft);
  font-size: 12px;
  white-space: nowrap;
}
.over {
  color: var(--red);
}
</style>
