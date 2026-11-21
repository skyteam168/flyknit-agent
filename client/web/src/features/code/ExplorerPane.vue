<script setup lang="ts">
/**
 * IDE 左边：项目文件 / 技能 / MCP 连接器 / 知识（资料库）。
 * 技能、连接器点一下用到右边的对话里（和技能页、连接器页里的「使用」一样）；知识里的文件点一下加进输入框当附件。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { BookOpen, FolderTree, Plug, Sparkles } from '@lucide/vue'
import { bridge } from '../../bridge'
import { current, loadMcp, loadSkills, state, toast } from '../../store'
import type { LibraryItem } from '../../types'
import ProjectTree from './ProjectTree.vue'
import { ide } from './store'

const emit = defineEmits<{ useSkill: [name: string]; useText: [text: string] }>()
const { t } = useI18n()

const tabs = [
  { id: 'project', icon: FolderTree },
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

const dot = (status: string) => (status === 'connected' ? 'ok' : status === 'failed' || status === 'needsauth' ? 'bad' : 'idle')
</script>

<template>
  <aside class="explorer">
    <nav class="rail" role="tablist">
      <button
        v-for="tab in tabs"
        :key="tab.id"
        type="button"
        role="tab"
        :aria-selected="ide.explorerTab === tab.id"
        :class="{ on: ide.explorerTab === tab.id }"
        :title="t(`ui.code.explorer.${tab.id}`)"
        @click="ide.explorerTab = tab.id"
      >
        <component :is="tab.icon" :size="17" />
      </button>
    </nav>
    <div class="pane">
      <h3>{{ t(`ui.code.explorer.${ide.explorerTab}`) }}</h3>
      <ProjectTree v-if="ide.explorerTab === 'project' && current" :key="current.id" :conversation-id="current.id" />

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
  </aside>
</template>

<style scoped>
.explorer {
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
.pane {
  flex: 1;
  min-width: 0;
  overflow-y: auto;
  padding: 8px 4px 12px;
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
