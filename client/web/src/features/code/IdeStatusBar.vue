<script setup lang="ts">
/**
 * 编码模式最下面的状态栏（参照 VS Code）：
 * 左边——当前分支（领先 / 落后、几个文件有改动，点开源代码管理）、报错 / 警告个数、当前文件的语言服务状态；
 * 右边——光标位置、换行符、语言、终端个数（点开终端）。
 * 数据都是已有的（scmStatus、编辑器标记、语言服务状态、终端），宿主不用改。
 */
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowDown, ArrowUp, CircleX, GitBranch, SquareTerminal, TriangleAlert } from '@lucide/vue'
import { bridge } from '../../bridge'
import { current, state } from '../../store'
import { lsp, problems, serverFor } from './lsp'
import { languageOf } from './monaco'
import { pty } from './pty'
import { activeTab, code, ide } from './store'
import type { ScmStatus } from './types'

const { t } = useI18n()

const scm = ref<ScmStatus | null>(null)
async function loadScm() {
  const id = current.value?.id
  if (!id) return
  try {
    const s = await bridge.scmStatus(id)
    if (current.value?.id === id) scm.value = s
  } catch {
    scm.value = null
  }
}
// 切任务、AI 改了文件、回到窗口时刷新；另外每 30 秒刷新一次（员工在外面的终端里提交、切分支）
watch(() => [current.value?.id, current.value ? code.treeVersion[current.value.id] : 0], loadScm, { immediate: true })
let timer = 0
const onFocus = () => void loadScm()
onMounted(() => {
  timer = window.setInterval(loadScm, 30000)
  window.addEventListener('focus', onFocus)
})
onUnmounted(() => {
  window.clearInterval(timer)
  window.removeEventListener('focus', onFocus)
})

function openGit() {
  ide.explorerOpen = true
  ide.explorerTab = 'git'
}

const tab = activeTab
const lspText = computed(() => {
  const id = current.value?.id
  const tb = tab.value
  if (!id || !tb || tb.virtual) return null
  const s = serverFor(id, tb.path)
  const st = lsp.status[id]
  if (!s || !st?.enabled) return null
  const state = s.disabled
    ? t('ui.code.lsp.disabled')
    : !s.available
      ? t('ui.code.lsp.notInstalled')
      : !st.trusted
        ? t('ui.code.status.lspUntrusted')
        : s.running
          ? t('ui.code.lsp.running')
          : t('ui.code.lsp.ready')
  return { name: s.name, state, ok: s.available && !s.disabled && st.trusted }
})

const cursor = computed(() => tab.value?.cursor ? { line: tab.value.cursor.line, col: tab.value.cursor.column ?? 1 } : null)
const eol = computed(() => (tab.value ? (tab.value.draft.includes('\r\n') ? 'CRLF' : tab.value.draft.includes('\n') ? 'LF' : '') : ''))
const language = computed(() => (tab.value ? languageOf(tab.value.path) : ''))

const terminals = computed(() => {
  const id = current.value?.id
  if (!id) return 0
  return pty.sessions.filter((s) => s.conversationId === id && !s.exited).length + (state.terminal[id] ?? []).filter((r) => r.running).length
})
function openTerminal() {
  ide.bottomOpen = true
  ide.bottomTab = 'terminal'
}
</script>

<template>
  <footer class="status-bar">
    <button v-if="scm?.isRepo" type="button" class="item" :title="t('ui.code.status.branchTip', { branch: scm.branch })" @click="openGit">
      <GitBranch :size="13" />
      <span>{{ scm.branch || 'HEAD' }}<template v-if="scm.changes.length">*</template></span>
      <span v-if="scm.ahead" class="sub" :title="t('ui.code.git.ahead', { n: scm.ahead })"><ArrowUp :size="11" />{{ scm.ahead }}</span>
      <span v-if="scm.behind" class="sub" :title="t('ui.code.git.behind', { n: scm.behind })"><ArrowDown :size="11" />{{ scm.behind }}</span>
    </button>
    <span v-if="scm?.isRepo && scm.changes.length" class="item plain" :title="t('ui.code.status.changes', { n: scm.changes.length })">
      {{ t('ui.code.status.changes', { n: scm.changes.length }) }}
    </span>
    <span class="item plain" :class="{ bad: problems.errors, warn: !problems.errors && problems.warnings }"
      :title="t('ui.code.status.problems', { errors: problems.errors, warnings: problems.warnings })">
      <CircleX :size="13" /> {{ problems.errors }}
      <TriangleAlert :size="13" /> {{ problems.warnings }}
    </span>
    <span v-if="lspText" class="item plain" :class="{ dim: !lspText.ok }" :title="t('ui.code.status.lspTip', { state: lspText.state })">
      {{ lspText.name }} · {{ lspText.state }}
    </span>

    <span class="spacer" />

    <span v-if="cursor" class="item plain">{{ t('ui.code.status.cursor', { line: cursor.line, col: cursor.col }) }}</span>
    <span v-if="eol" class="item plain" :title="t('ui.code.status.eolTip')">{{ eol }}</span>
    <span v-if="language" class="item plain" :title="t('ui.code.status.langTip')">{{ language }}</span>
    <button type="button" class="item" :title="t('ui.code.status.terminalsTip')" @click="openTerminal">
      <SquareTerminal :size="13" /> {{ t('ui.code.status.terminals', { n: terminals }) }}
    </button>
  </footer>
</template>

<style scoped>
.status-bar {
  display: flex;
  flex: none;
  align-items: center;
  gap: 2px;
  height: 24px;
  padding: 0 8px;
  overflow: hidden;
  border-top: 1px solid var(--line);
  background: var(--loom);
  color: var(--ink-soft);
  font-size: calc(11.5px * var(--font-scale));
  white-space: nowrap;
  user-select: none;
}
.item {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 4px;
  height: 100%;
  padding: 0 7px;
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
}
button.item {
  cursor: pointer;
}
button.item:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
  color: var(--ink);
}
.sub {
  display: inline-flex;
  align-items: center;
  color: var(--ink-faint);
}
.bad {
  color: var(--red);
}
.warn {
  color: var(--amber);
}
.dim {
  color: var(--ink-faint);
}
.spacer {
  flex: 1;
}
</style>
