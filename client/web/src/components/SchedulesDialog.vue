<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  CalendarClock,
  Check,
  CircleAlert,
  Clock,
  FileSpreadsheet,
  FolderTree,
  Languages,
  Loader2,
  Play,
  Plus,
  Trash2,
  X,
} from '@lucide/vue'
import { bridge } from '../bridge'
import { loadSchedules, openConversation, state, toast } from '../store'
import type { Permission, ScheduledTask, ScheduleKind } from '../types'

const { t } = useI18n()
const editing = ref<Partial<ScheduledTask> | null>(null)
const busy = ref('')
const removing = ref<ScheduledTask | null>(null)

const kinds: ScheduleKind[] = ['manual', 'hourly', 'daily', 'weekdays', 'weekly', 'monthly', 'once']
const permissions: Permission[] = ['readonly', 'workspace', 'full']
const weekdays = [1, 2, 3, 4, 5, 6, 0]

/** 新建时可以从模板起步，照抄常见的工厂场景 */
const templates = [
  { key: 'daily', icon: FolderTree, kind: 'weekdays' as ScheduleKind, hour: 8, minute: 30 },
  { key: 'report', icon: FileSpreadsheet, kind: 'weekly' as ScheduleKind, hour: 16, minute: 0, weekday: 5 },
  { key: 'cleanup', icon: Clock, kind: 'monthly' as ScheduleKind, hour: 9, minute: 0, dayOfMonth: 1 },
  { key: 'translate', icon: Languages, kind: 'daily' as ScheduleKind, hour: 17, minute: 0 },
]

const form = reactive({
  id: '',
  name: '',
  instructions: '',
  kind: 'daily' as ScheduleKind,
  hour: 9,
  minute: 0,
  weekday: 1,
  dayOfMonth: 1,
  at: '',
  permission: 'workspace' as Permission,
  catchUp: true,
  enabled: true,
})

const needsTime = computed(() => form.kind !== 'manual' && form.kind !== 'once' && form.kind !== 'hourly')
const canSave = computed(() => form.name.trim().length > 0 && form.instructions.trim().length > 0)

function openNew(template?: (typeof templates)[number]) {
  Object.assign(form, {
    id: '',
    name: template ? t(`ui.schedules.templates.${template.key}.name`) : '',
    instructions: template ? t(`ui.schedules.templates.${template.key}.instructions`) : '',
    kind: template?.kind ?? 'daily',
    hour: template?.hour ?? 9,
    minute: template?.minute ?? 0,
    weekday: template?.weekday ?? 1,
    dayOfMonth: template?.dayOfMonth ?? 1,
    at: localNow(60),
    permission: 'workspace',
    catchUp: true,
    enabled: true,
  })
  editing.value = form
}

function openEdit(task: ScheduledTask) {
  Object.assign(form, {
    id: task.id,
    name: task.name,
    instructions: task.instructions,
    kind: task.kind,
    hour: task.hour,
    minute: task.minute,
    weekday: task.weekday,
    dayOfMonth: task.dayOfMonth,
    at: task.at ? task.at.slice(0, 16) : localNow(60),
    permission: task.permission,
    catchUp: task.catchUp,
    enabled: task.enabled,
  })
  editing.value = form
}

/** datetime-local 需要本地时间字符串 */
function localNow(plusMinutes = 0) {
  const d = new Date(Date.now() + plusMinutes * 60000 - new Date().getTimezoneOffset() * 60000)
  return d.toISOString().slice(0, 16)
}

async function save() {
  if (!canSave.value) return
  busy.value = 'save'
  try {
    await bridge.saveSchedule({
      ...form,
      at: form.kind === 'once' && form.at ? new Date(form.at).toISOString() : null,
    } as Partial<ScheduledTask>)
    editing.value = null
    await loadSchedules()
  } catch (e) {
    toast(String(e))
  } finally {
    busy.value = ''
  }
}

async function toggle(task: ScheduledTask) {
  await bridge.setScheduleEnabled(task.id, !task.enabled).catch(() => {})
  await loadSchedules()
}

async function runNow(task: ScheduledTask) {
  busy.value = task.id
  try {
    const r = await bridge.runSchedule(task.id)
    if (!r.ok) {
      toast(r.message)
      return
    }
    toast(t('ui.schedules.started', { name: task.name }))
    state.schedulesOpen = false
    if (r.conversationId) await openConversation(r.conversationId)
  } catch (e) {
    toast(String(e))
  } finally {
    busy.value = ''
    await loadSchedules()
  }
}

