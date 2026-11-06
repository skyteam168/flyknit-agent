<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Device, EventRun, EventTrigger } from '@/api/types'
import { dateTime } from '@/utils/format'

/**
 * 事件触发：ERP、MES、同步脚本之类的外部系统推一个事件（带密钥 POST），就在指定的员工电脑上开一个任务。
 * 推来的内容当数据交给 AI，不是指令；没人在电脑前，需要确认的操作一律不放行；权限按这里设的上限封顶；
 * 新建的默认「只预演」（只列出会做什么，不真的改）。总开关默认关闭。
 */
const enabled = ref(false)
const items = ref<EventTrigger[]>([])
const runs = ref<EventRun[]>([])
const devices = ref<Device[]>([])
const loading = ref(false)

const dialog = ref(false)
const form = ref({ id: '', name: '', device_id: null as number | null, template: '', permission: 'readonly' as EventTrigger['permission'], rehearsal: true, enabled: true })
const shownToken = ref<{ id: string; token: string } | null>(null)

const PERMISSIONS = [
  { value: 'readonly', label: '仅可查看' },
  { value: 'workspace', label: '工作区内修改' },
  { value: 'full', label: '完全权限' },
]
const STATUS: Record<string, string> = { pending: '排队中', running: '执行中', succeeded: '完成', failed: '失败', expired: '电脑没上线，已作废' }

