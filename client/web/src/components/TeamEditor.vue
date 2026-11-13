<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { X } from '@lucide/vue'
import ExpertAvatar from './ExpertAvatar.vue'
import { loadModels, saveTeam, state, toast } from '../store'
import { TEAM_ICONS, TEAM_TOOL_KINDS } from '../teams'
import type { Team, TeamInput, TeamToolKind } from '../types'

/**
 * 新建 / 编辑团队（docs/team-mode.md 7.3）：两步——先选成员（2～上限位专家），再填团队设置。
 * 人数、范围、重名、并发修改都由服务端校验，这里只做「至少 2 位」「要有名字」这类即时提示。
 */
const props = defineProps<{ team: Team | null }>()
const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()

const step = ref<1 | 2>(1)
const category = ref('')
const query = ref('')
const saving = ref(false)
const max = computed(() => state.app?.teamMaxMembers ?? 8)

const form = reactive<TeamInput>({
  name: props.team?.name ?? '',
  description: props.team?.description ?? '',
  icon: props.team?.icon ?? 'team',
  leader_note: props.team?.leader_note ?? '',
  members: props.team ? props.team.members.map((m) => ({ expert_id: m.expert_id, note: m.note })) : [],
  model_id: props.team?.model_id ?? null,
  disabled_tools: props.team ? [...props.team.disabled_tools] : [],
  read_only: props.team?.read_only ?? false,
  share: props.team?.source === 'personal' ? 'personal' : 'department',
  updated_at: props.team?.updated_at ?? null,
})

onMounted(() => {
  if (!state.models.length) void loadModels()
})

const categories = computed(() => [...new Set(state.experts.map((e) => e.category || t('ui.experts.other')))])
const candidates = computed(() => {
  const q = query.value.trim().toLowerCase()
  return state.experts.filter(
    (e) =>
      (!category.value || (e.category || t('ui.experts.other')) === category.value) &&
      (!q || `${e.name} ${e.tagline} ${e.description}`.toLowerCase().includes(q)),
  )
})
const picked = (id: string) => form.members.some((m) => m.expert_id === id)
const expertOf = (id: string) => state.experts.find((e) => e.id === id)

function toggle(id: string) {
  if (picked(id)) form.members = form.members.filter((m) => m.expert_id !== id)
  else if (form.members.length < max.value) form.members.push({ expert_id: id, note: '' })
}
function move(i: number, by: number) {
  const j = i + by
  if (j < 0 || j >= form.members.length) return
  const list = [...form.members]
  ;[list[i], list[j]] = [list[j], list[i]]
  form.members = list
}

const toolModels = computed(() => state.models.filter((m) => m.supportsTools))
const deptName = computed(() => props.team?.department || '')

function toggleTool(kind: TeamToolKind, on: boolean) {
  form.disabled_tools = on ? [...form.disabled_tools, kind] : form.disabled_tools.filter((k) => k !== kind)
}

