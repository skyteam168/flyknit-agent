<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Promotion, Refresh, Search, MagicStick } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { AgentRun, Device, MachineAgent, MachineAgentDetail } from '@/api/types'
import { auth } from '@/store/auth'
import { bytes, dateTime, isOnline, relative, short } from '@/utils/format'
import { agentOnline, kindLabel, statusMeta } from '@/utils/tasks'
import DispatchDialog from '@/components/DispatchDialog.vue'
import InstructionDialog from '@/components/InstructionDialog.vue'

type Row = Device & { agent: MachineAgent | null }

const devices = ref<Device[]>([])
const agents = ref<MachineAgent[]>([])
const todayTokens = ref(new Map<number, number>())
const loading = ref(false)
const keyword = ref('')
const state = ref<'all' | 'online' | 'offline' | 'disabled'>('all')

const canDispatch = computed(() => !!auth.user?.can_dispatch && !auth.user?.must_change_password)

async function load() {
  loading.value = true
  try {
    const [list, usage, agentList] = await Promise.all([api.devices(), api.usage(1), api.agents().catch(() => [])])
    devices.value = list
    agents.value = agentList
    todayTokens.value = new Map(usage.filter((u) => u.device_id != null).map((u) => [u.device_id!, u.today_tokens]))
  } finally {
    loading.value = false
  }
}
onMounted(load)

// 按 machine_guid 把运维代理挂到对应设备上
const agentByGuid = computed(() => {
  const map = new Map<string, MachineAgent>()
  for (const a of agents.value) if (a.machine_guid) map.set(a.machine_guid, a)
  return map
})

const rows = computed<Row[]>(() =>
  devices.value.map((d) => ({ ...d, agent: d.machine_guid ? agentByGuid.value.get(d.machine_guid) ?? null : null })),
)

const counts = computed(() => ({
  all: rows.value.length,
  online: rows.value.filter((d) => !d.disabled && isOnline(d.last_seen)).length,
  offline: rows.value.filter((d) => !d.disabled && !isOnline(d.last_seen)).length,
  disabled: rows.value.filter((d) => d.disabled).length,
}))

const filtered = computed(() => {
  const q = keyword.value.trim().toLowerCase()
  return rows.value.filter((d) => {
    if (state.value === 'online' && (d.disabled || !isOnline(d.last_seen))) return false
    if (state.value === 'offline' && (d.disabled || isOnline(d.last_seen))) return false
    if (state.value === 'disabled' && !d.disabled) return false
    if (!q) return true
    return [d.machine_name, d.user_name, d.owner, d.department, d.domain, d.ip_addresses, d.observed_ip, d.mac_address, d.client_version]
      .join(' ')
      .toLowerCase()
      .includes(q)
  })
})

async function toggle(d: Row) {
  const next = !d.disabled
  if (next) {
    await ElMessageBox.confirm(
      `停用「${d.machine_name}」后，这台电脑上的员工端将无法再调用模型，直到重新启用。`,
      '停用设备',
      { type: 'warning', confirmButtonText: '停用' },
    )
  }
  const updated = await api.setDeviceDisabled(d.id, next)
  d.disabled = updated.disabled
  ElMessage.success(next ? '已停用' : '已启用')
}

// ---------- 编辑台账（使用者、部门、备注） ----------
const editOpen = ref(false)
const editing = ref(false)
const editForm = reactive({ id: 0, machine_name: '', owner: '', department: '', note: '' })

function openEdit(d: Row) {
  Object.assign(editForm, {
    id: d.id,
    machine_name: d.machine_name,
    owner: d.owner || '',
    department: d.department || '',
    note: d.note || '',
  })
  editOpen.value = true
}

async function saveEdit() {
  editing.value = true
  try {
    const updated = await api.updateDevice(editForm.id, {
      owner: editForm.owner,
      department: editForm.department,
      note: editForm.note,
    })
    const row = devices.value.find((x) => x.id === editForm.id)
    if (row) Object.assign(row, { owner: updated.owner, department: updated.department, note: updated.note })
    if (detailDevice.value?.id === editForm.id) {
      Object.assign(detailDevice.value, { owner: updated.owner, department: updated.department, note: updated.note })
    }
    editOpen.value = false
    ElMessage.success('已保存')
  } finally {
    editing.value = false
  }
}

