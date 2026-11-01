<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { FileMinus, FilePen, FilePlus } from '@lucide/vue'
import { current, openChanges, state } from '../store'

/** 右侧「改动」标签：这个对话每一轮改了哪些文件，点开看对比、还原 */
const { t } = useI18n()
const turns = computed(() => {
  const id = current.value?.id
  return id ? [...(state.changes[id] ?? [])].filter((x) => x.files.length).reverse() : []
})
const icon = { created: FilePlus, modified: FilePen, deleted: FileMinus }

function when(at: string) {
  const d = new Date(at)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleString(undefined, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}
</script>

<template>
  <aside class="changes">
    <p v-if="!turns.length" class="empty">{{ t('ui.right.noChanges') }}</p>
    <section v-for="turn in turns" :key="turn.turnId" class="turn">
      <header>
        <span>{{ when(turn.at) }}</span>
        <span class="count">{{ t('ui.changes.chip', { n: turn.files.length }) }}</span>
        <button type="button" class="open" @click="openChanges(current!.id, turn.turnId)">{{ t('ui.right.review') }}</button>
      </header>
      <div v-for="f in turn.files" :key="f.path" class="file" :class="{ reverted: f.reverted }" :title="f.path">
        <component :is="icon[f.kind]" :size="14" class="kind" :class="f.kind" />
        <span class="name">{{ f.name }}</span>
        <span v-if="f.reverted" class="tag">{{ t('ui.changes.reverted') }}</span>
        <span v-else-if="!f.restorable" class="tag warn">{{ t('ui.changes.manual') }}</span>
      </div>
    </section>
  </aside>
</template>

<style scoped>
.changes {
  width: 300px;
  overflow-y: auto;
  padding: 10px 12px;
  border-left: 1px solid var(--line);
  background: var(--loom);
  font-size: var(--t-sm);
}
.empty {
  color: var(--ink-faint);
}
.turn {
  margin-bottom: 14px;
}
header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.count {
  flex: 1;
}
.open {
  color: var(--indigo);
  font-size: var(--t-xs);
}
.file {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 3px 0;
}
.file.reverted .name {
  color: var(--ink-faint);
  text-decoration: line-through;
}
.kind.created {
  color: var(--thread);
}
.kind.modified {
  color: var(--indigo);
}
.kind.deleted {
  color: var(--red);
}
.name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.tag {
  padding: 0 6px;
  border-radius: 999px;
  background: var(--thread-wash);
  color: var(--thread);
  font-size: var(--t-xs);
}
.tag.warn {
  background: var(--amber-wash);
  color: var(--amber);
}
</style>