async function confirmRemove() {
  const task = removing.value
  removing.value = null
  if (!task) return
  await bridge.deleteSchedule(task.id).catch(() => {})
  await loadSchedules()
}

function describe(task: ScheduledTask) {
  const time = `${String(task.hour).padStart(2, '0')}:${String(task.minute).padStart(2, '0')}`
  switch (task.kind) {
    case 'manual':
      return t('ui.schedules.kinds.manual')
    case 'hourly':
      return t('ui.schedules.everyHour', { m: String(task.minute).padStart(2, '0') })
    case 'daily':
      return t('ui.schedules.everyDay', { time })
    case 'weekdays':
      return t('ui.schedules.everyWeekday', { time })
    case 'weekly':
      return t('ui.schedules.everyWeek', { day: t(`ui.schedules.weekdays.${task.weekday}`), time })
    case 'monthly':
      return t('ui.schedules.everyMonth', { day: task.dayOfMonth, time })
    case 'once':
      return task.at ? t('ui.schedules.onceAt', { at: when(task.at) }) : t('ui.schedules.kinds.once')
    default:
      return ''
  }
}

const when = (iso: string) => new Date(iso).toLocaleString([], { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
const statusText = (s: string) => (s ? t(`ui.schedules.status.${s}`, s) : '')

onMounted(loadSchedules)
const close = () => (state.schedulesOpen = false)
</script>

<template>
  <div class="scrim" @mousedown.self="close" @keydown.esc="close">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="t('ui.schedules.title')">
      <header>
        <div class="badge"><CalendarClock :size="20" /></div>
        <div class="head-text">
          <h2>{{ t('ui.schedules.title') }}</h2>
          <p>{{ t('ui.schedules.subtitle') }}</p>
        </div>
        <button v-if="!editing" type="button" class="btn primary" @click="openNew()"><Plus :size="15" /> {{ t('ui.schedules.new') }}</button>
        <button type="button" class="icon-btn" :aria-label="t('settings.close')" @click="close"><X :size="18" /></button>
      </header>

      <!-- 新建 / 编辑 -->
      <div v-if="editing" class="body form">
        <label class="field">
          <span>{{ t('ui.schedules.name') }}</span>
          <input v-model="form.name" maxlength="100" :placeholder="t('ui.schedules.namePlaceholder')" />
        </label>
        <label class="field">
          <span>{{ t('ui.schedules.instructions') }}</span>
          <textarea v-model="form.instructions" rows="4" maxlength="4000" :placeholder="t('ui.schedules.instructionsPlaceholder')" />
          <small>{{ t('ui.schedules.instructionsHint') }}</small>
        </label>

        <div class="row">
          <label class="field">
            <span>{{ t('ui.schedules.frequency') }}</span>
            <select v-model="form.kind">
              <option v-for="k in kinds" :key="k" :value="k">{{ t(`ui.schedules.kinds.${k}`) }}</option>
            </select>
          </label>
          <label v-if="form.kind === 'weekly'" class="field">
            <span>{{ t('ui.schedules.weekday') }}</span>
            <select v-model.number="form.weekday">
              <option v-for="d in weekdays" :key="d" :value="d">{{ t(`ui.schedules.weekdays.${d}`) }}</option>
            </select>
          </label>
          <label v-if="form.kind === 'monthly'" class="field">
            <span>{{ t('ui.schedules.dayOfMonth') }}</span>
            <select v-model.number="form.dayOfMonth">
              <option v-for="d in 31" :key="d" :value="d">{{ d }}</option>
            </select>
          </label>
          <label v-if="needsTime" class="field narrow">
            <span>{{ t('ui.schedules.time') }}</span>
            <span class="time">
              <select v-model.number="form.hour">
                <option v-for="h in 24" :key="h" :value="h - 1">{{ String(h - 1).padStart(2, '0') }}</option>
              </select>
              :
              <select v-model.number="form.minute">
                <option v-for="m in [0, 10, 15, 20, 30, 40, 45, 50]" :key="m" :value="m">{{ String(m).padStart(2, '0') }}</option>
              </select>
            </span>
          </label>
          <label v-if="form.kind === 'hourly'" class="field narrow">
            <span>{{ t('ui.schedules.minute') }}</span>
            <select v-model.number="form.minute">
              <option v-for="m in [0, 10, 15, 20, 30, 40, 45, 50]" :key="m" :value="m">{{ String(m).padStart(2, '0') }}</option>
            </select>
          </label>
          <label v-if="form.kind === 'once'" class="field">
            <span>{{ t('ui.schedules.at') }}</span>
            <input v-model="form.at" type="datetime-local" />
          </label>
        </div>

        <label class="field">
          <span>{{ t('ui.permission.title') }}</span>
          <select v-model="form.permission">
            <option v-for="p in permissions" :key="p" :value="p">{{ t(`ui.permission.${p}`) }}</option>
          </select>
          <small>{{ t('ui.schedules.permissionHint') }}</small>
        </label>

        <label class="check">
          <input v-model="form.catchUp" type="checkbox" />
          <span>
            <strong>{{ t('ui.schedules.catchUp') }}</strong>
            <small>{{ t('ui.schedules.catchUpHint') }}</small>
          </span>
        </label>

        <div class="actions">
          <button type="button" class="btn" @click="editing = null">{{ t('ui.fullAccess.cancel') }}</button>
          <button type="button" class="btn primary" :disabled="!canSave || busy === 'save'" @click="save">
            <component :is="busy === 'save' ? Loader2 : Check" :size="15" :class="{ spin: busy === 'save' }" />
            {{ t('ui.schedules.save') }}
          </button>
        </div>
      </div>

      <!-- 列表 -->
      <div v-else class="body">
        <template v-if="state.schedules.length">
          <div v-for="task in state.schedules" :key="task.id" class="card" :class="{ off: !task.enabled }">
            <div class="row-main">
              <span class="ico"><CalendarClock :size="16" /></span>
              <span class="main">
                <strong>{{ task.name }}</strong>
                <small class="ins">{{ task.instructions }}</small>
                <small class="meta">
                  <Clock :size="11" />{{ describe(task) }}
                  <template v-if="task.enabled && task.nextRunAt">· {{ t('ui.schedules.next', { at: when(task.nextRunAt) }) }}</template>
                </small>
              </span>
              <span v-if="task.lastStatus" class="status" :class="task.lastStatus">{{ statusText(task.lastStatus) }}</span>
              <button
                type="button"
                class="switch"
                role="switch"
                :aria-checked="task.enabled"
                :class="{ on: task.enabled }"
                :title="t('ui.skills.toggle')"
                @click="toggle(task)"
              >
                <i />
              </button>
            </div>
            <div class="row-actions">
              <button type="button" class="btn" :disabled="busy === task.id" @click="runNow(task)">
                <component :is="busy === task.id ? Loader2 : Play" :size="14" :class="{ spin: busy === task.id }" />
                {{ t('ui.schedules.runNow') }}
              </button>
              <button type="button" class="btn" @click="openEdit(task)">{{ t('menu.rename') }}</button>
              <button
                v-if="task.lastConversationId"
                type="button"
                class="btn"
                @click="state.schedulesOpen = false; openConversation(task.lastConversationId!)"
              >
                {{ t('ui.schedules.lastRun') }}
              </button>
              <span class="spacer" />
              <small v-if="task.runCount" class="count">{{ t('ui.schedules.runCount', { n: task.runCount }) }}</small>
              <button type="button" class="mini" :title="t('menu.delete')" @click="removing = task"><Trash2 :size="14" /></button>
            </div>
          </div>
        </template>

        <template v-else>
          <p class="empty"><CalendarClock :size="20" /> {{ t('ui.schedules.empty') }}</p>
          <h3>{{ t('ui.schedules.templatesTitle') }}</h3>
          <div class="templates">
            <button v-for="x in templates" :key="x.key" type="button" class="template" @click="openNew(x)">
              <span class="ico"><component :is="x.icon" :size="16" /></span>
              <span class="text">
                <strong>{{ t(`ui.schedules.templates.${x.key}.name`) }}</strong>
                <small>{{ t(`ui.schedules.templates.${x.key}.instructions`) }}</small>
              </span>
            </button>
          </div>
        </template>
      </div>

      <footer>
        <span class="note"><CircleAlert :size="13" /> {{ t('ui.schedules.footer') }}</span>
        <span class="spacer" />
        <button type="button" class="btn primary" @click="close">{{ t('settings.close') }}</button>
      </footer>
    </div>

    <div v-if="removing" class="scrim inner" @mousedown.self="removing = null">
      <div class="confirm" role="alertdialog">
        <h3>{{ t('ui.schedules.deleteTitle', { name: removing.name }) }}</h3>
        <div class="actions">
          <button type="button" class="btn" @click="removing = null">{{ t('ui.fullAccess.cancel') }}</button>
          <button type="button" class="btn danger" @click="confirmRemove">{{ t('menu.delete') }}</button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.scrim {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: grid;
  place-items: center;
  padding: 16px;
  background: color-mix(in srgb, var(--ink) 28%, transparent);
}
.scrim.inner {
  z-index: 70;
}
.dialog {
  display: flex;
  flex-direction: column;
  width: min(720px, 100%);
  height: min(640px, calc(100vh - 32px));
  padding: 22px 24px 16px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  animation: pop 160ms ease-out;
}
header {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 8px;
}
.badge {
  display: grid;
  place-items: center;
  flex: none;
  width: 40px;
  height: 40px;
  border-radius: 12px;
  background: var(--indigo-wash);
  color: var(--indigo);
}
.head-text {
  flex: 1;
  min-width: 0;
}
h2 {
  margin: 0;
  font-size: var(--t-lg);
  font-weight: 600;
}
.head-text p {
  margin: 2px 0 0;
  font-size: var(--t-sm);
  color: var(--ink-faint);
}
h3 {
  margin: 18px 0 10px;
  font-size: var(--t-sm);
  font-weight: 600;
  color: var(--ink-soft);
}
.body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 10px 2px;
}
.card {
  margin-bottom: 8px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
}
.card.off .main {
  opacity: 0.55;
}
.row-main {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 11px 12px 8px;
}
.ico {
  display: grid;
  place-items: center;
  flex: none;
  width: 30px;
  height: 30px;
  border-radius: 8px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
}
.main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
}
.main strong {
  font-size: var(--t-sm);
  font-weight: 600;
}
.ins {
  font-size: var(--t-xs);
  color: var(--ink-faint);
  overflow: hidden;
  text-overflow: ellipsis;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}
