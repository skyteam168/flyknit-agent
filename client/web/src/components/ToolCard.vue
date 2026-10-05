<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Ban, Check, ChevronDown, CircleAlert, Hand, LoaderCircle, Puzzle, ShieldAlert } from '@lucide/vue'
import type { ToolActivity } from '../types'

const props = defineProps<{ tool: ToolActivity; conversationId?: string }>()
const { t, te } = useI18n()
const open = ref(false)

const label = computed(() => (te(`tool.names.${props.tool.name}`) ? t(`tool.names.${props.tool.name}`) : props.tool.name))
const icon = computed(
  () =>
    ({ running: LoaderCircle, waiting: Hand, done: Check, failed: CircleAlert, blocked: ShieldAlert, rejected: Ban })[
      props.tool.state
    ],
)
// 计划和记忆类工具只显示一行，不展开结果
const quiet = computed(() => ['update_plan', 'memory_write'].includes(props.tool.name))
// 加载技能单独做成醒目的卡片：技能决定了 AI 的做法，用户要能一眼看到用了哪个
const isSkill = computed(() => props.tool.name === 'load_skill')
</script>

<template>
  <div class="tool" :class="[tool.state, { quiet, skill: isSkill }]">
    <button type="button" class="row" :disabled="!tool.output || quiet" :aria-expanded="open" @click="open = !open">
      <component :is="isSkill ? Puzzle : icon" :size="15" class="state-icon" :class="{ spin: !isSkill && tool.state === 'running' }" />
      <span class="name">{{ isSkill ? t('ui.skills.usingSkill') : label }}</span>
      <span class="summary" :class="{ strong: isSkill }">{{ tool.summary }}</span>
      <span class="state-text">{{ tool.remembered && tool.state === 'done' ? t('tool.remembered') : t(`tool.${tool.state}`) }}</span>
      <ChevronDown v-if="tool.output && !quiet" :size="15" class="chev" :class="{ up: open }" />
    </button>

    <pre v-if="open && tool.output" class="output">{{ tool.output }}</pre>

    <div v-if="tool.state === 'blocked' && tool.output" class="banner blocked">
      <ShieldAlert :size="16" />
      <div>
        <strong>{{ t('tool.blockedTitle') }}</strong>
        <p>{{ tool.output.replace(/^已被安全策略阻止：/, '').split('。请不要')[0] }}</p>
      </div>
    </div>

    <div v-if="tool.state === 'waiting'" class="banner confirm">
      <span>{{ t('tool.confirmBelow') }}</span>
    </div>
  </div>
</template>

<style scoped>
.tool {
  margin: 6px 0;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
  overflow: hidden;
}
.tool.skill {
  border-color: color-mix(in srgb, var(--indigo) 35%, var(--line));
  background: var(--indigo-wash);
}
.tool.skill .state-icon {
  color: var(--indigo);
}
.summary.strong {
  flex: none;
  color: var(--indigo);
  font-weight: 600;
  font-family: var(--font);
  font-size: var(--t-sm);
}
.tool.quiet {
  border-color: transparent;
  background: transparent;
}
.row {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  min-height: 38px;
  padding: 6px 12px;
  text-align: left;
  font-size: var(--t-sm);
}
.row:disabled {
  cursor: default;
}
.quiet .row {
  min-height: 28px;
  padding: 2px 4px;
  color: var(--ink-faint);
}
.state-icon {
  flex: none;
  color: var(--ink-faint);
}
.done .state-icon {
  color: var(--thread);
}
.failed .state-icon,
.blocked .state-icon {
  color: var(--red);
}
.waiting .state-icon {
  color: var(--amber);
}
.spin {
  animation: spin 900ms linear infinite;
}
.name {
  flex: none;
  font-weight: 500;
}
.summary {
  flex: 1;
  min-width: 0;
  color: var(--ink-soft);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  font-family: var(--font-code);
  font-size: var(--t-xs);
}
.state-text {
  flex: none;
  font-size: var(--t-xs);
  color: var(--ink-faint);
}
.chev {
  flex: none;
  color: var(--ink-faint);
  transition: transform 150ms;
}
.chev.up {
  transform: rotate(180deg);
}
.output {
  margin: 0;
  max-height: 280px;
  overflow: auto;
  padding: 10px 14px;
  border-top: 1px solid var(--line);
  background: var(--cloth-sunk);
  font-family: var(--font-code);
  font-size: var(--t-xs);
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
}
.banner {
  padding: 12px 14px;
  border-top: 1px solid var(--line);
}
.banner.blocked {
  display: flex;
  gap: 10px;
  background: var(--red-wash);
  color: var(--red);
}
.banner.blocked p {
  margin: 2px 0 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.banner.confirm {
  padding: 8px 14px;
  background: var(--amber-wash);
  border-top-color: color-mix(in srgb, var(--amber) 35%, transparent);
  color: var(--amber);
  font-size: var(--t-xs);
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
