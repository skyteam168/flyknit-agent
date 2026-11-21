<script setup lang="ts">
/** 项目文件树：点文件夹展开（按需向宿主要下一层），点文件在编辑器里打开。依赖、编译输出这类文件夹标灰 */
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronDown, ChevronRight, FileCode2, Folder, FolderOpen, RefreshCw } from '@lucide/vue'
import { bridge } from '../../bridge'
import type { WorkspaceEntry } from '../../types'
import { activeTab, code, openCodeFile } from './store'

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

function click(e: WorkspaceEntry) {
  if (e.isDir) open.value[e.path] = !open.value[e.path]
  else void openCodeFile(props.conversationId, e.path)
}

const isActive = (e: WorkspaceEntry) => !e.isDir && activeTab.value?.fullPath.toLowerCase() === e.fullPath.toLowerCase()
defineExpose({ reload: load })
</script>

<template>
  <div class="tree">
    <div v-if="!depth" class="tree-head">
      <span>{{ t('ui.code.files') }}</span>
      <button type="button" class="icon" :title="t('ui.code.refresh')" @click="load"><RefreshCw :size="13" /></button>
    </div>
    <p v-if="error" class="muted">{{ error }}</p>
    <p v-else-if="loading && !entries.length" class="muted">{{ t('ui.code.loading') }}</p>
    <ul>
      <li v-for="e in entries" :key="e.path">
        <button
          type="button"
          class="row"
          :class="{ noise: e.noise, active: isActive(e) }"
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
</template>

<style scoped>
.tree-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 2px 8px 6px;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
  text-transform: uppercase;
  letter-spacing: 0.04em;
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
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
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
