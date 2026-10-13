<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { UploadRequestOptions } from 'element-plus'
import { Clock, Delete, Edit, MagicStick, Plus, Promotion, Refresh, Search, SwitchButton } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Expert, ExpertFields, ExpertOptions, ExpertVersion, Subtask, SubtaskStatus } from '@/api/types'
import { auth } from '@/store/auth'
import { dateTime, num } from '@/utils/format'
import { toSquareDataUrl } from '@/utils/image'

/**
 * 专家：在这里建好、发布，员工端「专家」里就能看到，点一下呼叫。
 * 改的是草稿，员工端不受影响；点「发布」才生效，历史版本可以放回草稿再发布（回滚）。
 */
const experts = ref<Expert[]>([])
const options = ref<ExpertOptions>({ tools: [], skills: [], mcp: [], models: [] })
const loading = ref(false)
const keyword = ref('')

async function load() {
  loading.value = true
  try {
    const [list, opts] = await Promise.all([api.experts(), api.expertOptions()])
    experts.value = list
    options.value = opts
  } finally {
    loading.value = false
  }
}
onMounted(load)

const filtered = computed(() => {
  const q = keyword.value.trim().toLowerCase()
  return q
    ? experts.value.filter((e) => `${e.id} ${e.name} ${e.tagline} ${e.category}`.toLowerCase().includes(q))
    : experts.value
})
const liveCount = computed(() => experts.value.filter((e) => e.status === 'published').length)

const statusLabel: Record<Expert['status'], string> = { draft: '草稿', published: '已发布', offline: '已下线' }
const statusType: Record<Expert['status'], 'info' | 'success' | 'warning'> = { draft: 'info', published: 'success', offline: 'warning' }

const isImage = (avatar: string) => /^(https?:|data:image\/)/.test(avatar)
function initial(e: { name: string }) {
  return [...e.name.trim()][0] ?? '?'
}
function hue(id: string) {
  let h = 0
  for (const ch of id) h = (h * 31 + ch.charCodeAt(0)) % 360
  return h
}

// 只读专家用不了的工具（员工端也按这个名单筛）。执行命令保留，但只读权限下只放行查询类命令
const WRITE_TOOLS = ['write_file', 'delete_path']

const CARD_TEMPLATE = '负责：\n适合：\n不负责：\n需要的信息：'

// ---------- 编辑 ----------
const editing = ref(false)
const isNew = ref(true)
const saving = ref(false)
const notes = ref<string[]>([])
const showLimits = ref<string[]>([])
const form = reactive({
  id: '',
  name: '',
  avatar: '',
  tagline: '',
  category: '',
  tags: [] as string[],
  author: '',
  description: '',
  instructions: '',
  knowledge: '',
  examplesText: '',
  preferred_skills: [] as string[],
  mcp_servers: [] as string[],
  disallowed_tools: [] as string[],
  read_only: false,
  model: '' as string,
  allow_direct: true,
  allow_delegate: true,
  helper: false,
  sort_order: 0,
})

function fill(e: Partial<Expert>) {
  form.id = e.id ?? ''
  form.name = e.name ?? ''
  form.avatar = e.avatar ?? ''
  form.tagline = e.tagline ?? ''
  form.category = e.category ?? ''
  form.tags = [...(e.tags ?? [])]
  form.author = e.author ?? ''
  form.description = e.description || CARD_TEMPLATE
  form.instructions = e.instructions ?? ''
  form.knowledge = e.knowledge ?? ''
  form.examplesText = (e.examples ?? []).join('\n')
  form.preferred_skills = [...(e.preferred_skills ?? [])]
  form.mcp_servers = [...(e.mcp_servers ?? [])]
  form.disallowed_tools = [...(e.disallowed_tools ?? [])]
  form.read_only = e.read_only ?? false
  form.model = e.model_id != null ? String(e.model_id) : ''
  form.allow_direct = e.allow_direct ?? true
  form.allow_delegate = e.allow_delegate ?? true
  form.helper = e.helper ?? false
  form.sort_order = e.sort_order ?? 0
  // 有限制的才默认展开，没有就收着（默认全部沿用，大多数专家不用改）
  const limited = form.preferred_skills.length || form.mcp_servers.length || form.disallowed_tools.length || form.read_only || form.model
  showLimits.value = limited ? ['limits'] : []
}

