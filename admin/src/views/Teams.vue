<script setup lang="ts">
/**
 * 团队（docs/team-mode.md）：2～8 位专家组成，员工端主助手当队长。
 * - IT 在这里建、改、下发团队（使用范围：部门 + 单台电脑，都不选 = 全公司）；
 * - 各部门 / 个人在员工端建的团队在这里只能查看、解散、恢复（内容由建的人改）。
 * 团队只能在专家原有配置上收紧（模型、禁用工具、只读），安全规则和确认照旧。
 */
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Department, Device, Expert, ModelConfig, Team, TeamIn, TeamToolKind } from '@/api/types'

const list = ref<Team[]>([])
const experts = ref<Expert[]>([])
const models = ref<ModelConfig[]>([])
const departments = ref<Department[]>([])
const devices = ref<Device[]>([])
const loading = ref(false)
const tab = ref<'all' | 'it' | 'department' | 'personal' | 'dissolved'>('all')

const TOOL_LABEL: Record<TeamToolKind, string> = { shell: '执行命令', write: '写文件', browser: '浏览器', mcp: '连接器', developer: '开发者工具' }
const SOURCE_LABEL: Record<Team['source'], string> = { it: 'IT 下发', department: '部门', personal: '个人' }
const ICONS = [
  ['team', '团队'], ['truck', '交付'], ['chart', '报表'], ['search', '追溯'], ['clip', '清单'], ['gear', '设备'],
] as const

async function load() {
  loading.value = true
  try {
    const [t, e, m, d, v] = await Promise.all([api.teams(), api.experts(), api.models(), api.departments(), api.devices()])
    list.value = t
    experts.value = e
    models.value = m
    departments.value = d
    devices.value = v
  } finally {
    loading.value = false
  }
}
onMounted(load)

const expertName = (id: string) => experts.value.find((e) => e.id === id)?.name ?? id
const published = computed(() => experts.value.filter((e) => e.status === 'published'))
const toolModels = computed(() => models.value.filter((m) => m.enabled && m.supports_tools))

const shown = computed(() =>
  list.value.filter((t) =>
    tab.value === 'dissolved' ? !!t.dissolved_at : !t.dissolved_at && (tab.value === 'all' || t.source === tab.value),
  ),
)
const counts = computed(() => ({
  all: list.value.filter((t) => !t.dissolved_at).length,
  it: list.value.filter((t) => !t.dissolved_at && t.source === 'it').length,
  department: list.value.filter((t) => !t.dissolved_at && t.source === 'department').length,
  personal: list.value.filter((t) => !t.dissolved_at && t.source === 'personal').length,
  dissolved: list.value.filter((t) => !!t.dissolved_at).length,
}))

function scopeText(t: Team) {
  if (t.source === 'department') return `${t.department}（部门共享）`
  if (t.source === 'personal') return `只有 ${t.owner_name || '建的人'}`
  const parts = [...t.scope_departments]
  if (t.scope_devices.length) parts.push(`${t.scope_devices.length} 台电脑`)
  return parts.length ? parts.join('、') : '全公司'
}

// ---------- 编辑 ----------
const editing = ref(false)
const saving = ref(false)
const editId = ref<string | null>(null)
const form = reactive<TeamIn>({
  name: '', description: '', icon: 'team', leader_note: '', members: [], model_id: null,
  disabled_tools: [], read_only: false, scope_departments: [], scope_devices: [],
})
const memberIds = computed({
  get: () => form.members.map((m) => m.expert_id),
  set: (ids: string[]) => {
    const notes = Object.fromEntries(form.members.map((m) => [m.expert_id, m.note]))
    form.members = ids.map((id) => ({ expert_id: id, note: notes[id] ?? '' }))
  },
})

function openNew() {
  editId.value = null
  Object.assign(form, {
    name: '', description: '', icon: 'team', leader_note: '', members: [], model_id: null,
    disabled_tools: [], read_only: false, scope_departments: [], scope_devices: [],
  })
  editing.value = true
}
function openEdit(t: Team) {
  editId.value = t.id
  Object.assign(form, {
    name: t.name, description: t.description, icon: t.icon, leader_note: t.leader_note,
    members: t.members.map((m) => ({ expert_id: m.expert_id, note: m.note })), model_id: t.model_id,
    disabled_tools: [...t.disabled_tools], read_only: t.read_only,
    scope_departments: [...t.scope_departments], scope_devices: [...t.scope_devices],
  })
  editing.value = true
}

