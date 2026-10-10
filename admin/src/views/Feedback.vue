<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete, Download, Refresh, Search } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { FeedbackItem, FeedbackStatus } from '@/api/types'
import { auth } from '@/store/auth'
import { dateTime, relative } from '@/utils/format'

// 员工在客户端「设置 → 意见反馈」里提交的问题：文字、截图，可选附上日志包。
// 日志里可能有对话记录，下载要「可查看聊天记录」权限，并记入访问记录。
const PAGE_SIZE = 20
const canReadLogs = computed(() => !!auth.user?.can_read_chats && !auth.user?.must_change_password)
const canDelete = computed(() => !!auth.user?.is_owner && !auth.user?.must_change_password)

const status = ref<FeedbackStatus | ''>('open')
const keyword = ref('')
const rows = ref<FeedbackItem[]>([])
const total = ref(0)
const openCount = ref(0)
const page = ref(1)
const loading = ref(false)

const current = ref<FeedbackItem | null>(null)
const note = ref('')
const saving = ref(false)
/** 当前打开的这条反馈的截图（blob 地址），关掉就释放 */
const images = ref<string[]>([])

async function load() {
  loading.value = true
  try {
    const res = await api.feedback({ status: status.value, q: keyword.value.trim(), limit: PAGE_SIZE, offset: (page.value - 1) * PAGE_SIZE })
    rows.value = res.items
    total.value = res.total
    openCount.value = res.open
  } finally {
    loading.value = false
  }
}

function search() {
  page.value = 1
  load()
}

function go(p: number) {
  page.value = p
  load()
}

function releaseImages() {
  images.value.forEach((u) => URL.revokeObjectURL(u))
  images.value = []
}

async function open(row: FeedbackItem) {
  releaseImages()
  current.value = row
  note.value = row.note
  const id = row.id
  const urls = await Promise.all(row.images.map((name) => api.feedbackImage(id, name).catch(() => '')))
  if (current.value?.id === id) images.value = urls.filter(Boolean)
  else urls.forEach((u) => u && URL.revokeObjectURL(u))
}

function close() {
  current.value = null
  releaseImages()
}

function replace(item: FeedbackItem) {
  const i = rows.value.findIndex((r) => r.id === item.id)
  if (i >= 0) rows.value[i] = item
  if (current.value?.id === item.id) current.value = item
}

async function setStatus(row: FeedbackItem, next: FeedbackStatus) {
  const saved = await api.updateFeedback(row.id, { status: next, note: current.value?.id === row.id ? note.value : undefined })
  replace(saved)
  load()
  ElMessage.success(next === 'done' ? '已标记为已处理' : '已重新打开')
}

async function saveNote() {
  if (!current.value) return
  saving.value = true
  try {
    replace(await api.updateFeedback(current.value.id, { note: note.value }))
    ElMessage.success('备注已保存')
  } finally {
    saving.value = false
  }
}

async function remove(row: FeedbackItem) {
  await ElMessageBox.confirm('删除这条反馈和它的截图、日志？删除后不能恢复。', '删除反馈', { type: 'warning', confirmButtonText: '删除' })
  await api.deleteFeedback(row.id)
  if (current.value?.id === row.id) close()
  ElMessage.success('已删除')
  load()
}