.meta {
  display: flex;
  align-items: center;
  gap: 5px;
  font-size: 11px;
  color: var(--ink-soft);
}
.status {
  flex: none;
  padding: 1px 8px;
  border-radius: 9px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
  font-size: 11px;
}
.status.ok {
  background: var(--thread-wash);
  color: var(--thread);
}
.status.failed {
  background: var(--red-wash);
  color: var(--red);
}
.status.running {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.switch {
  position: relative;
  flex: none;
  width: 38px;
  height: 21px;
  border-radius: 11px;
  background: var(--line-strong);
  transition: background 140ms;
}
.switch i {
  position: absolute;
  top: 3px;
  left: 3px;
  width: 15px;
  height: 15px;
  border-radius: 50%;
  background: #fff;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.2);
  transition: transform 140ms;
}
.switch.on {
  background: var(--indigo);
}
.switch.on i {
  transform: translateX(17px);
}
.row-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 12px 10px 52px;
}
.row-actions .btn {
  height: 28px;
  padding: 0 10px;
  font-size: var(--t-xs);
}
.count {
  font-size: 11px;
  color: var(--ink-faint);
}
.mini {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 6px;
  color: var(--ink-faint);
}
.mini:hover {
  background: var(--line);
  color: var(--red);
}
.empty {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  margin: 26px 0 0;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.templates {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}
.template {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  text-align: left;
}
.template:hover {
  border-color: var(--indigo);
  background: var(--indigo-wash);
}
.template .text {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}
.template strong {
  font-size: var(--t-sm);
  font-weight: 500;
}
.template small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
  line-height: 1.45;
  overflow: hidden;
  text-overflow: ellipsis;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}