async function save() {
  if (!form.name.trim()) return ElMessage.warning('给团队起个名字')
  if (form.members.length < 2 || form.members.length > 8) return ElMessage.warning('团队要 2～8 位专家')
  saving.value = true
  try {
    if (editId.value) await api.updateTeam(editId.value, { ...form })
    else await api.createTeam({ ...form })
    ElMessage.success(form.scope_departments.length || form.scope_devices.length ? '已保存，范围内的电脑几秒内看到' : '已保存：全公司可用')
    editing.value = false
    await load()
  } finally {
    saving.value = false
  }
}

async function dissolve(t: Team) {
  await ElMessageBox.confirm(
    `解散后员工端看不到「${t.name}」；正在进行的团队任务会照常做完，对话和审计都保留，可以在「已解散」里恢复。`,
    '解散团队', { type: 'warning', confirmButtonText: '解散' },
  )
  await api.dissolveTeam(t.id)
  ElMessage.success('已解散')
  await load()
}
async function restore(t: Team) {
  await api.restoreTeam(t.id)
  ElMessage.success('已恢复')
  await load()
}

function deviceLabel(d: Device) {
  const who = d.owner || d.user_name
  return `${d.machine_name}${who ? ' · ' + who : ''}${d.department ? ' · ' + d.department : ''}`
}
</script>

