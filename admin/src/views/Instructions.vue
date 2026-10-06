<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { RemoteInstruction, RemoteInstructionDetail } from '@/api/types'
import { auth } from '@/store/auth'
import { dateTime, relative } from '@/utils/format'
import { statusMeta } from '@/utils/tasks'

const canDispatch = computed(() => !!auth.user?.can_dispatch && !auth.user?.must_change_password)

const list = ref<RemoteInstruction[]>([])
const loading = ref(false)
const offset = ref(0)
const limit = 30
const hasMore = ref(false)

async function load(reset = true) {
  loading.value = true
  try {
    if (reset) offset.value = 0
    const data = await api.instructions({ limit, offset: offset.value })
    list.value = reset ? data : [...list.value, ...data]
    hasMore.value = data.length === limit
  } finally {
    loading.value = false
  }
}
onMounted(() => load())

function progress(ins: RemoteInstruction): number {
  if (!ins.total) return 0
  const done = (ins.counts.succeeded ?? 0) + (ins.counts.failed ?? 0) + (ins.counts.cancelled ?? 0) + (ins.counts.expired ?? 0)
  return Math.round((done / ins.total) * 100)
}
function progressStatus(ins: RemoteInstruction): '' | 'success' | 'exception' | 'warning' {
  if (progress(ins) < 100) return ''
  if (ins.counts.failed) return 'exception'
  if (ins.counts.cancelled || ins.counts.expired) return 'warning'
  return 'success'
}

const canCancel = (ins: RemoteInstruction) => canDispatch.value && (ins.counts.pending ?? 0) > 0

async function cancel(ins: RemoteInstruction) {
  await ElMessageBox.confirm(
    '只会取消还没开始执行的电脑；已经在执行的不会中断。',
    `取消指令「${ins.title}」`,
    { type: 'warning', confirmButtonText: '取消指令', cancelButtonText: '关闭' },
  )
  const updated = await api.cancelInstruction(ins.id)
  Object.assign(ins, updated)
  if (detail.value?.id === ins.id) detail.value = await api.instruction(ins.id)
  ElMessage.success('已取消待执行的指令')
}

// ---------- 详情抽屉 ----------
const drawer = ref(false)
const detail = ref<RemoteInstructionDetail | null>(null)
const detailLoading = ref(false)

