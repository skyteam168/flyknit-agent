<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete, Document, MagicStick, Picture, Plus } from '@element-plus/icons-vue'
import { api } from '@/api'
import { bytes } from '@/utils/format'

/**
 * 在后台编写技能：SKILL.md + 需要的脚本、参考资料。可以手写，也可以让 AI 按描述写或改。
 * 图片之类的二进制文件不能在这里编辑，保存时原样保留（可以删掉）。
 */
const props = defineProps<{ modelValue: boolean; name: string | null }>()
const emit = defineEmits<{ 'update:modelValue': [boolean]; saved: [] }>()

const TEMPLATE = `---
name: my-skill
description: 这个技能做什么、什么时候该用它（AI 助手只看这一句决定要不要用）
version: 1.0.0
---

# 技能名称

## 适用场景
-

## 需要的输入
-

## 步骤
1.
2.

## 输出
-

## 注意事项
-
`

interface EditFile {
  path: string
  content: string
}
interface BinaryFile {
  path: string
  size: number
}

const open = computed({
  get: () => props.modelValue,
  set: (v) => emit('update:modelValue', v),
})
const creating = computed(() => !props.name)

const loading = ref(false)
const saving = ref(false)
const skillName = ref('')
const skillMd = ref('')
const files = ref<EditFile[]>([])
const binaries = ref<BinaryFile[]>([])
const removed = ref<string[]>([])
const required = ref(false)
const origin = ref('后台编写')
const notes = ref<string[]>([])
/** 正在编辑的文件：-1 是 SKILL.md */
const active = ref(-1)
const dirty = ref(false)
const drawer = ref<{ handleClose: () => void } | null>(null)

watch(
  () => [props.modelValue, props.name] as const,
  async ([visible, name]) => {
    if (!visible) return
    notes.value = []
    removed.value = []
    active.value = -1
    required.value = false
    origin.value = '后台编写'
    aiText.value = ''
    if (!name) {
      skillName.value = ''
      skillMd.value = TEMPLATE
      files.value = []
      binaries.value = []
      dirty.value = false
      return
    }
    loading.value = true
    try {
      const src = await api.skillSource(name)
      skillName.value = src.name
      skillMd.value = src.skill_md
      files.value = src.files.filter((f) => !f.binary).map((f) => ({ path: f.path, content: f.content ?? '' }))
      binaries.value = src.files.filter((f) => f.binary).map((f) => ({ path: f.path, size: f.size ?? 0 }))
      dirty.value = false
    } catch {
      open.value = false
    } finally {
      loading.value = false
    }
  },
)

const current = computed({
  get: () => (active.value < 0 ? skillMd.value : (files.value[active.value]?.content ?? '')),
  set: (v: string) => {
    dirty.value = true
    if (active.value < 0) skillMd.value = v
    else if (files.value[active.value]) files.value[active.value].content = v
  },
})

/** SKILL.md 里的 name 要和标识一致：新建时改了标识就顺手同步 */
function syncName() {
  const n = skillName.value.trim().toLowerCase()
  if (!n) return
  skillMd.value = skillMd.value.replace(/^(---\n(?:.*\n)*?)name:.*$/m, `$1name: ${n}`)
}

