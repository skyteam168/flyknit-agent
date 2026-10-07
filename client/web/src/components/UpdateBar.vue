<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowUp, RotateCw, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { state, toast } from '../store'

// 新版本就绪时挂在这里的一条。
//
// 要紧的是**不打断**：员工手上可能正跑着一个任务，弹窗会打断他。所以做成一条窄条，
// 点「重启升级」才会立刻装；不点也不会一直缠着问——退出程序或者下次开机会自己装上。
// 关掉这条只是不想看见它，不是取消更新。
const { t } = useI18n()

const dismissed = ref('')
const info = computed(() => state.update)
const show = computed(() =>
  info.value !== null &&
  (info.value.stage === 'ready' || info.value.stage === 'needsit') &&
  dismissed.value !== info.value.version,
)
const notesOpen = ref(false)
const busy = ref(false)

async function apply() {
  busy.value = true
  const r = await bridge.applyUpdate().catch(() => null)
  busy.value = false
  // 成功的话程序马上就退出了，这句只在失败时能被看见
  if (!r?.ok) toast(r?.message || t('ui.update.failed'))
}
</script>

<template>
  <div v-if="show && info" class="bar" :class="{ blocked: info.stage === 'needsit' }">
    <ArrowUp v-if="info.stage === 'ready'" :size="14" class="mark" />
    <span class="what">
      {{ info.stage === 'ready' ? t('ui.update.ready') : t('ui.update.needsIt') }}
      <small v-if="info.stage === 'ready'">{{ info.version }}</small>
    </span>

    <template v-if="info.stage === 'ready'">
      <button v-if="info.notes" type="button" class="link" @click="notesOpen = true">
        {{ t('ui.update.notes') }}
      </button>
      <button type="button" class="go" :disabled="busy" @click="apply">
        <RotateCw :size="13" /> {{ t('ui.update.restart') }}
      </button>
    </template>
    <span v-else class="where">{{ info.message }}</span>

    <button type="button" class="close" :aria-label="t('ui.update.later')" :title="t('ui.update.laterHint')"
            @click="dismissed = info.version">
      <X :size="14" />
    </button>
  </div>

  <!-- 更新日志 -->
  <div v-if="notesOpen && info" class="scrim" @mousedown.self="notesOpen = false">
    <div class="sheet" role="dialog" aria-modal="true">
      <header>
        <h3>{{ t('ui.update.notesTitle', { version: info.version }) }}</h3>
        <button type="button" class="close" @click="notesOpen = false"><X :size="16" /></button>
      </header>
      <pre>{{ info.notes }}</pre>
      <footer>
        <button type="button" class="btn" @click="notesOpen = false">{{ t('ui.update.later') }}</button>
        <button type="button" class="btn primary" :disabled="busy" @click="apply">{{ t('ui.update.restart') }}</button>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.bar {
  display: flex;
  gap: 10px;
  align-items: center;
  padding: 7px 10px 7px 12px;
  border-radius: var(--r-md);
  background: var(--thread-wash);
  color: var(--ink);
  font-size: var(--t-xs);
}
.bar.blocked {
  background: var(--amber-wash);
}
.mark {
  flex: none;
  color: var(--thread);
}
.what {
  flex: 1;
  display: flex;
  gap: 6px;
  align-items: baseline;
  min-width: 0;
  font-weight: 500;
}
.what small {
  color: var(--ink-faint);
  font-weight: 400;
}
.where {
  flex: none;
  overflow: hidden;
  max-width: 40%;
  color: var(--ink-faint);
  font-family: var(--font-code);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.link {
  flex: none;
  padding: 4px 9px;
  border: 1px solid var(--line-strong);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink-soft);
  font: inherit;
  font-size: var(--t-xs);
  cursor: pointer;
}
.link:hover {
  border-color: var(--ink-faint);
}
.go {
  display: inline-flex;
  flex: none;
  gap: 5px;
  align-items: center;
  padding: 4px 11px;
  border: 0;
  border-radius: 6px;
  background: var(--thread);
  color: #fff;
  font: inherit;
  font-size: var(--t-xs);
  font-weight: 500;
  cursor: pointer;
}
.go:disabled {
  opacity: 0.6;
  cursor: default;
}
.close {
  display: grid;
  flex: none;
  place-items: center;
  width: 22px;
  height: 22px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--ink-faint);
  cursor: pointer;
}
.close:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.scrim {
  position: fixed;
  inset: 0;
  z-index: 80;
  display: grid;
  place-items: center;
  padding: 24px;
  background: color-mix(in srgb, var(--ink) 28%, transparent);
}
.sheet {
  display: flex;
  flex-direction: column;
  width: min(560px, 100%);
  max-height: 70vh;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
.sheet header {
  display: flex;
  gap: 10px;
  align-items: center;
  padding: 16px 18px 8px;
}
.sheet h3 {
  flex: 1;
  margin: 0;
  font-size: calc(15px * var(--font-scale));
}
.sheet pre {
  overflow-y: auto;
  margin: 0;
  padding: 0 18px;
  color: var(--ink-soft);
  font-family: inherit;
  font-size: var(--t-sm);
  line-height: 1.6;
  white-space: pre-wrap;
}
.sheet footer {
  display: flex;
  gap: 8px;
  justify-content: flex-end;
  padding: 14px 18px 16px;
}
</style>
