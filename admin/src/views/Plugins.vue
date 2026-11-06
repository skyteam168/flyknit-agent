<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Upload } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { PluginConnector, PluginItem, PluginPreview } from '@/api/types'

/**
 * 插件：一包技能 + 专家 + 连接器 + 快捷指令 + 工具规则（目录结构同 Claude Code plugins），按部门一键下发。
 * 安装后内容出现在各自的页面（带「插件」标记），下发范围由服务端过滤：范围外的电脑什么都拿不到。
 * 再传同一个插件就是升级；卸载会把它带来的东西一起删掉。
 */
const items = ref<PluginItem[]>([])
const departments = ref<string[]>([])
const loading = ref(false)

// 安装对话框
const dialog = ref(false)
const source = ref<'file' | 'url'>('file')
const file = ref<File | null>(null)
const url = ref('')
const preview = ref<PluginPreview | null>(null)
/** 同名冲突时管理员填的新标识：插件里的服务名 → 新标识 */
const renames = ref<Record<string, string>>({})
const scope = ref<string[]>([])
const required = ref(false)
const busy = ref(false)

const partLabels: Record<string, string> = {
  skills: '技能',
  experts: '专家',
  connectors: '连接器',
  commands: '快捷指令',
  rules: '工具规则',
}

async function load() {
  loading.value = true
  try {
    const r = await api.plugins()
    items.value = r.items
    departments.value = r.departments
  } finally {
    loading.value = false
  }
}
onMounted(load)

function openInstall() {
  source.value = 'file'
  file.value = null
  url.value = ''
  preview.value = null
  scope.value = []
  required.value = false
  dialog.value = true
}

async function pick(e: Event) {
  const f = (e.target as HTMLInputElement).files?.[0]
  if (!f) return
  file.value = f
  preview.value = null
  renames.value = {}
  await refreshPreview()
}

async function refreshPreview() {
  if (!file.value) return
  busy.value = true
  try {
    preview.value = await api.previewPlugin(file.value, renames.value)
  } finally {
    busy.value = false
  }
}

function useSuggested(c: PluginConnector) {
  renames.value = { ...renames.value, [c.server]: c.suggested_rename }
  void refreshPreview()
}

const RESULT: Record<PluginConnector['result'], { label: string; type: 'success' | 'info' | 'warning' | 'danger' }> = {
  NEW: { label: '新建', type: 'success' },
  UPDATE: { label: '更新', type: 'info' },
  REUSE_EXISTING: { label: '复用现有', type: 'info' },
  RENAMED: { label: '改名导入', type: 'warning' },
  CONFLICT: { label: '冲突', type: 'danger' },
}

const canInstall = computed(() =>
  !busy.value && (source.value === 'url' ? url.value.trim().length > 0 : !!preview.value && !preview.value.conflicts.length),
)

async function install() {
  busy.value = true
  try {
    const p = source.value === 'url'
      ? await api.importPlugin(url.value.trim(), scope.value, required.value)
      : await api.uploadPlugin(file.value!, scope.value, required.value, renames.value)
    ElMessage.success(`已安装 ${p.name || p.id}${p.version ? ' v' + p.version : ''}`)
    dialog.value = false
    await load()
  } finally {
    busy.value = false
  }
}

async function toggle(p: PluginItem, key: 'enabled' | 'required', value: boolean) {
  const r = await api.updatePlugin(p.id, { [key]: value })
  Object.assign(p, r)
}

async function saveScope(p: PluginItem, value: string[]) {
  const r = await api.updatePlugin(p.id, { departments: value })
  Object.assign(p, r)
  ElMessage.success('下发范围已更新')
}

async function remove(p: PluginItem) {
  const c = p.counts
  await ElMessageBox.confirm(
    `卸载「${p.name || p.id}」会一起删掉它带来的 ${c.skills} 个技能、${c.experts} 位专家、${c.connectors} 个连接器、` +
      `${c.commands} 个快捷指令和 ${c.rules} 条工具规则。员工端下次拉配置时自动收起来。`,
    '卸载插件',
    { type: 'warning', confirmButtonText: '卸载', cancelButtonText: '取消' },
  )
  await api.deletePlugin(p.id)
  ElMessage.success('已卸载')
  await load()
}

