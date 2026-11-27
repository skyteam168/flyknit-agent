<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Department, Device, HookCallRow, HookEvent, HookIn, HookItem } from '@/api/types'
import { dateTime } from '@/utils/format'

/**
 * 钩子（docs/hooks.md）：AI 调用工具前、工具做完、一轮任务结束时，服务端把这件事发到公司自己的 HTTP 服务。
 * 调用前的回答只能收紧：allow 照旧、ask 必须员工确认、deny 拦下。地址和密钥只在服务端。
 * 看：管理员都能看；建、改、删、测试、总开关：超级管理员。
 */
const EVENTS: { value: HookEvent; label: string; hint: string }[] = [
  { value: 'pre_tool', label: '调用工具前', hint: '可以拦下或要求确认（回答 allow / ask / deny）' },
  { value: 'post_tool', label: '工具做完', hint: '只通知：工具名、参数、成功与否、结果摘要' },
  { value: 'stop', label: '任务结束', hint: '只通知：一轮办事任务的结果摘要' },
]
const ON_ERROR = [
  { value: 'ask', label: '要员工确认（推荐）' },
  { value: 'deny', label: '拦下' },
  { value: 'allow', label: '照旧执行' },
]
const DECISION: Record<string, { text: string; type: 'success' | 'warning' | 'danger' | 'info' }> = {
  allow: { text: '放行', type: 'success' },
  ask: { text: '要确认', type: 'warning' },
  deny: { text: '拦下', type: 'danger' },
  sent: { text: '已通知', type: 'info' },
}

const loading = ref(false)
const saving = ref(false)
const enabled = ref(false)
const items = ref<HookItem[]>([])
const calls = ref<HookCallRow[]>([])
const devices = ref<Device[]>([])
const departments = ref<Department[]>([])
const dialog = ref(false)
const editing = ref<HookItem | null>(null)

type Form = Omit<HookIn, 'secret'> & { secret: string; changeSecret: boolean }
const blank = (): Form => ({
  name: '', events: ['pre_tool'], tools: '*', match: '', url: '', secret: '', changeSecret: true, timeout_ms: 3000,
  on_error: 'ask', skip_tls_verify: false, enabled: true, scope_departments: [], scope_devices: [],
})
const form = ref<Form>(blank())

