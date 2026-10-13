<script setup lang="ts">
import { computed, nextTick, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, MessageCircleQuestion } from '@lucide/vue'
import { answerAsk, current, currentState } from '../store'
import type { AskAnswerInput } from '../types'

// 停靠在输入框上方的选项卡片：AI 缺信息时用选项提问，点一下就能回答，不用打字
const { t } = useI18n()

const tool = computed(() => Object.values(currentState.value?.tools ?? {}).find((x) => x.state === 'waiting' && x.ask) ?? null)
const questions = computed(() => tool.value?.ask?.questions ?? [])

/** 每个问题的选择：选中的选项，和「其他」里写的字（otherOn = 选了「其他」） */
const picks = reactive<{ selected: string[]; otherOn: boolean; other: string }[]>([])
const otherInputs = ref<HTMLInputElement[]>([])

watch(
  () => tool.value?.ask?.requestId,
  () => {
    picks.splice(0, picks.length, ...questions.value.map(() => ({ selected: [] as string[], otherOn: false, other: '' })))
  },
  { immediate: true },
)

function toggle(qi: number, label: string) {
  const q = questions.value[qi]
  const p = picks[qi]
  if (!q || !p) return
  if (q.multiSelect) {
    const i = p.selected.indexOf(label)
    if (i >= 0) p.selected.splice(i, 1)
    else p.selected.push(label)
  } else {
    p.selected = [label]
    p.otherOn = false
  }
}

async function chooseOther(qi: number) {
  const q = questions.value[qi]
  const p = picks[qi]
  if (!q || !p) return
  p.otherOn = q.multiSelect ? !p.otherOn : true
  if (!q.multiSelect) p.selected = []
  await nextTick()
  if (p.otherOn) otherInputs.value[qi]?.focus()
}

const answered = (qi: number) => {
  const p = picks[qi]
  return !!p && (p.selected.length > 0 || (p.otherOn && p.other.trim().length > 0))
}
const ready = computed(() => questions.value.length > 0 && questions.value.every((_, i) => answered(i)))

function submit() {
  if (!tool.value || !current.value || !ready.value) return
  const answers: AskAnswerInput[] = picks.map((p) => ({ selected: [...p.selected], other: p.otherOn ? p.other.trim() : '' }))
  void answerAsk(current.value.id, tool.value.callId, answers)
}

function skip() {
  if (tool.value && current.value) void answerAsk(current.value.id, tool.value.callId, null)
}
</script>

<template>
  <div v-if="tool && tool.ask" class="ask-bar" role="dialog" :aria-label="t('tool.askTitle')">
    <div class="head">
      <MessageCircleQuestion :size="16" />
      <strong>{{ t('tool.askTitle') }}</strong>
    </div>
    <div v-for="(q, qi) in questions" :key="qi" class="question" :class="{ sep: qi > 0 }">
      <div class="q-title">
        <span v-if="q.header" class="chip">{{ q.header }}</span>
        <span class="q-text">{{ qi + 1 }}. {{ q.question }}</span>
        <span v-if="q.multiSelect" class="multi">{{ t('tool.askMulti') }}</span>
      </div>
      <div class="options" :role="q.multiSelect ? 'group' : 'radiogroup'">
        <button
          v-for="o in q.options"
          :key="o.label"
          type="button"
          class="opt"
          :class="{ on: picks[qi]?.selected.includes(o.label), multi: q.multiSelect }"
          :role="q.multiSelect ? 'checkbox' : 'radio'"
          :aria-checked="picks[qi]?.selected.includes(o.label) ?? false"
          @click="toggle(qi, o.label)"
        >
          <span class="mark"><Check v-if="q.multiSelect && picks[qi]?.selected.includes(o.label)" :size="12" /></span>
          <span class="text">
            <span class="label">{{ o.label }}</span>
            <small v-if="o.description">{{ o.description }}</small>
          </span>
        </button>
        <div
          class="opt other"
          :class="{ on: picks[qi]?.otherOn, multi: q.multiSelect }"
          :role="q.multiSelect ? 'checkbox' : 'radio'"
          :aria-checked="picks[qi]?.otherOn ?? false"
          @click="!picks[qi]?.otherOn && chooseOther(qi)"
        >
          <span class="mark" @click.stop="chooseOther(qi)"><Check v-if="q.multiSelect && picks[qi]?.otherOn" :size="12" /></span>
          <span class="text">
            <span v-if="!picks[qi]?.otherOn" class="label">{{ t('tool.askOther') }}</span>
            <input
              v-else
              :ref="(el) => { if (el) otherInputs[qi] = el as HTMLInputElement }"
              v-model="picks[qi]!.other"
              type="text"
              maxlength="500"
              :placeholder="t('tool.askOtherPlaceholder')"
              @keydown.enter.prevent="submit"
            />
          </span>
        </div>
      </div>
    </div>
    <div class="actions">
      <button type="button" class="btn primary" :disabled="!ready" :title="ready ? '' : t('tool.askPending')" @click="submit">
        <Check :size="15" /> {{ t('tool.askSubmit') }}
      </button>
      <button type="button" class="btn" @click="skip">{{ t('tool.askSkip') }}</button>
    </div>
  </div>
</template>

<style scoped>
.ask-bar {
  max-width: var(--column);
  max-height: 60vh;
  overflow: auto;
  margin: 0 auto 10px;
  padding: 12px 14px;
  border: 1px solid color-mix(in srgb, var(--indigo) 30%, var(--line));
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-card);
  animation: rise 160ms ease-out;
}
.head {
  display: flex;
  align-items: center;
  gap: 8px;
  color: var(--indigo);
  font-size: var(--t-sm);
}
.question {
  padding: 10px 0 4px;
}
.question.sep {
  margin-top: 6px;
  border-top: 1px solid var(--line);
}
.q-title {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-bottom: 8px;
  color: var(--ink);
  font-size: var(--t-md);
}
.chip {
  padding: 1px 8px;
  border-radius: 9px;
  background: var(--indigo-wash);
  color: var(--indigo);
  font-size: var(--t-xs);
}
.multi {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.options {
  display: grid;
  gap: 4px;
}
.opt {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  padding: 7px 10px;
  border: 1px solid transparent;
  border-radius: var(--r-md);
  background: transparent;
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
  text-align: left;
  cursor: pointer;
}
.opt:hover {
  background: var(--cloth-sunk);
}
.opt.on {
  border-color: color-mix(in srgb, var(--indigo) 35%, var(--line));
  background: var(--indigo-wash);
}
.mark {
  display: grid;
  flex: none;
  place-items: center;
  width: 16px;
  height: 16px;
  border: 1.5px solid var(--line-strong);
  border-radius: 50%;
  color: var(--action-ink);
}
.opt.multi .mark {
  border-radius: 4px;
}
.opt.on .mark {
  border-color: var(--indigo);
  background: var(--indigo);
}
.opt:not(.multi).on .mark {
  background: var(--cloth);
  box-shadow: inset 0 0 0 3.5px var(--indigo);
}
.text {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}
.text small {
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.other input {
  width: 100%;
  padding: 3px 6px;
  border: 1px solid var(--line-strong);
  border-radius: var(--r-sm);
  background: var(--cloth);
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
}
.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 10px;
}
.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
@keyframes rise {
  from {
    opacity: 0;
    transform: translateY(6px);
  }
}
</style>
