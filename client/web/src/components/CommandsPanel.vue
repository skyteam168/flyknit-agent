<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { BUILTIN_NAMES } from '../builtinCommands'
import { useI18n } from 'vue-i18n'
import { Pencil, Plus, Trash2 } from '@lucide/vue'
import { bridge } from '../bridge'
import { loadCommands, state, toast } from '../store'
import { templateParams } from '../slash'
import type { SlashCommand } from '../types'

/**
 * 设置 →「快捷指令」：自己的指令（只存本机）可以增删改；IT 发布的只能看、可以在输入框里用。
 * 同名时自己的优先。
 */
const { t } = useI18n()

const editing = ref<{ index: number; cmd: SlashCommand } | null>(null)
const saving = ref(false)

onMounted(() => void loadCommands())

function add() {
  editing.value = { index: -1, cmd: { name: '', title: '', description: '', template: '' } }
}

function edit(i: number) {
  editing.value = { index: i, cmd: { ...state.commands.mine[i] } }
}

async function persist(list: SlashCommand[]) {
  saving.value = true
  try {
    const r = await bridge.saveCommands(list)
    if (!r.ok) {
      toast(r.message)
      return false
    }
    await loadCommands()
    return true
  } finally {
    saving.value = false
  }
}

async function save() {
  const e = editing.value
  if (!e) return
  // 和内置命令（/compact、/model……）重名的不会生效：输入框里内置命令优先
  const name = e.cmd.name.trim().replace(/^\//, '').toLowerCase()
  if (BUILTIN_NAMES.includes(name)) {
    toast(t('ui.commands.builtinName', { name }))
    return
  }
  const list = [...state.commands.mine]
  if (e.index < 0) list.push(e.cmd)
  else list[e.index] = e.cmd
  if (await persist(list)) {
    editing.value = null
    toast(t('ui.commands.saved'))
  }
}

async function remove(i: number) {
  if (!window.confirm(t('ui.commands.confirmDelete', { name: state.commands.mine[i].name }))) return
  await persist(state.commands.mine.filter((_, j) => j !== i))
}

const params = (c: SlashCommand) => templateParams(c.template)
</script>

<template>
  <div class="cmds">
    <p class="intro">{{ t('ui.commands.intro') }}</p>

    <div class="head">
      <h3>{{ t('ui.commands.mine') }}</h3>
      <button v-if="!editing" type="button" class="btn" @click="add"><Plus :size="14" /> {{ t('ui.commands.add') }}</button>
    </div>

    <div v-if="editing" class="editor">
      <label>
        <span>{{ t('ui.commands.name') }}</span>
        <div class="name"><b>/</b><input v-model="editing.cmd.name" maxlength="20" :placeholder="t('ui.commands.namePlaceholder')" /></div>
      </label>
      <label>
        <span>{{ t('ui.commands.titleLabel') }}</span>
        <input v-model="editing.cmd.title" maxlength="60" :placeholder="t('ui.commands.titlePlaceholder')" />
      </label>
      <label>
        <span>{{ t('ui.commands.template') }}</span>
        <textarea v-model="editing.cmd.template" rows="5" maxlength="4000" :placeholder="t('ui.commands.templatePlaceholder')" />
      </label>
      <p class="hint">{{ t('ui.commands.templateHint') }}</p>
      <div class="actions">
        <button type="button" class="btn ghost" @click="editing = null">{{ t('ui.commands.cancel') }}</button>
        <button type="button" class="btn primary" :disabled="saving" @click="save">{{ t('ui.commands.save') }}</button>
      </div>
    </div>

    <p v-if="!state.commands.mine.length && !editing" class="empty">{{ t('ui.commands.emptyMine') }}</p>
    <div v-for="(c, i) in state.commands.mine" :key="'m' + c.name" class="item">
      <div class="row">
        <code>/{{ c.name }}</code>
        <span v-for="p in params(c)" :key="p" class="param">{{ p }}</span>
        <span class="title">{{ c.title }}</span>
        <button type="button" class="icon" :title="t('ui.commands.edit')" @click="edit(i)"><Pencil :size="14" /></button>
        <button type="button" class="icon" :title="t('ui.commands.delete')" @click="remove(i)"><Trash2 :size="14" /></button>
      </div>
      <p class="tpl">{{ c.template }}</p>
    </div>

    <template v-if="state.commands.it.length">
      <h3 class="it-head">{{ t('ui.commands.it') }}</h3>
      <div v-for="c in state.commands.it" :key="'i' + c.name" class="item readonly">
        <div class="row">
          <code>/{{ c.name }}</code>
          <span v-for="p in params(c)" :key="p" class="param">{{ p }}</span>
          <span class="title">{{ c.title }}</span>
          <span v-if="c.source" class="param">{{ t('ui.commands.fromPlugin', { name: c.source }) }}</span>
        </div>
        <p class="tpl">{{ c.template }}</p>
      </div>
    </template>
  </div>
</template>

<style scoped>
.cmds {
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.intro,
.hint,
.empty {
  margin: 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.6;
}
.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
h3 {
  margin: 6px 0 0;
  font-size: var(--t-sm);
}
.it-head {
  margin-top: 14px;
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 5px 12px;
  border: 1px solid var(--line);
  border-radius: 8px;
  color: var(--ink);
  font-size: var(--t-sm);
}
.btn.primary {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
.btn.ghost {
  color: var(--ink-soft);
}
.editor {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--cloth-sunk);
}
.editor label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: var(--t-xs);
  color: var(--ink-soft);
}
.editor input,
.editor textarea {
  padding: 6px 8px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-sm);
  font-family: inherit;
  resize: vertical;
}
.name {
  display: flex;
  align-items: center;
  gap: 4px;
}
.name input {
  flex: 1;
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
.item {
  padding: 8px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
}
.item.readonly {
  background: var(--cloth-sunk);
}
.row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.row code {
  color: var(--indigo);
  font-weight: 600;
}
.param {
  padding: 0 6px;
  border-radius: 999px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.icon {
  display: inline-grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.icon:hover {
  background: var(--cloth-sunk);
  color: var(--ink);
}
.tpl {
  margin: 6px 0 0;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  line-height: 1.5;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}
</style>