const ips = (d: Device) => d.ip_addresses.split(/[,\s]+/).filter(Boolean)

function agentState(d: Row): { label: string; cls: string } {
  if (!d.agent) return { label: '未安装', cls: 'none' }
  if (d.agent.disabled) return { label: '已停用', cls: 'off' }
  return agentOnline(d.agent.last_seen) ? { label: '在线', cls: 'on' } : { label: '离线', cls: 'idle' }
}

// ---------- 多选下发 ----------
const selected = ref<Row[]>([])
const selectableAgents = computed(() => selected.value.filter((r) => r.agent && !r.agent.disabled))
const dispatchOpen = ref(false)

function openDispatch() {
  if (!selectableAgents.value.length) {
    ElMessage.warning('请先勾选已安装运维代理且未停用的电脑')
    return
  }
  dispatchOpen.value = true
}
const dispatchIds = computed(() => selectableAgents.value.map((r) => r.agent!.id))
const dispatchNames = computed(() => selectableAgents.value.map((r) => r.machine_name))

// ---------- 多选下发指令（给员工端 AI 助手，无需安装运维代理） ----------
const selectableDevices = computed(() => selected.value.filter((r) => !r.disabled))
const instructionOpen = ref(false)
function openInstruction() {
  if (!selectableDevices.value.length) {
    ElMessage.warning('请先勾选未停用的电脑')
    return
  }
  instructionOpen.value = true
}
const instructionIds = computed(() => selectableDevices.value.map((r) => r.id))
const instructionNames = computed(() => selectableDevices.value.map((r) => r.machine_name))

// ---------- 详情抽屉 ----------
const drawer = ref(false)
const detail = ref<MachineAgentDetail | null>(null)
const detailRuns = ref<AgentRun[]>([])
const detailLoading = ref(false)
const detailDevice = ref<Row | null>(null)

async function openDetail(d: Row) {
  detailDevice.value = d
  drawer.value = true
  if (!d.agent) {
    detail.value = null
    detailRuns.value = []
    return
  }
  detailLoading.value = true
  try {
    const [info, runs] = await Promise.all([api.agent(d.agent.id), api.agentRuns(d.agent.id)])
    detail.value = info
    detailRuns.value = runs
  } finally {
    detailLoading.value = false
  }
}

async function collectNow() {
  if (!detail.value) return
  await api.createJob({ kind: 'collect_info', agent_ids: [detail.value.id] })
  ElMessage.success('已下发采集任务，稍后刷新查看')
  detailRuns.value = await api.agentRuns(detail.value.id)
}

async function toggleAgent(disabled: boolean) {
  if (!detail.value) return
  if (disabled) {
    await ElMessageBox.confirm('停用后这台电脑的运维代理将不再领取任何任务，待执行的任务也会取消。', '停用运维代理', {
      type: 'warning',
      confirmButtonText: '停用',
    })
  }
  const updated = await api.setAgentDisabled(detail.value.id, disabled)
  detail.value.disabled = updated.disabled
  const inList = agents.value.find((a) => a.id === updated.id)
  if (inList) inList.disabled = updated.disabled
  ElMessage.success(disabled ? '已停用' : '已启用')
}

// 台账里挑几项关键信息平铺展示
const inv = computed(() => (detail.value?.inventory ?? {}) as Record<string, any>)
const software = computed<any[]>(() => (Array.isArray(inv.value.software) ? inv.value.software : []))
const softwareFilter = ref('')
const filteredSoftware = computed(() => {
  const q = softwareFilter.value.trim().toLowerCase()
  if (!q) return software.value
  return software.value.filter((s) => `${s.name} ${s.publisher}`.toLowerCase().includes(q))
})

