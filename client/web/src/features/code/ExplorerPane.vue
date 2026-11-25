<script setup lang="ts">
/**
 * IDE 左边：项目文件 / 搜索（Ctrl+Shift+F）/ 源代码管理（git）/ 技能 / MCP 连接器 / 知识（资料库）。
 * 技能、连接器点一下用到右边的对话里（和技能页、连接器页里的「使用」一样）；知识里的文件点一下加进输入框当附件。
 *
 * 参照 VS Code：左边的图标栏一直在；内容区可以收起（Ctrl+B，或再点一下当前图标），右边缘拖动调宽度。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { BookOpen, FolderTree, GitBranch, Plug, Search, Sparkles } from '@lucide/vue'
import { bridge } from '../../bridge'
import { current, loadMcp, loadSkills, state, toast } from '../../store'
import type { LibraryItem } from '../../types'
import ProjectTree from './ProjectTree.vue'
import SearchPane from './SearchPane.vue'
import GitPane from './GitPane.vue'
import { clampExplorer, ide } from './store'

const emit = defineEmits<{ useSkill: [name: string]; useText: [text: string] }>()
const { t } = useI18n()

const tabs = [
  { id: 'project', icon: FolderTree },
  { id: 'search', icon: Search },
  { id: 'git', icon: GitBranch },
  { id: 'skills', icon: Sparkles },
  { id: 'mcp', icon: Plug },
  { id: 'knowledge', icon: BookOpen },
] as const

const skills = computed(() => state.skills.filter((s) => s.enabled))
const connectors = computed(() => state.mcp.filter((m) => m.enabled))
const knowledge = ref<LibraryItem[]>([])
const knowledgeLoaded = ref(false)

async function loadKnowledge() {
  try {
    const r = await bridge.libraryList({ tab: 'all', sort: 'recent' })
    knowledge.value = r.items.slice(0, 200)
  } catch {
    knowledge.value = []
  } finally {
    knowledgeLoaded.value = true
  }
}

function ensure(tab: string) {
  if (tab === 'skills' && !state.skills.length) void loadSkills()
  if (tab === 'mcp' && !state.mcp.length) void loadMcp(false, true)
  if (tab === 'knowledge' && !knowledgeLoaded.value) void loadKnowledge()
}
onMounted(() => ensure(ide.explorerTab))
watch(() => ide.explorerTab, ensure)

function attach(item: LibraryItem) {
  if (state.pending.some((p) => p.localPath === item.path)) return
  state.pending.push({ fileName: item.name, localPath: item.path, mime: item.mime, size: item.size })
  toast(t('ui.code.attached', { name: item.name }))
}

/** 点图标：换到那一栏；点的就是当前这栏且开着 → 收起（和 VS Code 一样） */
function pick(id: (typeof tabs)[number]['id']) {
  if (ide.explorerOpen && ide.explorerTab === id) {
    ide.explorerOpen = false
    return
  }
  ide.explorerTab = id
  ide.explorerOpen = true
}

// 右边缘拖动调宽度（记住）
const dragging = ref(false)
let startX = 0
let startW = 0
function down(e: PointerEvent) {
  dragging.value = true
  startX = e.clientX
  startW = ide.explorerWidth
  ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
  e.preventDefault()
}
function move(e: PointerEvent) {
  if (dragging.value) ide.explorerWidth = clampExplorer(startW + e.clientX - startX)
}
function up(e: PointerEvent) {
  dragging.value = false
  ;(e.currentTarget as HTMLElement).releasePointerCapture?.(e.pointerId)
}

const dot = (status: string) => (status === 'connected' ? 'ok' : status === 'failed' || status === 'needsauth' ? 'bad' : 'idle')
</script>

