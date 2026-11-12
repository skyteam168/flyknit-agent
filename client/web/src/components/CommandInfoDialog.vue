<script setup lang="ts">
// 斜杠命令 /status（这个对话的当前设置一览）和 /help（全部命令）打开的信息卡。只读，不改任何设置。
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { Info, SquareSlash, X } from '@lucide/vue'
import { current, draftMode, refreshUsage, selectedModelId, selectedPermission, selectedWorkspace, state } from '../store'
import { BUILTINS, describeBuiltin } from '../builtinCommands'

const { t } = useI18n()
const kind = computed(() => state.commandInfo)
const close = () => (state.commandInfo = '')
const fmt = new Intl.NumberFormat()

// 焦点还在输入框里时按 Esc 也能关
const onKey = (e: KeyboardEvent) => {
  if (e.key === 'Escape') close()
}
onMounted(() => {
  if (kind.value === 'status') void refreshUsage()
  window.addEventListener('keydown', onKey)
})
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))

const mode = computed(() => current.value?.mode ?? draftMode.mode)
const model = computed(() => state.models.find((m) => m.id === selectedModelId.value)?.name ?? t('ui.model.auto'))
const connectors = computed(() => {
  const on = state.mcp.filter((v) => v.enabled)
  return { connected: on.filter((v) => v.status === 'connected').length, total: on.length }
})
const usage = computed(() => {
  const u = state.usage
  if (!u) return '—'
  return u.dailyLimit ? `${fmt.format(u.todayTokens)} / ${fmt.format(u.dailyLimit)}` : `${fmt.format(u.todayTokens)} · ${t('ui.usagePanel.noLimit')}`
})

const rows = computed(() => [
  { label: t('ui.slash.status.version'), value: state.app?.version ?? '—' },
  { label: t('ui.slash.status.mode'), value: t(`mode.${mode.value}`) },
  { label: t('ui.slash.status.model'), value: model.value },
  ...(mode.value === 'agent'
    ? [
        { label: t('ui.slash.status.permission'), value: t(`ui.permission.${selectedPermission.value}`) },
        { label: t('ui.slash.status.workspace'), value: selectedWorkspace.value || '—' },
        { label: t('ui.slash.status.plan'), value: (current.value ? state.planMode[current.value.id] : draftMode.planMode) ? t('ui.slash.status.on') : t('ui.slash.status.off') },
      ]
    : []),
  { label: t('ui.slash.status.connectors'), value: connectors.value.total ? t('ui.slash.status.connectorsValue', connectors.value) : '—' },
  { label: t('ui.slash.status.usage'), value: usage.value },
  { label: t('ui.slash.status.server'), value: state.app?.connected ? t('ui.slash.status.online') : t('ui.slash.status.offline') },
])

const commandRows = computed(() =>
  BUILTINS.map((c) => ({ name: `/${c.name}${c.aliases?.length ? ` (/${c.aliases.join(', /')})` : ''}`, desc: describeBuiltin(c) })),
)
const quick = computed(() => [
  ...state.commands.it.map((c) => ({ name: `/${c.name}`, desc: c.title || c.template, src: 'it' })),
  ...state.commands.mine.map((c) => ({ name: `/${c.name}`, desc: c.title || c.template, src: 'mine' })),
])
</script>

<template>
  <div class="scrim" @mousedown.self="close" @keydown.esc="close">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="kind === 'status' ? t('ui.slash.status.title') : t('ui.slash.help.title')">
      <header>
        <div class="badge"><component :is="kind === 'status' ? Info : SquareSlash" :size="20" /></div>
        <div class="head-text">
          <h2>{{ kind === 'status' ? t('ui.slash.status.title') : t('ui.slash.help.title') }}</h2>
          <p>{{ kind === 'status' ? t('ui.slash.status.subtitle') : t('ui.slash.help.subtitle') }}</p>
        </div>
        <button type="button" class="icon-btn" :aria-label="t('settings.close')" @click="close"><X :size="18" /></button>
      </header>

      <div class="body">
        <dl v-if="kind === 'status'" class="rows">
          <template v-for="r in rows" :key="r.label">
            <dt>{{ r.label }}</dt>
            <dd>{{ r.value }}</dd>
          </template>
        </dl>

        <template v-else>
          <h3>{{ t('ui.slash.source.builtin') }}</h3>
          <dl class="rows">
            <template v-for="c in commandRows" :key="c.name">
              <dt><code>{{ c.name }}</code></dt>
              <dd>{{ c.desc }}</dd>
            </template>
          </dl>
          <template v-if="quick.length">
            <h3>{{ t('ui.slash.help.quick') }}</h3>
            <dl class="rows">
              <template v-for="c in quick" :key="c.src + c.name">
                <dt><code>{{ c.name }}</code></dt>
                <dd>{{ c.desc }} <small>· {{ t(`ui.slash.source.${c.src}`) }}</small></dd>
              </template>
            </dl>
          </template>
          <p class="tip">{{ t('ui.slash.help.tip') }}</p>
        </template>
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
.dialog {
  display: flex;
  flex-direction: column;
  width: min(560px, 100%);
  max-height: min(640px, calc(100vh - 32px));
  padding: 22px 24px 16px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
header {
  display: flex;
  align-items: center;
  gap: 12px;
}
.badge {
  display: grid;
  place-items: center;
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
.icon-btn {
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
  color: var(--ink-soft);
}
.icon-btn:hover {
  background: var(--cloth-sunk);
}
.body {
  margin-top: 14px;
  overflow: auto;
}
h3 {
  margin: 14px 0 6px;
  font-size: var(--t-sm);
  font-weight: 600;
  color: var(--ink-soft);
}
.rows {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 8px 16px;
  margin: 0;
  font-size: var(--t-sm);
}
dt {
  color: var(--ink-faint);
}
dd {
  margin: 0;
  min-width: 0;
  overflow-wrap: anywhere;
  color: var(--ink);
}
dd small {
  color: var(--ink-faint);
}
code {
  color: var(--indigo);
  font-family: var(--font-code);
}
.tip {
  margin: 14px 0 0;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
</style>
