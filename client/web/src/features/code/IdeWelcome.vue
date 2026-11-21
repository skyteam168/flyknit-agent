<script setup lang="ts">
/**
 * 编码模式里还没说话的新任务：右边对话区的欢迎页（参照 Cursor 新开 Agent 时的空白页），不留一大片空白。
 * - 标志 + 「新对话」+ 在哪个项目里；
 * - 带了交接摘要的：整理中显示进度（骨架条），整理好显示摘要开头，可以展开、「接着做」（一键发出）、「不带了」；
 * - 没带摘要的：几个常用的开头，点一下放进输入框（员工看一眼、补几句再发）。
 * 不在整理好后自动发出：员工可能还想补充这次要做什么；「接着做」一下就发，不比自动发慢。
 */
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { FileSearch, Sparkles, TestTubeDiagonal, Wrench } from '@lucide/vue'
import KnitMark from '../../components/KnitMark.vue'
import { current, send } from '../../store'
import { dropHandoff, handoffs, projects } from './store'

const emit = defineEmits<{ fill: [text: string] }>()
const { t } = useI18n()

const handoff = computed(() => (current.value ? handoffs[current.value.id] : undefined))
const project = computed(() => {
  const id = current.value?.id
  const name = id ? projects[id]?.projectName : ''
  return name || (current.value?.workspace ?? '').split(/[\\/]/).filter(Boolean).pop() || ''
})
const expanded = ref(false)
/** 摘要开头几行（展开看全部） */
const preview = computed(() => {
  const lines = (handoff.value?.summary ?? '').split('\n').filter((l) => l.trim())
  return expanded.value ? lines : lines.slice(0, 6)
})
const more = computed(() => (handoff.value?.summary ?? '').split('\n').filter((l) => l.trim()).length > 6)

const starters = computed(() => [
  { icon: FileSearch, text: t('ui.code.welcome.starterExplain') },
  { icon: Wrench, text: t('ui.code.welcome.starterFix') },
  { icon: TestTubeDiagonal, text: t('ui.code.welcome.starterTest') },
])

function goOn() {
  void send(t('ui.code.welcome.continueText'))
}
</script>

<template>
  <div class="ide-welcome">
    <div class="hero">
      <KnitMark :size="56" />
      <h2>{{ t('ui.code.welcome.title') }}</h2>
      <p v-if="project" class="where">{{ t('ui.code.welcome.inProject', { name: project }) }}</p>
    </div>

    <div v-if="handoff" class="handoff" :class="handoff.status">
      <div class="head">
        <Sparkles :size="14" class="icon" />
        <span>{{ t(handoff.status === 'working' ? 'ui.code.welcome.handoffWorking' : 'ui.code.welcome.handoffReady', { title: handoff.fromTitle || t('ui.code.agent.untitled') }) }}</span>
      </div>
      <div v-if="handoff.status === 'working'" class="skeleton" aria-hidden="true">
        <span style="width: 72%" />
        <span style="width: 90%" />
        <span style="width: 58%" />
      </div>
      <template v-else>
        <div class="summary">
          <p v-for="(line, i) in preview" :key="i" :class="{ heading: line.startsWith('#') }">{{ line.replace(/^#+\s*/, '') }}</p>
        </div>
        <button v-if="more" type="button" class="link" @click="expanded = !expanded">{{ expanded ? t('ui.code.ctx.hide') : t('ui.code.welcome.showAll') }}</button>
      </template>
      <div class="actions">
        <button type="button" class="act primary" :disabled="handoff.status !== 'ready'" @click="goOn">{{ t('ui.code.welcome.continue') }}</button>
        <button type="button" class="act" @click="dropHandoff(current!.id)">{{ t('ui.code.ctx.drop') }}</button>
      </div>
      <p class="tip">{{ t(handoff.status === 'working' ? 'ui.code.welcome.tipWorking' : 'ui.code.welcome.tipReady') }}</p>
    </div>

    <div v-else class="starters">
      <p class="tip">{{ t('ui.code.welcome.tip') }}</p>
      <button v-for="s in starters" :key="s.text" type="button" class="starter" @click="emit('fill', s.text)">
        <component :is="s.icon" :size="14" />
        <span>{{ s.text }}</span>
      </button>
    </div>
  </div>
</template>

<style scoped>
.ide-welcome {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 18px;
  min-height: 0;
  padding: 24px 20px;
  overflow-y: auto;
}
.hero {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  text-align: center;
}
.hero h2 {
  margin: 6px 0 0;
  color: var(--ink);
  font-size: calc(18px * var(--font-scale));
  font-weight: 600;
}
.where {
  margin: 0;
  color: var(--ink-faint);
  font-size: calc(12.5px * var(--font-scale));
}
.handoff,
.starters {
  display: flex;
  flex-direction: column;
  gap: 10px;
  width: 100%;
  max-width: 440px;
}
.handoff {
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--loom);
}
.head {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  color: var(--ink);
  font-size: calc(13px * var(--font-scale));
}
.icon {
  flex: none;
  margin-top: 2px;
  color: var(--indigo);
}
.working .icon {
  animation: pulse 1.2s ease-in-out infinite;
}
@keyframes pulse {
  50% {
    opacity: 0.35;
  }
}
.skeleton {
  display: flex;
  flex-direction: column;
  gap: 7px;
  padding: 2px 0 2px 22px;
}
.skeleton span {
  height: 8px;
  border-radius: 4px;
  background: linear-gradient(90deg, color-mix(in srgb, var(--ink) 7%, transparent) 25%, color-mix(in srgb, var(--ink) 14%, transparent) 50%, color-mix(in srgb, var(--ink) 7%, transparent) 75%);
  background-size: 200% 100%;
  animation: shimmer 1.4s linear infinite;
}
@keyframes shimmer {
  from {
    background-position: 200% 0;
  }
  to {
    background-position: -200% 0;
  }
}
.summary {
  max-height: 260px;
  padding-left: 22px;
  overflow-y: auto;
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
  line-height: 1.6;
}
.summary p {
  margin: 0;
  white-space: pre-wrap;
}
.summary p.heading {
  margin-top: 4px;
  color: var(--ink);
  font-weight: 600;
}
.link {
  align-self: flex-start;
  margin-left: 22px;
  padding: 0;
  border: 0;
  background: none;
  color: var(--indigo);
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.actions {
  display: flex;
  gap: 8px;
  padding-left: 22px;
}
.act {
  padding: 4px 14px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font-size: calc(12.5px * var(--font-scale));
  cursor: pointer;
}
.act:hover:not(:disabled) {
  border-color: var(--indigo);
}
.act:disabled {
  opacity: 0.5;
  cursor: default;
}
.act.primary {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
.tip {
  margin: 0;
  color: var(--ink-faint);
  font-size: calc(12px * var(--font-scale));
}
.handoff .tip {
  padding-left: 22px;
}
.starters .tip {
  text-align: center;
}
.starter {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 9px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--loom);
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
  text-align: left;
  cursor: pointer;
}
.starter:hover {
  border-color: var(--indigo);
  color: var(--ink);
}
</style>
