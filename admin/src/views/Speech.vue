<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { CircleCheckFilled, CircleCloseFilled, Lightning, Microphone } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { AsrConfig, AsrProbe, ModelConfig, Provider } from '@/api/types'

const cfg = ref<AsrConfig | null>(null)
const models = ref<ModelConfig[]>([])
const providers = ref<Provider[]>([])
const loading = ref(false)

const form = reactive({ model_id: null as number | null, transport: 'auto', language: '', hotwords: '', vocabulary_id: '' })
const shared = ref<string[]>([])

async function load() {
  loading.value = true
  try {
    const [c, m, p] = await Promise.all([api.asr(), api.models(), api.providers()])
    cfg.value = c
    models.value = m
    providers.value = p
    Object.assign(form, {
      model_id: c.model_id,
      transport: c.transport || 'auto',
      language: c.language,
      hotwords: c.hotwords,
      vocabulary_id: c.vocabulary_id,
    })
    shared.value = [...c.shared_hotwords]
  } finally {
    loading.value = false
  }
}
onMounted(load)

const providerName = (id: number) => providers.value.find((p) => p.id === id)?.name ?? ''
const selectedModel = computed(() => models.value.find((m) => m.id === form.model_id) ?? null)

const savingModel = ref(false)
async function saveModel() {
  if (!form.model_id) {
    ElMessage.error('请选择语音模型')
    return
  }
  savingModel.value = true
  try {
    if (form.model_id !== cfg.value?.model_id) await api.setRoute('asr', form.model_id, null)
    const extra: Record<string, unknown> = { ...(selectedModel.value?.extra_body ?? {}) }
    const set = (key: string, value: string) => {
      if (value.trim()) extra[key] = value.trim()
      else delete extra[key]
    }
    extra.asr_transport = form.transport
    if (form.transport === 'auto') delete extra.asr_transport
    set('asr_language', form.language)
    set('hotwords', form.hotwords)
    set('asr_vocabulary_id', form.vocabulary_id)
    await api.updateModel(form.model_id, { extra_body: extra })
    ElMessage.success('语音配置已保存')
    await load()
  } finally {
    savingModel.value = false
  }
}

const savingWords = ref(false)
async function saveShared() {
  savingWords.value = true
  try {
    shared.value = (await api.setAsrHotwords(shared.value)).shared_hotwords
    ElMessage.success(`全厂热词已保存（${shared.value.length} 个），所有电脑即时生效`)
  } finally {
    savingWords.value = false
  }
}
function pasteWords(e: ClipboardEvent) {
  const text = e.clipboardData?.getData('text') ?? ''
  if (!/[,，\n、;；]/.test(text)) return
  e.preventDefault()
  const words = text.split(/[,，\n、;；]+/).map((w) => w.trim()).filter(Boolean)
  shared.value = Array.from(new Set([...shared.value, ...words]))
}

const probing = ref(false)
const probe = ref<AsrProbe | null>(null)
const probeSave = ref(true)
async function runProbe() {
  probing.value = true
  probe.value = null
  try {
    probe.value = await api.probeAsr(probeSave.value)
    if (probe.value.saved) await load()
  } finally {
    probing.value = false
  }
}
const TRANSPORT_LABELS: Record<string, string> = {
  inline: '同步识别（inline）',
  filetrans: '录音文件识别（filetrans）',
}
</script>

