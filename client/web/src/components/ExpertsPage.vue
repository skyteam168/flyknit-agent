<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronLeft, ChevronRight, Eye, History, Search, Sparkles, UsersRound, X, Zap } from '@lucide/vue'
import ExpertAvatar from './ExpertAvatar.vue'
import { loadExperts, state } from '../store'
import { compactNumber, hueOf } from '../experts'
import type { Expert } from '../types'

/** 专家广场：按分类的专题卡片、分类筛选、排序，点卡片看详情再召唤。 */
const { t } = useI18n()
const query = ref('')
const category = ref('')
const sort = ref<'overall' | 'hot' | 'newest'>('overall')
const mine = ref(false)

const other = computed(() => t('ui.experts.other'))
const catOf = (e: Expert) => e.category || other.value

/** 分类按专家多少排，「其他」放最后 */
const categories = computed(() => {
  const count = new Map<string, number>()
  for (const e of state.experts) count.set(catOf(e), (count.get(catOf(e)) ?? 0) + 1)
  return [...count.entries()]
    .sort((a, b) => (a[0] === other.value ? 1 : b[0] === other.value ? -1 : b[1] - a[1]))
    .map(([name]) => name)
})

const usedIds = computed(() => new Set(state.conversations.map((c) => c.expertId).filter(Boolean) as string[]))

/** 专题：每个分类一张卡片，列出用得最多的三位 */
const featured = computed(() =>
  categories.value.length < 2
    ? []
    : categories.value.map((name) => {
        const list = state.experts.filter((e) => catOf(e) === name)
        return { name, hue: hueOf(name), experts: [...list].sort((a, b) => b.uses - a.uses).slice(0, 3) }
      }),
)

const shown = computed(() => {
  const q = query.value.trim().toLowerCase()
  const list = state.experts.filter((e) => {
    if (mine.value && !usedIds.value.has(e.id)) return false
    if (category.value && catOf(e) !== category.value) return false
    return !q || `${e.name} ${e.tagline} ${e.category} ${e.tags.join(' ')} ${e.description}`.toLowerCase().includes(q)
  })
  if (sort.value === 'hot') return [...list].sort((a, b) => b.uses - a.uses)
  if (sort.value === 'newest') return [...list].sort((a, b) => (b.publishedAt ?? '').localeCompare(a.publishedAt ?? ''))
  return list // 综合：管理员排好的顺序
})

const showFeatured = computed(() => featured.value.length > 0 && !query.value && !mine.value && !category.value)

// ---------- 专题横向滚动 ----------
const rail = ref<HTMLElement>()
const canPrev = ref(false)
const canNext = ref(false)
function updateArrows() {
  const el = rail.value
  if (!el) return
  canPrev.value = el.scrollLeft > 4
  canNext.value = el.scrollLeft + el.clientWidth < el.scrollWidth - 4
}
function scrollRail(dir: number) {
  const el = rail.value
  if (el) el.scrollBy({ left: dir * (el.clientWidth - 80), behavior: 'smooth' })
}
watch(showFeatured, () => void nextTick(updateArrows))
onMounted(() => void nextTick(updateArrows))

const open = (e: Expert) => (state.expertDetail = e)
</script>

