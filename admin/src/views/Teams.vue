<script setup lang="ts">
/**
 * 团队（docs/team-mode.md）：2～8 位专家组成，员工端主助手当队长。
 * - IT 在这里建、改、下发团队（使用范围：部门 + 单台电脑，都不选 = 全公司）；
 * - 各部门 / 个人在员工端建的团队在这里只能查看、解散、恢复（内容由建的人改）。
 * 团队只能在专家原有配置上收紧（模型、禁用工具、只读），安全规则和确认照旧。
 */
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowDown, Plus, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Department, Device, Expert, ModelConfig, Team, TeamIn, TeamNote, TeamRunRow, TeamStat, TeamTemplate, TeamToolKind } from '@/api/types'

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

const templates = ref<TeamTemplate[]>([])

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
  api.teamTemplates().then((x) => (templates.value = x)).catch(() => {})
  if (page.value === 'stats') void loadStats()
}
onMounted(load)

// ---------- 效果统计（docs/team-mode.md 第 10 节）：只有数字和员工评价，不含对话内容 ----------
const page = ref<'teams' | 'stats'>('teams')
const stats = ref<TeamStat[]>([])
const statDays = ref(30)
const statsLoading = ref(false)
async function loadStats() {
  statsLoading.value = true
  try {
    stats.value = await api.teamStats(statDays.value)
  } finally {
    statsLoading.value = false
  }
}
const pct = (v: number) => `${Math.round(v * 100)}%`
const mins = (s: number) => (s >= 60 ? `${Math.round(s / 60)} 分钟` : `${s} 秒`)
const wan = (n: number) => `${(n / 10000).toFixed(1)} 万`
function upRate(s: TeamStat) {
  const n = s.up + s.down
  return n ? pct(s.up / n) : '—'
}

// ---------- 单个团队：最近的任务、团队经验 ----------
const detail = ref<Team | null>(null)
const runs = ref<TeamRunRow[]>([])
const notes = ref<TeamNote[]>([])
const noteText = ref('')
const noteKind = ref<TeamNote['kind']>('method')
const NOTE_KIND: Record<TeamNote['kind'], string> = { method: '做法', source: '数据口径', pitfall: '注意', other: '其他' }
const NOTE_SOURCE: Record<TeamNote['source'], string> = { leader: '队长记的', employee: '员工加的', it: 'IT 加的' }
const RUN_STATUS: Record<TeamRunRow['status'], string> = { finished: '完成', stopped: '有成员被停', failed: '失败', interrupted: '中断' }
async function openDetail(t: Team) {
  detail.value = t
  runs.value = []
  notes.value = []
  const [r, n] = await Promise.all([api.teamRuns(t.id), api.teamNotes(t.id)])
  runs.value = r
  notes.value = n
}
function openDetailById(id: string) {
  const t = list.value.find((x) => x.id === id)
  if (t) void openDetail(t)
}
async function addNote() {
  if (!detail.value || noteText.value.trim().length < 2) return
  const note = await api.addTeamNote(detail.value.id, { text: noteText.value.trim(), kind: noteKind.value })
  notes.value = [note, ...notes.value]
  noteText.value = ''
}
async function removeNote(n: TeamNote) {
  if (!detail.value) return
  await api.deleteTeamNote(detail.value.id, n.id)
  notes.value = notes.value.filter((x) => x.id !== n.id)
}

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
  disabled_tools: [], read_only: false, scope_departments: [], scope_devices: [], leader_expert_id: '', examples: [],
})
/** 从模板新建时，每个位置要什么样的专家（提示 IT 挑） */
const templateRoles = ref<TeamTemplate['roles']>([])
/** 能当队长的专家：已发布、不是成员 */
const leaderChoices = computed(() => published.value.filter((e) => !form.members.some((m) => m.expert_id === e.id)))
const memberIds = computed({
  get: () => form.members.map((m) => m.expert_id),
  set: (ids: string[]) => {
    const notes = Object.fromEntries(form.members.map((m) => [m.expert_id, m.note]))
    form.members = ids.map((id) => ({ expert_id: id, note: notes[id] ?? '' }))
  },
})