function body(): ExpertFields {
  return {
    name: form.name.trim(),
    avatar: form.avatar.trim(),
    tagline: form.tagline.trim(),
    category: form.category.trim(),
    tags: form.tags.map((x) => x.trim()).filter(Boolean).slice(0, 4),
    author: form.author.trim(),
    description: form.description.trim() === CARD_TEMPLATE ? '' : form.description.trim(),
    instructions: form.instructions.trim(),
    knowledge: form.knowledge.trim(),
    examples: form.examplesText.split('\n').map((l) => l.trim()).filter(Boolean),
    preferred_skills: form.preferred_skills,
    mcp_servers: form.mcp_servers,
    disallowed_tools: form.disallowed_tools,
    read_only: form.read_only,
    model_id: form.model ? Number(form.model) : null,
    allow_direct: form.allow_direct,
    allow_delegate: form.allow_delegate,
    helper: form.helper,
    sort_order: form.sort_order,
  }
}

function openNew() {
  isNew.value = true
  notes.value = []
  fill({})
  editing.value = true
}

function openEdit(e: Expert) {
  isNew.value = false
  notes.value = []
  fill(e)
  editing.value = true
}

async function onAvatar(opts: UploadRequestOptions) {
  try {
    form.avatar = await toSquareDataUrl(opts.file)
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : '头像上传失败')
  }
}

async function save(publish: boolean) {
  if (!form.name.trim()) return ElMessage.warning('请填写名称')
  if (!/^[a-z][a-z0-9_-]{1,39}$/.test(form.id)) return ElMessage.warning('标识只能用小写字母、数字、- 和 _，字母开头，2～40 个字符')
  if (publish && !form.instructions.trim()) return ElMessage.warning('先写好「工作方式」再发布')
  saving.value = true
  try {
    const saved = isNew.value ? await api.createExpert({ id: form.id, ...body() }) : await api.updateExpert(form.id, body())
    if (publish) await api.publishExpert(saved.id, '')
    ElMessage.success(publish ? '已发布，员工端马上就能看到' : '草稿已保存。员工端看到的还是已发布的版本，点「发布」才会生效')
    editing.value = false
    await load()
  } finally {
    saving.value = false
  }
}

// ---------- AI 起草 ----------
const drafting = ref(false)
const draftOpen = ref(false)
const draftText = ref('')

async function runDraft() {
  if (draftText.value.trim().length < 2) return ElMessage.warning('先说说想要一个什么样的专家')
  drafting.value = true
  try {
    const d = await api.draftExpert(draftText.value.trim())
    isNew.value = true
    fill({ ...d, model_id: null, allow_direct: true, allow_delegate: true, sort_order: 0, author: '' })
    notes.value = d.notes
    draftOpen.value = false
    draftText.value = '' // 下次打开是空的，从头说新的专家
    editing.value = true
  } finally {
    drafting.value = false
  }
}

// ---------- 发布 / 下线 / 删除 ----------
async function publish(e: Expert) {
  let note = ''
  try {
    const r = await ElMessageBox.prompt(
      e.version ? `发布后员工端换成新的内容（第 ${e.version + 1} 版）。可以写一句这次改了什么：` : '发布后员工端「专家」里就能看到。可以写一句说明：',
      `发布 · ${e.name}`,
      { confirmButtonText: '发布', cancelButtonText: '取消', inputPlaceholder: '选填，例如：补充了设备停机的分析思路', inputValue: '' },
    )
    note = (r as { value: string }).value ?? ''
  } catch {
    return
  }
  await api.publishExpert(e.id, note)
  ElMessage.success('已发布')
  await load()
}

async function offline(e: Expert) {
  await ElMessageBox.confirm(`下线后员工端不再显示「${e.name}」。草稿和历史版本都会保留，重新发布就回来了。`, '下线专家', {
    type: 'warning',
    confirmButtonText: '下线',
    cancelButtonText: '取消',
  })
  await api.offlineExpert(e.id)
  ElMessage.success('已下线')
  await load()
}

async function remove(e: Expert) {
  await ElMessageBox.confirm(`删除「${e.name}」和它的全部历史版本？员工端也会马上去掉。只是暂时不用的话，建议「下线」。`, '删除专家', {
    type: 'warning',
    confirmButtonText: '删除',
    cancelButtonText: '取消',
    confirmButtonClass: 'el-button--danger',
  })
  await api.deleteExpert(e.id)
  ElMessage.success('已删除')
  await load()
}