<template>
  <div class="experts">
    <div class="head">
      <h1><UsersRound :size="22" /> {{ t('ui.experts.title') }}</h1>
      <div class="tools">
        <label v-if="state.experts.length" class="search">
          <Search :size="15" />
          <input v-model="query" type="search" :placeholder="t('ui.experts.search')" />
          <button v-if="query" type="button" :aria-label="t('menu.delete')" @click="query = ''"><X :size="14" /></button>
        </label>
        <button v-if="state.experts.length" type="button" class="mine-btn" :class="{ on: mine }" @click="mine = !mine">
          <History :size="15" /> {{ t('ui.experts.mine') }}
        </button>
      </div>
    </div>

    <div class="content">
      <!-- 第一次加载：骨架屏，避免白屏 -->
      <div v-if="!state.loaded.experts && state.experts.length === 0" class="grid" aria-busy="true">
        <div v-for="i in 6" :key="i" class="card skel-card">
          <div class="top">
            <span class="skel round" style="width: 52px; height: 52px" />
            <div class="name" style="flex: 1">
              <span class="skel line lg" style="width: 60%" />
              <span class="skel line" style="width: 85%; margin-top: 8px" />
            </div>
          </div>
          <div class="tags">
            <span class="skel" style="width: 56px; height: 20px; border-radius: 999px" />
            <span class="skel" style="width: 68px; height: 20px; border-radius: 999px" />
          </div>
          <div class="foot">
            <span class="skel line" style="width: 40px" />
            <span class="skel line" style="width: 52px" />
          </div>
        </div>
      </div>
      <div v-else-if="state.expertsError && state.experts.length === 0" class="empty">
        <p>{{ t('ui.experts.loadFailed', { msg: state.expertsError }) }}</p>
        <button type="button" class="btn" @click="loadExperts()">{{ t('ui.common.retry') }}</button>
      </div>
      <p v-else-if="state.experts.length === 0" class="empty">{{ t('ui.experts.empty') }}</p>
      <template v-else>
        <!-- 专题：每个分类一张 -->
        <div v-if="showFeatured" class="rail-wrap">
          <div ref="rail" class="rail" @scroll="updateArrows">
            <section v-for="f in featured" :key="f.name" class="topic">
              <button
                type="button"
                class="banner"
                :style="{ '--h': f.hue }"
                @click="((category = f.name), (mine = false))"
              >
                <strong>{{ f.name }}</strong>
                <span class="faces" aria-hidden="true">
                  <ExpertAvatar v-for="e in f.experts" :key="e.id" :avatar="e.avatar" :name="e.name" :id="e.id" :size="40" />
                </span>
              </button>
              <ul>
                <li v-for="e in f.experts" :key="e.id">
                  <button type="button" @click="open(e)">
                    <span>{{ e.name }}</span>
                    <ChevronRight :size="14" />
                  </button>
                </li>
              </ul>
            </section>
          </div>
          <button v-if="canPrev" type="button" class="arrow prev" :aria-label="t('ui.experts.prev')" @click="scrollRail(-1)">
            <ChevronLeft :size="16" />
          </button>
          <button v-if="canNext" type="button" class="arrow next" :aria-label="t('ui.experts.next')" @click="scrollRail(1)">
            <ChevronRight :size="16" />
          </button>
        </div>

        <div class="bar">
          <h2>{{ mine ? t('ui.experts.mine') : t('ui.experts.allExperts') }}</h2>
          <div class="seg" role="radiogroup">
            <button
              v-for="s in ['overall', 'hot', 'newest'] as const"
              :key="s"
              type="button"
              role="radio"
              :aria-checked="sort === s"
              :class="{ on: sort === s }"
              @click="sort = s"
            >
              {{ t(`ui.experts.${s}`) }}
            </button>
          </div>
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

        <p v-if="shown.length === 0" class="empty">{{ mine && !query ? t('ui.experts.noMine') : t('ui.experts.noMatch') }}</p>
        <div v-else class="grid">
          <button v-for="e in shown" :key="e.id" type="button" class="card" @click="open(e)">
            <div class="top">
              <ExpertAvatar :avatar="e.avatar" :name="e.name" :id="e.id" :size="52" />
              <div class="name">
                <strong>{{ e.name }}</strong>
                <span class="tagline">{{ e.tagline || e.description }}</span>
              </div>
            </div>
            <div v-if="e.tags.length || e.readOnly" class="tags">
              <span v-for="tag in e.tags" :key="tag" class="tag">{{ tag }}</span>
              <span v-if="e.readOnly" class="tag muted" :title="t('ui.experts.readOnlyHint')"><Eye :size="11" /> {{ t('ui.experts.readOnly') }}</span>
            </div>
            <div class="foot">
              <span class="uses" :title="t('ui.experts.usersLine', { n: e.users })"><Zap :size="13" /> {{ compactNumber(e.uses) }}</span>
              <span class="call"><Sparkles :size="13" /> {{ t('ui.experts.call') }}</span>
            </div>
          </button>
        </div>
      </template>
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
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
  padding: 16px 28px 8px;
}
h1 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0;
  font-size: 22px;
  font-weight: 600;
}
.tools {
  display: flex;
  align-items: center;
  gap: 8px;
}
.search {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 220px;
  height: 34px;
  padding: 0 12px;
  border: 1px solid var(--line-strong);
  border-radius: 10px;
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
.mine-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 34px;
  padding: 0 12px;
  border: 1px solid var(--line-strong);
  border-radius: 10px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.mine-btn:hover {
  color: var(--ink);
}
.mine-btn.on {
  border-color: var(--indigo);
  background: var(--indigo-wash);
  color: var(--indigo);
}
.content {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 8px 28px 32px;
}
.empty {
  margin: 60px 0;
  color: var(--ink-faint);
  text-align: center;
}

/* ---------- 专题 ---------- */
.rail-wrap {
  position: relative;
  margin-bottom: 22px;
}
.rail {
  display: flex;
  gap: 14px;
  overflow-x: auto;
  scroll-snap-type: x mandatory;
  scrollbar-width: none;
}
.rail::-webkit-scrollbar {
  display: none;
}
.topic {
  flex: none;
  width: 268px;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: 16px;
  background: var(--cloth);
  scroll-snap-align: start;
}
.banner {
  position: relative;
  display: flex;
  align-items: center;
  width: 100%;
  height: 82px;
  padding: 0 18px;
  overflow: hidden;
  background:
    radial-gradient(120% 140% at 85% 20%, hsl(var(--h) 70% 62% / 0.55), transparent 60%),
    linear-gradient(120deg, hsl(var(--h) 55% 30%), hsl(calc(var(--h) + 40) 55% 42%));
  color: #fff;
  text-align: left;
}
.banner strong {
  position: relative;
  z-index: 1;
  font-size: var(--t-lg);
  font-weight: 700;
  text-shadow: 0 1px 6px rgba(0, 0, 0, 0.25);
}
.faces {
  position: absolute;
  right: 12px;
  bottom: -6px;
  display: flex;
}
.faces > * {
  margin-left: -10px;
  border: 2px solid rgba(255, 255, 255, 0.85);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.25);
  transform: rotate(-6deg);
}
.faces > *:nth-child(2) {
  transform: translateY(-8px) rotate(4deg);
}
.faces > *:nth-child(3) {
  transform: rotate(8deg);
}
.topic ul {
  margin: 0;
  padding: 8px 10px 12px;
  list-style: none;
}
.topic li button {
  display: flex;
  align-items: center;
  gap: 4px;
  width: 100%;
  height: 32px;
  padding: 0 8px;
  border-radius: 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  text-align: left;
}
.topic li button span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.topic li button:hover {
  background: var(--chip);
  color: var(--ink);
}
.topic li svg {
  flex: none;
  color: var(--ink-faint);
}
.arrow {
  position: absolute;
  top: 50%;
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border: 1px solid var(--line-strong);
  border-radius: 50%;
  background: var(--cloth);
  color: var(--ink);
  box-shadow: var(--shadow-pop);
  transform: translateY(-50%);
}
.arrow.prev {
  left: -12px;
}
.arrow.next {
  right: -12px;
}