async function addFile() {
  const { value } = await ElMessageBox.prompt('文件路径，如 scripts/build.py、reference/rules.md', '添加文件', {
    inputPattern: /^(?!\/)(?!.*\.\.)[^:*?"<>|]+\.[A-Za-z0-9]+$/,
    inputErrorMessage: '写一个相对路径，带扩展名',
  })
  const path = value.trim().replace(/\\/g, '/')
  if (path.toLowerCase() === 'skill.md' || files.value.some((f) => f.path.toLowerCase() === path.toLowerCase())) {
    ElMessage.warning('已经有这个文件了')
    return
  }
  files.value.push({ path, content: '' })
  active.value = files.value.length - 1
  dirty.value = true
}

async function removeFile(i: number) {
  await ElMessageBox.confirm(`删除 ${files.value[i].path}？`, '删除文件', { type: 'warning', confirmButtonText: '删除' })
  files.value.splice(i, 1)
  active.value = -1
  dirty.value = true
}

function removeBinary(i: number) {
  removed.value.push(binaries.value[i].path)
  binaries.value.splice(i, 1)
  dirty.value = true
}

// ---------- AI ----------
const aiText = ref('')
const aiBusy = ref(false)
async function runAi() {
  if (aiText.value.trim().length < 2) {
    ElMessage.warning(creating.value ? '先说说想让 AI 助手学会什么' : '说说想怎么改')
    return
  }
  if (dirty.value && !creating.value) {
    await ElMessageBox.confirm('AI 会按描述重写这个技能，覆盖编辑器里的内容（保存前都可以取消）。继续？', 'AI 修改', { type: 'info' })
  }
  aiBusy.value = true
  try {
    const d = await api.draftSkill(aiText.value.trim(), props.name ?? undefined)
    if (creating.value) skillName.value = d.name
    skillMd.value = d.skill_md
    files.value = d.files.map((f) => ({ ...f }))
    notes.value = d.notes
    origin.value = 'AI 生成'
    active.value = -1
    dirty.value = true
  } finally {
    aiBusy.value = false
  }
}

async function save() {
  const name = skillName.value.trim().toLowerCase()
  if (creating.value && !/^[a-z0-9][a-z0-9._-]{1,63}$/.test(name)) {
    ElMessage.warning('技能标识只能用小写字母、数字、点、横线和下划线，2～64 个字符')
    return
  }
  saving.value = true
  try {
    const body = { skill_md: skillMd.value, files: files.value }
    if (creating.value) {
      await api.createSkill({ ...body, name, required: required.value, origin: origin.value })
      ElMessage.success(`已创建技能 ${name}`)
    } else {
      const s = await api.saveSkillSource(name, { ...body, remove: removed.value })
      ElMessage.success(`已保存，版本 ${s.version}。员工电脑下次同步时更新`)
    }
    dirty.value = false
    open.value = false
    emit('saved')
  } finally {
    saving.value = false
  }
}

async function beforeClose(done: () => void) {
  if (dirty.value && !saving.value) {
    try {
      await ElMessageBox.confirm('修改还没有保存，确定关闭？', '关闭', { type: 'warning', confirmButtonText: '不保存，关闭' })
    } catch {
      return
    }
  }
  done()
}
</script>

<template>
  <el-drawer ref="drawer" v-model="open" :title="creating ? '新建技能' : `编辑技能 ${name}`" size="min(1100px, 96vw)" :before-close="beforeClose" destroy-on-close>
    <div v-loading="loading" class="editor">
      <div class="ai">
        <el-input
          v-model="aiText"
          type="textarea"
          :autosize="{ minRows: 2, maxRows: 5 }"
          :placeholder="
            creating
              ? '用几句话描述想让 AI 助手学会的能力，如：每周五把 MES 导出的产量 Excel 汇总成周报，按车间分 Sheet，最后一页画趋势图'
              : '说说想怎么改，如：加一步检查数据里有没有空行；输出改成 PDF'
          "
          :disabled="aiBusy"
        />
        <el-button type="primary" plain :icon="MagicStick" :loading="aiBusy" @click="runAi">
          {{ aiBusy ? 'AI 正在写…' : creating ? 'AI 帮我写' : 'AI 帮我改' }}
        </el-button>
      </div>

      <el-alert v-if="notes.length" type="info" :closable="false" show-icon title="AI 的提示" class="notes">
        <ul>
          <li v-for="n in notes" :key="n">{{ n }}</li>
        </ul>
      </el-alert>

      <div class="meta">
        <el-form-item label="技能标识" class="name-field">
          <el-input v-model="skillName" :disabled="!creating" placeholder="英文小写，如 weekly-report" @change="syncName" />
        </el-form-item>
        <el-checkbox v-if="creating" v-model="required">设为必装（自动下发到所有电脑）</el-checkbox>
        <span v-else class="hint">保存时版本号没改会自动加一，员工电脑下次同步时更新。</span>
      </div>

      <div class="body">
        <aside class="files">
          <button class="file" :class="{ on: active === -1 }" @click="active = -1">
            <el-icon><Document /></el-icon><span>SKILL.md</span>
          </button>
          <button v-for="(f, i) in files" :key="f.path" class="file" :class="{ on: active === i }" @click="active = i">
            <el-icon><Document /></el-icon><span :title="f.path">{{ f.path }}</span>
            <el-icon class="x" @click.stop="removeFile(i)"><Delete /></el-icon>
          </button>
          <div v-for="(b, i) in binaries" :key="b.path" class="file binary" :title="`${b.path}（${bytes(b.size)}，不能在这里编辑，保存时原样保留）`">
            <el-icon><Picture /></el-icon><span>{{ b.path }}</span>
            <el-icon class="x" @click.stop="removeBinary(i)"><Delete /></el-icon>
          </div>
          <el-button text :icon="Plus" class="add" @click="addFile">添加文件</el-button>
        </aside>
        <div class="code">
          <div class="code-head">{{ active < 0 ? 'SKILL.md' : files[active]?.path }}</div>
          <el-input v-model="current" type="textarea" class="code-input" resize="none" spellcheck="false" />
          <p v-if="active < 0" class="hint">
            开头 frontmatter 里的 <code>name</code> 要和技能标识一致；<code>description</code> 写清楚「做什么、什么时候用」——AI 助手只看它决定要不要加载这个技能。
          </p>
        </div>
      </div>
    </div>
    <template #footer>
      <el-button @click="drawer?.handleClose()">取消</el-button>
      <el-button type="primary" :loading="saving" :disabled="loading || aiBusy" @click="save">{{ creating ? '创建' : '保存' }}</el-button>
    </template>
  </el-drawer>
</template>

<style scoped>
.editor {
  display: flex;
  flex-direction: column;
  gap: 14px;
  height: 100%;
  min-height: 0;
}
.ai {
  display: flex;
  gap: 10px;
  align-items: flex-start;
}
.ai .el-button {
  flex: none;
}
.notes ul {
  margin: 4px 0 0;
  padding-left: 18px;
}
.meta {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  align-items: center;
}
.name-field {
  margin: 0;
  width: 340px;
}
.hint {
  color: var(--ink-faint);
  font-size: 12px;
}
.hint code {
  padding: 0 4px;
  border-radius: 4px;
  background: var(--cloth-sunk);
}
.body {
  display: grid;
  flex: 1;
  grid-template-columns: 220px minmax(0, 1fr);
  gap: 12px;
  min-height: 420px;
}
.files {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 6px;
  overflow-y: auto;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
}
.file {
  display: flex;
  gap: 6px;
  align-items: center;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: var(--r-sm);
  color: var(--ink-soft);
  background: transparent;
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
}
.file span {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.file:hover {
  background: var(--cloth-sunk);
}
.file.on {
  color: var(--indigo);
  background: var(--indigo-wash);
}
.file.binary {
  color: var(--ink-faint);
  cursor: default;
}
.file .x {
  visibility: hidden;
  color: var(--ink-faint);
}
.file:hover .x {
  visibility: visible;
}
.file .x:hover {
  color: var(--el-color-danger);
}
.add {
  justify-content: flex-start;
  margin-top: 4px;
}
.code {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
  min-height: 0;
}
.code-head {
  color: var(--ink-soft);
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12.5px;
}
.code-input {
  flex: 1;
  min-height: 0;
}
.code-input :deep(textarea) {
  height: 100%;
  min-height: 360px;
  font-family: ui-monospace, Consolas, 'Courier New', monospace;
  font-size: 13px;
  line-height: 1.6;
  tab-size: 2;
}
@media (max-width: 760px) {
  .body {
    grid-template-columns: 1fr;
  }
  .files {
    max-height: 160px;
  }
}
</style>
