<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { UploadRequestOptions } from 'element-plus'
import { CircleCheckFilled, Delete, DocumentCopy, Edit, Plus, Refresh, Search, Connection, WarningFilled } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { McpDraft, McpField, McpTestResult, McpVendor, McpVendorIn } from '@/api/types'
import { dateTime } from '@/utils/format'

/**
 * MCP 连接器：引入厂商的 MCP 服务，员工在客户端「连接器」里点一下就能用。
 * 和「技能库」是两回事——技能是装到员工电脑上的说明书，连接器是在线服务（或员工电脑上的一个进程）。
 */
const vendors = ref<McpVendor[]>([])
const loading = ref(false)
const keyword = ref('')

async function load() {
  loading.value = true
  try {
    vendors.value = await api.mcpVendors()
  } finally {
    loading.value = false
  }
}
onMounted(load)

const filtered = computed(() => {
  const q = keyword.value.trim().toLowerCase()
  return q
    ? vendors.value.filter((v) => `${v.id} ${v.name} ${v.description} ${v.publisher} ${v.category}`.toLowerCase().includes(q))
    : vendors.value
})
const enabledCount = computed(() => vendors.value.filter((v) => v.enabled).length)

const transportLabel: Record<string, string> = { http: '在线服务', sse: '在线服务（旧版 SSE）', stdio: '员工电脑上的进程' }
const authLabel: Record<string, string> = { none: '不用登录', fields: '填写密钥', oauth: '浏览器授权登录' }

function initial(v: { name: string }) {
  return [...v.name.trim()][0] ?? '?'
}
function hue(id: string) {
  let h = 0
  for (const ch of id) h = (h * 31 + ch.charCodeAt(0)) % 360
  return h
}

// ---------- 编辑 ----------
interface FieldRow extends McpField {
  /** 管理员统一预填的值；留空 = 不改原来的 */
  preset: string
  /** 原来有没有预填（显示掩码用） */
  masked: string
  clear: boolean
}

const editing = ref(false)
const isNew = ref(true)
const saving = ref(false)
const form = reactive({
  id: '',
  name: '',
  description: '',
  detail: '',
  icon: '',
  publisher: '',
  category: '',
  homepage: '',
  transport: 'http' as McpVendor['transport'],
  url: '',
  command: '',
  argsText: '',
  envText: '',
  headersText: '',
  auth: 'none' as McpVendor['auth'],
  fields: [] as FieldRow[],
  oauthClientId: '',
  oauthScopes: '',
  examplesText: '',
  timeoutSec: 60,
  sort_order: 0,
  enabled: true,
})

const toLines = (d: Record<string, string>, sep: string) =>
  Object.entries(d)
    .map(([k, v]) => `${k}${sep}${v}`)
    .join('\n')
function fromLines(text: string, sep: string): Record<string, string> {
  const out: Record<string, string> = {}
  for (const raw of text.split('\n')) {
    const line = raw.trim()
    if (!line) continue
    const i = line.indexOf(sep)
    if (i <= 0) throw new Error(`这一行少了「${sep.trim()}」：${line}`)
    out[line.slice(0, i).trim()] = line.slice(i + sep.length).trim()
  }
  return out
}

function blankField(key = ''): FieldRow {
  return { key, label: '', secret: true, required: true, placeholder: '', help: '', preset: '', masked: '', clear: false }
}

function fill(v: Partial<McpVendor> & { preset?: Record<string, string> }) {
  form.id = v.id ?? ''
  form.name = v.name ?? ''
  form.description = v.description ?? ''
  form.detail = v.detail ?? ''
  form.icon = v.icon ?? ''
  form.publisher = v.publisher ?? ''
  form.category = v.category ?? ''
  form.homepage = v.homepage ?? ''
  form.transport = v.transport ?? 'http'
  form.url = v.url ?? ''
  form.command = v.command ?? ''
  form.argsText = (v.args ?? []).join('\n')
  form.envText = toLines(v.env ?? {}, '=')
  form.headersText = toLines(v.headers ?? {}, ': ')
  form.auth = v.auth ?? 'none'
  form.fields = (v.fields ?? []).map((f) => ({
    ...blankField(),
    ...f,
    preset: v.preset?.[f.key] ?? '',
    masked: v.preset_masked?.[f.key] ?? '',
  }))
  form.oauthClientId = v.oauth?.client_id ?? ''
  form.oauthScopes = v.oauth?.scopes ?? ''
  form.examplesText = (v.examples ?? []).join('\n')
  form.timeoutSec = Math.round((v.timeout_ms ?? 60000) / 1000)
  form.sort_order = v.sort_order ?? 0
  form.enabled = v.enabled ?? true
}