async function save() {
  if (!form.name.trim()) {
    toast(t('ui.teams.editor.needName'))
    return
  }
  saving.value = true
  try {
    const saved = await saveTeam(props.team?.id ?? null, { ...form, name: form.name.trim() })
    if (saved) {
      toast(t('ui.teams.editor.saved', { name: saved.name }))
      emit('close')
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="scrim" @click.self="emit('close')">
    <section class="drawer" role="dialog" :aria-label="team ? t('ui.teams.editor.editTitle') : t('ui.teams.editor.newTitle')">
      <header>
        <h2>{{ team ? t('ui.teams.editor.editTitle') : t('ui.teams.editor.newTitle') }}</h2>
        <div class="steps">
          <span :aria-current="step === 1 ? 'step' : undefined">① {{ t('ui.teams.editor.step1') }}</span>
          <span>›</span>
          <span :aria-current="step === 2 ? 'step' : undefined">② {{ t('ui.teams.editor.step2') }}</span>
        </div>
        <button type="button" class="close" :aria-label="t('ui.teams.cancel')" @click="emit('close')"><X :size="16" /></button>
      </header>

      <div class="body">
        <div v-if="step === 1" class="pick">
          <div class="col">
            <h4>{{ t('ui.teams.editor.pickExperts') }}<span class="count">{{ form.members.length }} / {{ max }}</span></h4>
            <input v-model="query" class="find" type="search" :placeholder="t('ui.teams.editor.searchExperts')" />
            <div class="cats">
              <button type="button" :aria-pressed="!category" @click="category = ''">{{ t('ui.teams.editor.allCats') }}</button>
              <button v-for="c in categories" :key="c" type="button" :aria-pressed="category === c" @click="category = c">{{ c }}</button>
            </div>
            <div class="xlist">
              <label
                v-for="e in candidates"
                :key="e.id"
                class="xitem"
                :class="{ disabled: !picked(e.id) && form.members.length >= max }"
              >
                <input
                  type="checkbox"
                  :checked="picked(e.id)"
                  :disabled="!picked(e.id) && form.members.length >= max"
                  @change="toggle(e.id)"
                />
                <ExpertAvatar :avatar="e.avatar" :name="e.name" :id="e.id" :size="26" />
                <span class="t">
                  <b>{{ e.name }}</b>
                  <span v-if="e.readOnly" class="tag">{{ t('ui.teams.readOnlyExpert') }}</span>
                  <small>{{ e.tagline || e.description }}</small>
                </span>
              </label>
              <p v-if="!candidates.length" class="faint">{{ t('ui.teams.editor.noExperts') }}</p>
            </div>
          </div>
          <div class="col">
            <h4>{{ t('ui.teams.editor.members') }}</h4>
            <div class="sel">
              <div class="selrow leader-row">
                <span class="order">★</span>
                <span class="leader">{{ t('ui.teams.leaderShort') }}</span>
                <span><b>{{ t('ui.teams.leader') }}</b><small>{{ t('ui.teams.editor.leaderHint') }}</small></span>
                <span />
              </div>
              <div v-for="(m, i) in form.members" :key="m.expert_id" class="selrow">
                <span class="order">
                  <button type="button" :aria-label="t('ui.teams.editor.up')" :disabled="i === 0" @click="move(i, -1)">▲</button>
                  <button type="button" :aria-label="t('ui.teams.editor.down')" :disabled="i === form.members.length - 1" @click="move(i, 1)">▼</button>
                </span>
                <ExpertAvatar :avatar="expertOf(m.expert_id)?.avatar ?? ''" :name="expertOf(m.expert_id)?.name ?? m.expert_id" :id="m.expert_id" :size="26" />
                <span class="grow">
                  <b>{{ expertOf(m.expert_id)?.name ?? m.expert_id }}</b>
                  <input v-model="m.note" maxlength="200" :placeholder="t('ui.teams.editor.notePlaceholder')" />
                </span>
                <button type="button" class="x" :aria-label="t('ui.teams.editor.remove')" @click="toggle(m.expert_id)"><X :size="14" /></button>
              </div>
              <p v-if="!form.members.length" class="faint">{{ t('ui.teams.editor.pickHint', { max }) }}</p>
            </div>
          </div>
        </div>

        <div v-else class="form">
          <label class="k" for="team-name">{{ t('ui.teams.editor.name') }}</label>
          <div><input id="team-name" v-model="form.name" maxlength="30" :placeholder="t('ui.teams.editor.namePlaceholder')" /></div>

          <span class="k">{{ t('ui.teams.editor.icon') }}</span>
          <div class="icons">
            <button
              v-for="(icon, key) in TEAM_ICONS"
              :key="key"
              type="button"
              :aria-pressed="form.icon === key"
              :aria-label="String(key)"
              @click="form.icon = String(key)"
            >
              <component :is="icon" :size="17" />
            </button>
          </div>

          <label class="k" for="team-desc">{{ t('ui.teams.editor.description') }}</label>
          <div><input id="team-desc" v-model="form.description" maxlength="300" :placeholder="t('ui.teams.editor.descriptionPlaceholder')" /></div>

          <label class="k" for="team-leader">{{ t('ui.teams.editor.leaderNote') }}</label>
          <div>
            <textarea id="team-leader" v-model="form.leader_note" maxlength="1000" :placeholder="t('ui.teams.editor.leaderNotePlaceholder')" />
            <small>{{ t('ui.teams.editor.leaderNoteHelp') }}</small>
          </div>

          <label class="k" for="team-model">{{ t('ui.teams.editor.model') }}</label>
          <div>
            <select id="team-model" v-model="form.model_id">
              <option :value="null">{{ t('ui.teams.editor.modelDefault') }}</option>
              <option v-for="m in toolModels" :key="m.id" :value="m.id">{{ m.name }}</option>
            </select>
            <small>{{ t('ui.teams.editor.modelHelp') }}</small>
          </div>

          <span class="k">{{ t('ui.teams.editor.tools') }}</span>
          <div>
            <div class="checks">
              <label v-for="k in TEAM_TOOL_KINDS" :key="k">
                <input type="checkbox" :checked="form.disabled_tools.includes(k)" @change="toggleTool(k, ($event.target as HTMLInputElement).checked)" />
                {{ t(`ui.teams.tool.${k}`) }}
              </label>
            </div>
            <small>{{ t('ui.teams.editor.toolsHelp') }}</small>
          </div>

          <span class="k">{{ t('ui.teams.editor.readOnly') }}</span>
          <label class="checks"><input v-model="form.read_only" type="checkbox" /> {{ t('ui.teams.editor.readOnlyHelp') }}</label>

          <span class="k">{{ t('ui.teams.editor.share') }}</span>
          <div class="checks">
            <label><input v-model="form.share" type="radio" value="department" /> {{ deptName ? t('ui.teams.editor.shareDeptNamed', { dept: deptName }) : t('ui.teams.editor.shareDept') }}</label>
            <label><input v-model="form.share" type="radio" value="personal" /> {{ t('ui.teams.editor.sharePersonal') }}</label>
          </div>
        </div>
      </div>

      <footer>
        <template v-if="step === 1">
          <span class="hint">{{ form.members.length < 2 ? t('ui.teams.editor.atLeast2') : t('ui.teams.editor.picked', { n: form.members.length }) }}</span>
          <button type="button" class="btn" @click="emit('close')">{{ t('ui.teams.cancel') }}</button>
          <button type="button" class="btn primary" :disabled="form.members.length < 2" @click="step = 2">{{ t('ui.teams.editor.next') }}</button>
        </template>
        <template v-else>
          <span class="hint">{{ form.share === 'personal' ? t('ui.teams.editor.personalHint') : t('ui.teams.editor.deptHint') }}</span>
          <button type="button" class="btn" @click="step = 1">{{ t('ui.teams.editor.back') }}</button>
          <button type="button" class="btn primary" :disabled="saving" @click="save">{{ t('ui.teams.editor.save') }}</button>
        </template>
      </footer>
    </section>
  </div>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 50;
  display: flex;
  justify-content: flex-end;
  background: rgba(10, 10, 14, 0.35);
}
.drawer {
  display: flex;
  flex-direction: column;
  width: min(860px, 100%);
  height: 100%;
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
header {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 16px 20px;
  border-bottom: 1px solid var(--line);
}
header h2 {
  margin: 0;
  font-size: var(--t-lg);
  font-weight: 600;
}
.steps {
  display: flex;
  gap: 6px;
  margin-left: auto;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.steps [aria-current='step'] {
  color: var(--indigo);
  font-weight: 600;
}
.close {
  border: 0;
  background: none;
  color: var(--ink-soft);
  cursor: pointer;
}
.body {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 18px 20px;
}
footer {
  display: flex;
  align-items: center;
  gap: 8px;
  justify-content: flex-end;
  padding: 12px 20px;
  border-top: 1px solid var(--line);
}
footer .hint {
  margin-right: auto;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.pick {
  display: grid;
  grid-template-columns: minmax(0, 1.3fr) minmax(0, 1fr);
  gap: 16px;
}
.col {
  min-width: 0;
  padding: 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
}
h4 {
  display: flex;
  margin: 0 0 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  font-weight: 600;
}
.count {
  margin-left: auto;
  color: var(--indigo);
  font-variant-numeric: tabular-nums;
}
.find {
  width: 100%;
  padding: 5px 9px;
  border: 1px solid var(--line-strong);
  border-radius: 8px;
  background: var(--cloth);
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
}
.cats {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin: 8px 0;
}
.cats button {
  padding: 1px 10px;
  border: 1px solid var(--line);
  border-radius: 14px;
  background: var(--cloth);
  color: var(--ink-soft);
  font-size: var(--t-xs);
  cursor: pointer;
}
.cats button[aria-pressed='true'] {
  border-color: var(--indigo);
  background: var(--indigo-wash);
  color: var(--indigo);
}
.xlist {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-height: 380px;
  overflow: auto;
}
.xitem {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 7px 8px;
  border-radius: 8px;
  cursor: pointer;
}
.xitem:hover {
  background: var(--cloth-sunk);
}
.xitem.disabled {
  opacity: 0.45;
  cursor: default;
}
.xitem input {
  flex: none;
  accent-color: var(--indigo);
}
.t {
  flex: 1;
  min-width: 0;
}
.t b {
  font-size: var(--t-sm);
}
.t small,
.selrow small {
  display: block;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tag {
  margin-left: 4px;
  padding: 0 6px;
  border-radius: 8px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.sel {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.selrow {
  display: grid;
  grid-template-columns: 20px 26px minmax(0, 1fr) auto;
  gap: 8px;
  align-items: start;
  padding: 8px;
  border-radius: 8px;
  background: var(--cloth-sunk);
}
.selrow b {
  font-size: var(--t-sm);
}
.selrow input {
  width: 100%;
  margin-top: 4px;
  padding: 3px 7px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font: inherit;
  font-size: var(--t-xs);
}
.order {
  display: flex;
  flex-direction: column;
  color: var(--ink-faint);
  font-size: 10px;
}
.order button {
  padding: 0;
  border: 0;
  background: none;
  color: var(--ink-faint);
  font-size: 9px;
  cursor: pointer;
}
.order button:disabled {
  opacity: 0.3;
  cursor: default;
}
.grow {
  min-width: 0;
}
.x {
  border: 0;
  background: none;
  color: var(--ink-faint);
  cursor: pointer;
}
.leader {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 50%;
  background: var(--ink);
  color: var(--loom);
  font-size: 11px;
  font-weight: 600;
}
.faint {
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.form {
  display: grid;
  grid-template-columns: 120px minmax(0, 1fr);
  gap: 14px;
  align-items: start;
  max-width: 680px;
}
.form .k {
  padding-top: 6px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.form input:not([type]),
.form textarea,
.form select {
  width: 100%;
  padding: 7px 10px;
  border: 1px solid var(--line-strong);
  border-radius: 8px;
  background: var(--cloth);
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
}
.form textarea {
  min-height: 70px;
  resize: vertical;
}
.form small {
  display: block;
  margin-top: 4px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.icons {
  display: flex;
  gap: 6px;
}
.icons button {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  border: 1px solid var(--line);
  border-radius: 9px;
  background: var(--cloth);
  color: var(--ink-soft);
  cursor: pointer;
}
.icons button[aria-pressed='true'] {
  border-color: var(--indigo);
  background: var(--indigo-wash);
  color: var(--indigo);
}
.checks {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 14px;
  padding-top: 6px;
  font-size: var(--t-sm);
}
.checks label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.checks input {
  accent-color: var(--indigo);
}
@media (max-width: 760px) {
  .pick {
    grid-template-columns: minmax(0, 1fr);
  }
  .form {
    grid-template-columns: minmax(0, 1fr);
    gap: 6px;
  }
}
</style>
