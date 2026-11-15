<script setup lang="ts">
/**
 * 消息通知（docs/notices.md）：IT 写通知、发到员工端左下角的消息中心。
 * - 正文用富文本编辑器写，图片、视频传到本服务器；保存时服务端按白名单清洗（脚本、外部图片都留不下）；
 * - 使用范围：部门 + 单台电脑，都不选 = 全公司；
 * - 可以定时发布、设过期时间；「重要」的在员工端置顶，打开程序时没读过的直接弹出来；
 * - 发布、撤回、修改已发布的通知，员工端几秒内更新；能看到谁读了。
 */
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Department, Device, Notice, NoticeIn, NoticeKind, NoticeReader } from '@/api/types'
import RichEditor from '@/components/RichEditor.vue'

const KINDS: { value: NoticeKind; label: string; hint: string; tag: '' | 'success' | 'warning' | 'danger' | 'info' | 'primary' }[] = [
  { value: 'upgrade', label: '系统升级', hint: '新版本、升级时间和影响', tag: 'primary' },
  { value: 'feature', label: '功能介绍', hint: '新功能怎么用，配图或短视频', tag: 'success' },
  { value: 'maintenance', label: '停机维护', hint: '哪个系统、什么时候、影响什么（建议设过期时间）', tag: 'warning' },
  { value: 'security', label: '安全提醒', hint: '钓鱼邮件、资料外发、密码安全等', tag: 'danger' },
  { value: 'policy', label: '制度通知', hint: '公司制度、流程变更', tag: 'info' },
  { value: 'event', label: '培训活动', hint: '培训、考试、活动报名', tag: 'primary' },
  { value: 'tip', label: '使用技巧', hint: 'FlyknitBuddy 的小技巧', tag: 'success' },
  { value: 'general', label: '一般通知', hint: '其他', tag: 'info' },
]
const kindOf = (k: string) => KINDS.find((x) => x.value === k) ?? KINDS[KINDS.length - 1]

/** 各类型的正文模板：选了类型、正文还空着时可以一键套用 */
const TEMPLATES: Record<NoticeKind, string> = {
  upgrade: '<h2>升级时间</h2><p>11 月 20 日（周四）22:00–22:30</p><h2>这次更新</h2><ul><li>……</li><li>……</li></ul><h2>升级期间</h2><p>办事任务会暂停，升级后自动恢复。<b>请不要关机。</b></p>',
  feature: '<h2>能做什么</h2><p>……</p><h2>怎么用</h2><ol><li>……</li><li>……</li></ol><p>（可以插入截图或短视频）</p>',
  maintenance: '<table><thead><tr><th>系统</th><th>时间</th><th>影响</th></tr></thead><tbody><tr><td>MES</td><td>周六 02:00–04:00</td><td>查询、报工暂停</td></tr></tbody></table><p>维护期间请避开相关任务。有问题联系 IT。</p>',
  security: '<p>近期发现……</p><h3>请大家</h3><ol><li>……</li><li>……</li></ol><blockquote>如有疑问，先问 IT，不要自行处理。</blockquote>',
  policy: '<h2>变更内容</h2><p>……</p><h2>生效时间</h2><p>……</p><h2>适用范围</h2><p>……</p>',
  event: '<h2>时间地点</h2><p>……</p><h2>内容</h2><p>……</p><h2>报名方式</h2><p>……</p>',
  tip: '<p>……</p><p>（可以插入一张截图）</p>',
  general: '<p>……</p>',
}

const list = ref<Notice[]>([])
const departments = ref<Department[]>([])
const devices = ref<Device[]>([])
const loading = ref(false)
const tab = ref<'all' | 'draft' | 'published' | 'withdrawn'>('all')

async function load() {
  loading.value = true
  try {
    const [n, d, v] = await Promise.all([api.notices(), api.departments(), api.devices()])
    list.value = n
    departments.value = d
    devices.value = v
  } finally {
    loading.value = false
  }
}
onMounted(load)

const shown = computed(() => list.value.filter((n) => tab.value === 'all' || n.status === tab.value))

