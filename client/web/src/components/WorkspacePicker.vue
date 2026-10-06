<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, ChevronDown, ExternalLink, Folder, FolderPlus, X } from '@lucide/vue'
import Popover from './Popover.vue'
import { bridge } from '../bridge'
import { addWorkspace, loadWorkspaces, removeWorkspace, selectWorkspace, selectedWorkspace, state } from '../store'

const { t } = useI18n()

const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()
const current = computed(() => state.workspaces.find((w) => same(w.path, selectedWorkspace.value)))
const label = computed(() => current.value?.name ?? (selectedWorkspace.value.split(/[\\/]/).filter(Boolean).pop() || selectedWorkspace.value))

async function pick(path: string, close: () => void) {
  close()
  await selectWorkspace(path)
}

async function add(close: () => void) {
  close()
  await addWorkspace()
}

function onToggle(toggle: () => void) {
  void loadWorkspaces()
  toggle()
}
</script>

<template>
  <Popover :width="320">
    <template #trigger="{ toggle, open }">
      <button
        type="button"
        class="trigger"
        :class="{ open }"
        :title="`${t('ui.workspace.title')}: ${selectedWorkspace}`"
        @click="onToggle(toggle)"
      >
        <Folder :size="14" />
        <span class="label">{{ label || t('ui.workspace.title') }}</span>
        <ChevronDown :size="13" class="chev" />
      </button>
    </template>
    <template #default="{ close }">
      <div class="head">{{ t('ui.workspace.title') }}</div>
      <div v-for="w in state.workspaces" :key="w.path" class="row" :class="{ on: same(w.path, selectedWorkspace), missing: !w.exists }">
        <button type="button" class="opt" :disabled="!w.exists" :title="w.path" @click="pick(w.path, close)">
          <Folder :size="15" class="ico" />
          <span class="text">
            <strong>{{ w.name }}<em v-if="w.isDefault">{{ t('ui.workspace.default') }}</em></strong>
            <small>{{ w.exists ? w.path : t('ui.workspace.missing') }}</small>
          </span>
          <Check v-if="same(w.path, selectedWorkspace)" :size="15" class="tick" />
        </button>
        <span class="row-actions">
          <button v-if="w.exists" type="button" class="mini" :title="t('ui.workspace.open')" @click="bridge.openPath(w.path)">
            <ExternalLink :size="13" />
          </button>
          <button v-if="!w.isDefault" type="button" class="mini" :title="t('ui.workspace.remove')" @click="removeWorkspace(w.path)">
            <X :size="13" />
          </button>
        </span>
      </div>
      <div class="sep" />
      <button type="button" class="opt add" @click="add(close)">
        <FolderPlus :size="15" class="ico" />
        <span class="text"><strong>{{ t('ui.workspace.add') }}</strong></span>
      </button>
      <p class="hint">{{ t('ui.workspace.hint') }}</p>
    </template>
  </Popover>
</template>

<style scoped>
.trigger {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  max-width: 200px;
  padding: 0 8px;
  border-radius: 7px;
  color: var(--ink-soft);
  font-size: var(--t-xs);
  font-weight: 500;
}
.trigger:hover,
.trigger.open {
  background: var(--chip);
  color: var(--ink);
}
.label {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.chev {
  flex: none;
  color: var(--ink-faint);
}
.head {
  padding: 6px 8px 6px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.row {
  position: relative;
  display: flex;
  align-items: center;
  border-radius: 8px;
}
.row:hover {
  background: var(--chip);
}
.opt {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 42px;
  padding: 6px 8px;
  border-radius: 8px;
  text-align: left;
}
.opt.add:hover {
  background: var(--chip);
}
.opt:disabled {
  cursor: not-allowed;
  opacity: 0.5;
}
.ico {
  flex: none;
  color: var(--ink-faint);
}
.on .ico {
  color: var(--indigo);
}
.text {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  line-height: 1.3;
}
.text strong {
  color: var(--ink);
  font-weight: 500;
  font-size: var(--t-sm);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.text em {
  margin-left: 6px;
  padding: 0 6px;
  border-radius: 8px;
  background: var(--chip);
  color: var(--ink-faint);
  font-style: normal;
  font-size: calc(11px * var(--font-scale));
  font-weight: 400;
}
.text small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.tick {
  flex: none;
  color: var(--indigo);
}
.row-actions {
  display: none;
  gap: 2px;
  padding-right: 6px;
}
.row:hover .row-actions {
  display: flex;
}
.row:hover .tick {
  display: none;
}
.mini {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.mini:hover {
  background: var(--line);
  color: var(--ink);
}
.sep {
  height: 1px;
  margin: 4px 6px;
  background: var(--line);
}
.hint {
  margin: 4px 8px 6px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
  line-height: 1.45;
}
</style>