async function load() {
  loading.value = true
  try {
    const [r, d, deps] = await Promise.all([api.hooks(), api.devices(), api.departments()])
    enabled.value = r.enabled
    items.value = r.items
    calls.value = r.calls
    devices.value = d
    departments.value = deps
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function toggleMaster(v: string | number | boolean) {
  enabled.value = (await api.setHooksSwitch(!!v)).enabled
  ElMessage.success(enabled.value ? '钩子已打开，员工端下一轮任务起生效' : '钩子已关闭')
}

function openCreate() {
  editing.value = null
  form.value = blank()
  dialog.value = true
}

function openEdit(h: HookItem) {
  editing.value = h
  form.value = {
    name: h.name, events: [...h.events], tools: h.tools, match: h.match, url: h.url, secret: '', changeSecret: !h.has_secret,
    timeout_ms: h.timeout_ms, on_error: h.on_error, skip_tls_verify: h.skip_tls_verify, enabled: h.enabled,
    scope_departments: [...h.scope_departments], scope_devices: [...h.scope_devices],
  }
  dialog.value = true
}

const problems = computed(() => {
  const f = form.value
  const out: string[] = []
  if (!f.name.trim()) out.push('请填写名称')
  if (!f.events.length) out.push('至少选一个事件')
  if (!/^https?:\/\/\S+/.test(f.url.trim())) out.push('地址要以 http:// 或 https:// 开头')
  if (f.match.trim()) {
    try {
      new RegExp(f.match)
    } catch {
      out.push('参数匹配不是合法的正则表达式')
    }
  }
  return out
})

function payload(): Partial<HookIn> {
  const { secret, changeSecret, ...rest } = form.value
  return changeSecret ? { ...rest, secret } : rest
}

async function save() {
  if (problems.value.length) {
    ElMessage.error(problems.value[0])
    return
  }
  saving.value = true
  try {
    if (editing.value === null) {
      await api.createHook(payload() as HookIn)
      ElMessage.success(enabled.value ? '已创建，员工端下一轮任务起生效' : '已创建（总开关还关着，打开后才生效）')
    } else {
      await api.updateHook(editing.value.id, payload())
      ElMessage.success('已保存')
    }
    dialog.value = false
    await load()
  } finally {
    saving.value = false
  }
}

async function patch(h: HookItem, body: Partial<HookIn>) {
  Object.assign(h, await api.updateHook(h.id, body))
}

async function test(h: HookItem) {
  const r = await api.testHook(h.id)
  if (r.ok) {
    ElMessage.success(`钩子回应正常（${r.latency_ms} 毫秒）：${DECISION[r.decision ?? '']?.text ?? r.decision}${r.reason ? '，' + r.reason : ''}`)
  } else {
    ElMessage.error(`钩子没能正常回应：${r.error}`)
  }
}

async function remove(h: HookItem) {
  await ElMessageBox.confirm(`删除钩子「${h.name}」？员工端下一轮任务起不再调用。`, '删除钩子', { type: 'warning' })
  await api.deleteHook(h.id)
  await load()
}

const deviceLabel = (d: Device) => `${d.owner || d.user_name || ''} · ${d.machine_name}${d.department ? '（' + d.department + '）' : ''}`
const deviceName = (id: number | null) => (id ? devices.value.find((d) => d.id === id)?.owner || devices.value.find((d) => d.id === id)?.machine_name || `电脑 ${id}` : '')
const scopeText = (h: HookItem) => {
  const names = [...h.scope_departments, ...h.scope_devices.map((id) => deviceName(id))]
  return names.length ? names.join('、') : '全公司'
}
const eventText = (h: HookItem) => h.events.map((e) => EVENTS.find((x) => x.value === e)?.label ?? e).join('、')
</script>

<template>
  <div v-loading="loading" class="page">
    <div class="head">
      <div>
        <h2>钩子</h2>
        <p class="sub">
          AI 调用工具前、工具做完、一轮任务结束时，服务端把这件事（工具名、参数、电脑、员工）发到公司自己的 HTTP 服务，
          用来做额外的审核、登记或者对接内部系统。<b>只能收紧</b>：调用前的钩子可以回答 <code>allow</code>（照旧）、
          <code>ask</code>（必须员工确认，不能记成以后自动）或 <code>deny</code>（拦下），本来要确认或被拦的操作不会因为钩子放行。
          地址和签名密钥只存在服务端，员工电脑拿不到。
        </p>
      </div>
      <div class="ops">
        <el-switch :model-value="enabled" active-text="总开关" @change="toggleMaster" />
        <el-button type="primary" :icon="Plus" @click="openCreate">新增钩子</el-button>
      </div>
    </div>
    <el-alert v-if="!enabled" type="info" :closable="false" show-icon title="总开关关着：员工端不会调用任何钩子。" />

    <el-table :data="items" empty-text="还没有钩子">
      <el-table-column label="钩子" min-width="200">
        <template #default="{ row }">
          <strong>{{ row.name }}</strong>
          <div class="muted">{{ eventText(row as HookItem) }}</div>
        </template>
      </el-table-column>
      <el-table-column label="工具 / 参数" min-width="170" show-overflow-tooltip>
        <template #default="{ row }">
          <code>{{ row.tools || '*' }}</code>
          <div v-if="row.match" class="muted">参数含 <code>{{ row.match }}</code></div>
        </template>
      </el-table-column>
      <el-table-column label="地址" min-width="200" show-overflow-tooltip>
        <template #default="{ row }">
          {{ row.url }}
          <el-tag v-if="row.has_secret" size="small" class="tag">签名</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="范围" min-width="120" show-overflow-tooltip>
        <template #default="{ row }">{{ scopeText(row as HookItem) }}</template>
      </el-table-column>
      <el-table-column label="启用" width="70" align="center">
        <template #default="{ row }">
          <el-switch :model-value="row.enabled" size="small" @change="(v: string | number | boolean) => patch(row as HookItem, { enabled: !!v })" />
        </template>
      </el-table-column>
      <el-table-column label="" width="160">
        <template #default="{ row }">
          <el-button link type="primary" @click="test(row as HookItem)">测试</el-button>
          <el-button link @click="openEdit(row as HookItem)">编辑</el-button>
          <el-button link type="danger" @click="remove(row as HookItem)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>

    <h3>最近的调用</h3>
    <el-table :data="calls" empty-text="还没有调用过" size="small">
      <el-table-column label="时间" width="160"><template #default="{ row }">{{ dateTime(row.created_at) }}</template></el-table-column>
      <el-table-column label="钩子" width="150" prop="hook_name" show-overflow-tooltip />
      <el-table-column label="事件 / 工具" width="200" show-overflow-tooltip>
        <template #default="{ row }">{{ EVENTS.find((e) => e.value === row.event)?.label ?? row.event }}<span v-if="row.tool_name"> · {{ row.tool_name }}</span></template>
      </el-table-column>
      <el-table-column label="电脑" width="120" show-overflow-tooltip><template #default="{ row }">{{ deviceName(row.device_id) }}</template></el-table-column>
      <el-table-column label="结果" width="90">
        <template #default="{ row }">
          <el-tag v-if="DECISION[row.decision]" :type="DECISION[row.decision].type" size="small">{{ DECISION[row.decision].text }}</el-tag>
          <span v-else class="muted">—</span>
        </template>
      </el-table-column>
      <el-table-column label="说明" min-width="200" show-overflow-tooltip>
        <template #default="{ row }">
          <span v-if="row.error" class="warn">{{ row.error }}</span>
          <span v-else>{{ row.reason }}</span>
          <span class="muted"> {{ row.latency_ms }} 毫秒</span>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialog" :title="editing === null ? '新增钩子' : '编辑钩子'" width="700px">
      <el-form label-width="110px">
        <el-form-item label="名称"><el-input v-model="form.name" maxlength="100" placeholder="ERP 写操作审核" /></el-form-item>
        <el-form-item label="事件">
          <el-checkbox-group v-model="form.events">
            <el-checkbox v-for="e in EVENTS" :key="e.value" :value="e.value">{{ e.label }}</el-checkbox>
          </el-checkbox-group>
          <small class="muted">{{ EVENTS.filter((e) => form.events.includes(e.value)).map((e) => e.label + '：' + e.hint).join('；') }}</small>
        </el-form-item>
        <el-form-item label="工具">
          <el-input v-model="form.tools" placeholder="* 表示所有工具；例如 mcp__erp__*, run_shell, write_file" />
          <small class="muted">「任务结束」不看这一项。</small>
        </el-form-item>
        <el-form-item label="参数匹配">
          <el-input v-model="form.match" placeholder="可选：参数里包含这些内容时才调用（正则），例如 delete|update|amount" />
        </el-form-item>
        <el-form-item label="地址">
          <el-input v-model="form.url" placeholder="https://hooks.公司内网/flyknit" />
        </el-form-item>
        <el-form-item label="签名密钥">
          <template v-if="editing && editing.has_secret && !form.changeSecret">
            <span class="muted">已设置</span>
            <el-button link type="primary" @click="form.changeSecret = true">更换 / 去掉</el-button>
          </template>
          <template v-else>
            <el-input v-model="form.secret" type="password" show-password maxlength="200" placeholder="可选：填了会带 X-Flyknit-Signature: sha256=HMAC(密钥, 请求体)" />
          </template>
        </el-form-item>
        <el-form-item label="超时">
          <el-input-number v-model="form.timeout_ms" :min="500" :max="10000" :step="500" /> <span class="muted gap-left">毫秒（AI 每次调用匹配的工具都要等它）</span>
        </el-form-item>
        <el-form-item label="没回应时">
          <el-select v-model="form.on_error" style="width: 200px">
            <el-option v-for="o in ON_ERROR" :key="o.value" :value="o.value" :label="o.label" />
          </el-select>
          <small class="muted">只对「调用工具前」有效：钩子超时、出错、回答看不懂时怎么处理</small>
        </el-form-item>
        <el-form-item label="使用范围">
          <el-select v-model="form.scope_departments" multiple filterable placeholder="部门（都不选 = 全公司）" class="wide">
            <el-option v-for="d in departments" :key="d.id" :value="d.name" :label="d.name" />
          </el-select>
          <el-select v-model="form.scope_devices" multiple filterable placeholder="个别电脑" class="wide gap">
            <el-option v-for="d in devices" :key="d.id" :value="d.id" :label="deviceLabel(d)" />
          </el-select>
        </el-form-item>
        <el-form-item label="内网自签证书">
          <el-switch v-model="form.skip_tls_verify" />
          <small class="muted gap-left">只在钩子是内网自签 HTTPS 时打开</small>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialog = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
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
.gap-left {
  margin-left: 8px;
}
</style>