function statusOf(n: Notice): { text: string; type: '' | 'success' | 'warning' | 'danger' | 'info' } {
  if (n.status === 'draft') return { text: '草稿', type: 'info' }
  if (n.status === 'withdrawn') return { text: '已撤回', type: 'danger' }
  if (n.expired) return { text: '已过期', type: 'info' }
  if (n.scheduled) return { text: '定时发布', type: 'warning' }
  return { text: '已发布', type: 'success' }
}
const fmt = (s: string | null) => (s ? new Date(s).toLocaleString('zh-CN', { hour12: false }) : '—')
function scopeText(n: Notice) {
  const parts: string[] = []
  if (n.scope_departments.length) parts.push(n.scope_departments.join('、'))
  if (n.scope_devices.length) parts.push(`${n.scope_devices.length} 台电脑`)
  return parts.join(' + ') || '全公司'
}
function deviceLabel(d: Device) {
  const who = d.owner || d.user_name
  return `${d.machine_name}${who ? ' · ' + who : ''}${d.department ? ' · ' + d.department : ''}`
}

// ---------- 编辑 ----------
const editing = ref(false)
const editId = ref<string | null>(null)
const saving = ref(false)
const blank = (): NoticeIn => ({
  kind: 'general', title: '', summary: '', body_html: '', important: false, action_label: '', action_url: '',
  scope_departments: [], scope_devices: [], publish_at: null, expire_at: null,
})
const form = reactive<NoticeIn>(blank())

function openNew() {
  Object.assign(form, blank())
  editId.value = null
  editing.value = true
}
function openEdit(n: Notice) {
  Object.assign(form, {
    kind: n.kind, title: n.title, summary: n.summary, body_html: n.body_html, important: n.important,
    action_label: n.action_label, action_url: n.action_url, scope_departments: [...n.scope_departments],
    scope_devices: [...n.scope_devices], publish_at: n.publish_at, expire_at: n.expire_at,
  })
  editId.value = n.id
  editing.value = true
}
const bodyEmpty = computed(() => !form.body_html.replace(/<(p|br|\/p)[^>]*>|\s|&nbsp;/g, ''))
function useTemplate() {
  form.body_html = TEMPLATES[form.kind]
}

function validate(): boolean {
  if (!form.title.trim()) {
    ElMessage.warning('请填标题')
    return false
  }
  if (form.action_label && !/^https?:\/\//i.test(form.action_url)) {
    ElMessage.warning('按钮地址要以 http:// 或 https:// 开头')
    return false
  }
  return true
}

async function save(publish: boolean) {
  if (!validate()) return
  saving.value = true
  try {
    const body = { ...form, action_url: form.action_label ? form.action_url : '' }
    let saved = editId.value ? await api.updateNotice(editId.value, body) : await api.createNotice(body)
    if (publish && saved.status !== 'published') saved = await api.publishNotice(saved.id)
    editing.value = false
    ElMessage.success(
      publish || saved.status === 'published'
        ? saved.scheduled ? `已保存，${fmt(saved.publish_at)} 自动发布` : `已发布：${scopeText(saved)}的电脑几秒内收到`
        : '草稿已保存',
    )
    await load()
  } finally {
    saving.value = false
  }
}

async function publish(n: Notice) {
  await ElMessageBox.confirm(`发布「${n.title}」给 ${scopeText(n)}？${n.important ? '这是重要通知，员工打开程序时会直接弹出来。' : ''}`, '发布通知', { type: 'info' })
  await api.publishNotice(n.id)
  ElMessage.success('已发布')
  await load()
}
async function withdraw(n: Notice) {
  await ElMessageBox.confirm(`撤回后员工端不再显示「${n.title}」，已读记录保留。`, '撤回通知', { type: 'warning' })
  await api.withdrawNotice(n.id)
  ElMessage.success('已撤回')
  await load()
}
async function remove(n: Notice) {
  await ElMessageBox.confirm(`删除「${n.title}」？删除后不能恢复。`, '删除通知', { type: 'warning', confirmButtonText: '删除' })
  await api.deleteNotice(n.id)
  ElMessage.success('已删除')
  await load()
}

// ---------- 预览（按员工端的样子） ----------
const preview = ref<NoticeIn | null>(null)
function showPreview(n: NoticeIn) {
  preview.value = { ...n }
}

// ---------- 阅读情况 ----------
const readers = ref<NoticeReader[]>([])
const readersOf = ref<Notice | null>(null)
async function showReads(n: Notice) {
  readersOf.value = n
  readers.value = await api.noticeReads(n.id)
}
</script>

