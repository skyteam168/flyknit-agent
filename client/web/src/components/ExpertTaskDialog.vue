<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Activity, CircleAlert, CircleHelp, Clock, Cpu, FileText, Lightbulb, Wrench, X } from '@lucide/vue'
import { openSubtaskTrace, state } from '../store'
import { artifactExt, formatDuration, openArtifact, VerificationOrder, verificationCounts } from '../subtasks'
import ExpertAvatar from './ExpertAvatar.vue'

// 专家任务详情：交给专家的任务和背景、每条结论和它的依据（以及有没有核对过）、
// 待确认的问题、建议、产出文件、工具调用和用量；执行记录要看时再向宿主取
const { t } = useI18n()
const dialog = ref<HTMLElement>()
const loadingTrace = ref(false)

const dto = computed(() => state.subtaskDetail)
const counts = computed(() => (dto.value ? verificationCounts(dto.value) : null))
const fmt = new Intl.NumberFormat()
const startedAt = computed(() => {
  const d = new Date(dto.value?.startedAt ?? '')
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleString()
})

function close() {
  state.subtaskDetail = null
}

async function viewTrace() {
  if (!dto.value || loadingTrace.value) return
  loadingTrace.value = true
  try {
    await openSubtaskTrace(dto.value.id)
  } finally {
    loadingTrace.value = false
  }
}

onMounted(async () => {
  // 焦点挪进弹窗：Esc 关弹窗，而不是被输入框当成「停止」（App.vue 的 chat.stop 里先关它）
  await nextTick()
  dialog.value?.focus()
})
</script>