// ---------- 历史版本 ----------
const historyOpen = ref(false)
const historyOf = ref<Expert | null>(null)
const versions = ref<ExpertVersion[]>([])
const historyLoading = ref(false)

async function openHistory(e: Expert) {
  historyOf.value = e
  historyOpen.value = true
  historyLoading.value = true
  try {
    versions.value = await api.expertVersions(e.id)
  } finally {
    historyLoading.value = false
  }
}

async function restore(v: ExpertVersion) {
  const e = historyOf.value
  if (!e) return
  await ElMessageBox.confirm(
    `把第 ${v.version} 版的内容放回草稿？现在草稿里没发布的修改会被覆盖。放回后检查一下，再点「发布」员工端才会换成它。`,
    '放回草稿',
    { confirmButtonText: '放回草稿', cancelButtonText: '取消' },
  )
  const saved = await api.restoreExpert(e.id, v.version)
  historyOpen.value = false
  ElMessage.success('已放回草稿')
  await load()
  openEdit(saved)
}

async function discard(e: Expert) {
  await ElMessageBox.confirm('放弃没发布的修改，草稿恢复成员工端现在看到的样子？', '放弃修改', {
    type: 'warning',
    confirmButtonText: '放弃修改',
    cancelButtonText: '取消',
  })
  await api.discardExpert(e.id)
  historyOpen.value = false
  ElMessage.success('已恢复')
  await load()
}

// ---------- 专家派遣 ----------
// 开关存在安全中心的全厂设置里，放开就是全厂一起放开，所以和安全中心一样限超级管理员
const canSwitch = computed(() => !!auth.user?.is_owner && !auth.user?.must_change_password)
const delegation = ref(false)
const switching = ref(false)

async function loadDelegation() {
  delegation.value = (await api.expertDelegation()).enabled
}

async function toggleDelegation(next: string | number | boolean) {
  const on = Boolean(next)
  if (on) {
    try {
      await ElMessageBox.confirm(
        '开启后，主助手遇到专家擅长的任务时会自己派给专家做（每轮最多 3 次，有 token 和时间上限）。只有已发布、勾选了「可接受派遣」的专家会被派到。单台电脑可以在「设备列表 - 详情」里另外设置。',
        '允许主助手派遣专家',
        { confirmButtonText: '开启', cancelButtonText: '取消' },
      )
    } catch {
      return
    }
  }
  switching.value = true
  try {
    delegation.value = (await api.setExpertDelegation(on)).enabled
    ElMessage.success(on ? '已开启，员工端几秒内生效' : '已关闭，正在执行的派遣会被取消')
  } finally {
    switching.value = false
  }
}

const subtaskStatus: Record<SubtaskStatus, { label: string; type: 'primary' | 'success' | 'warning' | 'info' | 'danger' }> = {
  running: { label: '执行中', type: 'primary' },
  completed: { label: '完成', type: 'success' },
  partial: { label: '部分完成', type: 'warning' },
  needs_input: { label: '需要补充信息', type: 'warning' },
  failed: { label: '失败', type: 'danger' },
  cancelled: { label: '已取消', type: 'info' },
  timed_out: { label: '超时', type: 'danger' },
}
const subtasks = ref<Subtask[]>([])
const subtasksLoading = ref(false)
const subtaskFilter = reactive<{ expert_id: string; status: SubtaskStatus | '' }>({ expert_id: '', status: '' })

async function loadSubtasks() {
  subtasksLoading.value = true
  try {
    subtasks.value = await api.subtasks({ limit: 200, ...subtaskFilter })
  } finally {
    subtasksLoading.value = false
  }
}

function duration(ms: number | null) {
  if (ms == null) return '—'
  const sec = Math.round(ms / 1000)
  return sec < 60 ? `${sec} 秒` : `${Math.floor(sec / 60)} 分 ${sec % 60} 秒`
}

onMounted(() => {
  loadDelegation()
  loadSubtasks()
})

