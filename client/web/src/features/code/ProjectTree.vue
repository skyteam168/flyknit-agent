<script setup lang="ts">
/**
 * 项目文件树（像 VS Code 的资源管理器）：最上面是工作区文件夹本身（可以收起），工具按钮新建文件、新建文件夹、刷新、全部收起。
 * 点文件夹展开（按需向宿主要下一层）并选中它（新建放在选中的文件夹里），点文件在编辑器里打开。依赖、编译输出这类文件夹标灰。
 */
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronDown, ChevronRight, ChevronsDownUp, FileCode2, FilePlus, Folder, FolderOpen, FolderPlus, RefreshCw } from '@lucide/vue'
import { bridge } from '../../bridge'
import type { WorkspaceEntry } from '../../types'
import { current } from '../../store'
import { activeTab, code, collapseTree, finishCreate, openCodeFile, refreshTree, startCreate, tree } from './store'

const props = defineProps<{ conversationId: string; path?: string; depth?: number }>()
const { t } = useI18n()

const entries = ref<WorkspaceEntry[]>([])
const open = ref<Record<string, boolean>>({})
const loading = ref(false)
const error = ref('')
const truncated = ref(false)

async function load() {
  loading.value = true
  error.value = ''
  try {
    const r = await bridge.listWorkspace(props.conversationId, props.path ?? '')
    entries.value = r.entries
    truncated.value = r.truncated
    if (!r.exists) error.value = t('ui.code.noWorkspace')
    else if (r.error) error.value = r.error
  } catch (e) {
    error.value = String((e as Error)?.message ?? e)
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(() => props.conversationId, load)
// AI 新建 / 删除了文件：每一层（展开着的子文件夹也一样）各自重新列，展开状态不变
watch(() => code.treeVersion[props.conversationId], load)
// 全部收起
watch(() => tree.collapseVersion, () => (open.value = {}))

const parentOf = (p: string) => p.split(/[\\/]/).slice(0, -1).join('\\')

function click(e: WorkspaceEntry) {
  if (e.isDir) {
    open.value[e.path] = !open.value[e.path]
    tree.selectedDir[props.conversationId] = e.path
  } else {
    tree.selectedDir[props.conversationId] = parentOf(e.path)
    void openCodeFile(props.conversationId, e.path)
  }
}

/** 工作区文件夹的名字（树最上面那一行，像 VS Code 的 EMPLOYEE-MANAGER） */
const rootName = computed(() => (current.value?.workspace ?? '').split(/[\\/]/).filter(Boolean).pop() ?? '')
const here = computed(() => props.path ?? '')

// 新建：在这一层（选中的文件夹）显示一个输入框；新建到还没展开的子文件夹时先展开它
const creatingHere = computed(() => tree.creating?.conversationId === props.conversationId && tree.creating.parent === here.value)
const draft = ref('')
const input = ref<HTMLInputElement>()
watch(
  () => tree.creating?.parent,
  async (parent) => {
    if (parent === undefined) return
    if (parent !== here.value) {
      // 要新建到这一层下面的某个文件夹：把它展开，下一层会显示输入框
      const child = entries.value.find((e) => e.isDir && (parent === e.path || parent.startsWith(e.path + '\\')))
      if (child) open.value[child.path] = true
      return
    }
    draft.value = ''
    await nextTick()
    input.value?.focus()
  },
)
function commit() {
  void finishCreate(draft.value)
}
function cancel() {
  tree.creating = null
}
const selected = (e: WorkspaceEntry) => e.isDir && tree.selectedDir[props.conversationId] === e.path

const isActive = (e: WorkspaceEntry) => !e.isDir && activeTab.value?.fullPath.toLowerCase() === e.fullPath.toLowerCase()
defineExpose({ reload: load })
</script>

<template>
  <div class="tree" :class="{ top: !depth }">
    <div v-if="!depth" class="tree-head" :title="current?.workspace ?? ''">
      <button type="button" class="root" @click="(tree.rootOpen = !tree.rootOpen), (tree.selectedDir[conversationId] = '')">
        <ChevronDown v-if="tree.rootOpen" :size="13" />
        <ChevronRight v-else :size="13" />
        <span>{{ rootName || t('ui.code.files') }}</span>
      </button>
      <span class="actions">
        <button type="button" class="icon" :title="t('ui.code.tree.newFile')" @click="startCreate(conversationId, false)"><FilePlus :size="14" /></button>
        <button type="button" class="icon" :title="t('ui.code.tree.newFolder')" @click="startCreate(conversationId, true)"><FolderPlus :size="14" /></button>
        <button type="button" class="icon" :title="t('ui.code.refresh')" @click="refreshTree(conversationId)"><RefreshCw :size="13" /></button>
        <button type="button" class="icon" :title="t('ui.code.tree.collapse')" @click="collapseTree()"><ChevronsDownUp :size="14" /></button>
      </span>
    </div>
    <!-- 最外层自己滚动（上下 + 左右，参照 VS Code）：目录很深、名字很长时横向拖着看全，标题和按钮不跟着滚 -->
    <div v-if="depth || tree.rootOpen" :class="{ scroller: !depth }">
    <div :class="{ inner: !depth }">
    <p v-if="error" class="muted">{{ error }}</p>
    <p v-else-if="loading && !entries.length" class="muted">{{ t('ui.code.loading') }}</p>
    <div v-if="creatingHere" class="create" :style="{ paddingLeft: 6 + ((depth ?? 0) + (depth ? 0 : 0)) * 12 + 'px' }">
      <component :is="tree.creating?.directory ? Folder : FileCode2" :size="14" class="ico" :class="{ dir: tree.creating?.directory }" />
      <input
        ref="input"
        v-model="draft"
        :placeholder="tree.creating?.directory ? t('ui.code.tree.folderName') : t('ui.code.tree.fileName')"
        @keydown.enter.prevent="commit"
        @keydown.esc.prevent="cancel"
        @blur="draft.trim() ? commit() : cancel()"
      />
    </div>
    <ul>
      <li v-for="e in entries" :key="e.path">
        <button
          type="button"
          class="row"
          :class="{ noise: e.noise, active: isActive(e), selected: selected(e) }"
          :style="{ paddingLeft: 6 + (depth ?? 0) * 12 + 'px' }"
          :title="e.path"
          @click="click(e)"
        >
          <template v-if="e.isDir">
            <ChevronDown v-if="open[e.path]" :size="13" class="chev" />
            <ChevronRight v-else :size="13" class="chev" />
            <FolderOpen v-if="open[e.path]" :size="14" class="ico dir" />
            <Folder v-else :size="14" class="ico dir" />
          </template>
          <template v-else>
            <span class="chev" />
            <FileCode2 :size="14" class="ico" />
          </template>
          <span class="name">{{ e.name }}</span>
        </button>
        <ProjectTree v-if="e.isDir && open[e.path]" :conversation-id="conversationId" :path="e.path" :depth="(depth ?? 0) + 1" />
      </li>
    </ul>
    <p v-if="truncated" class="muted small">{{ t('ui.code.truncated') }}</p>
    </div>
    </div>
  </div>
</template>

<style scoped>
.tree.top {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}
.scroller {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding-bottom: 12px;
}
/* 和最宽的那一行一样宽（至少和面板一样宽）：每行的悬停底色都铺满，横向滚动条能拖到最长的名字 */
.inner {
  width: max-content;
  min-width: 100%;
}
.tree-head {
  flex: none;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 4px;
  padding: 0 4px 4px 2px;
}
.root {
  display: flex;
  flex: 1;
  align-items: center;
  gap: 3px;
  min-width: 0;
  padding: 3px 4px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--ink);
  font-size: calc(11.5px * var(--font-scale));
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  cursor: pointer;
}
.root span {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.actions {
  display: flex;
  flex: none;
  gap: 1px;
  opacity: 0.55;
}
.tree-head:hover .actions {
  opacity: 1;
}
.create {
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 2px 8px 2px 25px;
}
.create input {
  flex: 1;
  min-width: 0;
  padding: 2px 6px;
  border: 1px solid var(--indigo);
  border-radius: 3px;
  background: var(--cloth);
  color: var(--ink);
  font-size: calc(12.5px * var(--font-scale));
  outline: none;
}
ul {
  margin: 0;
  padding: 0;
  list-style: none;
}
.row {
  display: flex;
  align-items: center;
  gap: 4px;
  width: 100%;
  padding: 3px 8px 3px 6px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--ink);
  font-size: calc(12.5px * var(--font-scale));
  text-align: left;
  cursor: pointer;
}
.row:hover {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.row.selected:not(.active) {
  background: color-mix(in srgb, var(--ink) 5%, transparent);
}
.row.active {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.row.noise {
  opacity: 0.5;
}
.chev {
  flex: none;
  width: 13px;
  color: var(--ink-faint);
}
.ico {
  flex: none;
  color: var(--ink-soft);
}
.ico.dir {
  color: var(--amber);
}
.name {
  white-space: nowrap;
}
.icon {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: inherit;
  cursor: pointer;
}
.icon:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.muted {
  margin: 6px 10px;
  color: var(--ink-faint);
  font-size: calc(12px * var(--font-scale));
}
.small {
  font-size: calc(11px * var(--font-scale));
}
</style>