function openNew(tpl?: TeamTemplate) {
  editId.value = null
  templateRoles.value = tpl?.roles ?? []
  Object.assign(form, {
    name: tpl?.name ?? '', description: tpl?.description ?? '', icon: tpl?.icon ?? 'team', leader_note: tpl?.leader_note ?? '',
    members: [], model_id: null, disabled_tools: [...(tpl?.disabled_tools ?? [])], read_only: tpl?.read_only ?? false,
    scope_departments: [], scope_devices: [], leader_expert_id: '', examples: [...(tpl?.examples ?? [])],
  })
  editing.value = true
}
function onTemplate(key: string) {
  const tpl = templates.value.find((t) => t.key === key)
  if (tpl) openNew(tpl)
}
function openEdit(t: Team) {
  editId.value = t.id
  Object.assign(form, {
    name: t.name, description: t.description, icon: t.icon, leader_note: t.leader_note,
    members: t.members.map((m) => ({ expert_id: m.expert_id, note: m.note })), model_id: t.model_id,
    disabled_tools: [...t.disabled_tools], read_only: t.read_only,
    scope_departments: [...t.scope_departments], scope_devices: [...t.scope_devices],
    leader_expert_id: t.leader_expert_id ?? '', examples: [...(t.examples ?? [])],
  })
  templateRoles.value = []
  editing.value = true
}