/* ---------- 全部专家 ---------- */
.bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 10px;
}
h2 {
  margin: 0;
  font-size: var(--t-md);
  font-weight: 600;
}
.seg {
  display: flex;
  gap: 2px;
  padding: 3px;
  border-radius: 10px;
  background: var(--chip);
}
.seg button {
  height: 28px;
  padding: 0 12px;
  border-radius: 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.seg button.on {
  background: var(--seg);
  color: var(--seg-ink);
  box-shadow: var(--seg-shadow);
  font-weight: 600;
}
.cats {
  display: flex;
  gap: 6px;
  margin-bottom: 14px;
  overflow-x: auto;
  scrollbar-width: none;
}
.cats::-webkit-scrollbar {
  display: none;
}
.cats button {
  flex: none;
  height: 30px;
  padding: 0 14px;
  border-radius: 8px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.cats button:hover {
  color: var(--ink);
}
.cats button.on {
  background: var(--chip);
  color: var(--ink);
  font-weight: 600;
}
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
  gap: 14px;
}
.skel-card {
  pointer-events: none;
}
.card {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 18px;
  border: 1px solid var(--line);
  border-radius: 16px;
  background: var(--cloth);
  color: var(--ink);
  text-align: left;
  transition:
    border-color 0.15s,
    box-shadow 0.15s,
    transform 0.15s;
}
.card:hover {
  border-color: color-mix(in srgb, var(--indigo) 40%, var(--line));
  box-shadow: var(--shadow-card);
  transform: translateY(-2px);
}
.top {
  display: flex;
  align-items: center;
  gap: 14px;
  min-width: 0;
}
.top :deep(.expert-avatar) {
  border-radius: 14px;
}
.name {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
}
.name strong {
  overflow: hidden;
  font-size: var(--t-md);
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tagline {
  overflow: hidden;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.tag {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  height: 24px;
  padding: 0 9px;
  border-radius: 6px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: 12px;
}
.tag.muted {
  color: var(--ink-faint);
}
.foot {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: auto;
}
.uses {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--ink-faint);
  font-size: var(--t-sm);
  font-variant-numeric: tabular-nums;
}
.call {
  display: inline-flex;
  align-items: center;
  gap: 4px;
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
  .content {
    padding-left: 14px;
    padding-right: 14px;
  }
  .search {
    width: 100%;
  }
  .tools {
    width: 100%;
  }
}
</style>