function size(bytes: number) {
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${Math.max(1, Math.round(bytes / 1024))} KB`
}

onMounted(load)
onBeforeUnmount(releaseImages)
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>意见反馈</h1>
        <p>员工在客户端「设置 → 意见反馈」里提交的问题。附带的日志可能包含对话记录，下载需要「可查看聊天记录」权限，并会留下访问记录。</p>
      </div>
      <div class="toolbar">
        <el-radio-group v-model="status" @change="search">
          <el-radio-button value="open">待处理<span v-if="openCount" class="count">{{ openCount }}</span></el-radio-button>
          <el-radio-button value="done">已处理</el-radio-button>
          <el-radio-button value="">全部</el-radio-button>
        </el-radio-group>
        <el-input v-model="keyword" placeholder="搜索内容、计算机名、账号" clearable :prefix-icon="Search" style="width: 240px"
                  @keyup.enter="search" @clear="search" />
        <el-button :icon="Refresh" :loading="loading" @click="load" />
      </div>
    </div>

    <div class="panel">
      <el-table :data="rows" v-loading="loading" row-key="id" empty-text="没有反馈" class="clickable" @row-click="open">
        <el-table-column label="提交时间" width="150">
          <template #default="{ row }">
            <el-tooltip :content="dateTime(row.created_at)"><span>{{ relative(row.created_at) }}</span></el-tooltip>
          </template>
        </el-table-column>
        <el-table-column label="员工" width="210">
          <template #default="{ row }">
            <div>{{ row.machine_name }}</div>
            <div class="muted small">{{ row.user_name }}<span v-if="row.client_version"> · v{{ row.client_version }}</span></div>
          </template>
        </el-table-column>
        <el-table-column label="问题描述" min-width="320">
          <template #default="{ row }">
            <div class="content-cell">{{ row.content || '（只上传了图片）' }}</div>
          </template>
        </el-table-column>
        <el-table-column label="附件" width="130">
          <template #default="{ row }">
            <el-tag v-if="row.images.length" size="small" type="info">{{ row.images.length }} 张图</el-tag>
            <el-tag v-if="row.logs_size" size="small" type="warning" class="gap">日志</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="150">
          <template #default="{ row }">
            <el-tag v-if="row.status === 'open'" type="danger" size="small">待处理</el-tag>
            <template v-else>
              <el-tag type="success" size="small">已处理</el-tag>
              <span class="muted small gap">{{ row.handled_by }}</span>
            </template>
          </template>
        </el-table-column>
      </el-table>
      <div class="pager">
        <span class="muted">共 {{ total }} 条</span>
        <el-pagination layout="prev, pager, next" :page-size="PAGE_SIZE" :total="total" :current-page="page" @current-change="go" />
      </div>
    </div>

    <el-drawer :model-value="!!current" size="560px" :title="current ? `反馈 #${current.id}` : ''" @close="close">
      <template v-if="current">
        <dl class="meta">
          <dt>员工</dt>
          <dd>{{ current.machine_name }} · {{ current.user_name || '—' }}</dd>
          <dt>客户端版本</dt>
          <dd>{{ current.client_version || '—' }}</dd>
          <dt>提交时间</dt>
          <dd>{{ dateTime(current.created_at) }}</dd>
          <template v-if="current.status === 'done'">
            <dt>处理</dt>
            <dd>{{ current.handled_by }} · {{ dateTime(current.handled_at) }}</dd>
          </template>
        </dl>

        <h4>问题描述</h4>
        <pre class="content">{{ current.content || '（只上传了图片）' }}</pre>

        <template v-if="current.images.length">
          <h4>截图</h4>
          <div class="shots">
            <el-image v-for="(src, i) in images" :key="src" :src="src" fit="cover" class="shot"
                      :preview-src-list="images" :initial-index="i" preview-teleported />
            <div v-for="n in current.images.length - images.length" :key="`ph${n}`" class="shot placeholder" />
          </div>
        </template>

        <template v-if="current.logs_size">
          <h4>日志</h4>
          <div class="logs">
            <span>logs.zip · {{ size(current.logs_size) }}</span>
            <el-tooltip :disabled="canReadLogs" content="日志里可能有对话记录，需要「可查看聊天记录」权限">
              <span><el-button :icon="Download" size="small" :disabled="!canReadLogs" @click="api.feedbackLogs(current.id)">下载</el-button></span>
            </el-tooltip>
          </div>
        </template>

        <h4>处理备注</h4>
        <el-input v-model="note" type="textarea" :rows="3" maxlength="1000" show-word-limit placeholder="IT 内部可见，例如：已远程重装驱动" />
        <div class="actions">
          <el-button v-if="canDelete" :icon="Delete" type="danger" text @click="remove(current)">删除</el-button>
          <span class="spacer" />
          <el-button :disabled="note === current.note" :loading="saving" @click="saveNote">保存备注</el-button>
          <el-button v-if="current.status === 'open'" type="primary" @click="setStatus(current, 'done')">标记已处理</el-button>
          <el-button v-else @click="setStatus(current, 'open')">重新打开</el-button>
        </div>
      </template>
    </el-drawer>
  </div>
</template>

<style scoped>
.count {
  margin-left: 6px;
  padding: 0 6px;
  border-radius: 9px;
  background: var(--el-color-danger);
  color: #fff;
  font-size: 11px;
}
.clickable :deep(.el-table__row) {
  cursor: pointer;
}
.content-cell {
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  white-space: pre-wrap;
}
.small {
  font-size: 12px;
}
.gap {
  margin-left: 6px;
}
.pager {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 12px;
}
.meta {
  display: grid;
  grid-template-columns: 80px 1fr;
  gap: 6px 12px;
  margin: 0 0 8px;
  font-size: 13px;
}
.meta dt {
  color: var(--el-text-color-secondary);
}
.meta dd {
  margin: 0;
}
h4 {
  margin: 18px 0 8px;
  font-size: 14px;
}
.content {
  margin: 0;
  padding: 10px 12px;
  max-height: 320px;
  overflow: auto;
  border-radius: 6px;
  background: var(--el-fill-color-light);
  font-family: inherit;
  font-size: 13px;
  line-height: 1.7;
  white-space: pre-wrap;
  word-break: break-word;
}
.shots {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 8px;
}
.shot {
  aspect-ratio: 4 / 3;
  border-radius: 6px;
  border: 1px solid var(--el-border-color-lighter);
  background: var(--el-fill-color-light);
}
.logs {
  display: flex;
  align-items: center;
  gap: 12px;
  font-size: 13px;
}
.actions {
  display: flex;
  gap: 8px;
  margin-top: 16px;
}
.spacer {
  flex: 1;
}
</style>
