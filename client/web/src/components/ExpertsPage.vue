<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Eye, Search, X } from '@lucide/vue'
import ExpertAvatar from './ExpertAvatar.vue'
import { callExpert, state } from '../store'

/** 专家列表：管理员发布的专家，按分类分组，点一下就呼叫。 */
const { t } = useI18n()
const query = ref('')
const category = ref('')

const categories = computed(() => {
  const seen: string[] = []
  for (const e of state.experts) {
    const c = e.category || t('ui.experts.other')
    if (!seen.includes(c)) seen.push(c)
  }
  return seen
})

const shown = computed(() => {
  const q = query.value.trim().toLowerCase()
  return state.experts.filter((e) => {
    if (category.value && (e.category || t('ui.experts.other')) !== category.value) return false
    return !q || `${e.name} ${e.tagline} ${e.category} ${e.description}`.toLowerCase().includes(q)
  })
})
</script>

<template>
  <div class="experts">
    <div class="head">
      <div class="title">
        <h1>{{ t('ui.experts.title') }}</h1>
        <p>{{ t('ui.experts.subtitle') }}</p>
      </div>
      <label v-if="state.experts.length" class="search">
        <Search :size="15" />
        <input v-model="query" type="search" :placeholder="t('ui.experts.search')" />
        <button v-if="query" type="button" :aria-label="t('menu.delete')" @click="query = ''"><X :size="14" /></button>
      </label>
    </div>

    <div v-if="categories.length > 1" class="cats" role="radiogroup">
      <button type="button" role="radio" :aria-checked="!category" :class="{ on: !category }" @click="category = ''">
        {{ t('ui.experts.all') }}
      </button>
      <button
        v-for="c in categories"
        :key="c"
        type="button"
        role="radio"
        :aria-checked="category === c"
        :class="{ on: category === c }"
        @click="category = c"
      >
        {{ c }}
      </button>
    </div>

    <div class="content">
      <p v-if="state.experts.length === 0" class="empty">{{ t('ui.experts.empty') }}</p>
      <p v-else-if="shown.length === 0" class="empty">{{ t('ui.experts.noMatch') }}</p>
      <div v-else class="grid">
        <button v-for="e in shown" :key="e.id" type="button" class="card" @click="callExpert(e.id)">
          <div class="top">
            <ExpertAvatar :avatar="e.avatar" :name="e.name" :id="e.id" :size="44" />
            <div class="name">
              <strong>{{ e.name }}</strong>
              <small v-if="e.category">{{ e.category }}</small>
            </div>
          </div>
          <p class="tagline">{{ e.tagline || e.description }}</p>
          <div class="foot">
            <span v-if="e.readOnly" class="tag" :title="t('ui.experts.readOnlyHint')"><Eye :size="12" /> {{ t('ui.experts.readOnly') }}</span>
            <span class="call">{{ t('ui.experts.call') }}</span>
          </div>
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.experts {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  background: var(--loom);
}
.head {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
  padding: 18px 28px 10px;
}
.title {
  min-width: 0;
}
h1 {
  margin: 0;
  font-size: 24px;
  font-weight: 600;
}
.title p {
  margin: 4px 0 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.search {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 250px;
  height: 36px;
  padding: 0 12px;
  border: 1px solid var(--line-strong);
  border-radius: 18px;
  background: var(--cloth-sunk);
  color: var(--ink-faint);
}
.search input {
  flex: 1;
  min-width: 0;
  border: 0;
  outline: 0;
  background: transparent;
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
}
.search button {
  display: grid;
  place-items: center;
  color: var(--ink-faint);
}
.cats {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 4px 28px 8px;
}
.cats button {
  height: 30px;
  padding: 0 14px;
  border-radius: 999px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.cats button:hover {
  color: var(--ink);
}
.cats button.on {
  background: var(--indigo-wash);
  color: var(--indigo);
  font-weight: 500;
}
.content {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 8px 28px 28px;
}
.empty {
  margin: 60px 0;
  color: var(--ink-faint);
  text-align: center;
}
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 14px;
}
.card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-height: 150px;
  padding: 16px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth);
  color: var(--ink);
  text-align: left;
  transition:
    border-color 0.15s,
    box-shadow 0.15s,
    transform 0.15s;
}
.card:hover {
  border-color: color-mix(in srgb, var(--indigo) 45%, var(--line));
  box-shadow: var(--shadow-card, 0 6px 18px -10px rgba(27, 36, 64, 0.25));
  transform: translateY(-1px);
}
.top {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}
.name {
  min-width: 0;
  display: flex;
  flex-direction: column;
}
.name strong {
  overflow: hidden;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.name small {
  color: var(--ink-faint);
  font-size: var(--t-xs, 12px);
}
.tagline {
  margin: 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  line-height: 1.55;
  display: -webkit-box;
  overflow: hidden;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}
.foot {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: auto;
}
.tag {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 8px;
  border-radius: 999px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: 12px;
}
.call {
  margin-left: auto;
  color: var(--indigo);
  font-size: var(--t-sm);
  font-weight: 500;
  opacity: 0;
  transition: opacity 0.15s;
}
.card:hover .call,
.card:focus-visible .call {
  opacity: 1;
}
@media (max-width: 720px) {
  .head,
  .cats,
  .content {
    padding-left: 14px;
    padding-right: 14px;
  }
  .search {
    width: 100%;
  }
}
</style>