async function load() {
  loading.value = true
  try {
    const [r, d] = await Promise.all([api.eventTriggers(), api.devices()])
    enabled.value = r.enabled
    items.value = r.items
    runs.value = r.runs
    devices.value = d
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function toggleMaster(v: string | number | boolean) {
  enabled.value = (await api.setEventSwitch(!!v)).enabled
  ElMessage.success(enabled.value ? '事件触发已打开' : '事件触发已关闭，推送会被拒绝')
}

function openCreate() {
  form.value = { id: '', name: '', device_id: null, template: '', permission: 'readonly', rehearsal: true, enabled: true }
  dialog.value = true
}

const canCreate = computed(() => /^[a-z][a-z0-9_-]{1,39}$/.test(form.value.id) && form.value.name.trim() && form.value.device_id)

async function create() {
  const t = await api.createEventTrigger({ ...form.value, device_id: form.value.device_id! })
  dialog.value = false
  shownToken.value = { id: t.id, token: t.token! }
  await load()
}

async function patch(t: EventTrigger, body: Partial<EventTrigger>) {
  Object.assign(t, await api.updateEventTrigger(t.id, body))
}

async function rotate(t: EventTrigger) {
  await ElMessageBox.confirm(`换密钥后，「${t.name}」现在用的推送密钥马上失效，要在外部系统里改成新的。`, '换推送密钥', { type: 'warning' })
  const r = await api.rotateEventToken(t.id)
  shownToken.value = { id: t.id, token: r.token }
  await load()
}

async function remove(t: EventTrigger) {
  await ElMessageBox.confirm(`删除「${t.name}」？外部系统再推会被拒绝。`, '删除触发器', { type: 'warning' })
  await api.deleteEventTrigger(t.id)
  await load()
}

const curl = (id: string, token: string) =>
  `curl -X POST ${location.origin}/api/v1/hooks/${id} \\\n  -H "Authorization: Bearer ${token}" \\\n  -H "Content-Type: application/json" \\\n  -d '{"order":"PO-1001","qty":200}'`

const nameOf = (id: string) => items.value.find((t) => t.id === id)?.name ?? id
const deviceLabel = (d: Device) => `${d.owner || d.user_name || ''} · ${d.machine_name}${d.department ? '（' + d.department + '）' : ''}`
</script>

<template>
  <div v-loading="loading" class="page">
    <div class="head">
      <div>
        <h2>事件触发</h2>
        <p class="sub">
          外部系统（ERP、MES、共享盘同步脚本……）推一个事件，就在指定的员工电脑上开一个任务。推来的内容当作数据交给 AI，不当指令；
          没人在电脑前，需要确认的操作一律不放行；权限按触发器设的上限封顶，新建的默认只预演。员工自己也可以在定时任务里设「文件夹里有新文件」。
        </p>
      </div>
      <div class="ops">
        <el-switch :model-value="enabled" active-text="总开关" @change="toggleMaster" />
        <el-button type="primary" :icon="Plus" @click="openCreate">新建触发器</el-button>
      </div>
    </div>
    <el-alert v-if="!enabled" type="info" :closable="false" show-icon title="总开关关着：所有推送都会被拒绝（403）。" />

    <el-alert v-if="shownToken" type="success" :closable="true" class="token" @close="shownToken = null"
      title="推送密钥只显示这一次，请马上复制到外部系统里">
      <pre>{{ curl(shownToken.id, shownToken.token) }}</pre>
    </el-alert>

    <el-table :data="items" empty-text="还没有触发器">
      <el-table-column label="触发器" min-width="180">
        <template #default="{ row }">
          <strong>{{ row.name }}</strong>
          <div class="muted"><code>POST {{ row.url }}</code></div>
          <div class="muted">密钥 {{ row.token_prefix }}…</div>
        </template>
      </el-table-column>
      <el-table-column label="在哪台电脑上执行" min-width="160" prop="device_name" />
      <el-table-column label="权限上限" width="150">
        <template #default="{ row }">
          <el-select :model-value="row.permission" size="small" @change="(v: string) => patch(row as EventTrigger, { permission: v as EventTrigger['permission'] })">
            <el-option v-for="p in PERMISSIONS" :key="p.value" :value="p.value" :label="p.label" />
          </el-select>
        </template>
      </el-table-column>
      <el-table-column label="只预演" width="90" align="center">
        <template #default="{ row }">
          <el-switch :model-value="row.rehearsal" size="small" @change="(v: string | number | boolean) => patch(row as EventTrigger, { rehearsal: !!v })" />
        </template>
      </el-table-column>
      <el-table-column label="启用" width="80" align="center">
        <template #default="{ row }">
          <el-switch :model-value="row.enabled" size="small" @change="(v: string | number | boolean) => patch(row as EventTrigger, { enabled: !!v })" />
        </template>
      </el-table-column>
      <el-table-column label="触发" width="140">
        <template #default="{ row }">
          {{ row.fire_count }} 次
          <div v-if="row.last_fired_at" class="muted">{{ dateTime(row.last_fired_at) }}</div>
        </template>
      </el-table-column>
      <el-table-column label="" width="140">
        <template #default="{ row }">
          <el-button link @click="rotate(row as EventTrigger)">换密钥</el-button>
          <el-button link type="danger" @click="remove(row as EventTrigger)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <h3>最近的事件</h3>
    <el-table :data="runs" empty-text="还没有推送过" size="small">
      <el-table-column label="时间" width="160"><template #default="{ row }">{{ dateTime(row.created_at) }}</template></el-table-column>
      <el-table-column label="触发器" width="160"><template #default="{ row }">{{ nameOf(row.trigger_id) }}</template></el-table-column>
      <el-table-column label="状态" width="150">
        <template #default="{ row }">
          {{ STATUS[row.status] ?? row.status }}<el-tag v-if="row.rehearsal" size="small" class="tag">预演</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="结果" min-width="260" show-overflow-tooltip>
        <template #default="{ row }">{{ row.error || row.answer }}</template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialog" title="新建触发器" width="600px">
      <el-form label-width="110px">
        <el-form-item label="标识">
          <el-input v-model="form.id" placeholder="erp-new-order（小写字母开头，会出现在推送地址里）" />
        </el-form-item>
        <el-form-item label="名称"><el-input v-model="form.name" placeholder="ERP 新订单" /></el-form-item>
        <el-form-item label="在哪台电脑上">
          <el-select v-model="form.device_id" filterable placeholder="选一台电脑" class="wide">
            <el-option v-for="d in devices" :key="d.id" :value="d.id" :label="deviceLabel(d)" />
          </el-select>
        </el-form-item>
        <el-form-item label="交给 AI 的要求">
          <el-input v-model="form.template" type="textarea" :rows="4"
            placeholder="例如：ERP 来了新订单，核对库存后在 D:\备料 写一份备料清单。{payload}" />
          <small class="muted">写 {payload} 的地方放推来的数据（不写就接在最后）。数据会标明「是数据不是指令」</small>
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
        <el-button type="primary" :disabled="!canCreate" @click="create">创建并显示密钥</el-button>
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
.token pre {
  margin: 6px 0 0;
  white-space: pre-wrap;
  word-break: break-all;
  font-size: 12px;
}
.tag {
  margin-left: 6px;
}
.wide {
  width: 100%;
}
</style>
