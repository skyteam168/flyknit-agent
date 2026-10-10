<script lang="ts">
import { ref } from 'vue'

/** 关掉的是哪个版本。侧边栏里的和浮在左下角的是两个组件实例，共用这一个，免得收起侧边栏又冒出来 */
const dismissed = ref('')
</script>

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowUp, RotateCw, X } from '@lucide/vue'
import { bridge } from '../bridge'
import { state, toast } from '../store'

// 新版本就绪时左下角（侧边栏用户信息上方）的一张小卡片。
//
// 要紧的是**不打断**：员工手上可能正跑着一个任务，弹窗会打断他。所以做成一张小卡片，
// 点「重启升级」才会立刻装；不点也不会一直缠着问——退出程序或者下次开机会自己装上。
// 关掉这条只是不想看见它，不是取消更新。
const { t } = useI18n()
// 平时放在侧边栏用户信息上方；侧边栏收起时 App 把它浮在左下角（floating）
defineProps<{ floating?: boolean }>()

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
  <div v-if="show && info" class="card" :class="{ blocked: info.stage === 'needsit', floating }">
    <ArrowUp v-if="info.stage === 'ready'" :size="15" class="mark" />
    <span class="what" :title="info.stage === 'ready' ? info.version : info.message">
      {{ info.stage === 'ready' ? t('ui.update.ready') : t('ui.update.needsIt') }}
    </span>

    <template v-if="info.stage === 'ready'">
      <button v-if="info.notes" type="button" class="link" @click="notesOpen = true">
        {{ t('ui.update.notes') }}
      </button>
      <button type="button" class="go" :disabled="busy" @click="apply">
        {{ t('ui.update.restart') }}
      </button>
    </template>

    <button type="button" class="close" :aria-label="t('ui.update.later')" :title="t('ui.update.laterHint')"
            @click="dismissed = info.version">
      <X :size="11" :stroke-width="2.5" />
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
.card {
  position: relative;
  display: flex;
  gap: 6px;
  align-items: center;
  margin: 8px 12px 0;
  padding: 8px 8px 8px 10px;
  border: 1px solid color-mix(in srgb, var(--indigo) 35%, var(--line));
  border-radius: 12px;
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-xs);
  box-shadow: var(--shadow-card);
  animation: rise 0.2s ease-out;
}
.card.blocked {
  border-color: color-mix(in srgb, var(--amber) 40%, var(--line));
}
/* 侧边栏收起时浮在窗口左下角 */
.card.floating {
  position: fixed;
  bottom: 16px;
  left: 16px;
  z-index: 40;
  margin: 0;
  width: 280px;
  box-shadow: var(--shadow-pop);
}
@keyframes rise {
  from {
    opacity: 0;
    transform: translateY(6px);
  }
}
.mark {
  flex: none;
  color: var(--indigo);
}
.what {
  flex: 1 0 auto;
  overflow: hidden;
  min-width: 0;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.blocked .what {
  flex: 1;
  color: var(--amber);
  white-space: normal;
}
.link,
.go {
  flex: none;
  height: 26px;
  padding: 0 8px;
  border-radius: 7px;
  font: inherit;
  font-size: var(--t-xs);
  font-weight: 500;
  cursor: pointer;
  white-space: nowrap;
}
.link {
  border: 1px solid color-mix(in srgb, var(--indigo) 45%, transparent);
  background: transparent;
  color: var(--indigo);
}
.link:hover {
  background: var(--indigo-wash);
}
.go {
  border: 0;
  background: var(--indigo);
  color: #fff;
}
.go:hover {
  background: var(--indigo-hover);
}
.go:disabled {
  opacity: 0.6;
  cursor: default;
}
/* 右上角的小圆叉：不占一行的位置 */
.close {
  position: absolute;
  top: -7px;
  right: -7px;
  display: grid;
  place-items: center;
  width: 18px;
  height: 18px;
  border: 1px solid var(--line-strong);
  border-radius: 50%;
  background: var(--cloth);
  color: var(--ink-faint);
  cursor: pointer;
  opacity: 0;
  transition: opacity 0.15s;
}
.card:hover .close,
.close:focus-visible {
  opacity: 1;
}
.close:hover {
  color: var(--ink);
}
.sheet .close {
  position: static;
  width: 26px;
  height: 26px;
  border: 0;
  opacity: 1;
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