function summaryText(): string {
  const o = inv.value
  const parts: string[] = []
  if (o.cpu?.name) parts.push(o.cpu.name)
  if (o.memory?.total_gb) parts.push(`${o.memory.total_gb} GB 内存`)
  if (Array.isArray(o.disks)) {
    const total = o.disks.filter((d: any) => d.drive).reduce((s: number, d: any) => s + (d.size_gb || 0), 0)
    if (total) parts.push(`${Math.round(total)} GB 磁盘`)
  }
  return parts.join(' · ')
}
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>设备列表</h1>
        <p>装了员工端并注册过的电脑。最近 15 分钟内连过服务端的算在线；装了运维代理的可下发任务。</p>
      </div>
      <div class="toolbar">
        <el-input v-model="keyword" :prefix-icon="Search" placeholder="电脑名、用户、IP、MAC…" clearable style="width: 240px" />
        <el-button v-if="canDispatch" type="primary" :icon="Promotion" :disabled="!selectableAgents.length" @click="openDispatch">
          下发任务<template v-if="selectableAgents.length"> ({{ selectableAgents.length }})</template>
        </el-button>
        <el-button v-if="canDispatch" type="primary" :icon="MagicStick" plain :disabled="!selectableDevices.length" @click="openInstruction">
          下发指令<template v-if="selectableDevices.length"> ({{ selectableDevices.length }})</template>
        </el-button>
        <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      </div>
    </div>

    <div class="panel">
      <el-radio-group v-model="state" class="filter">
        <el-radio-button value="all">全部 {{ counts.all }}</el-radio-button>
        <el-radio-button value="online">在线 {{ counts.online }}</el-radio-button>
        <el-radio-button value="offline">离线 {{ counts.offline }}</el-radio-button>
        <el-radio-button value="disabled">已停用 {{ counts.disabled }}</el-radio-button>
      </el-radio-group>

      <el-table
        :data="filtered"
        v-loading="loading"
        empty-text="没有符合条件的设备"
        row-key="id"
        @selection-change="(v: Row[]) => (selected = v)"
      >
        <el-table-column v-if="canDispatch" type="selection" width="44" :selectable="(row: Row) => !row.disabled" />
        <el-table-column label="电脑" min-width="190" prop="machine_name" sortable>
          <template #default="{ row }">
            <div class="who">
              <span class="dot" :class="row.disabled ? 'off' : isOnline(row.last_seen) ? 'on' : ''" />
              <div>
                <strong>{{ row.machine_name }}</strong>
                <el-tooltip v-if="row.signed_out_at" :content="`员工于 ${dateTime(row.signed_out_at)} 退出登录，这条记录不会再上线`" placement="top">
                  <el-tag size="small" type="info" class="signed-out">已退出登录</el-tag>
                </el-tooltip>
                <small>{{ row.owner || row.user_name || '—' }}<template v-if="row.department"> · {{ row.department }}</template><template v-if="row.domain"> @ {{ row.domain }}</template></small>
              </div>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="运维代理" width="110">
          <template #default="{ row }">
            <span class="agent-tag" :class="agentState(row as Row).cls">{{ agentState(row as Row).label }}</span>
          </template>
        </el-table-column>
        <el-table-column label="IP 地址" min-width="150">
          <template #default="{ row }">
            <div class="ips">
              <code v-for="ip in ips(row as Device)" :key="ip">{{ ip }}</code>
              <span v-if="!ips(row as Device).length" class="muted">—</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="版本" width="90" prop="client_version" sortable>
          <template #default="{ row }">{{ row.client_version ? `v${row.client_version}` : '—' }}</template>
        </el-table-column>
        <el-table-column label="最后在线" width="120" prop="last_seen" sortable>
          <template #default="{ row }">
            <el-tooltip :content="dateTime(row.last_seen)" placement="top">
              <span>{{ relative(row.last_seen) }}</span>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column label="今日 Token" width="100" align="right">
          <template #default="{ row }">{{ short(todayTokens.get(row.id) ?? 0) }}</template>
        </el-table-column>
        <el-table-column width="200" align="right" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openEdit(row as Row)">编辑</el-button>
            <el-button link type="primary" @click="openDetail(row as Row)">详情</el-button>
            <el-switch :model-value="!row.disabled" style="margin-left: 8px" @update:model-value="toggle(row as Row)" />
          </template>
        </el-table-column>
      </el-table>
    </div>

    <DispatchDialog v-model="dispatchOpen" :agent-ids="dispatchIds" :agent-names="dispatchNames" @dispatched="load" />
    <InstructionDialog v-model="instructionOpen" :device-ids="instructionIds" :device-names="instructionNames" @dispatched="load" />

    <el-dialog v-model="editOpen" :title="`编辑台账 · ${editForm.machine_name}`" width="440px" :close-on-click-modal="false">
      <el-form label-position="top">
        <el-form-item label="使用者">
          <el-input v-model="editForm.owner" maxlength="50" placeholder="这台电脑的实际使用人" />
        </el-form-item>
        <el-form-item label="部门">
          <el-input v-model="editForm.department" maxlength="50" placeholder="所属部门" />
        </el-form-item>
        <el-form-item label="备注">
          <el-input v-model="editForm.note" type="textarea" :rows="3" maxlength="200" show-word-limit placeholder="位置、用途等（可选）" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="editOpen = false">取消</el-button>
        <el-button type="primary" :loading="editing" @click="saveEdit">保存</el-button>
      </template>
    </el-dialog>

    <el-drawer v-model="drawer" :title="detailDevice?.machine_name" size="620px">
      <div v-if="detailDevice" v-loading="detailLoading" class="detail">
        <div class="meta">
          <div><label>使用者</label><span>{{ detailDevice.owner || '—' }}</span></div>
          <div><label>部门</label><span>{{ detailDevice.department || '—' }}</span></div>
          <div><label>Windows 账号</label><span>{{ detailDevice.user_name || '—' }}</span></div>
          <div><label>系统</label><span>{{ detailDevice.os_version || '—' }}</span></div>
          <div><label>MAC</label><code>{{ detailDevice.mac_address || '—' }}</code></div>
          <div><label>设备标识</label><code class="muted">{{ detailDevice.machine_guid || '—' }}</code></div>
          <div v-if="detailDevice.note" class="note"><label>备注</label><span>{{ detailDevice.note }}</span></div>
        </div>

        <template v-if="detailDevice.agent">
          <div class="agent-bar">
            <div>
              <span class="agent-tag" :class="agentState(detailDevice).cls">代理{{ agentState(detailDevice).label }}</span>
              <small>v{{ detail?.agent_version || detailDevice.agent.agent_version || '—' }} · 最后心跳 {{ relative(detailDevice.agent.last_seen) }}</small>
            </div>
            <div v-if="canDispatch" class="agent-actions">
              <el-button size="small" @click="collectNow">立即采集</el-button>
              <el-button
                size="small"
                :type="detail?.disabled ? 'success' : 'danger'"
                plain
                @click="toggleAgent(!detail?.disabled)"
              >{{ detail?.disabled ? '启用代理' : '停用代理' }}</el-button>
            </div>
          </div>

          <section class="block">
            <h3>硬件概况 <small v-if="detail?.inventory_at">采集于 {{ dateTime(detail.inventory_at) }}</small></h3>
            <p v-if="summaryText()" class="summary">{{ summaryText() }}</p>
            <el-empty v-else :image-size="60" description="还没有采集过信息，点「立即采集」" />
          </section>

          <section v-if="Array.isArray(inv.disks) && inv.disks.length" class="block">
            <h3>磁盘</h3>
            <div v-for="(d, i) in inv.disks.filter((x: any) => x.drive)" :key="i" class="disk">
              <span>{{ d.drive }} {{ d.label || '' }}</span>
              <el-progress
                :percentage="d.size_gb ? Math.round(((d.size_gb - (d.free_gb || 0)) / d.size_gb) * 100) : 0"
                :stroke-width="12"
              />
              <small>剩余 {{ d.free_gb }} / {{ d.size_gb }} GB</small>
            </div>
          </section>

          <section v-if="software.length" class="block">
            <h3>已装软件 <small>{{ software.length }} 项</small></h3>
            <el-input v-model="softwareFilter" :prefix-icon="Search" placeholder="筛选软件" size="small" clearable style="margin-bottom: 8px" />
            <el-table :data="filteredSoftware" size="small" max-height="240">
              <el-table-column label="名称" prop="name" show-overflow-tooltip />
              <el-table-column label="版本" prop="version" width="120" />
              <el-table-column label="发行商" prop="publisher" width="140" show-overflow-tooltip />
            </el-table>
          </section>

          <section class="block">
            <h3>最近任务</h3>
            <el-empty v-if="!detailRuns.length" :image-size="60" description="还没有执行过任务" />
            <el-timeline v-else>
              <el-timeline-item
                v-for="r in detailRuns"
                :key="r.id"
                :type="statusMeta(r.status).type === 'danger' ? 'danger' : statusMeta(r.status).type === 'success' ? 'success' : 'primary'"
                :timestamp="dateTime(r.created_at)"
              >
                <strong>{{ r.title || kindLabel(r.kind) }}</strong>
                <el-tag :type="statusMeta(r.status).type" size="small" effect="plain" style="margin-left: 6px">
                  {{ statusMeta(r.status).label }}
                </el-tag>
                <pre v-if="r.output" class="run-output">{{ r.output }}</pre>
              </el-timeline-item>
            </el-timeline>
          </section>
        </template>
        <el-empty v-else description="这台电脑还没有安装运维代理，无法下发任务" />
      </div>
    </el-drawer>
  </div>
