<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { RefreshLeft } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { LegalDoc, LegalKind } from '@/api/types'
import { auth } from '@/store/auth'
import { dateTime } from '@/utils/format'
import { renderMarkdown } from '@/utils/markdown'

// 员工端登录界面上的「用户协议」「隐私政策」。员工登录前要勾选同意，同意的是哪一版会记在设备上。
// 改了就是新版本：之后登录的电脑要同意新的，已登录的不受影响。
const canEdit = computed(() => !!auth.user?.is_owner && !auth.user?.must_change_password)

const kind = ref<LegalKind>('terms')
const docs = ref<Partial<Record<LegalKind, LegalDoc>>>({})
const draft = ref('')
const saving = ref(false)
const doc = computed(() => docs.value[kind.value])
const dirty = computed(() => !!doc.value && draft.value !== doc.value.content)
const preview = computed(() => renderMarkdown(draft.value))

async function load(k: LegalKind) {
  docs.value[k] = await api.legal(k)
}
onMounted(async () => {
  await Promise.all([load('terms'), load('privacy')])
  draft.value = doc.value?.content ?? ''
})

watch(kind, async (next, prev) => {
  if (dirty.value) {
    const keep = await ElMessageBox.confirm('当前修改还没保存，切换后会丢失。确定切换吗？', '未保存的修改', {
      type: 'warning',
      confirmButtonText: '切换',
    }).then(() => false, () => true)
    if (keep) {
      kind.value = prev
      return
    }
  }
  draft.value = docs.value[next]?.content ?? ''
})

async function save() {
  saving.value = true
  try {
    const saved = await api.saveLegal(kind.value, draft.value)
    docs.value[kind.value] = saved
    draft.value = saved.content
    ElMessage.success(`已保存。之后登录的员工需要同意新版本的${saved.title}`)
  } finally {
    saving.value = false
  }
}

async function reset() {
  await ElMessageBox.confirm(`把${doc.value?.title}恢复成系统默认的文本？你的修改会被丢弃。`, '恢复默认', {
    type: 'warning',
    confirmButtonText: '恢复默认',
  })
  const restored = await api.resetLegal(kind.value)
  docs.value[kind.value] = restored
  draft.value = restored.content
  ElMessage.success('已恢复默认')
}
</script>

<template>
  <div class="page">
    <el-alert v-if="!canEdit" type="info" :closable="false" show-icon class="notice"
              title="只有超级管理员能修改" description="这里可以查看员工登录时看到的用户协议和隐私政策。" />

    <div class="page-head">
      <div>
        <h1>协议与隐私</h1>
        <p>员工端登录界面上的《用户协议》和《隐私政策》。员工登录前要勾选同意；修改后，之后登录的员工需要同意新版本。</p>
      </div>
      <div class="toolbar">
        <el-radio-group v-model="kind">
          <el-radio-button value="terms">用户协议</el-radio-button>
          <el-radio-button value="privacy">隐私政策</el-radio-button>
        </el-radio-group>
      </div>
    </div>

    <div v-if="doc" class="meta">
      <el-tag v-if="doc.customized" type="success" size="small">已自定义</el-tag>
      <el-tag v-else type="info" size="small">系统默认文本</el-tag>
      <span v-if="doc.updated_at">{{ dateTime(doc.updated_at) }} 由 {{ doc.updated_by }} 修改</span>
      <span class="version">版本 {{ doc.version }}</span>
      <span class="spacer" />
      <el-button v-if="doc.customized" :icon="RefreshLeft" :disabled="!canEdit" @click="reset">恢复默认</el-button>
      <el-button type="primary" :disabled="!canEdit || !dirty" :loading="saving" @click="save">保存并发布</el-button>
    </div>

    <div class="editor">
      <section class="panel">
        <div class="panel-title"><h3>编辑<small>Markdown：# 标题、空行分段、- 列表、**粗体**</small></h3></div>
        <el-input v-model="draft" type="textarea" :disabled="!canEdit" resize="none" class="source" />
      </section>
      <section class="panel">
        <div class="panel-title"><h3>预览<small>员工在登录界面看到的样子</small></h3></div>
        <!-- eslint-disable-next-line vue/no-v-html -- renderMarkdown 先转义再加标签 -->
        <article class="doc" v-html="preview" />
      </section>
    </div>
  </div>
</template>

<style scoped>
.notice {
  margin-bottom: 16px;
}
.meta {
  display: flex;
  gap: 12px;
  align-items: center;
  margin-bottom: 12px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
.version {
  font-family: ui-monospace, monospace;
  font-size: 12px;
}
.spacer {
  flex: 1;
}
.editor {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: 16px;
}
@media (max-width: 1100px) {
  .editor {
    grid-template-columns: 1fr;
  }
}
.panel-title small {
  margin-left: 10px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  font-weight: normal;
}
.source :deep(textarea) {
  height: calc(100vh - 300px);
  min-height: 420px;
  font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace;
  font-size: 13px;
  line-height: 1.7;
}
.doc {
  height: calc(100vh - 300px);
  min-height: 420px;
  overflow: auto;
  padding: 4px 8px;
  font-size: 14px;
  line-height: 1.8;
}
.doc :deep(h1) {
  margin: 4px 0 16px;
  font-size: 20px;
  text-align: center;
}
.doc :deep(h2) {
  margin: 20px 0 8px;
  font-size: 15px;
}
.doc :deep(p),
.doc :deep(li) {
  color: var(--el-text-color-regular);
}
.doc :deep(ol),
.doc :deep(ul) {
  padding-left: 22px;
}
</style>