<template>
  <div class="page" v-loading="loading && !cfg">
    <div class="page-head">
      <div>
        <h1>语音转文字</h1>
        <p>员工端按住说话后，录音经服务端转给这里配置的语音模型识别。热词能显著提高机台号、工序名等专有词的识别率。</p>
      </div>
    </div>

    <div class="layout">
      <section class="panel">
        <div class="panel-title">
          <h3><el-icon><Microphone /></el-icon> 语音模型</h3>
          <el-tag v-if="cfg?.model_id" type="success" effect="plain">已配置</el-tag>
          <el-tag v-else type="warning" effect="plain">未配置</el-tag>
        </div>
        <el-form :model="form" label-width="100px">
          <el-form-item label="模型">
            <el-select v-model="form.model_id" placeholder="选择用于语音转文字的模型" filterable style="width: 100%">
              <el-option
                v-for="m in models"
                :key="m.id"
                :value="m.id"
                :label="`${m.name}（${providerName(m.provider_id)}）`"
                :disabled="!m.enabled"
              />
            </el-select>
            <p v-if="selectedModel" class="hint">上游模型名 <code>{{ selectedModel.model }}</code><template v-if="cfg?.base_url && form.model_id === cfg.model_id"> · {{ cfg.base_url }}</template></p>
          </el-form-item>
          <el-form-item label="调用方式">
            <el-radio-group v-model="form.transport">
              <el-radio-button value="auto">自动</el-radio-button>
              <el-radio-button value="inline">同步识别</el-radio-button>
              <el-radio-button value="filetrans">录音文件识别</el-radio-button>
            </el-radio-group>
            <p class="hint">「自动」会依次试两种方式并记住能用的那个；不确定就点下面的「检测」。</p>
          </el-form-item>
          <el-form-item label="识别语言">
            <el-select v-model="form.language" placeholder="自动检测" clearable style="width: 200px">
              <el-option value="zh" label="中文" />
              <el-option value="en" label="英语" />
              <el-option value="vi" label="越南语" />
              <el-option value="km" label="高棉语" />
              <el-option value="id" label="印尼语" />
            </el-select>
          </el-form-item>
          <el-form-item label="模型热词">
            <el-input v-model="form.hotwords" type="textarea" :rows="3" placeholder="只对这个模型生效的热词，逗号或换行分隔" />
          </el-form-item>
          <el-form-item label="热词表 ID">
            <el-input v-model="form.vocabulary_id" placeholder="百炼控制台创建的热词表 vocabulary_id（可选）" />
          </el-form-item>
          <el-form-item>
            <el-button type="primary" :loading="savingModel" @click="saveModel">保存</el-button>
          </el-form-item>
        </el-form>
      </section>

      <div class="side">
        <section class="panel">
          <div class="panel-title">
            <h3>全厂热词 <small>{{ shared.length }} 个</small></h3>
            <el-button type="primary" plain size="small" :loading="savingWords" @click="saveShared">保存</el-button>
          </div>
          <el-select
            v-model="shared"
            multiple
            filterable
            allow-create
            default-first-option
            :reserve-keyword="false"
            placeholder="输入后回车添加；也可以直接粘贴一串用逗号分隔的词"
            style="width: 100%"
            @paste.capture="pasteWords"
          />
          <p class="hint" style="margin-top: 8px">对所有语音模型生效，改完不用重启客户端。</p>
        </section>

        <section class="panel">
          <div class="panel-title">
            <h3>连通性检测</h3>
          </div>
          <p class="hint">发一段 1 秒的测试音频，逐个尝试调用方式，看哪个能通。</p>
          <div class="toolbar" style="margin-top: 12px">
            <el-button type="primary" :icon="Lightning" :loading="probing" :disabled="!cfg?.model_id" @click="runProbe">检测</el-button>
            <el-checkbox v-model="probeSave">检测通过后写入配置</el-checkbox>
          </div>
          <ul v-if="probe" class="notes">
            <li v-for="(note, name) in probe.notes" :key="name" :class="{ ok: probe.transport === name }">
              <el-icon :size="16">
                <CircleCheckFilled v-if="probe.transport === name" />
                <CircleCloseFilled v-else />
              </el-icon>
              <div>
                <strong>{{ TRANSPORT_LABELS[name] ?? name }}</strong>
                <small>{{ note }}</small>
              </div>
            </li>
          </ul>
          <el-alert
            v-if="probe"
            :type="probe.ok ? 'success' : 'error'"
            :closable="false"
            show-icon
            style="margin-top: 12px"
            :title="probe.ok ? (probe.saved ? `可用，已把调用方式固定为「${TRANSPORT_LABELS[probe.transport!]}」` : '可用') : '两种调用方式都不通，请检查模型名、密钥和网络'"
          />
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: minmax(0, 1.3fr) minmax(0, 1fr);
  gap: 16px;
  align-items: start;
}
@media (max-width: 1100px) {
  .layout {
    grid-template-columns: 1fr;
  }
}
.side {
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.panel-title h3 {
  display: flex;
  gap: 6px;
  align-items: center;
}
.hint {
  margin-top: 4px;
}
.notes {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 14px 0 0;
  padding: 0;
  list-style: none;
}
.notes li {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  padding: 10px 12px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
  color: var(--red);
}
.notes li.ok {
  color: var(--thread);
}
.notes li > div {
  display: flex;
  flex-direction: column;
  min-width: 0;
  color: var(--ink);
}
.notes strong {
  font-weight: 500;
}
.notes small {
  color: var(--ink-soft);
  word-break: break-all;
}
</style>
