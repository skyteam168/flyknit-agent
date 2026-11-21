<script setup lang="ts">
/**
 * IDE 中间：打开的文件标签 + Monaco。
 * - AI 改过的文件显示对比视图，上面一条「AI 改了这个文件」，看完点「收起对比」；
 * - 员工有没保存的修改时 AI 也改了：不动员工的内容，让员工选「用 AI 的」或「用我的」；
 * - Ctrl+S 保存；只读文件（二进制、太大、链接到工作区外）说明原因。
 */
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, Code2, ExternalLink, FolderGit2, Save, X } from '@lucide/vue'
import { bridge } from '../../bridge'
import { current, send, toast } from '../../store'
import MonacoView from './MonacoView.vue'
import { acceptDiff, activeTab, closeCodeTab, currentCode, isDirty, keepMine, narrowToProject, projects, saveCodeTab, selectionMarkdown, takeTheirs } from './store'
import type { CodeSelection } from './store'

const emit = defineEmits<{ insertText: [text: string] }>()
const { t } = useI18n()

/** Ctrl+L：选中的代码带上文件和行号放进输入框，员工接着说要做什么 */
function addToChat(sel: CodeSelection) {
  emit('insertText', selectionMarkdown(sel, t('ui.code.snippetHeader', { path: sel.path, from: sel.from, to: sel.to })))
}

/** Ctrl+K：在编辑器里直接说怎么改，交给 AI（只改选中的这几行） */
async function quickEdit(sel: CodeSelection, instruction: string) {
  const text = selectionMarkdown(sel, t('ui.code.quickEditPrompt', { path: sel.path, from: sel.from, to: sel.to, instruction }))
  if (await send(text, { noSplit: true })) {
    toast(t('ui.code.quickEditSent'))
  } else {
    // AI 正在做别的：先放进输入框，等它做完再发（或者员工自己插话）
    emit('insertText', text)
    toast(t('ui.code.quickEditBusy'))
  }
}
const tabs = computed(() => currentCode.value?.tabs ?? [])

async function save() {
  const tab = activeTab.value
  const id = current.value?.id
  if (!tab || !id) return
  const r = await saveCodeTab(id, tab)
  if (r === 'saved') toast(t('ui.code.saved', { name: tab.name }))
}

function close(path: string) {
  const id = current.value?.id
  const tab = tabs.value.find((x) => x.path === path)
  if (!id || !tab) return
  if (isDirty(tab) && !window.confirm(t('ui.code.closeDirty', { name: tab.name }))) return
  closeCodeTab(id, path)
}

/** 用员工自己装的 VS Code 打开工作区（开着文件就跳到这个文件） */
async function openVsCode() {
  if (!current.value) return
  try {
    const r = await bridge.openInVsCode(current.value.id, activeTab.value?.path ?? '')
    toast(r.ok ? t('ui.code.vscodeOpened') : r.message)
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  }
}

/** 当前文件属于工作区里的某个项目、而工作区比它大（D:\\AIWORK 里的 vue3-login）：提示可以收窄 */
const project = computed(() => (current.value ? projects[current.value.id] : undefined))
const base = (p: string) => p.split(/[\\/]/).filter(Boolean).pop() ?? p

const reasonText = computed(() => {
  const r = activeTab.value?.reason ?? ''
  return r === 'binary' || r === 'tooLarge' || r === 'outside' ? t(`ui.code.readOnly.${r}`) : r
})
</script>