<template>
  <div class="page">
    <div class="head">
      <div>
        <h2>团队</h2>
        <p class="sub">
          把 2～8 位专家组成团队，员工端主助手当队长：列分工计划，员工确认后成员分头做，最后汇总报告。
          需要在「安全中心」打开「团队模式」。
        </p>
      </div>
      <div class="actions">
        <el-button :icon="Refresh" @click="load">刷新</el-button>
        <el-button type="primary" :icon="Plus" @click="openNew">新建并下发团队</el-button>
      </div>
    </div>

    <el-radio-group v-model="tab" class="tabs">
      <el-radio-button value="all">全部（{{ counts.all }}）</el-radio-button>
      <el-radio-button value="it">IT 下发（{{ counts.it }}）</el-radio-button>
      <el-radio-button value="department">部门（{{ counts.department }}）</el-radio-button>
      <el-radio-button value="personal">个人（{{ counts.personal }}）</el-radio-button>
      <el-radio-button value="dissolved">已解散（{{ counts.dissolved }}）</el-radio-button>
    </el-radio-group>

    <el-table v-loading="loading" :data="shown" empty-text="还没有团队">
      <el-table-column label="团队" min-width="200">
        <template #default="{ row }">
          <div class="name">{{ row.name }}</div>
          <div class="muted">{{ row.description }}</div>
        </template>
      </el-table-column>
      <el-table-column label="来源" width="150">
        <template #default="{ row }">
          <el-tag size="small" :type="row.source === 'it' ? 'primary' : row.source === 'department' ? 'success' : 'info'">
            {{ SOURCE_LABEL[row.source as Team['source']] }}
          </el-tag>
          <div v-if="row.source !== 'it'" class="muted">{{ row.owner_name }}</div>
        </template>
      </el-table-column>
      <el-table-column label="使用范围" min-width="160">
        <template #default="{ row }">{{ scopeText(row as Team) }}</template>
      </el-table-column>
      <el-table-column label="成员" min-width="240">
        <template #default="{ row }">
          <el-tag
            v-for="m in row.members"
            :key="m.expert_id"
            size="small"
            :type="m.available === false ? 'danger' : 'info'"
            class="member"
            :title="m.available === false ? '这位专家已下线' : m.note"
          >
            {{ expertName(m.expert_id) }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="限制" min-width="150">
        <template #default="{ row }">
          <span v-if="row.read_only" class="muted">只读团队</span>
          <span v-else-if="row.disabled_tools.length" class="muted">不用：{{ row.disabled_tools.map((t: TeamToolKind) => TOOL_LABEL[t]).join('、') }}</span>
          <span v-else class="muted">沿用专家配置</span>
        </template>
      </el-table-column>
      <el-table-column label="用过" width="80" align="right">
        <template #default="{ row }">{{ row.runs }} 次</template>
      </el-table-column>
      <el-table-column label="" width="150" align="right">
        <template #default="{ row }">
          <template v-if="row.dissolved_at">
            <el-button size="small" @click="restore(row as Team)">恢复</el-button>
          </template>
          <template v-else>
            <el-button v-if="row.source === 'it'" size="small" @click="openEdit(row as Team)">编辑</el-button>
            <el-button size="small" type="danger" plain @click="dissolve(row as Team)">解散</el-button>
          </template>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="editing" :title="editId ? '编辑团队' : '新建并下发团队'" width="720px">
      <el-form label-width="96px">
        <el-form-item label="名称" required><el-input v-model="form.name" maxlength="30" show-word-limit placeholder="例如：月报小组" /></el-form-item>
        <el-form-item label="简介"><el-input v-model="form.description" maxlength="300" placeholder="这个团队擅长做什么" /></el-form-item>
        <el-form-item label="图标">
          <el-radio-group v-model="form.icon">
            <el-radio-button v-for="[k, l] in ICONS" :key="k" :value="k">{{ l }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="成员" required>
          <el-select v-model="memberIds" multiple filterable :multiple-limit="8" placeholder="选 2～8 位已发布的专家" style="width: 100%">
            <el-option v-for="e in published" :key="e.id" :label="e.name" :value="e.id" />
          </el-select>
          <div v-for="m in form.members" :key="m.expert_id" class="note-row">
            <span>{{ expertName(m.expert_id) }}</span>
            <el-input v-model="m.note" size="small" maxlength="200" placeholder="职责补充（可选），如：只看 A 车间" />
          </div>
          <small class="tip">员工端队长是主助手，不用选。员工用不了的成员（范围外、已下线）开工时自动跳过。</small>
        </el-form-item>
        <el-form-item label="队长要求">
          <el-input v-model="form.leader_note" type="textarea" :rows="2" maxlength="1000" placeholder="例如：结论先行，报告用表格；数据注明来源系统" />
        </el-form-item>
        <el-form-item label="团队模型">
          <el-select v-model="form.model_id" clearable placeholder="跟随默认" style="width: 260px">
            <el-option v-for="m in toolModels" :key="m.id" :label="m.name" :value="m.id" />
          </el-select>
          <small class="tip">队长用这个模型；专家被指定了模型的，那位专家仍用指定的。</small>
        </el-form-item>
        <el-form-item label="不用的工具">
          <el-checkbox-group v-model="form.disabled_tools">
            <el-checkbox v-for="(l, k) in TOOL_LABEL" :key="k" :value="k">{{ l }}</el-checkbox>
          </el-checkbox-group>
          <small class="tip">只能关掉，不能给专家增加它原来没有的能力。</small>
        </el-form-item>
        <el-form-item label="只读团队">
          <el-switch v-model="form.read_only" />
          <small class="tip inline">全队只查看、不修改（适合分析类团队）</small>
        </el-form-item>
        <el-form-item label="使用部门">
          <el-select v-model="form.scope_departments" multiple filterable placeholder="不选 = 不按部门限制" style="width: 100%">
            <el-option v-for="d in departments" :key="d.id" :label="`${d.name}（${d.devices} 台）`" :value="d.name" />
          </el-select>
        </el-form-item>
        <el-form-item label="另外的电脑">
          <el-select v-model="form.scope_devices" multiple filterable placeholder="可选：单独给某几台电脑" style="width: 100%">
            <el-option v-for="d in devices" :key="d.id" :label="deviceLabel(d)" :value="d.id" />
          </el-select>
          <small class="tip">部门和电脑都不选 = 全公司可用。</small>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="editing = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 14px;
}
.sub {
  margin: 4px 0 0;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
.actions {
  display: flex;
  gap: 8px;
  flex: none;
}
.tabs {
  margin-bottom: 12px;
}
.name {
  font-weight: 600;
}
.muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.member {
  margin: 2px 4px 2px 0;
}
.note-row {
  display: grid;
  grid-template-columns: 140px 1fr;
  gap: 8px;
  align-items: center;
  width: 100%;
  margin-top: 6px;
  font-size: 13px;
}
.tip {
  display: block;
  width: 100%;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.tip.inline {
  display: inline;
  width: auto;
  margin-left: 8px;
}
</style>
