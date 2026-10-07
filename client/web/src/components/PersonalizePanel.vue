<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Lock } from '@lucide/vue'
import { bridge } from '../bridge'
import { toast } from '../store'
import type { PersonaInfo, PersonaUpdate } from '../types'

// 个性化：回复语气、称呼和名字、关于我。
// 语气对应系统提示词里“你的性格与语气”那一段（原来的 soul.md），“关于我”就是 role.md。
// 改完下一条消息就生效。IT 关掉的选项照样显示，带锁、不能选——让人知道为什么选不了。
const { t } = useI18n()

const info = ref<PersonaInfo | null>(null)
const custom = ref('')
const callName = ref('')
const assistantName = ref('')
const about = reactive({ department: '', position: '', language: '', systems: '', folders: '', other: '' })
const savingAbout = ref(false)

function apply(p: PersonaInfo) {
  info.value = p
  custom.value = p.custom
  callName.value = p.callName
  assistantName.value = p.assistantName
  Object.assign(about, p.about)
}

onMounted(async () => {
  const p = await bridge.getPersona().catch(() => null)
  if (p) apply(p)
})

/** 预设加上最后的“自定义” */
const options = computed(() => [...(info.value?.presets ?? []).map((p) => ({ key: p.key, playful: p.playful })), { key: 'custom', playful: false }])

function locked(key: string) {
  const i = info.value
  if (!i) return false
  if (key === 'custom') return !i.customAllowed
  return !i.playfulAllowed && !!i.presets.find((p) => p.key === key)?.playful
}

async function save(update: PersonaUpdate, done?: string) {
  const r = await bridge.setPersona(update).catch((e) => ({ ok: false, message: String(e), persona: null }))
  if (r.persona) apply(r.persona)
  if (!r.ok) toast(r.message)
  else if (done) toast(done)
}

function pick(key: string) {
  if (locked(key) || info.value?.preset === key) return
  void save({ preset: key }, t('ui.persona.saved'))
}

function saveCustom() {
  if (custom.value.trim() === (info.value?.custom ?? '').trim()) return
  void save({ custom: custom.value }, t('ui.persona.saved'))
}

function saveNames() {
  const i = info.value
  if (!i) return
  const update: PersonaUpdate = {}
  if (callName.value.trim() !== i.callName) update.callName = callName.value
  if (assistantName.value.trim() !== i.assistantName) update.assistantName = assistantName.value
  if (Object.keys(update).length) void save(update, t('ui.persona.saved'))
}

async function saveAbout() {
  savingAbout.value = true
  try {
    await save({ about: { ...about } }, t('ui.persona.aboutSaved'))
  } finally {
    savingAbout.value = false
  }
}

const aboutFields = ['department', 'position', 'language', 'systems', 'folders'] as const
</script>

