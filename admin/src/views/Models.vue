<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete, Edit, Plus, Refresh, Lightning } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { ModelConfig, Provider, RouteRule } from '@/api/types'
import { dateTime, num, sceneLabel } from '@/utils/format'

const tab = ref<'routes' | 'models' | 'providers'>('routes')
const providers = ref<Provider[]>([])
const models = ref<ModelConfig[]>([])
const routes = ref<RouteRule[]>([])
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    ;[providers.value, models.value, routes.value] = await Promise.all([api.providers(), api.models(), api.routes()])
  } finally {
    loading.value = false
  }
}
onMounted(load)

const providerName = (id: number) => providers.value.find((p) => p.id === id)?.name ?? `#${id}`
const modelOptions = computed(() =>
  models.value.map((m) => ({ value: m.id, label: `${m.name}（${providerName(m.provider_id)}）`, disabled: !m.enabled })),
)

// ---------- 场景路由 ----------
const SCENE_HINTS: Record<string, string> = {
  chat: '普通问答。员工端「对话」模式',
  agent: '能调用工具干活的任务模式，需要模型支持工具调用',
  translate: '翻译模式',
  title: '给会话起标题，用便宜快速的模型即可',
  vision: '带图片的请求，需要模型支持识图',
  asr: '语音转文字。详细参数在「语音转文字」页配置',
}
const savingScene = ref('')
async function saveRoute(r: RouteRule) {
  savingScene.value = r.scene
  try {
    await api.setRoute(r.scene, r.model_id, r.scene === 'asr' ? null : r.fallback_model_id)
    ElMessage.success(`「${sceneLabel(r.scene)}」已保存，员工端下次拉取配置（最长 10 分钟）后生效`)
  } finally {
    savingScene.value = ''
  }
}

// ---------- 模型 ----------
const modelDialog = ref(false)
const editingModel = ref<ModelConfig | null>(null)
const modelForm = reactive({
  provider_id: 0,
  name: '',
  model: '',
  supports_tools: true,
  supports_vision: false,
  context_length: 131072,
  enabled: true,
  extra: '{}',
})
function openModel(m?: ModelConfig) {
  editingModel.value = m ?? null
  Object.assign(modelForm, {
    provider_id: m?.provider_id ?? providers.value[0]?.id ?? 0,
    name: m?.name ?? '',
    model: m?.model ?? '',
    supports_tools: m?.supports_tools ?? true,
    supports_vision: m?.supports_vision ?? false,
    context_length: m?.context_length ?? 131072,
    enabled: m?.enabled ?? true,
    extra: JSON.stringify(m?.extra_body ?? {}, null, 2),
  })
  modelDialog.value = true
}
async function saveModel() {
  let extra_body: Record<string, unknown>
  try {
    extra_body = JSON.parse(modelForm.extra || '{}')
  } catch {
    ElMessage.error('额外参数不是合法的 JSON')
    return
  }
  if (!modelForm.name.trim() || !modelForm.model.trim() || !modelForm.provider_id) {
    ElMessage.error('请填写提供方、显示名和上游模型名')
    return
  }
  const body = { ...modelForm, name: modelForm.name.trim(), model: modelForm.model.trim(), extra_body }
  delete (body as Partial<typeof body>).extra
  if (editingModel.value) await api.updateModel(editingModel.value.id, body)
  else await api.createModel(body)
  modelDialog.value = false
  ElMessage.success('已保存')
  await load()
}
async function toggleModel(m: ModelConfig) {
  await api.updateModel(m.id, { enabled: m.enabled }).catch(() => (m.enabled = !m.enabled))
}
async function removeModel(m: ModelConfig) {
  await ElMessageBox.confirm(`删除模型「${m.name}」？用到它的场景会变成未配置。`, '删除模型', { type: 'warning', confirmButtonText: '删除' })
  await api.deleteModel(m.id)
  ElMessage.success('已删除')
  await load()
}
const testing = ref<number | null>(null)
async function testModel(m: ModelConfig) {
  testing.value = m.id
  try {
    const r = await api.testModel(m.id)
    if (r.ok) ElMessage.success(`「${m.name}」连通正常，耗时 ${r.elapsed_ms} ms`)
    else ElMessage.error({ message: `「${m.name}」不通：${r.error ?? `HTTP ${r.status}`}`, duration: 6000 })
  } finally {
    testing.value = null
  }
}