const parts = (c: Record<string, number>) => Object.entries(partLabels).filter(([k]) => c[k]).map(([k, label]) => `${c[k]} 个${label}`)
</script>

<template>
  <div v-loading="loading" class="page">
    <div class="head">
      <div>
        <h2>插件</h2>
        <p class="sub">
          把一个部门要用的技能、专家、连接器、快捷指令、工具规则打成一包，按部门一键下发。目录结构和 Claude Code 插件一样，
          社区插件可以直接导入。插件里要在员工电脑上执行脚本的 hooks 不支持，请改写成 <code>rules.json</code> 工具规则。
        </p>
      </div>
      <el-button type="primary" :icon="Upload" @click="openInstall">安装插件</el-button>
    </div>

    <el-empty v-if="!items.length && !loading" description="还没有插件" />

    <el-card v-for="p in items" :key="p.id" class="plugin" shadow="never">
      <div class="row">
        <div class="title">
          <strong>{{ p.name || p.id }}</strong>
          <el-tag v-if="p.version" size="small" type="info">v{{ p.version }}</el-tag>
          <el-tag v-if="!p.enabled" size="small" type="warning">已停用</el-tag>
          <span class="id">{{ p.id }}</span>
        </div>
        <div class="ops">
          <el-switch :model-value="p.enabled" active-text="启用" @change="(v: string | number | boolean) => toggle(p, 'enabled', !!v)" />
          <el-switch :model-value="p.required" active-text="技能必装" @change="(v: string | number | boolean) => toggle(p, 'required', !!v)" />
          <el-button link type="danger" @click="remove(p)">卸载</el-button>
        </div>
      </div>
      <p v-if="p.description" class="desc">{{ p.description }}</p>
      <div class="parts">
        <el-tag v-for="x in parts(p.counts as unknown as Record<string, number>)" :key="x" size="small" effect="plain">{{ x }}</el-tag>
      </div>
      <div class="scope">
        <span>下发范围</span>
        <el-select
          :model-value="p.departments"
          multiple
          filterable
          allow-create
          default-first-option
          placeholder="全公司"
          class="depts"
          @change="(v: string[]) => saveScope(p, v)"
        >
          <el-option v-for="d in departments" :key="d" :label="d" :value="d" />
        </el-select>
        <small>不选 = 全公司。部门取「设备列表」里填的所属部门</small>
      </div>
      <el-collapse class="detail">
        <el-collapse-item title="包里有什么">
          <ul>
            <li v-for="s in p.manifest.skills" :key="'s' + s.name">技能 <code>{{ s.name }}</code>：{{ s.description }}</li>
            <li v-for="e in p.manifest.experts" :key="'e' + e.id">专家 <code>{{ e.id }}</code>：{{ e.name }}</li>
            <li v-for="c in p.manifest.connectors" :key="'c' + (c.server || c.id)">
              连接器 <code>{{ c.vendor_id || c.id }}</code>（{{ c.transport }}）<template v-if="c.result">· {{ RESULT[c.result]?.label ?? c.result }}</template>
              <template v-if="c.source_mcp_config"> · 来源 {{ c.source_mcp_config }}</template>
            </li>
            <li v-for="c in p.manifest.commands" :key="'k' + c.name">快捷指令 <code>/{{ c.name }}</code> {{ c.title }}</li>
            <li v-for="(r, i) in p.manifest.rules" :key="'r' + i">工具规则「{{ r.name }}」：{{ r.action }} {{ r.tools }}</li>
          </ul>
          <el-alert v-for="w in p.manifest.warnings" :key="w" :title="w" type="warning" :closable="false" show-icon class="warn" />
          <small class="meta">来源：{{ p.origin }} · 安装人：{{ p.installed_by }}</small>
        </el-collapse-item>
      </el-collapse>
    </el-card>

    <el-dialog v-model="dialog" title="安装插件" width="620px">
      <el-radio-group v-model="source" class="src">
        <el-radio-button value="file">上传 zip</el-radio-button>
        <el-radio-button value="url">从链接导入</el-radio-button>
      </el-radio-group>

      <template v-if="source === 'file'">
        <input type="file" accept=".zip" @change="pick" />
        <div v-if="preview" class="preview">
          <p>
            <strong>{{ preview.name }}</strong> <span v-if="preview.version">v{{ preview.version }}</span>
            <el-tag v-if="preview.upgrade_from !== null" size="small" type="success">升级（当前 v{{ preview.upgrade_from || '?' }}）</el-tag>
          </p>
          <p class="desc">{{ preview.description }}</p>
          <div class="parts">
            <el-tag v-for="x in parts(preview.counts as unknown as Record<string, number>)" :key="x" size="small" effect="plain">{{ x }}</el-tag>
          </div>
          <el-alert v-if="preview.conflicts.length" type="error" :closable="false" show-icon
            :title="`这些东西已经存在、不是这个插件装的：${preview.conflicts.join('、')}。先改名或删掉再装`" />
          <el-alert v-for="w in preview.manifest.warnings" :key="w" :title="w" type="warning" :closable="false" show-icon class="warn" />
          <div v-for="c in preview.manifest.connectors" :key="c.server" class="conn">
            <div class="conn-head">
              <el-tag size="small" :type="RESULT[c.result].type">{{ RESULT[c.result].label }}</el-tag>
              <strong>{{ c.server }}</strong>
              <span class="muted">→ 连接器 <code>{{ c.vendor_id }}</code>（工具名 mcp__{{ c.vendor_id }}__*）</span>
            </div>
            <div class="muted">
              {{ c.transport === 'stdio' ? `${c.command} ${c.args.join(' ')}` : c.url }}
              · 来源 {{ c.source_manifest }}<template v-if="c.source_mcp_config !== c.source_manifest"> → {{ c.source_mcp_config }}</template>
            </div>
            <div v-if="c.reason" class="muted">{{ c.reason }}</div>
            <div v-if="c.result === 'CONFLICT' || c.result === 'RENAMED'" class="rename">
              <el-input v-model="renames[c.server]" size="small" placeholder="新的连接器标识" class="rename-input" @change="refreshPreview" />
              <el-button v-if="c.suggested_rename" size="small" link type="primary" @click="useSuggested(c)">用建议的 {{ c.suggested_rename }}</el-button>
            </div>
          </div>
        </div>
      </template>
      <template v-else>
        <el-input v-model="url" placeholder="GitHub 链接（可以指到仓库里的某个插件目录）或 zip 下载链接" />
        <small class="hint">一个仓库里有多个插件（marketplace）时，链接要指到具体的插件目录，例如 …/tree/main/plugins/qc</small>
      </template>

      <el-form label-width="90px" class="opts">
        <el-form-item label="下发范围">
          <el-select v-model="scope" multiple filterable allow-create default-first-option placeholder="全公司" class="depts">
            <el-option v-for="d in departments" :key="d" :label="d" :value="d" />
          </el-select>
        </el-form-item>
        <el-form-item label="技能必装">
          <el-switch v-model="required" />
          <small class="hint">范围内的电脑自动安装插件里的技能</small>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialog = false">取消</el-button>
        <el-button type="primary" :loading="busy" :disabled="!canInstall" @click="install">安装</el-button>
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
.sub,
.desc,
.hint,
.meta {
  margin: 0;
  color: var(--el-text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}
.row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.title {
  display: flex;
  align-items: center;
  gap: 8px;
}
.id {
  color: var(--el-text-color-placeholder);
  font-family: monospace;
  font-size: 12px;
}
.ops {
  display: flex;
  align-items: center;
  gap: 16px;
}
.desc {
  margin-top: 6px;
}
.parts {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}
.scope {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 10px;
  font-size: 13px;
}
.scope small {
  color: var(--el-text-color-secondary);
}
.depts {
  width: 320px;
}
.detail {
  margin-top: 8px;
}
.detail ul {
  margin: 0;
  padding-left: 18px;
  line-height: 1.8;
}
.warn {
  margin-top: 6px;
}
.src {
  margin-bottom: 12px;
}
.preview {
  margin-top: 12px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.preview p {
  margin: 0;
}
.conn {
  padding: 8px 10px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
}
.conn-head {
  display: flex;
  align-items: center;
  gap: 8px;
}
.muted {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.rename {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
}
.rename-input {
  width: 220px;
}
.opts {
  margin-top: 16px;
}
.hint {
  display: block;
  margin-left: 8px;
}
</style>
