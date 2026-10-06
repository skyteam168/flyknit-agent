<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { Lock, Paperclip, Refresh, Search, View } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { AuditLog, ChatAccess, ChatConversation, ChatRecord, Device } from '@/api/types'
import { auth } from '@/store/auth'
import { DECISION_LABELS, dateTime, num, parseTime, relative, sceneLabel, short } from '@/utils/format'

const tab = ref<'list' | 'access'>('list')
const canRead = computed(() => !!auth.user?.can_read_chats && !auth.user?.must_change_password)

// ---------- 会话列表（只有元数据） ----------
const PAGE_SIZE = 50
const rows = ref<ChatConversation[]>([])
const devices = ref<Device[]>([])
const loading = ref(false)
const days = ref(7)
const deviceId = ref<number | null>(null)
const scene = ref('')
const keyword = ref('')
const page = ref(1)
const hasMore = ref(false)

async function load() {
  loading.value = true
  try {
    const list = await api.chats({
      days: days.value,
      device_id: deviceId.value,
      scene: scene.value || undefined,
      keyword: keyword.value.trim() || undefined,
      limit: PAGE_SIZE + 1,
      offset: (page.value - 1) * PAGE_SIZE,
    })
    hasMore.value = list.length > PAGE_SIZE
    rows.value = list.slice(0, PAGE_SIZE)
  } finally {
    loading.value = false
  }
}
function search() {
  page.value = 1
  void load()
}
function go(delta: number) {
  page.value += delta
  void load()
}

// ---------- 查看正文 ----------
type Item =
  | { kind: 'user'; at: string; text: string; attachments: number }
  | { kind: 'assistant'; at: string; text: string; model: string; tokens: number }
  | { kind: 'tool'; at: string; log: AuditLog }

const drawer = ref(false)
const current = ref<ChatConversation | null>(null)
const reading = ref(false)
const items = ref<Item[]>([])

async function open(c: ChatConversation) {
  await ElMessageBox.confirm(
    `将打开「${c.user_name || c.machine_name}」的这段对话正文。查看行为会以你的账号（${auth.username}）记录在案，可在「查看留痕」中查到。`,
    '查看聊天正文',
    { type: 'warning', confirmButtonText: '确认查看', cancelButtonText: '取消' },
  )
  current.value = c
  items.value = []
  drawer.value = true
  reading.value = true
  try {
    const [records, tools] = await Promise.all([
      api.readChat(c.conversation_id),
      api.audit({ conversation_id: c.conversation_id, limit: 500, offset: 0 }).catch(() => [] as AuditLog[]),
    ])
    items.value = timeline(records, tools)
  } finally {
    reading.value = false
  }
}

function timeline(records: ChatRecord[], tools: AuditLog[]): Item[] {
  const list: Item[] = []
  for (const r of records) {
    if (r.user_content || r.attachments) list.push({ kind: 'user', at: r.created_at, text: r.user_content, attachments: r.attachments })
    if (r.assistant_content) {
      list.push({ kind: 'assistant', at: r.created_at, text: r.assistant_content, model: r.model, tokens: r.prompt_tokens + r.completion_tokens })
    }
  }
  for (const t of tools) list.push({ kind: 'tool', at: t.occurred_at, log: t })
  // 同一时刻：用户消息在前，工具调用居中，回答在后
  const order = { user: 0, tool: 1, assistant: 2 }
  const ms = (s: string) => parseTime(s)?.getTime() ?? 0
  return list.sort((a, b) => ms(a.at) - ms(b.at) || order[a.kind] - order[b.kind])
}

const decisionOf = (d: string) => DECISION_LABELS[d] ?? { label: d || '—', type: 'info' as const }

// ---------- 查看留痕 ----------
const access = ref<ChatAccess[]>([])
const accessLoading = ref(false)
async function loadAccess() {
  accessLoading.value = true
  try {
    access.value = await api.chatAccess()
  } finally {
    accessLoading.value = false
  }
}
function onTab(name: string | number) {
  if (name === 'access' && !access.value.length) void loadAccess()
}