function openNew() {
  isNew.value = true
  fill({})
  editing.value = true
}
function openEdit(v: McpVendor) {
  isNew.value = false
  fill(v)
  editing.value = true
}

function onIcon(opts: UploadRequestOptions) {
  const file = opts.file
  if (file.size > 200 * 1024) {
    ElMessage.error('图标请控制在 200KB 以内')
    return Promise.resolve()
  }
  return new Promise<void>((resolve) => {
    const reader = new FileReader()
    reader.onload = () => {
      form.icon = String(reader.result)
      resolve()
    }
    reader.readAsDataURL(file)
  })
}

/** 配置里用到但还没定义的 ${KEY}，一键补成填写项 */
const undefinedKeys = computed(() => {
  const text = [form.url, form.command, form.argsText, form.envText, form.headersText].join('\n')
  const used = new Set<string>()
  for (const m of text.matchAll(/\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-[^}]*)?\}/g)) {
    if (!m[0].includes(':-')) used.add(m[1])
  }
  const defined = new Set(form.fields.map((f) => f.key))
  return [...used].filter((k) => !defined.has(k))
})
function addUndefined() {
  for (const k of undefinedKeys.value) form.fields.push({ ...blankField(k), label: k, secret: /key|token|secret|password/i.test(k) })
  if (form.auth === 'none') form.auth = 'fields'
}

function payload(): McpVendorIn {
  const preset: Record<string, string> = {}
  for (const f of form.fields) {
    if (f.clear) preset[f.key] = ''
    else if (f.preset) preset[f.key] = f.preset
  }
  const oauth: Record<string, string> = {}
  if (form.oauthClientId.trim()) oauth.client_id = form.oauthClientId.trim()
  if (form.oauthScopes.trim()) oauth.scopes = form.oauthScopes.trim()
  return {
    id: form.id.trim(),
    name: form.name.trim(),
    description: form.description.trim(),
    detail: form.detail,
    icon: form.icon.trim(),
    publisher: form.publisher.trim(),
    category: form.category.trim(),
    homepage: form.homepage.trim(),
    transport: form.transport,
    url: form.transport === 'stdio' ? '' : form.url.trim(),
    command: form.transport === 'stdio' ? form.command.trim() : '',
    args: form.transport === 'stdio' ? form.argsText.split('\n').map((a) => a.trim()).filter(Boolean) : [],
    env: form.transport === 'stdio' ? fromLines(form.envText, '=') : {},
    headers: form.transport === 'stdio' ? {} : fromLines(form.headersText, ':'),
    auth: form.auth,
    fields: form.fields.map((f) => ({
      key: f.key.trim(),
      label: f.label.trim() || f.key.trim(),
      secret: f.secret,
      required: f.required,
      placeholder: f.placeholder,
      help: f.help,
    })),
    preset: Object.keys(preset).length ? preset : undefined,
    oauth,
    examples: form.examplesText.split('\n').map((e) => e.trim()).filter(Boolean),
    timeout_ms: Math.max(5, Math.min(600, form.timeoutSec || 60)) * 1000,
    sort_order: form.sort_order,
    enabled: form.enabled,
  }
}

async function save() {
  let body: McpVendorIn
  try {
    body = payload()
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
    return
  }
  saving.value = true
  try {
    if (isNew.value) await api.createMcpVendor(body)
    else await api.updateMcpVendor(body.id, body)
    ElMessage.success('已保存，员工端会马上看到')
    editing.value = false
    await load()
  } finally {
    saving.value = false
  }
}

async function toggle(v: McpVendor) {
  try {
    await api.patchMcpVendor(v.id, { enabled: v.enabled })
  } catch {
    v.enabled = !v.enabled
  }
}

