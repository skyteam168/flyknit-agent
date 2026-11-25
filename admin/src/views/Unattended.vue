<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Department, Device, UnattendedJob, UnattendedJobIn, UnattendedKind, UnattendedRun } from '@/api/types'
import { dateTime } from '@/utils/format'

/**
 * 无人值守任务（docs/unattended-jobs.md）：服务端按时间表把任务派到一台常开的执行机（装了 FlyknitBuddy 的专用电脑），
 * 员工电脑关机也照常跑；做完的结果作为「定时报告」发到指定部门 / 电脑的消息中心。
 * 没人在场：需要确认的操作一律拒绝；权限按这里设的上限封顶（最多工作区内修改）；新建默认「只预演」。总开关默认关闭。
 */
const enabled = ref(false)
const items = ref<UnattendedJob[]>([])
const runs = ref<UnattendedRun[]>([])
const devices = ref<Device[]>([])
const departments = ref<Department[]>([])
const loading = ref(false)

const KINDS: { value: UnattendedKind; label: string }[] = [
  { value: 'daily', label: '每天' },
  { value: 'weekdays', label: '工作日' },
  { value: 'weekly', label: '每周' },
  { value: 'monthly', label: '每月' },
  { value: 'hourly', label: '每隔几小时' },
]
const WEEKDAYS = ['一', '二', '三', '四', '五', '六', '日']
const OFFSETS = [
  { value: 420, label: '越南（UTC+7）' },
  { value: 480, label: '中国（UTC+8）' },
  { value: 0, label: 'UTC' },
]
const PERMISSIONS = [
  { value: 'readonly', label: '仅可查看' },
  { value: 'workspace', label: '工作区内修改' },
]
const STATUS: Record<string, string> = { pending: '排队中', running: '执行中', succeeded: '完成', failed: '失败', expired: '执行机没上线，已作废' }

type Form = Omit<UnattendedJobIn, 'device_id'> & { device_id: number | null }
const blank = (): Form => ({
  name: '', prompt: '', device_id: null, permission: 'readonly', rehearsal: true, enabled: true,
  schedule: { kind: 'daily', time: '08:00', days: [1], day: 1, every: 4 }, utc_offset: 420,
  notify_departments: [], notify_devices: [],
})
const dialog = ref(false)
const editing = ref<number | null>(null)
const form = ref<Form>(blank())
const saving = ref(false)