</template>

<style scoped>
.signed-out {
  margin-left: 6px;
  vertical-align: 1px;
}
.filter {
  margin-bottom: 14px;
}
.who {
  display: flex;
  gap: 10px;
  align-items: center;
}
.who > div {
  display: flex;
  flex-direction: column;
  min-width: 0;
  line-height: 1.35;
}
.who strong {
  font-weight: 500;
}
.who small {
  color: var(--ink-faint);
  font-size: 12px;
}
.dot {
  flex: none;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--line-strong);
}
.dot.on {
  background: var(--thread);
  box-shadow: 0 0 0 3px var(--thread-wash);
}
.dot.off {
  background: var(--red);
}
.ips {
  display: flex;
  flex-direction: column;
  line-height: 1.4;
}
.agent-tag {
  font-size: 12px;
  padding: 2px 8px;
  border-radius: 10px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
}
.agent-tag.on {
  background: var(--thread-wash);
  color: var(--thread);
}
.agent-tag.idle {
  background: var(--cloth-sunk);
  color: var(--ink);
}
.agent-tag.off {
  background: #fde7e7;
  color: var(--red);
}
.agent-tag.none {
  opacity: 0.6;
}
.detail {
  display: flex;
  flex-direction: column;
  gap: 18px;
}
.meta {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px 20px;
}
.meta > div {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.meta .note {
  grid-column: 1 / -1;
}
.meta label {
  color: var(--ink-faint);
  font-size: 12px;
}
.agent-bar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 14px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
}
.agent-bar small {
  color: var(--ink-faint);
  font-size: 12px;
  margin-left: 8px;
}
.block h3 {
  font-size: 14px;
  font-weight: 600;
  margin: 0 0 10px;
}
.block h3 small {
  color: var(--ink-faint);
  font-weight: 400;
  margin-left: 6px;
}
.summary {
  margin: 0;
  color: var(--ink);
}
.disk {
  margin-bottom: 10px;
}
.disk span {
  font-size: 13px;
}
.disk small {
  color: var(--ink-faint);
  font-size: 12px;
}
.run-output {
  margin: 6px 0 0;
  padding: 8px 10px;
  background: var(--cloth-sunk);
  border-radius: var(--r-sm);
  font-size: 12px;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 120px;
  overflow: auto;
}
</style>
