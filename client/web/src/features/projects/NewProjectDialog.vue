<script setup lang="ts">
/**
 * 新建项目（参照 Claude 的「新建项目」）：名称、想做什么（可选）、放在哪（在某个工作区里新建文件夹 / 用已有的文件夹）、补充说明（可选）。
 */
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { FolderKanban, X } from '@lucide/vue'
import { state } from '../../store'
import { createProject } from './store'

const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()

const name = ref('')
const goal = ref('')
const notes = ref('')
const where = ref<'new' | 'existing'>('new')
const parent = ref(state.app?.defaultWorkspace || state.workspaces.find((w) => w.isDefault)?.path || state.workspaces[0]?.path || '')
const parents = computed(() => state.workspaces.filter((w) => w.exists))
const error = ref('')
const busy = ref(false)
const nameBox = ref<HTMLInputElement>()
onMounted(() => nameBox.value?.focus())

const sep = computed(() => (parent.value.includes('/') && !parent.value.includes('\\') ? '/' : '\\'))
const preview = computed(() => (where.value === 'new' && parent.value && name.value.trim() ? parent.value.replace(/[\\/]+$/, '') + sep.value + name.value.trim() : ''))

async function submit() {
  if (!name.value.trim() || busy.value) return
  busy.value = true
  error.value = ''
  try {
    const why = await createProject({ name: name.value, goal: goal.value, notes: notes.value, parent: parent.value, existing: where.value === 'existing' })
    if (why && why !== 'cancelled') error.value = why
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="scrim" @click.self="emit('close')" @keydown.esc="emit('close')">
    <form class="dialog" role="dialog" :aria-label="t('ui.projects.new')" @submit.prevent="submit">
      <header>
        <h3>{{ t('ui.projects.new') }}</h3>
        <button type="button" class="x" :aria-label="t('ui.code.close')" @click="emit('close')"><X :size="16" /></button>
      </header>
      <label class="field">
        <span>{{ t('ui.projects.name') }}</span>
        <div class="name-box">
          <FolderKanban :size="15" />
          <input ref="nameBox" v-model="name" maxlength="60" :placeholder="t('ui.projects.namePlaceholder')" />
        </div>
      </label>
      <label class="field">
        <span>{{ t('ui.projects.goalAsk') }} <em>{{ t('ui.projects.optional') }}</em></span>
        <textarea v-model="goal" rows="3" maxlength="2000" :placeholder="t('ui.projects.goalPlaceholder')" />
      </label>
      <div class="field">
        <span>{{ t('ui.projects.location') }}</span>
        <label class="radio"><input v-model="where" type="radio" value="new" /> {{ t('ui.projects.newFolderIn') }}</label>
        <select v-if="where === 'new'" v-model="parent" class="parent">
          <option v-for="w in parents" :key="w.path" :value="w.path">{{ w.name }} — {{ w.path }}</option>
        </select>
        <small v-if="preview" class="preview">{{ t('ui.projects.willCreate', { path: preview }) }}</small>
        <label class="radio"><input v-model="where" type="radio" value="existing" /> {{ t('ui.projects.useExisting') }}</label>
      </div>
      <label class="field">
        <span>{{ t('ui.projects.notes') }} <em>{{ t('ui.projects.optional') }}</em></span>
        <textarea v-model="notes" rows="3" maxlength="8000" :placeholder="t('ui.projects.notesPlaceholder')" />
      </label>
      <p v-if="error" class="error">{{ error }}</p>
      <footer>
        <button type="button" class="btn" @click="emit('close')">{{ t('ui.projects.cancel') }}</button>
        <button type="submit" class="btn primary" :disabled="!name.trim() || busy || (where === 'new' && !parent)">
          {{ where === 'existing' ? t('ui.projects.pickAndCreate') : t('ui.projects.create') }}
        </button>
      </footer>
    </form>
  </div>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 80;
  display: grid;
  place-items: center;
  padding: 16px;
  background: color-mix(in srgb, #000 45%, transparent);
}
.dialog {
  display: flex;
  flex-direction: column;
  gap: 14px;
  width: 520px;
  max-width: 100%;
  max-height: calc(100vh - 32px);
  padding: 16px 18px;
  overflow-y: auto;
  border: 1px solid var(--line);
  border-radius: 14px;
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
header {
  display: flex;
  align-items: center;
}
h3 {
  flex: 1;
  margin: 0;
  font-size: var(--t-md);
  font-weight: 600;
}
.x {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--ink-soft);
  cursor: pointer;
}
.x:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: var(--t-sm);
  color: var(--ink);
  font-weight: 500;
}
.field em {
  color: var(--ink-faint);
  font-style: normal;
  font-weight: 400;
}
.name-box {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 0 10px;
  border: 1px solid var(--indigo);
  border-radius: 9px;
  background: var(--loom);
  color: var(--ink-faint);
}
.name-box input {
  flex: 1;
  height: 38px;
  border: 0;
  outline: none;
  background: transparent;
  color: var(--ink);
  font-size: var(--t-md);
}
textarea,
select {
  padding: 8px 10px;
  border: 1px solid var(--line);
  border-radius: 9px;
  background: var(--loom);
  color: var(--ink);
  font: inherit;
  font-weight: 400;
}
textarea {
  resize: vertical;
}
.radio {
  display: flex;
  align-items: center;
  gap: 6px;
  font-weight: 400;
  color: var(--ink-soft);
  cursor: pointer;
}
.parent {
  margin-left: 22px;
}
.preview {
  margin-left: 22px;
  color: var(--ink-faint);
  font-weight: 400;
  word-break: break-all;
}
.error {
  margin: 0;
  color: var(--red, #e5534b);
  font-size: var(--t-sm);
}
footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
.btn {
  padding: 6px 14px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--loom);
  color: var(--ink);
  font-size: var(--t-sm);
  cursor: pointer;
}
.btn:disabled {
  opacity: 0.5;
  cursor: default;
}
.btn.primary {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
</style>