async function save() {
  if (!form.name.trim()) return ElMessage.warning('给团队起个名字')
  if (form.members.length < 2 || form.members.length > 8) return ElMessage.warning('团队要 2～8 位专家')
  if (form.members.some((m) => m.expert_id === form.leader_expert_id)) form.leader_expert_id = ''
  if (form.examples.length > 5) return ElMessage.warning('示例任务最多 5 条')
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
        <el-dropdown v-if="templates.length" @command="onTemplate">
          <el-button>从模板新建<el-icon class="el-icon--right"><ArrowDown /></el-icon></el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item v-for="tpl in templates" :key="tpl.key" :command="tpl.key">
                <div>{{ tpl.name }}</div>
                <div class="muted">{{ tpl.description }}</div>
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <el-button type="primary" :icon="Plus" @click="openNew()">新建并下发团队</el-button>
      </div>
    </div>

    <el-tabs v-model="page" class="pages" @tab-change="(p: string | number) => p === 'stats' && loadStats()">
      <el-tab-pane label="团队" name="teams" />
      <el-tab-pane label="效果统计" name="stats" />
    </el-tabs>

    <template v-if="page === 'stats'">
      <div class="statbar">
        <span class="muted">只统计员工端上报的数字和员工的 👍 / 👎、一句话反馈，不含对话内容。先在一个部门试点两周，看 👍 比例和平均用量再决定推广。</span>
        <el-select v-model="statDays" style="width: 120px" @change="loadStats">
          <el-option :value="7" label="最近 7 天" />
          <el-option :value="30" label="最近 30 天" />
          <el-option :value="90" label="最近 90 天" />
        </el-select>
      </div>
      <el-table v-loading="statsLoading" :data="stats" empty-text="还没有团队任务">
        <el-table-column label="团队" min-width="180">
          <template #default="{ row }">
            <div class="name">{{ row.name }}<el-tag v-if="row.dissolved" size="small" type="info" class="member">已解散</el-tag></div>
            <div class="muted">{{ SOURCE_LABEL[row.source as Team['source']] }}{{ row.department ? ' · ' + row.department : '' }}</div>
          </template>
        </el-table-column>
        <el-table-column label="任务数" prop="runs" width="80" align="right" />
        <el-table-column label="成员交卷率" width="110" align="right">
          <template #default="{ row }">{{ row.planned ? pct(row.completion_rate) : '—' }}</template>
        </el-table-column>
        <el-table-column label="补派率" width="90" align="right">
          <template #default="{ row }">{{ row.runs ? pct(row.reassign_rate) : '—' }}</template>
        </el-table-column>
        <el-table-column label="平均用量" width="110" align="right">
          <template #default="{ row }">{{ row.runs ? wan(row.avg_tokens) : '—' }}</template>
        </el-table-column>
        <el-table-column label="平均耗时" width="100" align="right">
          <template #default="{ row }">{{ row.runs ? mins(row.avg_seconds) : '—' }}</template>
        </el-table-column>
        <el-table-column label="评价" width="150">
          <template #default="{ row }">👍 {{ row.up }} · 👎 {{ row.down }} <span class="muted">（{{ upRate(row as TeamStat) }}）</span></template>
        </el-table-column>
        <el-table-column label="经验" prop="notes" width="70" align="right" />
        <el-table-column label="" width="90" align="right">
          <template #default="{ row }"><el-button size="small" @click="openDetailById(row.team_id)">明细</el-button></template>
        </el-table-column>
      </el-table>
    </template>

    <template v-else>

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
            <el-button size="small" @click="openDetail(row as Team)">效果</el-button>
            <el-button size="small" type="danger" plain @click="dissolve(row as Team)">解散</el-button>
          </template>
        </template>
      </el-table-column>
    </el-table>
    </template>

    <el-drawer :model-value="!!detail" :title="detail ? `${detail.name} · 效果和经验` : ''" size="720px" @close="detail = null">
      <template v-if="detail">
        <h4>最近的团队任务</h4>
        <el-table :data="runs" size="small" empty-text="还没有上报过的团队任务" max-height="320">
          <el-table-column label="时间" width="150">
            <template #default="{ row }">{{ new Date(row.created_at).toLocaleString() }}</template>
          </el-table-column>
          <el-table-column label="电脑" min-width="120">
            <template #default="{ row }">{{ row.device_name || row.device_id }}<div class="muted">{{ row.department }}</div></template>
          </el-table-column>
          <el-table-column label="成员" width="100">
            <template #default="{ row }">{{ row.completed }}/{{ row.planned }}<span v-if="row.reassigned" class="muted"> · 补派</span></template>
          </el-table-column>
          <el-table-column label="状态" width="90">
            <template #default="{ row }">{{ RUN_STATUS[row.status as TeamRunRow['status']] ?? row.status }}</template>
          </el-table-column>
          <el-table-column label="评价" min-width="160">
            <template #default="{ row }">
              <span v-if="row.rating > 0">👍</span><span v-else-if="row.rating < 0">👎</span><span v-else class="muted">没评</span>
              <span v-if="row.feedback" class="muted"> {{ row.feedback }}</span>
            </template>
          </el-table-column>
        </el-table>

        <h4>团队经验</h4>
        <p class="muted">队长和员工记下的做法、数据口径、注意事项，开工时作为参考资料给队长。过时或不对的可以删掉。</p>
        <div class="noteadd">
          <el-select v-model="noteKind" style="width: 110px">
            <el-option v-for="(l, k) in NOTE_KIND" :key="k" :value="k" :label="l" />
          </el-select>
          <el-input v-model="noteText" maxlength="300" placeholder="一句话，例如：月底最后一天的数据第二天上午才全" @keyup.enter="addNote" />
          <el-button type="primary" :disabled="noteText.trim().length < 2" @click="addNote">记下</el-button>
        </div>
        <el-table :data="notes" size="small" empty-text="还没有团队经验">
          <el-table-column label="类别" width="90">
            <template #default="{ row }"><el-tag size="small">{{ NOTE_KIND[row.kind as TeamNote['kind']] ?? row.kind }}</el-tag></template>
          </el-table-column>
          <el-table-column label="经验" prop="text" min-width="260" />
          <el-table-column label="来源" width="130">
            <template #default="{ row }">{{ NOTE_SOURCE[row.source as TeamNote['source']] }}<div class="muted">{{ row.author }}</div></template>
          </el-table-column>
          <el-table-column label="" width="70" align="right">
            <template #default="{ row }"><el-button size="small" type="danger" link @click="removeNote(row as TeamNote)">删除</el-button></template>
          </el-table-column>
        </el-table>
      </template>
    </el-drawer>

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
          <small class="tip">员工用不了的成员（范围外、已下线）开工时自动跳过。</small>
          <div v-if="templateRoles.length" class="roles">
            <div class="muted">模板建议的位置（在本厂的专家里挑对应的人）：</div>
            <div v-for="r in templateRoles" :key="r.role" class="muted">· {{ r.role }}：{{ r.note }}</div>
          </div>
        </el-form-item>
        <el-form-item label="队长">
          <el-select v-model="form.leader_expert_id" clearable placeholder="主助手（默认）" style="width: 260px">
            <el-option v-for="e in leaderChoices" :key="e.id" :label="e.name" :value="e.id" />
          </el-select>
          <small class="tip">可以让一位专家当队长：带上它的业务知识和模型，也受它的限制（只收紧）。队长不能同时是成员。</small>
        </el-form-item>
        <el-form-item label="示例任务">
          <el-select
            v-model="form.examples"
            multiple
            filterable
            allow-create
            default-first-option
            :multiple-limit="5"
            :reserve-keyword="false"
            placeholder="输入一句话回车添加，最多 5 条"
            style="width: 100%"
          />
          <small class="tip">员工在团队卡片上点一下就带着这句话开始。</small>
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
.pages {
  margin-bottom: 4px;
}
.statbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 10px;
}
.noteadd {
  display: flex;
  gap: 8px;
  margin: 8px 0;
}
.roles {
  width: 100%;
  margin-top: 6px;
  line-height: 1.6;
}
h4 {
  margin: 16px 0 6px;
}
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