async function open(id: number) {
  drawer.value = true
  detailLoading.value = true
  try {
    detail.value = await api.instruction(id)
  } finally {
    detailLoading.value = false
  }
}
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>指令中心</h1>
        <p>查看下发给员工端 AI 助手的指令执行进度与回报结果。下发入口在「设备列表」里勾选电脑。</p>
      </div>
      <div class="toolbar">
        <el-button :icon="Refresh" :loading="loading" @click="load()">刷新</el-button>
      </div>
    </div>

    <div class="panel">
      <el-table :data="list" v-loading="loading" empty-text="还没有下发过指令" @row-click="(row: RemoteInstruction) => open(row.id)">
        <el-table-column label="指令" min-width="260">
          <template #default="{ row }">
            <strong class="link">{{ row.title }}</strong>
            <small class="by">{{ row.created_by }}<template v-if="row.cancelled_by"> · 已由 {{ row.cancelled_by }} 取消</template></small>
          </template>
        </el-table-column>
        <el-table-column label="进度" min-width="220">
          <template #default="{ row }">
            <el-progress :percentage="progress(row as RemoteInstruction)" :status="progressStatus(row as RemoteInstruction)" :stroke-width="10" />
            <small class="counts">
              共 {{ row.total }}
              <span v-if="row.counts.succeeded"> · 成功 {{ row.counts.succeeded }}</span>
              <span v-if="row.counts.failed" class="bad"> · 失败 {{ row.counts.failed }}</span>
              <span v-if="row.counts.running"> · 执行中 {{ row.counts.running }}</span>
              <span v-if="row.counts.pending"> · 待执行 {{ row.counts.pending }}</span>
              <span v-if="row.counts.cancelled"> · 取消 {{ row.counts.cancelled }}</span>
              <span v-if="row.counts.expired"> · 过期 {{ row.counts.expired }}</span>
            </small>
          </template>
        </el-table-column>
        <el-table-column label="下发时间" width="150">
          <template #default="{ row }">
            <el-tooltip :content="dateTime(row.created_at)" placement="top"><span>{{ relative(row.created_at) }}</span></el-tooltip>
          </template>
        </el-table-column>
        <el-table-column width="90" align="right">
          <template #default="{ row }">
            <el-button v-if="canCancel(row as RemoteInstruction)" link type="warning" @click.stop="cancel(row as RemoteInstruction)">取消</el-button>
          </template>
        </el-table-column>
      </el-table>
      <div v-if="hasMore" class="more">
        <el-button text @click="(offset += limit), load(false)">加载更多</el-button>
      </div>
    </div>

    <el-drawer v-model="drawer" :title="detail?.title" size="680px">
      <div v-if="detail" v-loading="detailLoading">
        <p class="drawer-sub">由 {{ detail.created_by }} 于 {{ dateTime(detail.created_at) }} 下发<template v-if="detail.cancelled_by"> · 已由 {{ detail.cancelled_by }} 取消</template></p>
        <section class="block">
          <h3>指令内容</h3>
          <pre class="prompt">{{ detail.prompt }}</pre>
        </section>
        <section class="block">
          <h3>各电脑执行情况</h3>
          <el-collapse accordion>
            <el-collapse-item v-for="r in detail.runs" :key="r.id" :name="r.id">
              <template #title>
                <div class="run-head">
                  <el-tag :type="statusMeta(r.status).type" size="small" effect="plain">{{ statusMeta(r.status).label }}</el-tag>
                  <strong>{{ r.machine_name }}</strong>
                  <small>{{ r.user_name || '—' }}</small>
                  <small class="when">{{ r.finished_at ? relative(r.finished_at) : r.started_at ? '执行中…' : '等待中' }}</small>
                </div>
              </template>
              <div class="run-body">
                <template v-if="r.answer">
                  <label>AI 助手回报</label>
                  <pre class="answer">{{ r.answer }}</pre>
                </template>
                <template v-if="r.error">
                  <label class="bad">错误</label>
                  <pre class="answer bad">{{ r.error }}</pre>
                </template>
                <p v-if="!r.answer && !r.error" class="muted">暂无回报</p>
              </div>
            </el-collapse-item>
          </el-collapse>
        </section>
      </div>
    </el-drawer>
  </div>
</template>

<style scoped>
.link {
  font-weight: 500;
  color: var(--thread);
  cursor: pointer;
  display: block;
}
.by {
  display: block;
  color: var(--ink-faint);
  font-size: 12px;
}
.counts {
  display: block;
  color: var(--ink-faint);
  font-size: 12px;
  margin-top: 2px;
}
.counts .bad {
  color: var(--red);
}
.more {
  text-align: center;
  margin-top: 10px;
}
.drawer-sub {
  color: var(--ink-faint);
  font-size: 13px;
  margin: 0 0 16px;
}
.block {
  margin-bottom: 20px;
}
.block h3 {
  font-size: 14px;
  font-weight: 600;
  margin: 0 0 10px;
}
.prompt {
  margin: 0;
  padding: 10px 12px;
  background: var(--cloth-sunk);
  border-radius: var(--r-sm);
  font-size: 13px;
  white-space: pre-wrap;
  word-break: break-word;
}
.run-head {
  display: flex;
  gap: 8px;
  align-items: center;
  width: 100%;
}
.run-head strong {
  font-weight: 500;
}
.run-head small {
  color: var(--ink-faint);
  font-size: 12px;
}
.run-head .when {
  margin-left: auto;
}
.run-body label {
  display: block;
  color: var(--ink-faint);
  font-size: 12px;
  margin-bottom: 4px;
}
.run-body label.bad {
  color: var(--red);
}
.answer {
  margin: 0 0 12px;
  padding: 8px 10px;
  background: var(--cloth-sunk);
  border-radius: var(--r-sm);
  font-size: 12.5px;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 260px;
  overflow: auto;
}
.answer.bad {
  color: var(--red);
}
</style>