onMounted(async () => {
  void load()
  devices.value = await api.devices()
})
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>聊天记录</h1>
        <p>员工与 AI 的对话在服务端网关落库，员工删除本机记录也不影响这里。只存用户的提问和 AI 的回答，保留 90 天后自动清理。</p>
      </div>
    </div>

    <el-alert type="info" :closable="false" show-icon class="notice">
      <template #title>
        列表只显示谁、哪台电脑、什么时候、聊了几轮，不含正文。查看正文需要「可查看聊天记录」权限，且每次打开都会记名留痕。
        <template v-if="!canRead"><br /><strong>你的账号没有查看正文的权限</strong>，如需开通请联系其他管理员在「管理员账号」中授予。</template>
      </template>
    </el-alert>

    <div class="panel">
      <el-tabs v-model="tab" @tab-change="onTab">
        <el-tab-pane label="会话" name="list">
          <div class="toolbar filters">
            <el-input
              v-model="keyword"
              :prefix-icon="Search"
              placeholder="电脑名或 Windows 用户名"
              clearable
              style="width: 220px"
              @keyup.enter="search"
              @clear="search"
            />
            <el-select v-model="deviceId" placeholder="全部设备" clearable filterable style="width: 200px" @change="search">
              <el-option v-for="d in devices" :key="d.id" :value="d.id" :label="`${d.machine_name}${d.user_name ? ` · ${d.user_name}` : ''}`" />
            </el-select>
            <el-select v-model="scene" placeholder="全部模式" clearable style="width: 120px" @change="search">
              <el-option value="chat" label="对话" />
              <el-option value="agent" label="任务" />
              <el-option value="translate" label="翻译" />
              <el-option value="vision" label="识图" />
            </el-select>
            <el-select v-model="days" style="width: 110px" @change="search">
              <el-option :value="1" label="今天" />
              <el-option :value="7" label="近 7 天" />
              <el-option :value="30" label="近 30 天" />
              <el-option :value="90" label="近 90 天" />
            </el-select>
            <el-button :icon="Refresh" :loading="loading" @click="load" />
          </div>

          <el-table :data="rows" v-loading="loading" empty-text="这段时间没有聊天记录">
            <el-table-column label="用户 / 电脑" min-width="180">
              <template #default="{ row }">
                <div class="who">
                  <strong>{{ row.user_name || '—' }}</strong>
                  <small>{{ row.machine_name }}</small>
                </div>
              </template>
            </el-table-column>
            <el-table-column label="模式" width="90">
              <template #default="{ row }">
                <el-tag size="small" effect="plain" :type="row.scene === 'agent' ? 'primary' : 'info'">{{ sceneLabel(row.scene) }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column label="模型" min-width="150" show-overflow-tooltip>
              <template #default="{ row }"><span class="muted">{{ row.model || '—' }}</span></template>
            </el-table-column>
            <el-table-column label="提问" width="80" align="right">
              <template #default="{ row }">{{ num(row.turns) }} 次</template>
            </el-table-column>
            <el-table-column label="Token" width="100" align="right">
              <template #default="{ row }">{{ short(row.tokens) }}</template>
            </el-table-column>
            <el-table-column label="开始" width="150">
              <template #default="{ row }">{{ dateTime(row.started_at) }}</template>
            </el-table-column>
            <el-table-column label="最后活跃" width="110">
              <template #default="{ row }">
                <el-tooltip :content="dateTime(row.last_at)" placement="top"><span>{{ relative(row.last_at) }}</span></el-tooltip>
              </template>
            </el-table-column>
            <el-table-column width="100" align="right">
              <template #default="{ row }">
                <el-button v-if="canRead" link type="primary" :icon="View" @click="open(row as ChatConversation)">查看正文</el-button>
                <el-tooltip v-else content="没有查看正文的权限" placement="left">
                  <el-button link disabled :icon="Lock">查看正文</el-button>
                </el-tooltip>
              </template>
            </el-table-column>
          </el-table>
          <div class="pager">
            <span class="muted">第 {{ page }} 页 · 每页 {{ PAGE_SIZE }} 段对话</span>
            <el-button-group>
              <el-button :disabled="page <= 1 || loading" @click="go(-1)">上一页</el-button>
              <el-button :disabled="!hasMore || loading" @click="go(1)">下一页</el-button>
            </el-button-group>
          </div>
        </el-tab-pane>

        <el-tab-pane label="查看留痕" name="access">
          <div class="toolbar filters">
            <span class="hint">谁在什么时候打开了谁的对话正文。只看列表不记录。保留 365 天。</span>
            <el-button :icon="Refresh" :loading="accessLoading" style="margin-left: auto" @click="loadAccess" />
          </div>
          <el-table :data="access" v-loading="accessLoading" empty-text="还没有人查看过聊天正文">
            <el-table-column label="时间" width="160">
              <template #default="{ row }">{{ dateTime(row.created_at) }}</template>
            </el-table-column>
            <el-table-column label="管理员" width="140" prop="username" />
            <el-table-column label="操作" width="110">
              <template #default="{ row }">
                <el-tag size="small" type="warning" effect="plain">{{ row.action === 'read_chat' ? '查看正文' : row.action }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column label="对象" min-width="200">
              <template #default="{ row }">{{ row.detail }}</template>
            </el-table-column>
            <el-table-column label="会话编号" min-width="200">
              <template #default="{ row }"><code class="muted">{{ row.target }}</code></template>
            </el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-drawer v-model="drawer" size="min(760px, 92vw)" :with-header="false" destroy-on-close>
      <div v-if="current" class="viewer">
        <header class="viewer-head">
          <div>
            <h3>{{ current.user_name || current.machine_name }} 的对话</h3>
            <p class="muted">
              {{ current.machine_name }} · {{ sceneLabel(current.scene) }} · {{ current.turns }} 次提问 · {{ short(current.tokens) }} token ·
              {{ dateTime(current.started_at) }} 开始
            </p>
          </div>
          <el-tag type="warning" effect="plain" size="small">本次查看已留痕</el-tag>
        </header>

        <div v-loading="reading" class="messages">
          <template v-for="(it, i) in items" :key="i">
            <div v-if="it.kind === 'user'" class="msg user">
              <div class="bubble">
                <div v-if="it.text" class="text">{{ it.text }}</div>
                <div v-if="it.attachments" class="attach">
                  <el-icon><Paperclip /></el-icon>{{ it.attachments }} 个附件（内容未存档）
                </div>
              </div>
              <time>{{ dateTime(it.at) }}</time>
            </div>

            <div v-else-if="it.kind === 'tool'" class="tool">
              <code>{{ it.log.tool_name }}</code>
              <el-tag size="small" :type="decisionOf(it.log.decision).type">{{ decisionOf(it.log.decision).label }}</el-tag>
              <span class="args mono" :title="it.log.arguments">{{ it.log.summary || it.log.arguments }}</span>
            </div>

            <div v-else class="msg assistant">
              <div class="bubble">
                <div class="text">{{ it.text }}</div>
              </div>
              <time>{{ it.model }} · {{ short(it.tokens) }} token · {{ dateTime(it.at) }}</time>
            </div>
          </template>
          <el-empty v-if="!reading && !items.length" description="这段对话没有可显示的内容" :image-size="80" />
        </div>
      </div>
    </el-drawer>
  </div>
</template>

<style scoped>
.notice {
  border-radius: var(--r-md);
}
.filters {
  margin-bottom: 12px;
}
.who {
  display: flex;
  flex-direction: column;
  line-height: 1.35;
}
.who strong {
  font-weight: 500;
}
.who small {
  color: var(--ink-faint);
  font-size: 12px;
}
.pager {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 14px;
}

.viewer {
  display: flex;
  flex-direction: column;
  height: 100%;
}
.viewer-head {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  justify-content: space-between;
  padding-bottom: 14px;
  border-bottom: 1px solid var(--line);
}
.viewer-head h3 {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
}
.viewer-head p {
  margin: 2px 0 0;
  font-size: 12.5px;
}
.messages {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 14px;
  min-height: 200px;
  padding: 18px 2px;
  overflow-y: auto;
}
.msg {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-width: 86%;
}
.msg.user {
  align-self: flex-end;
  align-items: flex-end;
}
.msg.assistant {
  align-self: flex-start;
}
.bubble {
  padding: 10px 14px;
  border-radius: 14px;
  line-height: 1.65;
}
.user .bubble {
  border-bottom-right-radius: 4px;
  background: var(--indigo);
  color: #fff;
}
html.dark .user .bubble {
  color: #10132e;
}
.assistant .bubble {
  border: 1px solid var(--line);
  border-bottom-left-radius: 4px;
  background: var(--cloth-sunk);
}
.text {
  white-space: pre-wrap;
  word-break: break-word;
}
.attach {
  display: flex;
  gap: 4px;
  align-items: center;
  margin-top: 4px;
  font-size: 12px;
  opacity: 0.85;
}
.msg time {
  color: var(--ink-faint);
  font-size: 11.5px;
}
.tool {
  display: flex;
  gap: 8px;
  align-items: center;
  align-self: flex-start;
  max-width: 92%;
  padding: 6px 10px;
  border: 1px dashed var(--line-strong);
  border-radius: var(--r-md);
  font-size: 12px;
}
.tool .args {
  overflow: hidden;
  color: var(--ink-soft);
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