const toolLabel = (name: string) => options.value.tools.find((t) => t.value === name)?.label ?? name
const readOnlyBlocked = computed(() => (form.read_only ? WRITE_TOOLS.map(toolLabel).join('、') : ''))
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>专家</h1>
        <p>
          为垂直场景建专家（MES 生产分析、IT 运维、报表助手……）。发布后员工在客户端「专家」里点一下就能呼叫。
          技能、连接器、工具默认全部沿用，以后新加的自动可用，不用回来改专家。
        </p>
      </div>
      <div class="toolbar">
        <el-input v-model="keyword" :prefix-icon="Search" placeholder="搜索专家" clearable style="width: 200px" />
        <el-button :icon="Refresh" :loading="loading" @click="load" />
        <el-button :icon="MagicStick" @click="draftOpen = true">AI 帮我创建</el-button>
        <el-button type="primary" :icon="Plus" @click="openNew">新建专家</el-button>
      </div>
    </div>

    <div class="panel">
      <div class="switch-row">
        <div>
          <strong>允许主助手派遣专家</strong>
          <p>开启后，主助手遇到专家擅长的任务时可以自己派给专家去做，再汇总结果；只派给已发布、可接受派遣的专家。默认关闭。</p>
        </div>
        <el-tooltip :disabled="canSwitch" content="只有超级管理员能改：放开就是全厂所有电脑一起放开" placement="left">
          <el-switch :model-value="delegation" :loading="switching" :disabled="!canSwitch" @change="toggleDelegation" />
        </el-tooltip>
      </div>
    </div>

    <div class="panel">
      <div class="panel-title">
        <h3>全部专家 <small>共 {{ experts.length }} 个，其中 {{ liveCount }} 个已发布</small></h3>
      </div>
      <el-empty v-if="!loading && experts.length === 0" description="还没有专家。点「AI 帮我创建」，用一句话说想要什么专家就行。" />
      <div v-loading="loading" class="grid">
        <div v-for="e in filtered" :key="e.id" class="card" :class="{ off: e.status === 'offline' }">
          <div class="top">
            <img v-if="isImage(e.avatar)" :src="e.avatar" alt="" class="avatar" />
            <span v-else-if="e.avatar" class="avatar emoji">{{ e.avatar }}</span>
            <span v-else class="avatar letter" :style="{ background: `hsl(${hue(e.id)} 55% 48%)` }">{{ initial(e) }}</span>
            <div class="title">
              <strong>{{ e.name }}</strong>
              <code>{{ e.id }}</code>
            </div>
            <el-tag size="small" :type="statusType[e.status]" effect="light">
              {{ statusLabel[e.status] }}<template v-if="e.version"> · v{{ e.version }}</template>
            </el-tag>
          </div>
          <p class="desc">{{ e.tagline || '没有介绍' }}</p>
          <div class="tags">
            <el-tag v-if="e.dirty" size="small" type="warning" effect="plain">有未发布的修改</el-tag>
            <el-tag v-if="e.category" size="small" effect="plain">{{ e.category }}</el-tag>
            <el-tag v-if="e.read_only" size="small" effect="plain" type="info">只读</el-tag>
            <el-tag v-if="e.helper" size="small" effect="plain" type="info">通用辅助</el-tag>
            <el-tag v-if="e.preferred_skills.length" size="small" effect="plain" type="info">优先技能 {{ e.preferred_skills.length }}</el-tag>
            <el-tag v-if="e.mcp_servers.length" size="small" effect="plain" type="info">限定连接器 {{ e.mcp_servers.length }}</el-tag>
            <el-tag v-if="!e.allow_direct" size="small" effect="plain" type="info">不对员工显示</el-tag>
          </div>
          <div class="usage">
            <span>使用 <strong>{{ e.uses.toLocaleString() }}</strong> 次</span>
            <span><strong>{{ e.users }}</strong> 台电脑用过</span>
          </div>
          <div class="foot">
            <span class="muted">{{ e.updated_at ? dateTime(e.updated_at) : '' }}<template v-if="e.updated_by"> · {{ e.updated_by }}</template></span>
            <span class="actions">
              <el-button v-if="e.status !== 'published' || e.dirty" link type="primary" :icon="Promotion" @click="publish(e)">发布</el-button>
              <el-button link type="primary" :icon="Edit" @click="openEdit(e)">编辑</el-button>
              <el-dropdown trigger="click">
                <el-button link type="primary">更多</el-button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item :icon="Clock" :disabled="!e.version" @click="openHistory(e)">历史版本</el-dropdown-item>
                    <el-dropdown-item :icon="SwitchButton" :disabled="e.status !== 'published'" @click="offline(e)">下线</el-dropdown-item>
                    <el-dropdown-item :icon="Delete" divided @click="remove(e)">删除</el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </span>
          </div>
        </div>
      </div>
    </div>

    <div class="panel">
      <div class="panel-title">
        <h3>派遣记录 <small>主助手派给专家的子任务，和审计日志保留同样长的时间</small></h3>
        <div class="toolbar">
          <el-select v-model="subtaskFilter.expert_id" placeholder="全部专家" clearable style="width: 180px" @change="loadSubtasks">
            <el-option v-for="e in experts" :key="e.id" :label="e.name" :value="e.id" />
          </el-select>
          <el-select v-model="subtaskFilter.status" placeholder="全部状态" clearable style="width: 140px" @change="loadSubtasks">
            <el-option v-for="(v, k) in subtaskStatus" :key="k" :label="v.label" :value="k" />
          </el-select>
          <el-button :icon="Refresh" :loading="subtasksLoading" @click="loadSubtasks" />
        </div>
      </div>
      <el-table v-loading="subtasksLoading" :data="subtasks" size="default" empty-text="还没有派遣记录">
        <el-table-column label="时间" width="150">
          <template #default="{ row }">{{ dateTime(row.created_at) }}</template>
        </el-table-column>
        <el-table-column label="员工 / 电脑" min-width="150">
          <template #default="{ row }">
            <div class="who">
              <span>{{ row.owner || row.user_name || '—' }}</span>
              <small>{{ row.machine_name }}</small>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="专家" min-width="140">
          <template #default="{ row }">
            <div class="who">
              <span>{{ row.expert_name || row.expert_id }}</span>
              <small>v{{ row.published_version }} · 第 {{ row.seq }} 次</small>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="120">
          <template #default="{ row }">
            <el-tag size="small" :type="subtaskStatus[row.status as SubtaskStatus]?.type ?? 'info'" effect="light">
              {{ subtaskStatus[row.status as SubtaskStatus]?.label ?? row.status }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="摘要" min-width="220">
          <template #default="{ row }">
            <el-tooltip v-if="row.task" :content="row.task" placement="top" popper-class="subtask-tip">
              <span class="clip">{{ row.summary || row.task }}</span>
            </el-tooltip>
            <span v-else class="muted">{{ row.summary || '—' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="Token" width="130" align="right">
          <template #default="{ row }">
            <el-tooltip :content="`提示 ${num(row.prompt_tokens)} · 回答 ${num(row.completion_tokens)} · 上限 ${num(row.token_cap)}`" placement="top">
              <span>{{ num(row.prompt_tokens + row.completion_tokens) }}<template v-if="row.estimated_calls">（含估算）</template></span>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column label="耗时" width="100">
          <template #default="{ row }">{{ duration(row.duration_ms) }}</template>
        </el-table-column>
        <el-table-column label="错误" min-width="140">
          <template #default="{ row }">
            <span v-if="row.error_code || row.error" class="clip err" :title="row.error">{{ row.error || row.error_code }}</span>
            <span v-else class="muted">—</span>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <!-- AI 起草 -->
    <el-dialog v-model="draftOpen" title="AI 帮我创建专家" width="min(560px, 94vw)">
      <p class="lead">用一两句话说说想要什么专家：负责什么业务、帮谁做什么事。AI 会参考现有的技能和连接器起草一份，你确认后再保存。</p>
      <el-input
        v-model="draftText"
        type="textarea"
        :rows="4"
        maxlength="2000"
        placeholder="例如：我要一个 MES 生产分析专家，帮车间主任分析产量下降、设备停机、工单延期的原因"
      />
      <template #footer>
        <el-button @click="draftOpen = false">取消</el-button>
        <el-button type="primary" :icon="MagicStick" :loading="drafting" @click="runDraft">{{ drafting ? '正在起草…' : '起草' }}</el-button>
      </template>
    </el-dialog>

    <!-- 编辑 -->
    <el-dialog v-model="editing" :title="isNew ? '新建专家' : `编辑 · ${form.name}`" width="min(920px, 94vw)" top="4vh" :close-on-click-modal="false">
      <el-alert v-if="notes.length" type="info" :closable="false" show-icon class="notes" title="AI 的提示">
        <ul>
          <li v-for="n in notes" :key="n">{{ n }}</li>
        </ul>
      </el-alert>
      <el-form label-width="104px" class="form">
        <h4>基本信息</h4>
        <el-form-item label="名称" required><el-input v-model="form.name" placeholder="MES生产分析专家" maxlength="100" /></el-form-item>
        <el-form-item label="标识" required>
          <el-input v-model="form.id" :disabled="!isNew" placeholder="mes_analyst" maxlength="40" />
          <p class="hint">小写字母、数字、- 和 _，字母开头。创建后不能改。</p>
        </el-form-item>
        <el-form-item label="头像">
          <div class="inline">
            <img v-if="isImage(form.avatar)" :src="form.avatar" alt="" class="avatar big" />
            <span v-else-if="form.avatar" class="avatar big emoji">{{ form.avatar }}</span>
            <span v-else class="avatar big letter" :style="{ background: `hsl(${hue(form.id || form.name)} 55% 48%)` }">{{ initial(form) }}</span>
            <el-input v-model="form.avatar" placeholder="一个 emoji（如 🏭），或图片链接" clearable />
            <el-upload :http-request="onAvatar" :show-file-list="false" accept="image/*"><el-button>上传图片</el-button></el-upload>
          </div>
        </el-form-item>
        <el-form-item label="一句话介绍"><el-input v-model="form.tagline" maxlength="200" show-word-limit placeholder="显示在员工端的专家卡片上" /></el-form-item>
        <el-form-item label="标签">
          <el-select v-model="form.tags" multiple filterable allow-create default-first-option :multiple-limit="4"
            placeholder="2～3 个能力标签，输入后回车，如 产量分析" style="width: 100%" />
          <p class="hint">显示在员工端的专家卡片上，最多 4 个。</p>
        </el-form-item>
        <el-form-item label="维护人">
          <el-input v-model="form.author" maxlength="100" placeholder="选填，例如「IT 部 · 张工」。员工在专家详情里能看到" />
        </el-form-item>
        <el-form-item label="分类 / 排序">
          <div class="inline">
            <el-input v-model="form.category" placeholder="生产 / 供应链 / IT / 行政……" maxlength="50" />
            <el-input-number v-model="form.sort_order" :step="1" controls-position="right" />
          </div>
          <p class="hint">员工端按分类分组；排序数字小的在前。</p>
        </el-form-item>
        <el-form-item label="示例问题">
          <el-input v-model="form.examplesText" type="textarea" :rows="3" placeholder="一行一个。员工打开专家时显示，点一下就能问" />
        </el-form-item>

        <h4>名片</h4>
        <el-form-item label="负责什么">
          <el-input v-model="form.description" type="textarea" :rows="5" maxlength="4000" show-word-limit />
          <p class="hint">写清楚负责什么、适合什么任务、不负责什么、需要用户提供什么信息。员工打开专家时能看到；以后主智能体也靠它判断该把任务交给哪位专家。</p>
        </el-form-item>

        <h4>工作方式</h4>
        <el-form-item label="提示词" required>
          <el-input v-model="form.instructions" type="textarea" :rows="10" maxlength="20000" show-word-limit
            placeholder="它是谁、怎么理解任务、按什么思路分析、查数据时注意什么、结论怎么组织、什么时候停下来问用户" />
        </el-form-item>
        <el-form-item label="专家知识">
          <el-input v-model="form.knowledge" type="textarea" :rows="5" maxlength="20000" show-word-limit
            placeholder="选填。固定不变的业务信息，每次都会带给专家：产线和车间的对应关系、设备编码规则、系统使用说明……" />
        </el-form-item>

        <el-collapse v-model="showLimits" class="limits">
          <el-collapse-item name="limits">
            <template #title>
              <h4 class="collapse-title">能力与限制 <small>默认全部沿用，大多数专家不用改</small></h4>
            </template>
            <el-form-item label="优先技能">
              <el-select v-model="form.preferred_skills" multiple filterable placeholder="不选 = 自己按需查找" style="width: 100%" fit-input-width popper-class="expert-opt-popper">
                <el-option v-for="s in options.skills" :key="s.value" :label="s.label" :value="s.value" :title="s.detail">
                  <span>{{ s.label }}</span><span class="opt-detail">{{ s.detail }}</span>
                </el-option>
              </el-select>
              <p class="hint">只是提示专家优先用这些，不是限制：技能库里别的技能照样能找到、能用。</p>
            </el-form-item>
            <el-form-item label="只用这些连接器">
              <el-select v-model="form.mcp_servers" multiple filterable placeholder="不选 = 员工连上的全部都能用" style="width: 100%" fit-input-width popper-class="expert-opt-popper">
                <el-option v-for="m in options.mcp" :key="m.value" :label="m.label" :value="m.value" />
              </el-select>
              <p class="hint">只在专家明确只该碰某几个系统时才选。以后接入的新系统，不选时自动可用。</p>
            </el-form-item>
            <el-form-item label="不让用的工具">
              <el-select v-model="form.disallowed_tools" multiple placeholder="不选 = 全部沿用" style="width: 100%" fit-input-width popper-class="expert-opt-popper">
                <el-option v-for="t in options.tools" :key="t.value" :label="t.label" :value="t.value">
                  <span>{{ t.label }}</span><span class="opt-detail">{{ t.value }}</span>
                </el-option>
              </el-select>
            </el-form-item>
            <el-form-item label="只读">
              <el-switch v-model="form.read_only" />
              <p class="hint">
                只做查询分析、不该改动任何东西的专家打开它。
                <template v-if="form.read_only">会去掉{{ readOnlyBlocked }}，执行命令只放行查询类的。</template>
                员工自己的权限设置更严时，以更严的为准。
              </p>
            </el-form-item>
            <el-form-item label="模型">
              <el-select v-model="form.model" placeholder="跟随「办事」场景" clearable style="width: 100%" fit-input-width popper-class="expert-opt-popper">
                <el-option label="跟随「办事」场景" value="" />
                <el-option v-for="m in options.models" :key="m.value" :label="m.label" :value="m.value" :title="m.detail">
                  <span>{{ m.label }}</span><span class="opt-detail">{{ m.detail }}</span>
                </el-option>
              </el-select>
              <p class="hint">指定的模型停用或删掉后，自动回到「办事」场景的模型。辅助类专家建议选更快、更便宜的模型。</p>
            </el-form-item>
          </el-collapse-item>
        </el-collapse>

        <h4>谁能用</h4>
        <el-form-item label="员工可以呼叫">
          <el-switch v-model="form.allow_direct" />
          <p class="hint">关掉后员工端不显示这位专家（以后只给主智能体派任务用）。</p>
        </el-form-item>
        <el-form-item label="可接受派遣">
          <el-switch v-model="form.allow_delegate" />
          <p class="hint">开启页面顶部的「允许主助手派遣专家」后，主助手遇到这位专家擅长的任务时可以交给它做。</p>
        </el-form-item>
        <el-form-item label="通用辅助">
          <el-switch v-model="form.helper" />
          <p class="hint">总在主助手的候选专家里（不看和问题相不相关）。适合信息调查、文件理解、交付评审这类通用助手；业务专家不要开</p>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="editing = false">取消</el-button>
        <el-button :loading="saving" @click="save(false)">保存草稿</el-button>
        <el-button type="primary" :icon="Promotion" :loading="saving" @click="save(true)">保存并发布</el-button>
      </template>
    </el-dialog>

    <!-- 历史版本 -->
    <el-dialog v-model="historyOpen" :title="`历史版本 · ${historyOf?.name ?? ''}`" width="min(720px, 94vw)">
      <el-alert v-if="historyOf?.dirty" type="warning" :closable="false" show-icon class="notes">
        <template #title>
          草稿里有没发布的修改。
          <el-button link type="primary" @click="historyOf && discard(historyOf)">放弃修改，恢复成已发布的样子</el-button>
        </template>
      </el-alert>
      <el-table v-loading="historyLoading" :data="versions" size="default" empty-text="还没有发布过">
        <el-table-column label="版本" width="80">
          <template #default="{ row }">
            v{{ row.version }}
            <el-tag v-if="historyOf && row.version === historyOf.version" size="small" type="success" effect="plain">当前</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="说明" min-width="200">
          <template #default="{ row }">{{ row.note || '—' }}</template>
        </el-table-column>
        <el-table-column label="发布" width="200">
          <template #default="{ row }">
            <div class="who">
              <span>{{ dateTime(row.published_at) }}</span>
              <small>{{ row.published_by }}</small>
            </div>
          </template>
        </el-table-column>
        <el-table-column width="110" align="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="restore(row as ExpertVersion)">放回草稿</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-dialog>
  </div>
</template>

<style scoped>
.switch-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}
.switch-row strong {
  color: var(--ink);
  font-weight: 600;
}
.switch-row p {
  margin: 4px 0 0;
  color: var(--ink-faint);
  font-size: 13px;
  line-height: 1.6;
}
.clip {
  display: -webkit-box;
  overflow: hidden;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}
.err {
  color: var(--el-color-danger);
}
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: 14px;
  min-height: 80px;
}
.card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 16px;
  border: 1px solid var(--line);
  border-radius: 12px;
  background: var(--cloth);
  color: var(--ink);
}
.card.off {
  opacity: 0.6;
}
.top {
  display: flex;
  align-items: center;
  gap: 10px;
}
.avatar {
  flex: none;
  width: 40px;
  height: 40px;
  border-radius: 10px;
  object-fit: cover;
}
.avatar.big {
  width: 48px;
  height: 48px;
}
.emoji {
  display: grid;
  place-items: center;
  border: 1px solid var(--line);
  background: var(--cloth-sunk);
  font-size: 22px;
  line-height: 1;
}
.avatar.big.emoji {
  font-size: 26px;
}
.letter {
  display: grid;
  place-items: center;
  color: #fff;
  font-weight: 700;
}
.title {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}
.title strong {
  overflow: hidden;
  color: var(--ink);
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.title code {
  color: var(--ink-faint);
  font-size: 12px;
}
.desc {
  margin: 0;
  color: var(--ink-faint);
  font-size: 13px;
  line-height: 1.6;
  display: -webkit-box;
  overflow: hidden;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}
.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.usage {
  display: flex;
  gap: 16px;
  color: var(--ink-faint);
  font-size: 12px;
}
.usage strong {
  color: var(--ink);
  font-weight: 600;
}
.foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-top: auto;
  font-size: 12px;
}
.foot .actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: 10px;
}
.foot .actions .el-button + .el-button {
  margin-left: 0;
}
.lead {
  margin: 0 0 12px;
  color: var(--ink-soft);
  font-size: 13px;
  line-height: 1.6;
}
.notes {
  margin-bottom: 14px;
}
.notes ul {
  margin: 4px 0 0;
  padding-left: 18px;
}
.form h4 {
  margin: 18px 0 10px;
  padding-bottom: 6px;
  border-bottom: 1px solid var(--line);
  font-size: 14px;
}
.form h4:first-child {
  margin-top: 0;
}
.limits {
  margin-top: 8px;
  border-top: 0;
}
.limits :deep(.el-collapse-item__header) {
  height: auto;
  border-bottom: 1px solid var(--line);
  background: transparent;
}
.limits :deep(.el-collapse-item__wrap) {
  border-bottom: 0;
  background: transparent;
}
.limits :deep(.el-collapse-item__content) {
  padding: 12px 0 0;
}
.form h4.collapse-title {
  flex: 1;
  margin: 10px 0 6px;
  padding: 0;
  border: 0;
}
.collapse-title small,
.muted {
  color: var(--ink-faint);
  font-weight: 400;
}
.collapse-title small {
  margin-left: 6px;
  font-size: 12px;
}
.hint {
  flex-basis: 100%;
  margin: 4px 0 0;
  color: var(--ink-faint);
  font-size: 12px;
  line-height: 1.5;
}
.inline {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
}
.opt-detail {
  margin-left: 10px;
  color: var(--ink-faint);
  font-size: 12px;
}
.who {
  display: flex;
  flex-direction: column;
  line-height: 1.35;
}
.who small {
  color: var(--ink-faint);
  font-size: 12px;
}
</style>

<style>
.subtask-tip {
  max-width: 420px;
  white-space: pre-wrap;
}
/* 下拉框挂在 body 上，scoped 样式管不到。技能说明往往很长：下拉和输入框一样宽，说明一行放不下就省略，鼠标停上去看全文 */
.expert-opt-popper .el-select-dropdown__item {
  display: flex;
  align-items: center;
  gap: 10px;
  max-width: 100%;
}
.expert-opt-popper .el-select-dropdown__item > span:first-child {
  flex: none;
  max-width: 45%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.expert-opt-popper .opt-detail {
  flex: 1;
  min-width: 0;
  margin-left: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
