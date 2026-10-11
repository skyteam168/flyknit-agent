<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { Eye, Lightbulb, ListChecks, MessageCircle, Sparkles, UserRound, X, Zap } from '@lucide/vue'
import ExpertAvatar from './ExpertAvatar.vue'
import { callExpert, state } from '../store'
import { compactNumber } from '../experts'

/** 专家详情：名片、能帮你做什么（点一下就带着这句话召唤）、版本和使用量。 */
const { t } = useI18n()
const e = computed(() => state.expertDetail!)
const close = () => (state.expertDetail = null)
const onKey = (ev: KeyboardEvent) => ev.key === 'Escape' && close()
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>

<template>
  <div class="scrim" @mousedown.self="close">
    <div class="dialog" role="dialog" aria-modal="true" :aria-label="e.name">
      <button type="button" class="x" :aria-label="t('menu.delete')" @click="close"><X :size="18" /></button>

      <div class="who">
        <ExpertAvatar :avatar="e.avatar" :name="e.name" :id="e.id" :size="72" />
        <div class="id">
          <h2>
            {{ e.name }}
            <small v-if="e.author">{{ e.author }}</small>
          </h2>
          <button type="button" class="summon" @click="callExpert(e.id)"><Sparkles :size="15" /> {{ t('ui.experts.call') }}</button>
        </div>
      </div>

      <p v-if="e.tagline" class="tagline">{{ e.tagline }}</p>
      <div v-if="e.tags.length || e.readOnly" class="tags">
        <span v-for="tag in e.tags" :key="tag" class="tag">{{ tag }}</span>
        <span v-if="e.readOnly" class="tag muted" :title="t('ui.experts.readOnlyHint')"><Eye :size="11" /> {{ t('ui.experts.readOnly') }}</span>
      </div>

      <div class="stats">
        <div>
          <strong>v{{ e.version }}</strong>
          <small>{{ t('ui.experts.version') }}</small>
        </div>
        <div :title="t('ui.experts.usersLine', { n: e.users })">
          <strong><Zap :size="15" /> {{ compactNumber(e.uses) }}</strong>
          <small>{{ t('ui.experts.uses') }}</small>
        </div>
        <div>
          <strong class="author"><UserRound :size="15" /> {{ e.author || t('ui.experts.authorDefault') }}</strong>
          <small>{{ t('ui.experts.author') }}</small>
        </div>
      </div>

      <template v-if="e.examples.length">
        <h3><Lightbulb :size="16" /> {{ t('ui.experts.helpDo') }}</h3>
        <button v-for="ex in e.examples" :key="ex" type="button" class="example" @click="callExpert(e.id, ex)">
          <span>“{{ ex }}”</span>
          <MessageCircle :size="16" />
        </button>
      </template>

      <template v-if="e.description">
        <h3><ListChecks :size="16" /> {{ t('ui.experts.card') }}</h3>
        <p class="card-desc">{{ e.description }}</p>
      </template>

      <h3><ListChecks :size="16" /> {{ t('ui.experts.moreInfo') }}</h3>
      <div class="meta">ID: {{ e.id }}<template v-if="e.readOnly"> · {{ t('ui.experts.readOnlyHint') }}</template></div>
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
  padding: 24px;
  background: color-mix(in srgb, #000 42%, transparent);
  animation: fade 140ms ease-out;
}
.dialog {
  position: relative;
  width: min(600px, 100%);
  max-height: calc(100vh - 48px);
  overflow-y: auto;
  padding: 26px 26px 24px;
  border: 1px solid var(--line);
  border-radius: 20px;
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
  animation: rise 160ms ease-out;
}
.x {
  position: absolute;
  top: 16px;
  right: 16px;
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
  color: var(--ink-faint);
}
.x:hover {
  background: var(--chip);
  color: var(--ink);
}
.who {
  display: flex;
  align-items: center;
  gap: 16px;
  padding-right: 32px;
}
.who :deep(.expert-avatar) {
  border-radius: 18px;
}
.id {
  min-width: 0;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 10px;
}
h2 {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 8px;
  margin: 0;
  font-size: var(--t-xl);
  font-weight: 700;
}
h2 small {
  padding-left: 8px;
  border-left: 1px solid var(--line-strong);
  color: var(--ink-soft);
  font-size: var(--t-md);
  font-weight: 400;
}
.summon {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 34px;
  padding: 0 16px;
  border-radius: 10px;
  background: var(--indigo);
  color: #fff;
  font-weight: 600;
}
.summon:hover {
  background: var(--indigo-hover);
}
.tagline {
  margin: 18px 0 0;
  color: var(--ink);
  line-height: 1.65;
}
.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 10px;
}
.tag {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  height: 26px;
  padding: 0 10px;
  border-radius: 6px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.tag.muted {
  color: var(--ink-faint);
}
.stats {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  margin: 20px 0 6px;
  padding: 16px 0;
  border-top: 1px solid var(--line);
  border-bottom: 1px solid var(--line);
  text-align: center;
}
.stats div {
  min-width: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
}
.stats strong {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  max-width: 100%;
  overflow: hidden;
  font-size: var(--t-md);
  font-weight: 700;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.stats .author svg {
  flex: none;
  color: var(--amber);
}
.stats small {
  color: var(--ink-faint);
  font-size: var(--t-xs);
}
h3 {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 20px 0 10px;
  font-size: var(--t-md);
  font-weight: 600;
}
.example {
  display: flex;
  align-items: center;
  gap: 12px;
  width: 100%;
  min-height: 48px;
  margin-bottom: 8px;
  padding: 10px 16px;
  border-radius: 12px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
  font-size: var(--t-sm);
  text-align: left;
}
.example span {
  flex: 1;
  min-width: 0;
}
.example svg {
  flex: none;
  color: var(--ink-faint);
}
.example:hover {
  background: var(--chip);
  color: var(--ink);
}
.example:hover svg {
  color: var(--indigo);
}
.card-desc {
  margin: 0;
  padding: 12px 16px;
  border-radius: 12px;
  background: var(--cloth-sunk);
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.7;
  white-space: pre-wrap;
}
.meta {
  padding: 12px 16px;
  border-radius: 12px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
@keyframes fade {
  from {
    opacity: 0;
  }
}
@keyframes rise {
  from {
    opacity: 0;
    transform: translateY(8px) scale(0.98);
  }
}
</style>