<template>
  <Teleport to="body">
    <div v-if="dto" class="scrim" @mousedown.self="close">
      <div ref="dialog" class="dialog" tabindex="-1" role="dialog" aria-modal="true" :aria-label="t('ui.delegate.title')" @keydown.esc.stop="close">
        <header>
          <ExpertAvatar :avatar="dto.expert.avatar" :name="dto.expert.name" :id="dto.expert.id" :size="34" />
          <div class="title">
            <h2>{{ dto.expert.name }}</h2>
            <span class="sub">
              {{ t('ui.delegate.title') }}
              <template v-if="dto.expert.version"> · {{ t('ui.delegate.version', { n: dto.expert.version }) }}</template>
            </span>
          </div>
          <span class="badge" :class="dto.status">{{ t(`ui.delegate.status.${dto.status}`) }}</span>
          <button type="button" class="ico" :aria-label="t('settings.close')" @click="close"><X :size="16" /></button>
        </header>

        <div class="facts">
          <span :title="startedAt"><Clock :size="13" />{{ t('ui.delegate.duration') }} <strong>{{ formatDuration(dto.durationMs) }}</strong></span>
          <span>
            <Cpu :size="13" />{{ t('ui.delegate.tokens') }}
            <strong>{{ fmt.format(dto.tokens.prompt + dto.tokens.completion) }}</strong>
            <small>({{ t('ui.delegate.tokensValue', { prompt: fmt.format(dto.tokens.prompt), completion: fmt.format(dto.tokens.completion) }) }})</small>
            <em v-if="dto.tokens.estimated" class="est" :title="t('ui.delegate.estimatedHint')">{{ t('ui.delegate.estimated') }}</em>
          </span>
          <span v-if="dto.confidence !== null && dto.confidence !== undefined">
            {{ t('ui.delegate.confidence', { n: Math.round(dto.confidence * (dto.confidence <= 1 ? 100 : 1)) }) }}
          </span>
        </div>

        <div class="body">
          <section>
            <h3>{{ t('ui.delegate.task') }}</h3>
            <p class="pre">{{ dto.task }}</p>
          </section>
          <section v-if="dto.context">
            <h3>{{ t('ui.delegate.context') }}</h3>
            <p class="pre soft">{{ dto.context }}</p>
          </section>

          <section v-if="dto.error || dto.errorCode" class="err">
            <h3><CircleAlert :size="14" />{{ t('ui.delegate.error') }}</h3>
            <p>
              {{ dto.error || dto.errorCode }}
              <code v-if="dto.errorCode">{{ dto.errorCode }}</code>
            </p>
          </section>

          <section>
            <h3>{{ t('ui.delegate.summary') }}</h3>
            <p class="pre">{{ dto.summary || t('ui.delegate.noSummary') }}</p>
          </section>

          <section>
            <h3>
              {{ t('ui.delegate.findingsTitle') }}
              <span v-if="counts && dto.findings.length" class="counts">
                <span
                  v-for="v in VerificationOrder"
                  :key="v"
                  class="vc"
                  :class="[v, { zero: counts[v] === 0 }]"
                  :title="t(`ui.delegate.verify.${v}`)"
                  ><i />{{ counts[v] }}</span
                >
              </span>
            </h3>
            <p v-if="!dto.findings.length" class="muted">{{ t('ui.delegate.noFindings') }}</p>
            <ol v-else class="findings">
              <li v-for="f in dto.findings" :key="f.id" :class="f.verification">
                <div class="f-head">
                  <span class="f-text">{{ f.statement }}</span>
                  <span class="vbadge" :class="f.verification" :title="t(`ui.delegate.verifyHint.${f.verification}`)">
                    {{ t(`ui.delegate.verify.${f.verification}`) }}
                  </span>
                </div>
                <p v-if="f.note" class="note">{{ f.note }}</p>
                <ul v-if="f.evidence.length" class="evidence">
                  <li v-for="ev in f.evidence" :key="ev.ref" :class="ev.check">
                    <component :is="ev.kind === 'file' ? FileText : Wrench" :size="13" />
                    <span class="ev-label">{{ ev.label || ev.ref }}</span>
                    <code v-if="ev.label && ev.ref !== ev.label" class="ev-ref">{{ ev.ref }}</code>
                    <em v-if="ev.check !== 'ok'" class="ev-bad">{{ t(`ui.delegate.check.${ev.check}`) }}</em>
                  </li>
                </ul>
                <p v-else class="note muted">{{ t('ui.delegate.noEvidence') }}</p>
              </li>
            </ol>
          </section>

          <section v-if="dto.openQuestions.length">
            <h3><CircleHelp :size="14" />{{ t('ui.delegate.openQuestions') }}</h3>
            <ul class="bullets">
              <li v-for="q in dto.openQuestions" :key="q">{{ q }}</li>
            </ul>
          </section>
          <section v-if="dto.nextSuggestions.length">
            <h3><Lightbulb :size="14" />{{ t('ui.delegate.next') }}</h3>
            <ul class="bullets">
              <li v-for="q in dto.nextSuggestions" :key="q">{{ q }}</li>
            </ul>
          </section>

          <section v-if="dto.artifacts.length">
            <h3>{{ t('ui.delegate.artifacts') }}</h3>
            <div class="chips">
              <button
                v-for="a in dto.artifacts"
                :key="a.path"
                type="button"
                class="chip"
                :class="{ gone: !a.exists }"
                :disabled="!a.exists"
                :title="a.exists ? a.path : t('ui.delegate.missingFile')"
                @click="openArtifact(a)"
              >
                <FileText :size="13" :data-ext="artifactExt(a)" />
                <span>{{ a.name }}</span>
              </button>
            </div>
          </section>

          <section>
            <h3>{{ t('ui.delegate.toolCalls') }}</h3>
            <p v-if="!dto.toolCalls.length" class="muted">{{ t('ui.delegate.noTools') }}</p>
            <ul v-else class="tools">
              <li v-for="c in dto.toolCalls" :key="c.name">
                <code>{{ c.name }}</code>
                <span>{{ t('ui.delegate.toolCount', { n: c.count }) }}</span>
                <em v-if="c.failed" class="bad">{{ t('ui.delegate.toolFailed', { n: c.failed }) }}</em>
              </li>
            </ul>
          </section>
        </div>

        <footer>
          <button type="button" class="btn" :disabled="loadingTrace" @click="viewTrace">
            <Activity :size="14" />{{ t('ui.delegate.viewTrace') }}
          </button>
          <span class="spacer" />
          <button type="button" class="btn primary" @click="close">{{ t('settings.close') }}</button>
        </footer>
      </div>
    </div>
  </Teleport>
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
  app-region: no-drag;
  -webkit-app-region: no-drag;
}
.dialog {
  display: flex;
  flex-direction: column;
  width: min(760px, 100%);
  max-height: calc(100vh - 32px);
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  outline: none;
}
header {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 18px 22px 12px;
}
.title {
  flex: 1;
  min-width: 0;
}
.title h2 {
  margin: 0;
  overflow: hidden;
  font-size: calc(16px * var(--font-scale));
  text-overflow: ellipsis;
  white-space: nowrap;
}
.title .sub {
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.ico {
  flex: none;
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--ink-soft);
  cursor: pointer;
}
.ico:hover {
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.badge {
  flex: none;
  padding: 2px 10px;
  border-radius: 999px;
  background: var(--indigo-wash);
  color: var(--indigo);
  font-size: var(--t-xs);
  font-weight: 600;
  white-space: nowrap;
}
.badge.completed {
  background: var(--thread-wash);
  color: var(--thread);
}
.badge.partial,
.badge.waiting_confirmation {
  background: var(--amber-wash);
  color: var(--amber);
}
.badge.needs_input {
  background: color-mix(in srgb, #7c3aed 13%, var(--cloth));
  color: color-mix(in srgb, #7c3aed 80%, var(--ink));
}
.badge.failed {
  background: var(--red-wash);
  color: var(--red);
}
.badge.timed_out {
  background: color-mix(in srgb, #d9480f 13%, var(--cloth));
  color: color-mix(in srgb, #d9480f 85%, var(--ink));
}
.badge.cancelled {
  background: var(--chip);
  color: var(--ink-soft);
}
.facts {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 18px;
  padding: 0 22px 12px;
  border-bottom: 1px solid var(--line);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.facts > span {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 5px;
}
.facts strong {
  color: var(--ink);
  font-size: var(--t-sm);
  font-variant-numeric: tabular-nums;
}
.facts small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
.est {
  padding: 0 6px;
  border-radius: 999px;
  background: var(--amber-wash);
  color: var(--amber);
  font-style: normal;
  cursor: help;
}
.body {
  flex: 1;
  min-height: 0;
  padding: 6px 22px 16px;
  overflow-y: auto;
}
section {
  margin-top: 14px;
}
h3 {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0 0 6px;
  color: var(--ink-faint);
  font-size: var(--t-xs);
  font-weight: 600;
}
.pre {
  margin: 0;
  font-size: var(--t-sm);
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-word;
}
.pre.soft {
  max-height: 180px;
  overflow-y: auto;
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
}
.muted {
  margin: 0;
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.err p {
  margin: 0;
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--red-wash);
  color: var(--red);
  font-size: var(--t-sm);
  word-break: break-word;
}
.err h3 {
  color: var(--red);
}
.err code {
  margin-left: 6px;
  padding: 0 5px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--red) 12%, transparent);
  font-family: var(--font-code);
  font-size: var(--t-xs);
}
.counts {
  display: inline-flex;
  gap: 8px;
  margin-left: auto;
  font-weight: 400;
  font-variant-numeric: tabular-nums;
}
.vc {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--ink-soft);
}
.vc i {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--thread);
}
.vc.source_verified i {
  background: var(--indigo);
}
.vc.unverified i {
  background: var(--ink-faint);
}
.vc.verification_failed i {
  background: var(--red);
}
.vc.zero {
  color: var(--ink-faint);
}
.vc.zero i {
  opacity: 0.45;
}
.findings {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 0;
  padding: 0;
  list-style: none;
}
.findings > li {
  padding: 10px 12px;
  border: 1px solid var(--line);
  border-left: 3px solid var(--thread);
  border-radius: 8px;
}
.findings > li.source_verified {
  border-left-color: var(--indigo);
}
.findings > li.unverified {
  border-left-color: var(--line-strong);
}
.findings > li.verification_failed {
  border-left-color: var(--red);
  background: color-mix(in srgb, var(--red-wash) 50%, transparent);
}
.f-head {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}
.f-text {
  flex: 1;
  min-width: 0;
  font-size: var(--t-sm);
  line-height: 1.55;
  word-break: break-word;
}
.vbadge {
  flex: none;
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--thread-wash);
  color: var(--thread);
  font-size: var(--t-xs);
  white-space: nowrap;
  cursor: help;
}
.vbadge.source_verified {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.vbadge.unverified {
  background: var(--chip);
  color: var(--ink-soft);
}
.vbadge.verification_failed {
  background: var(--red-wash);
  color: var(--red);
}
.note {
  margin: 4px 0 0;
  color: var(--ink-soft);
  font-size: var(--t-xs);
  line-height: 1.5;
}
.evidence {
  display: flex;
  flex-direction: column;
  gap: 3px;
  margin: 6px 0 0;
  padding: 0;
  list-style: none;
  font-size: var(--t-xs);
}
.evidence li {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 2px 6px;
  min-width: 0;
  color: var(--ink-soft);
}
.evidence svg {
  flex: none;
  color: var(--ink-faint);
}
.ev-label {
  min-width: 0;
  word-break: break-word;
}
.ev-ref {
  max-width: 100%;
  overflow: hidden;
  padding: 0 5px;
  border-radius: 4px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
  font-family: var(--font-code);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ev-bad {
  color: var(--red);
  font-style: normal;
}
.evidence li.missing .ev-label,
.evidence li.failed_call .ev-label {
  text-decoration: line-through;
}
.bullets {
  margin: 0;
  padding-left: 20px;
  font-size: var(--t-sm);
  line-height: 1.6;
}
.chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  max-width: 100%;
  min-width: 0;
  padding: 3px 10px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--cloth);
  color: var(--ink);
  font-size: var(--t-xs);
  cursor: pointer;
}
.chip span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.chip svg {
  flex: none;
  color: var(--indigo);
}
.chip svg[data-ext='xlsx'],
.chip svg[data-ext='xls'],
.chip svg[data-ext='csv'] {
  color: #1d6f42;
}
.chip:hover:not(:disabled) {
  border-color: var(--indigo);
  color: var(--indigo);
}
.chip.gone {
  color: var(--ink-faint);
  text-decoration: line-through;
  cursor: default;
}
.tools {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
  font-size: var(--t-xs);
}
.tools li {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  max-width: 100%;
  padding: 3px 9px;
  border-radius: 6px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
}
.tools code {
  overflow: hidden;
  color: var(--ink);
  font-family: var(--font-code);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tools .bad {
  color: var(--red);
  font-style: normal;
}
footer {
  display: flex;
  gap: 10px;
  align-items: center;
  padding: 12px 22px 18px;
  border-top: 1px solid var(--line);
}
footer .btn {
  display: inline-flex;
  gap: 6px;
  align-items: center;
}
.spacer {
  flex: 1;
}
@media (max-width: 560px) {
  .scrim {
    padding: 8px;
  }
  .dialog {
    max-height: calc(100vh - 16px);
  }
  header,
  .facts,
  .body,
  footer {
    padding-left: 14px;
    padding-right: 14px;
  }
  .f-head {
    flex-direction: column;
    gap: 4px;
  }
}
</style>