.form {
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.field {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
  flex: 1;
}
.field > span {
  font-size: var(--t-sm);
  font-weight: 500;
}
.field small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.field input,
.field textarea,
.field select {
  padding: 8px 11px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-sm);
  font-family: inherit;
  outline: 0;
}
.field textarea {
  resize: vertical;
  line-height: 1.6;
}
.field input:focus,
.field textarea:focus,
.field select:focus {
  border-color: var(--indigo);
}
.field.narrow {
  flex: 0 0 auto;
}
.time {
  display: flex;
  align-items: center;
  gap: 4px;
}
.row {
  display: flex;
  gap: 10px;
  align-items: flex-end;
}
.check {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 10px 12px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
  cursor: pointer;
}
.check input {
  margin-top: 2px;
  width: 16px;
  height: 16px;
  accent-color: var(--indigo);
}
.check span {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.check strong {
  font-size: var(--t-sm);
  font-weight: 500;
}
.check small {
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
footer {
  display: flex;
  align-items: center;
  gap: 10px;
  padding-top: 12px;
  border-top: 1px solid var(--line);
}
.note {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.spacer {
  flex: 1;
}
.confirm {
  width: min(400px, 100%);
  padding: 22px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
.confirm h3 {
  margin: 0 0 18px;
  font-size: var(--t-lg);
  font-weight: 600;
}
.spin {
  animation: spin 900ms linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
@keyframes pop {
  from {
    opacity: 0;
    transform: translateY(6px) scale(0.98);
  }
}
</style>