<template>
  <aside class="explorer" :class="{ collapsed: !ide.explorerOpen, dragging }" :style="ide.explorerOpen ? { width: ide.explorerWidth + 'px' } : undefined">
    <nav class="rail" role="tablist">
      <button
        v-for="tab in tabs"
        :key="tab.id"
        type="button"
        role="tab"
        :aria-selected="ide.explorerOpen && ide.explorerTab === tab.id"
        :class="{ on: ide.explorerOpen && ide.explorerTab === tab.id }"
        :title="t(`ui.code.explorer.${tab.id}`)"
        @click="pick(tab.id)"
      >
        <component :is="tab.icon" :size="17" />
      </button>
    </nav>
    <div v-if="ide.explorerOpen" class="pane" :class="{ fill: ide.explorerTab === 'project' }">
      <h3>{{ t(`ui.code.explorer.${ide.explorerTab}`) }}</h3>
      <ProjectTree v-if="ide.explorerTab === 'project' && current" :key="current.id" :conversation-id="current.id" />
      <SearchPane v-else-if="ide.explorerTab === 'search'" />
      <GitPane v-else-if="ide.explorerTab === 'git'" />

      <template v-else-if="ide.explorerTab === 'skills'">
        <p class="hint">{{ t('ui.code.skillsHint') }}</p>
        <button v-for="s in skills" :key="s.name" type="button" class="item" :title="s.description" @click="emit('useSkill', s.name)">
          <Sparkles :size="13" class="lead" />
          <span class="text"><b>{{ s.name }}</b><small>{{ s.description }}</small></span>
        </button>
        <p v-if="!skills.length" class="muted">{{ t('ui.code.empty') }}</p>
      </template>

      <template v-else-if="ide.explorerTab === 'mcp'">
        <p class="hint">{{ t('ui.code.mcpHint') }}</p>
        <button
          v-for="m in connectors"
          :key="m.id"
          type="button"
          class="item"
          :title="m.description"
          @click="emit('useText', m.examples[0] || t('ui.code.useConnector', { name: m.name }))"
        >
          <span class="dot" :class="dot(m.status)" />
          <span class="text"><b>{{ m.name }}</b><small>{{ t('ui.code.toolCount', { n: m.tools.length }) }}</small></span>
        </button>
        <p v-if="!connectors.length" class="muted">{{ t('ui.code.empty') }}</p>
      </template>

      <template v-else>
        <p class="hint">{{ t('ui.code.knowledgeHint') }}</p>
        <button v-for="k in knowledge" :key="k.id" type="button" class="item" :title="k.path" @click="attach(k)">
          <BookOpen :size="13" class="lead" />
          <span class="text"><b>{{ k.name }}</b></span>
        </button>
        <p v-if="knowledgeLoaded && !knowledge.length" class="muted">{{ t('ui.code.empty') }}</p>
      </template>
    </div>
    <div
      v-if="ide.explorerOpen"
      class="grip"
      :class="{ active: dragging }"
      role="separator"
      aria-orientation="vertical"
      :title="t('ui.code.explorerResize')"
      @pointerdown="down"
      @pointermove="move"
      @pointerup="up"
      @pointercancel="up"
      @dblclick="ide.explorerWidth = 260"
    />
  </aside>
</template>

<style scoped>
.explorer {
  position: relative;
  display: flex;
  flex: none;
  width: 260px;
  min-height: 0;
  border-right: 1px solid var(--line);
  background: var(--loom);
}
.rail {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 6px;
  border-right: 1px solid var(--line);
}
.rail button {
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--ink-faint);
  cursor: pointer;
}
.rail button:hover {
  color: var(--ink);
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.rail button.on {
  color: var(--indigo);
  background: var(--indigo-wash);
}
.explorer.collapsed {
  width: auto;
}
.explorer.collapsed .rail {
  border-right: 0;
}
.pane {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
  padding: 8px 4px 12px;
}
/* 项目文件：文件树自己滚（上下 + 左右），标题和按钮固定在上面 */
.pane.fill {
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding-bottom: 0;
}
.pane.fill > h3 {
  flex: none;
}
.grip {
  position: absolute;
  top: 0;
  right: -3px;
  bottom: 0;
  width: 6px;
  cursor: col-resize;
  z-index: 5;
}
.grip:hover,
.grip.active {
  background: color-mix(in srgb, var(--indigo) 45%, transparent);
}
h3 {
  margin: 2px 8px 8px;
  font-size: calc(12px * var(--font-scale));
  font-weight: 600;
  color: var(--ink-soft);
}
.hint,
.muted {
  margin: 0 8px 8px;
  color: var(--ink-faint);
  font-size: calc(11.5px * var(--font-scale));
}
.item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--ink);
  text-align: left;
  cursor: pointer;
}
.item:hover {
  background: color-mix(in srgb, var(--ink) 6%, transparent);
}
.lead {
  flex: none;
  margin-top: 3px;
  color: var(--indigo);
}
.text {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.text b {
  overflow: hidden;
  font-size: calc(12.5px * var(--font-scale));
  font-weight: 500;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.text small {
  display: -webkit-box;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}
.dot {
  flex: none;
  width: 8px;
  height: 8px;
  margin-top: 6px;
  border-radius: 50%;
  background: var(--ink-faint);
}
.dot.ok {
  background: var(--thread);
}
.dot.bad {
  background: var(--red);
}
</style>