<template>
  <section class="editor-area">
    <div class="tabs" role="tablist">
      <div
        v-for="tab in tabs"
        :key="tab.path"
        class="tab"
        :class="{ on: currentCode?.active === tab.path }"
        role="tab"
        :aria-selected="currentCode?.active === tab.path"
        :title="tab.path"
        @click="currentCode!.active = tab.path"
        @auxclick.middle="close(tab.path)"
      >
        <span class="name">{{ tab.name }}</span>
        <span v-if="tab.aiChanged" class="ai" :title="t('ui.code.aiChanged')"><Bot :size="11" /></span>
        <span v-if="isDirty(tab)" class="dirty" :title="t('ui.code.unsaved')">●</span>
        <button type="button" class="x" :aria-label="t('ui.code.close')" @click.stop="close(tab.path)"><X :size="12" /></button>
      </div>
      <span class="spacer" />
      <label v-if="currentCode" class="follow" :title="t('ui.code.followHint')">
        <input v-model="currentCode.follow" type="checkbox" /> {{ t('ui.code.follow') }}
      </label>
      <button type="button" class="save" :title="t('ui.code.vscodeHint')" @click="openVsCode"><ExternalLink :size="13" /> VS Code</button>
      <button v-if="activeTab && !activeTab.readOnly" type="button" class="save" :disabled="!isDirty(activeTab)" :title="t('ui.code.saveHint')" @click="save">
        <Save :size="13" /> {{ t('ui.code.save') }}
      </button>
    </div>

    <!-- 当前项目（从打开的文件往上找到 package.json / .git 之类推断）；工作区比它大时可以一键收窄 -->
    <div v-if="project?.projectRoot && project.narrower" class="banner project">
      <FolderGit2 :size="14" />
      <span>{{ t('ui.code.project.current', { name: project.projectName, workspace: base(project.workspace) }) }}</span>
      <button type="button" class="primary" :title="t('ui.code.project.narrowHint', { path: project.projectRoot })" @click="narrowToProject(current!.id)">
        {{ t('ui.code.project.narrow') }}
      </button>
    </div>
    <template v-if="activeTab">
      <div v-if="activeTab.conflict" class="banner warn">
        <span>{{ t('ui.code.conflict') }}</span>
        <button type="button" @click="takeTheirs(activeTab)">{{ t('ui.code.takeTheirs') }}</button>
        <button type="button" class="primary" @click="keepMine(current!.id, activeTab)">{{ t('ui.code.keepMine') }}</button>
      </div>
      <div v-else-if="activeTab.before !== null" class="banner ai">
        <Bot :size="14" />
        <span>{{ t('ui.code.diffBanner') }}</span>
        <button type="button" class="primary" @click="acceptDiff(activeTab)">{{ t('ui.code.closeDiff') }}</button>
      </div>
      <div v-else-if="activeTab.readOnly" class="banner">{{ reasonText }}</div>
      <MonacoView :tab="activeTab" @save="save" @add-to-chat="addToChat" @quick-edit="quickEdit" />
    </template>
    <div v-else class="empty">
      <Code2 :size="34" />
      <strong>{{ t('ui.code.emptyTitle') }}</strong>
      <p>{{ t('ui.code.emptyHint') }}</p>
    </div>
  </section>
</template>

<style scoped>
.editor-area {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  min-width: 0;
  background: var(--cloth);
}
.tabs {
  display: flex;
  flex: none;
  align-items: stretch;
  min-height: 34px;
  overflow-x: auto;
  border-bottom: 1px solid var(--line);
  background: var(--loom);
}
.tab {
  display: flex;
  align-items: center;
  gap: 5px;
  max-width: 220px;
  padding: 0 6px 0 12px;
  border-right: 1px solid var(--line);
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
  cursor: pointer;
  white-space: nowrap;
}
.tab.on {
  background: var(--cloth);
  color: var(--ink);
  box-shadow: inset 0 2px 0 var(--indigo);
}
.tab .name {
  overflow: hidden;
  text-overflow: ellipsis;
}
.ai {
  display: grid;
  place-items: center;
  color: var(--indigo);
}
.dirty {
  color: var(--amber);
  font-size: 10px;
}
.x {
  display: grid;
  place-items: center;
  width: 20px;
  height: 20px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: inherit;
  opacity: 0.5;
  cursor: pointer;
}
.x:hover {
  opacity: 1;
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.spacer {
  flex: 1;
}
.follow {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 0 10px;
  color: var(--ink-soft);
  font-size: calc(12px * var(--font-scale));
  white-space: nowrap;
}
.save {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  align-self: center;
  margin-right: 8px;
  padding: 3px 10px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.save:disabled {
  opacity: 0.45;
  cursor: default;
}
.banner {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--line);
  background: var(--loom);
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
}
.banner span {
  flex: 1;
}
.banner.project {
  background: var(--thread-wash);
  color: var(--ink);
}
.banner.ai {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.banner.warn {
  background: var(--amber-wash);
  color: var(--amber);
}
.banner button {
  padding: 3px 10px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.banner button.primary {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
.empty {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 24px;
  color: var(--ink-faint);
  text-align: center;
}
.empty strong {
  color: var(--ink-soft);
}
.empty p {
  max-width: 360px;
  margin: 0;
  font-size: calc(12.5px * var(--font-scale));
}
</style>