async function load() {
  loading.value = true
  try {
    const [r, d, deps] = await Promise.all([api.unattended(), api.devices(), api.departments()])
    enabled.value = r.enabled
    items.value = r.items
    runs.value = r.runs
    devices.value = d
    departments.value = deps
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function toggleMaster(v: string | number | boolean) {
  enabled.value = (await api.setUnattendedSwitch(!!v)).enabled
  ElMessage.success(enabled.value ? '无人值守任务已打开，到点会自动派发' : '无人值守任务已关闭，到点不再派发')
}

function openCreate() {
  editing.value = null
  form.value = blank()
  dialog.value = true
}

function openEdit(job: UnattendedJob) {
  editing.value = job.id
  form.value = {
    name: job.name, prompt: job.prompt, device_id: job.device_id, permission: job.permission, rehearsal: job.rehearsal,
    enabled: job.enabled, schedule: { days: [1], day: 1, every: 4, ...job.schedule }, utc_offset: job.utc_offset,
    notify_departments: [...job.notify_departments], notify_devices: [...job.notify_devices],
  }
  dialog.value = true
}

const canSave = computed(() => form.value.name.trim() && form.value.prompt.trim() && form.value.device_id
  && /^([01]\d|2[0-3]):[0-5]\d$/.test(form.value.schedule.time)
  && (form.value.schedule.kind !== 'weekly' || (form.value.schedule.days?.length ?? 0) > 0))
const noRecipients = computed(() => !form.value.notify_departments.length && !form.value.notify_devices.length)

function payload(): UnattendedJobIn {
  const s = form.value.schedule
  const schedule = { kind: s.kind, time: s.time, ...(s.kind === 'weekly' ? { days: s.days } : {}),
    ...(s.kind === 'monthly' ? { day: s.day } : {}), ...(s.kind === 'hourly' ? { every: s.every } : {}) }
  return { ...form.value, device_id: form.value.device_id!, schedule }
}

async function save() {
  saving.value = true
  try {
    if (editing.value === null) {
      await api.createUnattended(payload())
      ElMessage.success('已创建，到点会自动派发（总开关打开时）')
    } else {
      await api.updateUnattended(editing.value, payload())
      ElMessage.success('已保存')
    }
    dialog.value = false
    await load()
  } finally {
    saving.value = false
  }
}

async function patch(job: UnattendedJob, body: Partial<UnattendedJobIn>) {
  Object.assign(job, await api.updateUnattended(job.id, body))
}

async function runNow(job: UnattendedJob) {
  await api.runUnattended(job.id)
  ElMessage.success(`已派给执行机「${job.device_name}」，做完结果会出现在下面的执行记录里`)
  await load()
}

async function remove(job: UnattendedJob) {
  await ElMessageBox.confirm(`删除「${job.name}」？以后不再执行（已经发出的报告不受影响）。`, '删除任务', { type: 'warning' })
  await api.deleteUnattended(job.id)
  await load()
}

const deviceLabel = (d: Device) => `${d.owner || d.user_name || ''} · ${d.machine_name}${d.department ? '（' + d.department + '）' : ''}`
const recipients = (job: UnattendedJob) => {
  const names = [...job.notify_departments, ...job.notify_devices.map((id) => devices.value.find((d) => d.id === id)?.owner || `电脑 ${id}`)]
  return names.length ? names.join('、') : '不发（只在这里看）'
}
</script>

<template>
  <div v-loading="loading" class="page">
    <div class="head">
      <div>
        <h2>无人值守任务</h2>
        <p class="sub">
          按时间表把任务派到一台常开的<b>执行机</b>（装了 FlyknitBuddy、自动登录的专用电脑或虚拟机），员工电脑关机也照常跑；
          做完的结果作为「定时报告」发到指定部门 / 电脑的消息中心。执行机前没有人：需要确认的操作一律拒绝，权限按这里设的上限封顶，
          新建默认只预演。上一次还没做完时这一次自动跳过。
        </p>
      </div>
      <div class="ops">
        <el-switch :model-value="enabled" active-text="总开关" @change="toggleMaster" />
        <el-button type="primary" :icon="Plus" @click="openCreate">新建任务</el-button>
      </div>
    </div>
    <el-alert v-if="!enabled" type="info" :closable="false" show-icon title="总开关关着：到点不会派发（时间会自动顺延，打开后不会一下子补跑一堆）。" />

    <el-table :data="items" empty-text="还没有任务">
      <el-table-column label="任务" min-width="200">
        <template #default="{ row }">
          <strong>{{ row.name }}</strong>
          <div class="muted">{{ row.schedule_text }}</div>
          <div v-if="row.skipped_at" class="muted warn">{{ dateTime(row.skipped_at) }} 那次因为上一次还没做完，跳过了</div>
        </template>
      </el-table-column>
      <el-table-column label="执行机" min-width="150">
        <template #default="{ row }">
          {{ row.device_name || '（已删除）' }}
          <el-tag :type="row.device_online ? 'success' : 'info'" size="small" class="tag">{{ row.device_online ? '在线' : '不在线' }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="结果发给" min-width="150" show-overflow-tooltip>
        <template #default="{ row }">{{ recipients(row as UnattendedJob) }}</template>
      </el-table-column>
      <el-table-column label="下一次 / 上一次" width="170">
        <template #default="{ row }">
          <div>{{ row.enabled && row.next_run_at ? dateTime(row.next_run_at) : '—' }}</div>
          <div class="muted">{{ row.last_run_at ? dateTime(row.last_run_at) : '还没跑过' }}（{{ row.run_count }} 次）</div>
        </template>
      </el-table-column>
      <el-table-column label="只预演" width="80" align="center">
        <template #default="{ row }">
          <el-switch :model-value="row.rehearsal" size="small" @change="(v: string | number | boolean) => patch(row as UnattendedJob, { rehearsal: !!v })" />
        </template>
      </el-table-column>
      <el-table-column label="启用" width="70" align="center">
        <template #default="{ row }">
          <el-switch :model-value="row.enabled" size="small" @change="(v: string | number | boolean) => patch(row as UnattendedJob, { enabled: !!v })" />
        </template>
      </el-table-column>
      <el-table-column label="" width="180">
        <template #default="{ row }">
          <el-button link type="primary" :disabled="!enabled" @click="runNow(row as UnattendedJob)">立即运行</el-button>
          <el-button link @click="openEdit(row as UnattendedJob)">编辑</el-button>
          <el-button link type="danger" @click="remove(row as UnattendedJob)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <h3>最近的执行记录</h3>
    <el-table :data="runs" empty-text="还没有执行过" size="small">
      <el-table-column label="时间" width="160"><template #default="{ row }">{{ dateTime(row.created_at) }}</template></el-table-column>
      <el-table-column label="任务" width="160" prop="job_name" />
      <el-table-column label="状态" width="170">
        <template #default="{ row }">
          {{ STATUS[row.status] ?? row.status }}<el-tag v-if="row.rehearsal" size="small" class="tag">预演</el-tag>
          <el-tag v-if="row.delivered" size="small" type="success" class="tag">已发报告</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="结果" min-width="260" show-overflow-tooltip>
        <template #default="{ row }">{{ row.error || row.answer }}</template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialog" :title="editing === null ? '新建无人值守任务' : '编辑无人值守任务'" width="680px">
      <el-form label-width="110px">
        <el-form-item label="名称"><el-input v-model="form.name" maxlength="100" placeholder="上周生产周报" /></el-form-item>
        <el-form-item label="交给 AI 的要求">
          <el-input v-model="form.prompt" type="textarea" :rows="5" maxlength="4000" show-word-limit
            placeholder="例如：用中台连接器查上周各车间的产量、计划达成率和不良率，和前一周对比，用表格列出，最后写三条需要关注的地方。" />
        </el-form-item>
        <el-form-item label="时间">
          <div class="when">
            <el-select v-model="form.schedule.kind" style="width: 130px">
              <el-option v-for="k in KINDS" :key="k.value" :value="k.value" :label="k.label" />
            </el-select>
            <el-checkbox-group v-if="form.schedule.kind === 'weekly'" v-model="form.schedule.days">
              <el-checkbox-button v-for="(w, i) in WEEKDAYS" :key="i" :value="i + 1">周{{ w }}</el-checkbox-button>
            </el-checkbox-group>
            <span v-if="form.schedule.kind === 'monthly'">
              <el-input-number v-model="form.schedule.day" :min="1" :max="28" size="default" style="width: 110px" /> 号
            </span>
            <el-time-select v-model="form.schedule.time" start="00:00" step="00:30" end="23:30" style="width: 120px" :clearable="false" />
            <span v-if="form.schedule.kind === 'hourly'">
              起，每 <el-input-number v-model="form.schedule.every" :min="1" :max="12" style="width: 100px" /> 小时
            </span>
            <el-select v-model="form.utc_offset" style="width: 150px">
              <el-option v-for="o in OFFSETS" :key="o.value" :value="o.value" :label="o.label" />
            </el-select>
          </div>
        </el-form-item>
        <el-form-item label="执行机">
          <el-select v-model="form.device_id" filterable placeholder="选一台常开的专用电脑" class="wide">
            <el-option v-for="d in devices" :key="d.id" :value="d.id" :label="deviceLabel(d)" />
          </el-select>
          <small class="muted">建议用专门的电脑或虚拟机，不要选员工自己在用的电脑。</small>
        </el-form-item>
        <el-form-item label="结果发给">
          <el-select v-model="form.notify_departments" multiple filterable placeholder="部门" class="wide">
            <el-option v-for="d in departments" :key="d.id" :value="d.name" :label="d.name" />
          </el-select>
          <el-select v-model="form.notify_devices" multiple filterable placeholder="个别电脑" class="wide gap">
            <el-option v-for="d in devices" :key="d.id" :value="d.id" :label="deviceLabel(d)" />
          </el-select>
          <small v-if="noRecipients" class="muted warn">没选收件人：结果只在这页的执行记录里看，不发消息中心。</small>
        </el-form-item>
        <el-form-item label="权限上限">
          <el-radio-group v-model="form.permission">
            <el-radio-button v-for="p in PERMISSIONS" :key="p.value" :value="p.value">{{ p.label }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="只预演">
          <el-switch v-model="form.rehearsal" />
          <small class="muted">先跑几次只列出会做什么、不真的改；看过结果没问题再关掉</small>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialog = false">取消</el-button>
        <el-button type="primary" :loading="saving" :disabled="!canSave" @click="save">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}
.head h2 {
  margin: 0 0 4px;
}
.ops {
  display: flex;
  align-items: center;
  gap: 16px;
  flex-shrink: 0;
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
.tag {
  margin-left: 6px;
}
.wide {
  width: 100%;
}
.gap {
  margin-top: 8px;
}
.when {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}
</style>