<template>
  <div v-if="info" class="panel">
    <section class="card">
      <h3>{{ t('ui.persona.tone') }}</h3>
      <p class="hint">{{ t('ui.persona.toneHint') }}</p>
      <ul class="tones" role="radiogroup" :aria-label="t('ui.persona.tone')">
        <li v-for="o in options" :key="o.key">
          <button
            type="button"
            role="radio"
            :aria-checked="info.preset === o.key"
            :class="{ on: info.preset === o.key, locked: locked(o.key) }"
            :disabled="locked(o.key)"
            :title="locked(o.key) ? t('ui.persona.lockedHint') : undefined"
            @click="pick(o.key)"
          >
            <span class="text">
              <strong>{{ t(`ui.persona.presets.${o.key}.name`) }}<Lock v-if="locked(o.key)" :size="11" /></strong>
              <small>{{ t(`ui.persona.presets.${o.key}.desc`) }}</small>
            </span>
            <Check v-if="info.preset === o.key" :size="16" class="check" />
          </button>
        </li>
      </ul>
      <p v-if="info.effective !== info.preset" class="warn">{{ t('ui.persona.fallback') }}</p>
      <div v-if="info.preset === 'custom' && info.customAllowed" class="custom">
        <textarea v-model="custom" rows="4" maxlength="2000" :placeholder="t('ui.persona.customPlaceholder')" @blur="saveCustom" />
      </div>
    </section>

    <section class="card">
      <h3>{{ t('ui.persona.names') }}</h3>
      <label class="row">
        <span class="label"><strong>{{ t('ui.persona.callName') }}</strong><small>{{ t('ui.persona.callNameHint') }}</small></span>
        <input v-model="callName" maxlength="20" :placeholder="t('ui.persona.empty')" @change="saveNames" />
      </label>
      <label class="row">
        <span class="label">
          <strong>{{ t('ui.persona.assistantName') }}<Lock v-if="!info.customAllowed" :size="11" /></strong>
          <small>{{ info.customAllowed ? t('ui.persona.assistantNameHint') : t('ui.persona.lockedHint') }}</small>
        </span>
        <input v-model="assistantName" maxlength="20" :disabled="!info.customAllowed" :placeholder="t('ui.persona.empty')" @change="saveNames" />
      </label>
    </section>

    <section class="card">
      <h3>{{ t('ui.persona.about') }}</h3>
      <p class="hint">{{ t('ui.persona.aboutHint') }}</p>
      <div class="about">
        <label v-for="f in aboutFields" :key="f">
          <span>{{ t(`ui.persona.fields.${f}`) }}</span>
          <input v-model="about[f]" maxlength="200" :placeholder="t(`ui.persona.placeholders.${f}`)" />
        </label>
        <label class="wide">
          <span>{{ t('ui.persona.fields.other') }}</span>
          <textarea v-model="about.other" rows="3" maxlength="2000" :placeholder="t('ui.persona.placeholders.other')" />
        </label>
      </div>
      <div class="actions">
        <button type="button" class="btn primary" :disabled="savingAbout" @click="saveAbout">{{ t('ui.persona.saveAbout') }}</button>
      </div>
    </section>
  </div>
</template>

<style scoped>
.card {
  margin-bottom: 14px;
  padding: 6px 16px 14px;
  border-radius: var(--r-lg);
  background: var(--chip);
}
.card > h3 {
  margin: 0;
  padding: 10px 0 4px;
  font-size: var(--t-sm);
  font-weight: 600;
}
.hint {
  margin: 0 0 10px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.warn {
  margin: 8px 0 0;
  color: var(--amber);
  font-size: var(--t-xs);
}
.tones {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(210px, 1fr));
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}
.tones button {
  display: flex;
  gap: 8px;
  align-items: center;
  width: 100%;
  height: 100%;
  padding: 10px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
  color: inherit;
  text-align: left;
  cursor: pointer;
}
.tones button:hover:not(:disabled) {
  border-color: var(--line-strong);
}
.tones button.on {
  border-color: var(--indigo);
  background: var(--indigo-wash);
}
.tones button.locked {
  cursor: not-allowed;
  opacity: 0.55;
}
.tones .text {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.tones strong {
  display: flex;
  gap: 5px;
  align-items: center;
  font-size: var(--t-sm);
  font-weight: 600;
}
.tones small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.check {
  flex: none;
  color: var(--indigo);
}
.custom {
  margin-top: 10px;
}
textarea,
input {
  box-sizing: border-box;
  padding: 7px 10px;
  border: 1px solid var(--line);
  border-radius: 8px;
  background: var(--cloth);
  color: inherit;
  font: inherit;
  font-size: var(--t-sm);
}
textarea {
  width: 100%;
  resize: vertical;
}
input:disabled {
  opacity: 0.55;
}
.row {
  display: flex;
  gap: 16px;
  align-items: center;
  justify-content: space-between;
  padding: 12px 0;
  border-top: 1px solid var(--line);
}
.card > h3 + .row {
  border-top: 0;
}
.row input {
  width: 220px;
  max-width: 45%;
}
.label {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.label strong {
  display: flex;
  gap: 6px;
  align-items: center;
  font-size: var(--t-sm);
  font-weight: 500;
}
.label small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.about {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 10px;
}
.about label {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: var(--t-xs);
  color: var(--ink-soft);
}
.about .wide {
  grid-column: 1 / -1;
}
.actions {
  display: flex;
  justify-content: flex-end;
  margin-top: 10px;
}
</style>