<template>
  <div class="page">
    <div class="head">
      <div>
        <h2>消息通知</h2>
        <p class="sub">发到员工端左下角的消息中心。正文支持文字、图片、视频、表格；按部门或电脑下发，可定时发布。</p>
      </div>
      <div class="actions">
        <el-button :icon="Refresh" @click="load">刷新</el-button>
        <el-button type="primary" :icon="Plus" @click="openNew">写通知</el-button>
      </div>
    </div>

    <el-radio-group v-model="tab" class="tabs">
      <el-radio-button value="all">全部</el-radio-button>
      <el-radio-button value="draft">草稿</el-radio-button>
      <el-radio-button value="published">已发布</el-radio-button>
      <el-radio-button value="withdrawn">已撤回</el-radio-button>
    </el-radio-group>

    <el-table v-loading="loading" :data="shown" empty-text="还没有通知，点右上角「写通知」">
      <el-table-column label="类型" width="110">
        <template #default="{ row }">
          <el-tag :type="kindOf(row.kind).tag || undefined" effect="light">{{ kindOf(row.kind).label }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="标题" min-width="260">
        <template #default="{ row }">
          <div class="title-cell">
            <el-tag v-if="row.important" type="danger" size="small" effect="dark">重要</el-tag>
            <a class="title" @click="showPreview(row as Notice)">{{ row.title }}</a>
          </div>
          <div class="muted">{{ row.author || '—' }} · 更新于 {{ fmt(row.updated_at) }}</div>
        </template>
      </el-table-column>
      <el-table-column label="范围" min-width="140">
        <template #default="{ row }">{{ scopeText(row as Notice) }}</template>
      </el-table-column>
      <el-table-column label="状态" width="170">
        <template #default="{ row }">
          <el-tag :type="statusOf(row as Notice).type || undefined">{{ statusOf(row as Notice).text }}</el-tag>
          <div class="muted">
            <template v-if="row.status === 'published'">{{ fmt(row.publish_at || row.published_at) }}</template>
            <template v-if="row.expire_at"><br />到 {{ fmt(row.expire_at) }}</template>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="已读" width="150">
        <template #default="{ row }">
          <template v-if="row.status !== 'draft'">
            <a class="reads" @click="showReads(row as Notice)">{{ row.reads }} / {{ row.audience }} 台</a>
            <el-progress :percentage="row.audience ? Math.round((row.reads / row.audience) * 100) : 0" :show-text="false" :stroke-width="5" />
          </template>
          <span v-else class="muted">—</span>
        </template>
      </el-table-column>
      <el-table-column label="操作" width="230" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row as Notice)">编辑</el-button>
          <el-button v-if="row.status !== 'published'" link type="success" @click="publish(row as Notice)">发布</el-button>
          <el-button v-else link type="warning" @click="withdraw(row as Notice)">撤回</el-button>
          <el-button link @click="showPreview(row as Notice)">预览</el-button>
          <el-button v-if="row.status !== 'published'" link type="danger" @click="remove(row as Notice)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 编辑 -->
    <el-drawer v-model="editing" :title="editId ? '编辑通知' : '写通知'" size="min(1040px, 92vw)" :close-on-click-modal="false">
      <el-form label-width="96px" label-position="right">
        <el-form-item label="类型">
          <el-radio-group v-model="form.kind" class="kinds">
            <el-radio-button v-for="k in KINDS" :key="k.value" :value="k.value">{{ k.label }}</el-radio-button>
          </el-radio-group>
          <div class="hint">
            {{ kindOf(form.kind).hint }}
            <el-button v-if="bodyEmpty" link type="primary" @click="useTemplate">套用「{{ kindOf(form.kind).label }}」模板</el-button>
          </div>
        </el-form-item>
        <el-form-item label="标题" required>
          <el-input v-model="form.title" maxlength="120" show-word-limit placeholder="一句话说清楚，比如：MES 周六 02:00–04:00 停机维护" />
        </el-form-item>
        <el-form-item label="摘要">
          <el-input v-model="form.summary" maxlength="300" show-word-limit placeholder="列表里显示的一行；不填就取正文开头" />
        </el-form-item>
        <el-form-item label="正文">
          <RichEditor v-model="form.body_html" />
        </el-form-item>
        <el-form-item label="按钮">
          <div class="row">
            <el-input v-model="form.action_label" maxlength="30" placeholder="可选，比如：立即报名" style="width: 200px" />
            <el-input v-model="form.action_url" :disabled="!form.action_label" maxlength="500" placeholder="https://…（员工点了用系统浏览器打开）" />
          </div>
        </el-form-item>
        <el-form-item label="重要">
          <el-switch v-model="form.important" />
          <span class="hint inline">置顶；员工打开程序时没读过的直接弹出来。安全提醒、停机通知建议打开</span>
        </el-form-item>
        <el-form-item label="使用部门">
          <el-select v-model="form.scope_departments" multiple filterable placeholder="不选 = 不按部门限制" style="width: 100%">
            <el-option v-for="d in departments" :key="d.id" :label="`${d.name}（${d.devices} 台）`" :value="d.name" />
          </el-select>
        </el-form-item>
        <el-form-item label="指定电脑">
          <el-select v-model="form.scope_devices" multiple filterable placeholder="可选：单独发给某几台电脑；部门和电脑都不选 = 全公司" style="width: 100%">
            <el-option v-for="d in devices" :key="d.id" :label="deviceLabel(d)" :value="d.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="发布时间">
          <div class="row">
            <el-date-picker v-model="form.publish_at" type="datetime" value-format="YYYY-MM-DDTHH:mm:ssZ" placeholder="不选 = 点发布时立即发" clearable />
            <span class="hint inline">过期时间</span>
            <el-date-picker v-model="form.expire_at" type="datetime" value-format="YYYY-MM-DDTHH:mm:ssZ" placeholder="不选 = 一直显示" clearable />
          </div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showPreview(form)">预览</el-button>
        <el-button :loading="saving" @click="save(false)">{{ editId && list.find((x) => x.id === editId)?.status === 'published' ? '保存修改' : '存草稿' }}</el-button>
        <el-button v-if="!(editId && list.find((x) => x.id === editId)?.status === 'published')" type="primary" :loading="saving" @click="save(true)">
          保存并发布
        </el-button>
      </template>
    </el-drawer>

    <!-- 预览：按员工端的样子 -->
    <el-dialog :model-value="!!preview" title="预览（员工端看到的样子）" width="min(720px, 94vw)" @close="preview = null">
      <article v-if="preview" class="notice-preview">
        <div class="meta">
          <el-tag size="small" :type="kindOf(preview.kind).tag || undefined">{{ kindOf(preview.kind).label }}</el-tag>
          <el-tag v-if="preview.important" size="small" type="danger">重要</el-tag>
        </div>
        <h1>{{ preview.title || '（没有标题）' }}</h1>
        <!-- 后台自己的预览；员工端渲染前服务端和员工端各清洗一遍 -->
        <div class="rich" v-html="preview.body_html" />
        <el-button v-if="preview.action_label" type="primary">{{ preview.action_label }}</el-button>
      </article>
    </el-dialog>

    <!-- 阅读情况 -->
    <el-dialog :model-value="!!readersOf" :title="`阅读情况 · ${readersOf?.title ?? ''}`" width="min(680px, 94vw)" @close="readersOf = null">
      <p v-if="readersOf" class="muted">范围内 {{ readersOf.audience }} 台电脑，已读 {{ readersOf.reads }} 台。</p>
      <el-table :data="readers" max-height="420" empty-text="还没有人读">
        <el-table-column prop="machine_name" label="电脑" />
        <el-table-column prop="user_name" label="账号" />
        <el-table-column prop="department" label="部门" />
        <el-table-column label="读的时间" width="180">
          <template #default="{ row }">{{ fmt(row.read_at) }}</template>
        </el-table-column>
      </el-table>
    </el-dialog>
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
}
.tabs {
  margin-bottom: 12px;
}
.title-cell {
  display: flex;
  align-items: center;
  gap: 6px;
}
.title,
.reads {
  color: var(--el-color-primary);
  cursor: pointer;
}
.title {
  font-weight: 600;
}
.kinds {
  flex-wrap: wrap;
}
.hint {
  width: 100%;
  margin-top: 4px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.hint.inline {
  width: auto;
  margin: 0 8px;
}
.row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  width: 100%;
}
.row > .el-input:last-child {
  flex: 1;
  min-width: 240px;
}
.notice-preview h1 {
  margin: 10px 0 12px;
  font-size: 20px;
}
.notice-preview .meta {
  display: flex;
  gap: 6px;
}
.rich {
  margin-bottom: 16px;
  line-height: 1.75;
}
.rich :deep(img),
.rich :deep(video) {
  display: block;
  max-width: 100%;
  margin: 10px 0;
  border-radius: 8px;
}
.rich :deep(blockquote) {
  margin: 10px 0;
  padding: 6px 12px;
  border-left: 3px solid var(--el-color-primary);
  background: var(--el-fill-color-light);
}
.rich :deep(table) {
  width: 100%;
  border-collapse: collapse;
}
.rich :deep(th),
.rich :deep(td) {
  padding: 6px 10px;
  border: 1px solid var(--el-border-color);
}
</style>
