<script setup lang="ts">
import { computed, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { CircleAlert, CircleCheck, CircleStop, Clock, LoaderCircle, ShieldAlert, Split } from '@lucide/vue'
import { answerConfirm, current, loadGroups, openConversation, pendingConfirmsOf, state } from '../store'
import type { GroupView } from '../types'
import { bridge } from '../bridge'

/**
 * 汇总卡（docs/c2-router.md 3.3）：这个对话拆出来的几件事各自做到哪了；
 * 同一组里等确认的操作合在这里一起看——每条可以单独允许 / 拒绝，「全部允许」只管不是高危的（高危必须逐条点）。
 * 合并只是换个地方呈现，每一条照旧走原来的确认、照旧写审计。
 */
const { t } = useI18n()

/** PR 链接：交给宿主打开（宿主按「盯 PR」的记录找地址，不接受界面传任意网址） */
function openPr(conversationId: string, prId: string) {
  void bridge.repoOpen(conversationId, 'pr', prId).catch(() => undefined)
}

const groups = computed<GroupView[]>(() => (current.value ? state.groups[current.value.id] ?? [] : []))
watch(
  () => current.value?.id,
  (id) => {
    if (id && state.groups[id] === undefined) void loadGroups(id)
  },
  { immediate: true },
)

const confirmsOf = (g: GroupView) => pendingConfirmsOf(g.items.map((i) => i.conversationId))
const titleOf = (g: GroupView, conversationId: string) => g.items.find((i) => i.conversationId === conversationId)?.title ?? ''

const icon = (s: string) =>
  s === 'done' ? CircleCheck : s === 'stopped' ? CircleStop : s === 'failed' || s === 'interrupted' ? CircleAlert : s === 'pending' ? Clock : LoaderCircle

/** 「全部允许」只管看得出是只读 / 写入的；高危的、认不出来的都必须逐条点（不变量 3） */
const bulkOk = (effect: string) => effect === 'read' || effect === 'write'

async function allowAllSafe(g: GroupView) {
  for (const c of confirmsOf(g).filter((x) => bulkOk(x.effect))) {
    await answerConfirm(c.conversationId, c.callId, 'allowOnce')
  }
}
</script>

<template>
  <section v-for="g in groups" :key="g.groupId" class="group" :aria-label="t('ui.router.groupTitle', { n: g.items.length })">
    <div class="head">
      <Split :size="14" />
      <strong>{{ t('ui.router.groupTitle', { n: g.items.length }) }}</strong>
      <span class="muted">{{ t('ui.router.groupDone', { done: g.items.filter((i) => ['done', 'failed', 'stopped'].includes(i.state)).length, n: g.items.length }) }}</span>
    </div>
    <button v-for="it in g.items" :key="it.taskId" type="button" class="item" :title="t('ui.tasks.open')" @click="openConversation(it.conversationId)">
      <component :is="icon(it.state)" :size="14" class="st" :class="[`st-${it.state}`, { spin: it.state === 'running' }]" />
      <span class="name">{{ it.title }}</span>
      <span class="muted">{{ it.state === 'running' && it.step ? it.step : t(`ui.tasks.state.${it.state}`) }}</span>
      <span v-if="it.total" class="muted">{{ it.done }}/{{ it.total }}</span>
    </button>
    <div v-for="it in g.items.filter((x) => x.branch || x.prUrl)" :key="it.taskId + '-iso'" class="iso">
      <span class="muted">{{ it.title.split(' · ')[0] }}</span>
      <code v-if="it.branch">{{ it.branch }}</code>
      <a v-if="it.prUrl && it.prId" href="#" @click.prevent="openPr(it.conversationId, it.prId)">{{ t('ui.router.openPr') }}</a>
    </div>

    <!-- 合并确认：同一组里等你确认的操作 -->
    <div v-if="confirmsOf(g).length" class="confirms">
      <div class="chead">
        <ShieldAlert :size="14" />
        <strong>{{ t('ui.router.confirmTitle', { n: confirmsOf(g).length }) }}</strong>
        <button
          v-if="confirmsOf(g).some((c) => bulkOk(c.effect))"
          type="button"
          class="btn"
          @click="allowAllSafe(g)"
        >{{ t('ui.router.allowAllSafe') }}</button>
      </div>
      <div v-for="c in confirmsOf(g)" :key="c.conversationId + c.callId" class="crow" :class="{ danger: c.effect === 'destructive' }">
        <span class="which">{{ titleOf(g, c.conversationId) }}</span>
        <span class="what">{{ c.summary || c.name }}</span>
        <span v-if="c.effect === 'destructive'" class="tag">{{ t('ui.router.destructive') }}</span>
        <button type="button" class="btn" @click="answerConfirm(c.conversationId, c.callId, 'reject')">{{ t('ui.router.reject') }}</button>
        <button type="button" class="btn primary" @click="answerConfirm(c.conversationId, c.callId, 'allowOnce')">{{ t('ui.router.allow') }}</button>
      </div>
    </div>
  </section>
</template>

<style scoped>
.group {
  max-height: 40vh;
  overflow-y: auto;
  max-width: 760px;
  margin: 0 auto 8px;
  padding: 10px 12px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
  font-size: var(--t-sm);
  width: calc(100% - 32px);
}
.head,
.chead {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 6px;
}
.head strong,
.chead strong {
  flex: 1;
}
.muted {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 5px 6px;
  border: 0;
  border-radius: var(--r-sm);
  background: none;
  color: var(--ink);
  text-align: left;
  cursor: pointer;
}
.item:hover {
  background: var(--cloth-sunk);
}
.item .name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.st.st-done {
  color: var(--green, #16a34a);
}
.st.st-failed,
.st.st-interrupted {
  color: var(--danger, #dc2626);
}
.st.st-running {
  color: var(--indigo);
}
.spin {
  animation: spin 1.2s linear infinite;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
.iso {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 2px 6px 2px 28px;
  font-size: var(--t-xs);
}
.iso code {
  overflow: hidden;
  color: var(--ink-soft);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.iso a {
  color: var(--indigo);
}
.confirms {
  margin-top: 8px;
  padding: 8px 10px;
  border-radius: var(--r-sm);
  background: color-mix(in srgb, var(--amber, #d97706) 10%, transparent);
}
.crow {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 0;
}
.crow .which {
  flex: none;
  max-width: 30%;
  overflow: hidden;
  color: var(--ink-soft);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.crow .what {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-family: Consolas, 'Cascadia Mono', monospace;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.crow.danger .what {
  color: var(--danger, #dc2626);
}
.tag {
  flex: none;
  padding: 0 6px;
  border-radius: 999px;
  background: var(--danger, #dc2626);
  color: #fff;
  font-size: var(--t-xs);
}
.btn {
  flex: none;
  padding: 2px 10px;
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-xs);
  cursor: pointer;
}
.btn.primary {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
</style>