async function remove(v: McpVendor) {
  await ElMessageBox.confirm(`删除连接器「${v.name}」？已经连上的员工电脑会自动断开。`, '删除连接器', {
    type: 'warning',
    confirmButtonText: '删除',
  })
  await api.deleteMcpVendor(v.id)
  ElMessage.success('已删除')
  await load()
}

// ---------- 从配置导入 ----------
const importing = ref(false)
const importText = ref('')
const parsing = ref(false)
const drafts = ref<McpDraft[]>([])

function openImport() {
  importText.value = ''
  drafts.value = []
  importing.value = true
}
async function parse() {
  parsing.value = true
  try {
    drafts.value = await api.parseMcpConfig(importText.value)
    if (drafts.value.length === 1) useDraft(drafts.value[0])
  } finally {
    parsing.value = false
  }
}
function useDraft(d: McpDraft) {
  const existing = vendors.value.find((v) => v.id === d.id)
  isNew.value = !existing
  fill({ ...(existing ?? {}), ...d, name: existing?.name ?? d.name })
  importing.value = false
  editing.value = true
  if (existing) ElMessage.warning(`标识 ${d.id} 已经存在，保存会覆盖它的连接配置`)
}

// ---------- 测试连接 ----------
const testing = ref<McpVendor | null>(null)
const testValues = reactive<Record<string, string>>({})
const testResult = ref<McpTestResult | null>(null)
const testRunning = ref(false)
const testInputs = computed(() => testing.value?.fields.filter((f) => !testing.value?.preset_masked[f.key]) ?? [])

