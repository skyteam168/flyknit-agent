<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, Plug, Puzzle, ScrollText, SquareSlash } from '@lucide/vue'
import { bridge } from '../bridge'
import type { PluginInfo } from '../types'

/**
 * 设置 →「插件」：IT 按部门下发的插件（一包技能、专家、连接器、快捷指令、工具规则）。只读：
 * 插件里的东西已经出现在各自的地方（技能、专家、连接器、输入框的 /），这里只看是从哪个插件来的。
 */
const { t } = useI18n()
const items = ref<PluginInfo[]>([])
const loading = ref(true)
const offline = ref(false)

onMounted(async () => {
  try {
    const r = await bridge.listPlugins()
    items.value = r.items
    offline.value = !r.ok
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div class="plugins">
    <p class="intro">{{ t('ui.plugins.intro') }}</p>
    <p v-if="loading" class="empty">{{ t('ui.plugins.loading') }}</p>
    <p v-else-if="offline" class="empty">{{ t('ui.plugins.offline') }}</p>
    <p v-else-if="!items.length" class="empty">{{ t('ui.plugins.empty') }}</p>
    <div v-for="p in items" :key="p.id" class="item">
      <div class="row">
        <Puzzle :size="16" class="ico" />
        <strong>{{ p.name || p.id }}</strong>
        <span v-if="p.version" class="ver">v{{ p.version }}</span>
        <span v-if="p.required" class="tag">{{ t('ui.plugins.required') }}</span>
      </div>
      <p v-if="p.description" class="desc">{{ p.description }}</p>
      <div class="parts">
        <span v-if="p.skills.length" :title="p.skills.join('、')"><ScrollText :size="12" />{{ t('ui.plugins.skills', { n: p.skills.length }) }}</span>
        <span v-if="p.experts.length" :title="p.experts.join('、')"><Bot :size="12" />{{ t('ui.plugins.experts', { n: p.experts.length }) }}</span>
        <span v-if="p.connectors.length" :title="p.connectors.join('、')"><Plug :size="12" />{{ t('ui.plugins.connectors', { n: p.connectors.length }) }}</span>
        <span v-if="p.commands.length" :title="p.commands.map((c) => '/' + c).join(' ')"><SquareSlash :size="12" />{{ t('ui.plugins.commands', { n: p.commands.length }) }}</span>
        <span v-if="p.counts.rules">{{ t('ui.plugins.rules', { n: p.counts.rules }) }}</span>
      </div>
      <small v-if="p.author" class="author">{{ t('ui.plugins.by', { author: p.author }) }}</small>
    </div>
  </div>
</template>

<style scoped>
.plugins {
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.intro,
.empty {
  margin: 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.6;
}
.item {
  padding: 10px 12px;
  border: 1px solid var(--line);
  border-radius: 10px;
}
.row {
  display: flex;
  align-items: center;
  gap: 6px;
}
.ico {
  color: var(--indigo);
}
.ver,
.author {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.tag {
  padding: 0 6px;
  border-radius: 999px;
  background: var(--cloth-sunk);
  color: var(--indigo);
  font-size: var(--t-xs);
}
.desc {
  margin: 6px 0 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.5;
}
.parts {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}
.parts span {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.author {
  display: block;
  margin-top: 6px;
}
</style>