// ---------- 提供方 ----------
const providerDialog = ref(false)
const editingProvider = ref<Provider | null>(null)
const providerForm = reactive({ name: '', base_url: '', api_key: '', enabled: true })
function openProvider(p?: Provider) {
  editingProvider.value = p ?? null
  Object.assign(providerForm, { name: p?.name ?? '', base_url: p?.base_url ?? '', api_key: '', enabled: p?.enabled ?? true })
  providerDialog.value = true
}
async function saveProvider() {
  if (!providerForm.name.trim() || !providerForm.base_url.trim()) {
    ElMessage.error('请填写名称和接口地址')
    return
  }
  if (editingProvider.value) {
    const body: Partial<typeof providerForm> = { name: providerForm.name, base_url: providerForm.base_url, enabled: providerForm.enabled }
    if (providerForm.api_key) body.api_key = providerForm.api_key
    await api.updateProvider(editingProvider.value.id, body)
  } else {
    await api.createProvider({ ...providerForm })
  }
  providerDialog.value = false
  ElMessage.success('已保存')
  await load()
}
async function toggleProvider(p: Provider) {
  await api.updateProvider(p.id, { enabled: p.enabled }).catch(() => (p.enabled = !p.enabled))
}
async function removeProvider(p: Provider) {
  const count = models.value.filter((m) => m.provider_id === p.id).length
  await ElMessageBox.confirm(
    `删除提供方「${p.name}」${count ? `会连同它下面的 ${count} 个模型一起删除` : ''}，确定吗？`,
    '删除提供方',
    { type: 'warning', confirmButtonText: '删除' },
  )
  await api.deleteProvider(p.id)
  ElMessage.success('已删除')
  await load()
}
const syncing = ref<number | null>(null)
async function syncModels(p: Provider) {
  syncing.value = p.id
  try {
    const r = await api.syncModels(p.id)
    ElMessage.success(
      r.added.length ? `新增 ${r.added.length} 个模型：${r.added.slice(0, 5).join('、')}${r.added.length > 5 ? ' 等' : ''}` : `没有新模型（共 ${r.total} 个，已跳过 ${r.skipped} 个带日期的快照版本）`,
    )
    await load()
  } finally {
    syncing.value = null
  }
}
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>模型路由</h1>
        <p>员工端的每种用法走哪个模型、出故障时切到哪个备用模型。密钥只存在服务端，不下发到电脑上。</p>
      </div>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
    </div>

    <div class="panel">
      <el-tabs v-model="tab">
        <el-tab-pane label="场景路由" name="routes">
          <el-table :data="routes" v-loading="loading">
            <el-table-column label="场景" width="200">
              <template #default="{ row }">
                <div class="scene">
                  <strong>{{ sceneLabel(row.scene) }}</strong>
                  <code class="muted">{{ row.scene }}</code>
                </div>
              </template>
            </el-table-column>
            <el-table-column label="说明" min-width="200">
              <template #default="{ row }"><span class="muted">{{ SCENE_HINTS[row.scene] }}</span></template>
            </el-table-column>
            <el-table-column label="主模型" min-width="220">
              <template #default="{ row }">
                <el-select v-model="row.model_id" placeholder="未配置" clearable filterable style="width: 100%">
                  <el-option v-for="o in modelOptions" :key="o.value" v-bind="o" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="备用模型" min-width="220">
              <template #default="{ row }">
                <span v-if="row.scene === 'asr'" class="muted">语音模型不通用，没有备用</span>
                <el-select v-else v-model="row.fallback_model_id" placeholder="不设置" clearable filterable style="width: 100%">
                  <el-option v-for="o in modelOptions" :key="o.value" v-bind="o" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column width="96" align="right">
              <template #default="{ row }">
                <el-button type="primary" plain size="small" :loading="savingScene === row.scene" @click="saveRoute(row as RouteRule)">保存</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane :label="`模型（${models.length}）`" name="models">
          <div class="toolbar tab-tools">
            <el-button type="primary" :icon="Plus" :disabled="!providers.length" @click="openModel()">添加模型</el-button>
            <span v-if="!providers.length" class="hint">先在「提供方」里添加一个模型提供方</span>
          </div>
          <el-table :data="models" v-loading="loading" empty-text="还没有模型。可以在「提供方」里一键同步">
            <el-table-column label="显示名" min-width="160" prop="name" />
            <el-table-column label="上游模型名" min-width="180">
              <template #default="{ row }"><code>{{ row.model }}</code></template>
            </el-table-column>
            <el-table-column label="提供方" min-width="120">
              <template #default="{ row }">{{ providerName(row.provider_id) }}</template>
            </el-table-column>
            <el-table-column label="能力" width="150">
              <template #default="{ row }">
                <el-tag v-if="row.supports_tools" size="small" type="primary" effect="plain">工具</el-tag>
                <el-tag v-if="row.supports_vision" size="small" type="success" effect="plain" style="margin-left: 4px">识图</el-tag>
              </template>
            </el-table-column>
            <el-table-column label="上下文" width="100" align="right">
              <template #default="{ row }">{{ num(Math.round(row.context_length / 1024)) }}K</template>
            </el-table-column>
            <el-table-column label="启用" width="80">
              <template #default="{ row }"><el-switch v-model="row.enabled" @change="toggleModel(row as ModelConfig)" /></template>
            </el-table-column>
            <el-table-column label="操作" width="200" align="right">
              <template #default="{ row }">
                <el-button link type="primary" :icon="Lightning" :loading="testing === row.id" @click="testModel(row as ModelConfig)">测试</el-button>
                <el-button link type="primary" :icon="Edit" @click="openModel(row as ModelConfig)">编辑</el-button>
                <el-button link type="danger" :icon="Delete" @click="removeModel(row as ModelConfig)">删除</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane :label="`提供方（${providers.length}）`" name="providers">
          <div class="toolbar tab-tools">
            <el-button type="primary" :icon="Plus" @click="openProvider()">添加提供方</el-button>
            <span class="hint">内部推理服务、阿里云百炼等，统一按 OpenAI 兼容接口访问</span>
          </div>
          <el-table :data="providers" v-loading="loading" empty-text="还没有提供方">
            <el-table-column label="名称" min-width="140" prop="name" />
            <el-table-column label="接口地址" min-width="260">
              <template #default="{ row }"><code>{{ row.base_url }}</code></template>
            </el-table-column>
            <el-table-column label="密钥" width="150">
              <template #default="{ row }"><code class="muted">{{ row.api_key_masked || '未设置' }}</code></template>
            </el-table-column>
            <el-table-column label="模型数" width="80" align="right">
              <template #default="{ row }">{{ models.filter((m) => m.provider_id === row.id).length }}</template>
            </el-table-column>
            <el-table-column label="添加时间" width="150">
              <template #default="{ row }">{{ dateTime(row.created_at) }}</template>
            </el-table-column>
            <el-table-column label="启用" width="80">
              <template #default="{ row }"><el-switch v-model="row.enabled" @change="toggleProvider(row as Provider)" /></template>
            </el-table-column>
            <el-table-column label="操作" width="230" align="right">
              <template #default="{ row }">
                <el-button link type="primary" :icon="Refresh" :loading="syncing === row.id" @click="syncModels(row as Provider)">同步模型</el-button>
                <el-button link type="primary" :icon="Edit" @click="openProvider(row as Provider)">编辑</el-button>
                <el-button link type="danger" :icon="Delete" @click="removeProvider(row as Provider)">删除</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="modelDialog" :title="editingModel ? '编辑模型' : '添加模型'" width="560px">
      <el-form :model="modelForm" label-width="110px">
        <el-form-item label="提供方" required>
          <el-select v-model="modelForm.provider_id" style="width: 100%">
            <el-option v-for="p in providers" :key="p.id" :value="p.id" :label="p.name" />
          </el-select>
        </el-form-item>
        <el-form-item label="显示名" required><el-input v-model="modelForm.name" placeholder="员工端看到的名字，如 Qwen-Plus" /></el-form-item>
        <el-form-item label="上游模型名" required><el-input v-model="modelForm.model" placeholder="调用上游时用的 model，如 qwen-plus" /></el-form-item>
        <el-form-item label="能力">
          <el-checkbox v-model="modelForm.supports_tools">支持工具调用</el-checkbox>
          <el-checkbox v-model="modelForm.supports_vision">支持识图</el-checkbox>
        </el-form-item>
        <el-form-item label="上下文长度">
          <el-input-number v-model="modelForm.context_length" :min="1024" :step="8192" style="width: 200px" />
          <span class="hint" style="margin-left: 8px">token</span>
        </el-form-item>
        <el-form-item label="启用"><el-switch v-model="modelForm.enabled" /></el-form-item>
        <el-form-item label="额外参数">
          <el-input v-model="modelForm.extra" type="textarea" :rows="4" class="mono" placeholder='透传给上游的参数，如 {"enable_thinking": false}' />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="modelDialog = false">取消</el-button>
        <el-button type="primary" @click="saveModel">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="providerDialog" :title="editingProvider ? '编辑提供方' : '添加提供方'" width="520px">
      <el-form :model="providerForm" label-width="90px">
        <el-form-item label="名称" required><el-input v-model="providerForm.name" placeholder="如 阿里云百炼" /></el-form-item>
        <el-form-item label="接口地址" required>
          <el-input v-model="providerForm.base_url" placeholder="https://dashscope.aliyuncs.com/compatible-mode/v1" />
        </el-form-item>
        <el-form-item label="密钥">
          <el-input
            v-model="providerForm.api_key"
            type="password"
            show-password
            :placeholder="editingProvider ? `留空保持不变（当前 ${editingProvider.api_key_masked || '未设置'}）` : 'sk-...'"
          />
        </el-form-item>
        <el-form-item label="启用"><el-switch v-model="providerForm.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="providerDialog = false">取消</el-button>
        <el-button type="primary" @click="saveProvider">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.scene {
  display: flex;
  flex-direction: column;
  line-height: 1.35;
}
.scene strong {
  font-weight: 500;
}
.tab-tools {
  margin-bottom: 12px;
}
</style>