function openTest(v: McpVendor) {
  testing.value = v
  testResult.value = null
  for (const k of Object.keys(testValues)) delete testValues[k]
  if (testInputs.value.length === 0) void runTest()
}
async function runTest() {
  if (!testing.value) return
  testRunning.value = true
  try {
    testResult.value = await api.testMcpVendor(testing.value.id, { ...testValues })
  } finally {
    testRunning.value = false
  }
}
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>MCP 连接器</h1>
        <p>引入各家厂商的 MCP 服务（腾讯文档、企业微信、飞书……）。上架后员工在客户端「连接器」里点「连接」，AI 就能直接操作这些服务。和技能库互不影响。</p>
      </div>
      <div class="toolbar">
        <el-input v-model="keyword" :prefix-icon="Search" placeholder="搜索连接器" clearable style="width: 200px" />
        <el-button :icon="Refresh" :loading="loading" @click="load" />
        <el-button :icon="DocumentCopy" @click="openImport">从配置导入</el-button>
        <el-button type="primary" :icon="Plus" @click="openNew">新增连接器</el-button>
      </div>
    </div>

    <div class="panel">
      <div class="panel-title">
        <h3>全部连接器 <small>共 {{ vendors.length }} 个，其中 {{ enabledCount }} 个已上架</small></h3>
      </div>
      <el-empty v-if="!loading && vendors.length === 0" description="还没有连接器。可以直接粘贴厂商文档里给的 MCP 配置，点「从配置导入」。" />
      <div v-loading="loading" class="grid">
        <div v-for="v in filtered" :key="v.id" class="card" :class="{ off: !v.enabled }">
          <div class="top">
            <img v-if="v.icon" :src="v.icon" alt="" class="logo" />
            <span v-else class="logo letter" :style="{ background: `hsl(${hue(v.id)} 55% 48%)` }">{{ initial(v) }}</span>
            <div class="title">
              <strong>{{ v.name }}</strong>
              <code>{{ v.id }}</code>
            </div>
            <el-switch v-model="v.enabled" size="small" @change="toggle(v)" />
          </div>
          <p class="desc">{{ v.description || '没有介绍' }}</p>
          <div class="tags">
            <el-tag size="small" effect="plain">{{ transportLabel[v.transport] }}</el-tag>
            <el-tag size="small" effect="plain" :type="v.auth === 'none' ? 'info' : 'warning'">{{ authLabel[v.auth] }}</el-tag>
            <el-tag v-if="Object.keys(v.preset_masked).length" size="small" effect="plain" type="success">已预填密钥</el-tag>
            <el-tag v-if="v.examples.length" size="small" effect="plain" type="info">{{ v.examples.length }} 个示例</el-tag>
          </div>
          <div class="target">{{ v.transport === 'stdio' ? `${v.command} ${v.args.join(' ')}` : v.url }}</div>
          <div class="foot">
            <span class="muted">{{ dateTime(v.updated_at) }}</span>
            <span class="actions">
              <el-button link type="primary" :icon="Connection" @click="openTest(v)">测试</el-button>
              <el-button link type="primary" :icon="Edit" @click="openEdit(v)">编辑</el-button>
              <el-button link type="danger" :icon="Delete" @click="remove(v)">删除</el-button>
            </span>
          </div>
        </div>
      </div>
    </div>

    <!-- 编辑 -->
    <el-dialog v-model="editing" :title="isNew ? '新增连接器' : `编辑 · ${form.name}`" width="min(960px, 94vw)" top="4vh" :close-on-click-modal="false">
      <el-form label-width="96px" class="form">
        <h4>展示</h4>
        <el-form-item label="名称" required><el-input v-model="form.name" placeholder="腾讯文档" maxlength="100" /></el-form-item>
        <el-form-item label="标识" required>
          <el-input v-model="form.id" :disabled="!isNew" placeholder="tencent-docs" maxlength="40" />
          <p class="hint">小写字母、数字、- 和 _。员工端的工具名会是 mcp__{{ form.id || '标识' }}__工具名，创建后不能改。</p>
        </el-form-item>
        <el-form-item label="图标">
          <div class="icon-row">
            <img v-if="form.icon" :src="form.icon" alt="" class="logo big" />
            <span v-else class="logo big letter" :style="{ background: `hsl(${hue(form.id || form.name)} 55% 48%)` }">{{ initial(form) }}</span>
            <el-input v-model="form.icon" placeholder="图片链接，或者上传一张" clearable />
            <el-upload :http-request="onIcon" :show-file-list="false" accept="image/*"><el-button>上传</el-button></el-upload>
          </div>
        </el-form-item>
        <el-form-item label="一句话介绍"><el-input v-model="form.description" type="textarea" :rows="2" maxlength="300" show-word-limit placeholder="卡片上显示，一两句话说清楚能做什么" /></el-form-item>
        <el-form-item label="详细介绍"><el-input v-model="form.detail" type="textarea" :rows="4" placeholder="点开卡片后显示，支持 Markdown：能做什么、怎么申请密钥、注意事项" /></el-form-item>
        <el-form-item label="提供方">
          <div class="inline">
            <el-input v-model="form.publisher" placeholder="腾讯" />
            <el-input v-model="form.category" placeholder="分类，例如 文档 / 办公" />
            <el-input v-model="form.homepage" placeholder="官网链接" />
          </div>
        </el-form-item>
        <el-form-item label="示例问题"><el-input v-model="form.examplesText" type="textarea" :rows="3" placeholder="一行一个，显示在「试试这样用」里，员工点一下就放进输入框" /></el-form-item>

        <h4>连接方式</h4>
        <el-form-item label="类型">
          <el-radio-group v-model="form.transport">
            <el-radio-button value="http">在线服务（HTTP）</el-radio-button>
            <el-radio-button value="sse">旧版 SSE</el-radio-button>
            <el-radio-button value="stdio">员工电脑上的进程</el-radio-button>
          </el-radio-group>
          <p class="hint">
            <template v-if="form.transport === 'http'">厂商给的是一个网址就选这个。对方还是旧版协议时员工端会自动改用 SSE。</template>
            <template v-else-if="form.transport === 'sse'">只有厂商明确说是 SSE（地址常以 /sse 结尾）时才选。</template>
            <template v-else>在每台员工电脑上启动一个程序（例如 npx、uvx），需要员工电脑上已经装好对应的运行环境。</template>
          </p>
        </el-form-item>
        <template v-if="form.transport !== 'stdio'">
          <el-form-item label="服务地址" required><el-input v-model="form.url" placeholder="https://docs.qq.com/openapi/mcp" /></el-form-item>
          <el-form-item label="请求头"><el-input v-model="form.headersText" type="textarea" :rows="2" placeholder="一行一个，例如&#10;Authorization: Bearer ${API_KEY}" /></el-form-item>
        </template>
        <template v-else>
          <el-form-item label="启动命令" required><el-input v-model="form.command" placeholder="npx" /></el-form-item>
          <el-form-item label="参数"><el-input v-model="form.argsText" type="textarea" :rows="2" placeholder="一行一个，例如&#10;-y&#10;@modelcontextprotocol/server-filesystem" /></el-form-item>
          <el-form-item label="环境变量"><el-input v-model="form.envText" type="textarea" :rows="2" placeholder="一行一个，例如&#10;API_TOKEN=${API_TOKEN}" /></el-form-item>
        </template>
        <el-form-item label="超时（秒）"><el-input-number v-model="form.timeoutSec" :min="5" :max="600" /></el-form-item>

        <h4>登录方式</h4>
        <el-form-item label="方式">
          <el-radio-group v-model="form.auth">
            <el-radio-button value="none">不用登录</el-radio-button>
            <el-radio-button value="fields">填写密钥</el-radio-button>
            <el-radio-button value="oauth" :disabled="form.transport === 'stdio'">浏览器授权登录</el-radio-button>
          </el-radio-group>
          <p class="hint">
            <template v-if="form.auth === 'fields'">在上面的地址、请求头、环境变量里写 ${KEY}，下面定义每个 KEY 由谁填：管理员预填（全公司共用一个）或员工自己填。</template>
            <template v-else-if="form.auth === 'oauth'">员工点「连接」会打开浏览器登录自己的账号。对方支持自动注册时 client_id 可以不填。</template>
            <template v-else>对方不需要任何凭证。</template>
          </p>
        </el-form-item>
        <el-alert v-if="undefinedKeys.length" type="warning" :closable="false" show-icon class="alert">
          配置里用到了 {{ undefinedKeys.map((k) => '${' + k + '}').join('、') }}，还没有定义。
          <el-button link type="primary" @click="addUndefined">一键添加</el-button>
        </el-alert>
        <template v-if="form.auth !== 'none' || form.fields.length">
          <div v-for="(f, i) in form.fields" :key="i" class="field-row">
            <el-input v-model="f.key" placeholder="变量名 API_KEY" class="w-key" />
            <el-input v-model="f.label" placeholder="员工看到的名字" class="w-label" />
            <el-input
              v-model="f.preset"
              :type="f.secret ? 'password' : 'text'"
              show-password
              :placeholder="f.masked ? `已预填 ${f.masked}，留空不改` : '管理员预填（可选）'"
              :disabled="f.clear"
              class="w-preset"
            />
            <div class="checks">
              <el-checkbox v-model="f.secret">密钥</el-checkbox>
              <el-checkbox v-model="f.required">必填</el-checkbox>
              <el-checkbox v-if="f.masked" v-model="f.clear">清掉预填</el-checkbox>
              <el-button link type="danger" :icon="Delete" class="del" @click="form.fields.splice(i, 1)">删除</el-button>
            </div>
            <el-input v-model="f.help" placeholder="怎么拿到这个值（显示在输入框下面）" class="w-help" />
          </div>
          <el-button :icon="Plus" @click="form.fields.push(blankField())">添加填写项</el-button>
        </template>
        <template v-if="form.auth === 'oauth'">
          <el-form-item label="client_id" class="mt"><el-input v-model="form.oauthClientId" placeholder="对方不支持自动注册时填写" /></el-form-item>
          <el-form-item label="scope"><el-input v-model="form.oauthScopes" placeholder="空格分隔，不填用对方默认的" /></el-form-item>
        </template>

        <h4>其他</h4>
        <el-form-item label="排序"><el-input-number v-model="form.sort_order" /> <span class="hint inline-hint">小的排前面</span></el-form-item>
        <el-form-item label="上架"><el-switch v-model="form.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="editing = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>

    <!-- 从配置导入 -->
    <el-dialog v-model="importing" title="从配置导入" width="640px">
      <el-input
        v-model="importText"
        type="textarea"
        :rows="10"
        class="mono"
        placeholder='把厂商文档里给的配置粘贴进来，例如&#10;{&#10;  "mcpServers": {&#10;    "tencent-docs": {&#10;      "type": "http",&#10;      "url": "https://docs.qq.com/openapi/mcp",&#10;      "headers": { "Authorization": "Bearer 你的密钥" }&#10;    }&#10;  }&#10;}&#10;&#10;也认 claude mcp add --transport http 名字 地址 这样的一行命令。'
      />
      <p class="hint">写死在配置里的密钥会自动挪成「管理员预填」，加密保存，配置里只留 ${API_KEY}。</p>
      <div v-if="drafts.length > 1" class="drafts">
        <p>识别出 {{ drafts.length }} 个服务，选一个继续：</p>
        <el-button v-for="d in drafts" :key="d.id" @click="useDraft(d)">{{ d.name }} <small class="muted">（{{ d.transport }}）</small></el-button>
      </div>
      <template #footer>
        <el-button @click="importing = false">取消</el-button>
        <el-button type="primary" :loading="parsing" @click="parse">识别</el-button>
      </template>
    </el-dialog>

    <!-- 测试连接 -->
    <el-dialog :model-value="testing !== null" :title="`测试连接 · ${testing?.name ?? ''}`" width="560px" @close="testing = null">
      <template v-if="testing">
        <el-form v-if="testInputs.length" label-width="96px">
          <el-form-item v-for="f in testInputs" :key="f.key" :label="f.label || f.key">
            <el-input v-model="testValues[f.key]" :type="f.secret ? 'password' : 'text'" show-password placeholder="只用于这次测试，不保存" />
          </el-form-item>
        </el-form>
        <div v-if="testRunning" v-loading="true" class="test-wait" />
        <template v-else-if="testResult">
          <el-alert v-if="testResult.ok" type="success" :closable="false" show-icon>
            <template #title>
              <el-icon><CircleCheckFilled /></el-icon>
              连上了{{ testResult.server_name ? `：${testResult.server_name} ${testResult.server_version}` : '' }}，共 {{ testResult.tools.length }} 个工具
            </template>
          </el-alert>
          <el-alert v-else type="error" :closable="false" show-icon>
            <template #title><el-icon><WarningFilled /></el-icon> {{ testResult.error }}</template>
          </el-alert>
          <ul v-if="testResult.tools.length" class="tool-list">
            <li v-for="t in testResult.tools" :key="t.name"><code>{{ t.name }}</code><small>{{ t.description }}</small></li>
          </ul>
        </template>
      </template>
      <template #footer>
        <el-button @click="testing = null">关闭</el-button>
        <el-button type="primary" :loading="testRunning" @click="runTest">测试</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
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
.logo {
  flex: none;
  width: 36px;
  height: 36px;
  padding: 4px;
  border: 1px solid var(--line);
  border-radius: 9px;
  background: #fff;
  object-fit: contain;
}
.logo.big {
  width: 44px;
  height: 44px;
}
.letter {
  display: grid;
  place-items: center;
  padding: 0;
  border: 0;
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
  color: var(--ink);
  font-weight: 600;
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
.target {
  overflow: hidden;
  color: var(--ink-faint);
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
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
}
.foot .actions .el-button + .el-button {
  margin-left: 8px;
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
.hint {
  flex-basis: 100%;
  margin: 4px 0 0;
  color: var(--ink-faint);
  font-size: 12px;
  line-height: 1.5;
}
.inline-hint {
  margin-left: 10px;
}
.icon-row,
.inline {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
}
.field-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) minmax(0, 1.4fr) auto;
  align-items: center;
  gap: 8px 10px;
  margin: 0 0 12px 96px;
  padding: 12px;
  border: 1px dashed var(--line);
  border-radius: 8px;
}
.field-row .w-help {
  grid-column: 1 / 4;
}
.field-row .checks {
  grid-column: 4;
  grid-row: 1 / 3;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
}
.field-row .checks .el-checkbox {
  height: 24px;
  margin-right: 0;
}
.field-row .del {
  align-self: flex-end;
}
.field-row + .el-button,
.form > .el-button {
  margin-left: 96px;
}
.alert {
  margin: 0 0 12px 96px;
  width: auto;
}
.mt {
  margin-top: 14px;
}
.mono :deep(textarea) {
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12.5px;
}
.drafts {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 12px;
}
.drafts p {
  width: 100%;
  margin: 0;
}
.test-wait {
  height: 80px;
}
.tool-list {
  max-height: 280px;
  margin: 12px 0 0;
  padding: 0;
  overflow-y: auto;
  list-style: none;
}
.tool-list li {
  display: flex;
  flex-direction: column;
  padding: 6px 0;
  border-bottom: 1px solid var(--line);
  font-size: 13px;
}
.tool-list small {
  color: var(--ink-faint);
}
</style>
