<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Copy, Pencil, Plus, Search, Trash2 } from '@lucide/vue'
import ExpertAvatar from './ExpertAvatar.vue'
import TeamEditor from './TeamEditor.vue'
import { copyTeam, dissolveTeam, loadTeams, state, toast } from '../store'
import { teamIcon } from '../teams'
import type { Team } from '../types'

/**
 * 团队（docs/team-mode.md 7.2）：把几位专家组成团队，给团队一件事，主助手当队长列计划、派活、汇总。
 * 列表和权限都来自服务端：IT 下发 / 本部门 / 只我自己；只有建的人能改、解散。
 */
const { t } = useI18n()
const filter = ref<'all' | 'mine' | 'department' | 'it'>('all')
const query = ref('')
const editing = ref<Team | null | undefined>(undefined) // undefined = 没打开；null = 新建
const confirming = ref<Team | null>(null)

onMounted(() => void loadTeams())

const expertOf = (id: string) => state.experts.find((e) => e.id === id)
const expertName = (id: string) => expertOf(id)?.name ?? id

const shown = computed(() => {
  const q = query.value.trim().toLowerCase()
  return state.teams.filter((team) => {
    if (filter.value === 'mine' && !team.editable) return false
    if (filter.value === 'department' && team.source !== 'department') return false
    if (filter.value === 'it' && team.source !== 'it') return false
    return !q || `${team.name} ${team.description} ${team.members.map((m) => expertName(m.expert_id)).join(' ')}`.toLowerCase().includes(q)
  })
})

const canCreate = computed(() => !!state.app?.teamCreate)
const sourceLabel = (team: Team) =>
  team.source === 'it' ? t('ui.teams.sourceIt') : team.source === 'department' ? t('ui.teams.sourceDept', { dept: team.department }) : t('ui.teams.sourceMine')
const unavailable = (team: Team) => team.members.filter((m) => m.available === false).length

async function copy(team: Team) {
  const created = await copyTeam(team.id)
  if (created) toast(t('ui.teams.copied', { name: created.name }))
}

async function dissolve() {
  const team = confirming.value
  if (!team) return
  confirming.value = null
  await dissolveTeam(team.id)
  toast(t('ui.teams.dissolved', { name: team.name }))
}
</script>

<template>
  <section class="teams-page">
    <header class="head">
      <div>
        <h1>{{ t('ui.teams.title') }}</h1>
        <p>{{ t('ui.teams.lead') }}</p>
      </div>
      <button v-if="canCreate" type="button" class="btn primary" @click="editing = null"><Plus :size="15" /> {{ t('ui.teams.new') }}</button>
    </header>

    <div class="filters">
      <div class="seg" role="tablist">
        <button
          v-for="f in (['all', 'mine', 'department', 'it'] as const)"
          :key="f"
          type="button"
          role="tab"
          :aria-pressed="filter === f"
          @click="filter = f"
        >
          {{ t(`ui.teams.filter.${f}`) }}
        </button>
      </div>
      <label class="search">
        <Search :size="14" />
        <input v-model="query" type="search" :placeholder="t('ui.teams.search')" />
      </label>
    </div>

    <p v-if="state.teamsLoading && !state.teams.length" class="empty">{{ t('ui.teams.loading') }}</p>
    <div v-else class="grid">
      <article v-for="team in shown" :key="team.id" class="tcard">
        <div class="row1">
          <span class="ticon"><component :is="teamIcon(team.icon)" :size="19" /></span>
          <h3>{{ team.name }}</h3>
          <span class="tag" :class="team.source">{{ sourceLabel(team) }}</span>
        </div>
        <p v-if="team.description" class="desc">{{ team.description }}</p>
        <div class="meta">
          <span class="stack">
            <span class="leader" :title="t('ui.teams.leader')">{{ t('ui.teams.leaderShort') }}</span>
            <ExpertAvatar
              v-for="m in team.members.slice(0, 6)"
              :key="m.expert_id"
              :avatar="expertOf(m.expert_id)?.avatar ?? ''"
              :name="expertName(m.expert_id)"
              :id="m.expert_id"
              :size="24"
              :class="{ off: m.available === false }"
              :title="expertName(m.expert_id)"
            />
          </span>
          <span>{{ t('ui.teams.memberCount', { n: team.members.length }) }}</span>
          <span v-if="unavailable(team)" class="tag warn">{{ t('ui.teams.unavailable', { n: unavailable(team) }) }}</span>
        </div>
        <div class="meta faint">
          <span>{{ team.source === 'it' ? t('ui.teams.byIt') : t('ui.teams.by', { name: team.owner_name }) }}</span>
          <span v-if="team.runs">· {{ t('ui.teams.runs', { n: team.runs }) }}</span>
          <span v-if="team.read_only">· {{ t('ui.teams.readOnly') }}</span>
        </div>
        <div class="acts">
          <template v-if="team.editable">
            <button type="button" class="btn sm" @click="editing = team"><Pencil :size="13" /> {{ t('ui.teams.edit') }}</button>
            <button type="button" class="btn sm ghost danger" @click="confirming = team"><Trash2 :size="13" /> {{ t('ui.teams.dissolve') }}</button>
          </template>
          <button v-else-if="canCreate" type="button" class="btn sm" @click="copy(team)"><Copy :size="13" /> {{ t('ui.teams.copy') }}</button>
        </div>
      </article>
      <button v-if="canCreate" type="button" class="add" @click="editing = null">
        <Plus :size="18" />
        <span>{{ t('ui.teams.new') }}</span>
        <small>{{ t('ui.teams.newHint', { max: state.app?.teamMaxMembers ?? 8 }) }}</small>
      </button>
      <p v-if="!shown.length && !canCreate" class="empty">{{ t('ui.teams.none') }}</p>
    </div>

    <TeamEditor v-if="editing !== undefined" :team="editing" @close="editing = undefined" />

    <div v-if="confirming" class="scrim" @click.self="confirming = null">
      <div class="dialog" role="dialog" :aria-label="t('ui.teams.dissolveTitle', { name: confirming.name })">
        <h3>{{ t('ui.teams.dissolveTitle', { name: confirming.name }) }}</h3>
        <p>{{ confirming.source === 'department' ? t('ui.teams.dissolveDept', { dept: confirming.department }) : '' }}{{ t('ui.teams.dissolveBody') }}</p>
        <div class="acts end">
          <button type="button" class="btn" @click="confirming = null">{{ t('ui.teams.cancel') }}</button>
          <button type="button" class="btn danger-fill" @click="dissolve">{{ t('ui.teams.dissolve') }}</button>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.teams-page {
  max-width: 1120px;
  margin: 0 auto;
  padding: 22px 24px 40px;
}
.head {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 12px;
  margin-bottom: 16px;
}
.head > div {
  flex: 1;
  min-width: 0;
}
.head h1 {
  margin: 0;
  font-size: var(--t-xl);
  font-weight: 600;
}
.head p {
  margin: 4px 0 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.filters {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  align-items: center;
  margin-bottom: 14px;
}
.seg {
  display: inline-flex;
  gap: 2px;
  padding: 2px;
  border-radius: 8px;
  background: var(--chip);
}
.seg button {
  padding: 3px 12px;
  border: 0;
  border-radius: 6px;
  background: none;
  color: var(--ink-soft);
  font-size: var(--t-sm);
  cursor: pointer;
}
.seg button[aria-pressed='true'] {
  background: var(--seg);
  color: var(--seg-ink);
  box-shadow: var(--seg-shadow);
}
.search {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
  padding: 5px 10px;
  border: 1px solid var(--line-strong);
  border-radius: 8px;
  background: var(--cloth);
  color: var(--ink-faint);
}
.search input {
  width: 200px;
  border: 0;
  background: none;
  color: var(--ink);
  font: inherit;
  font-size: var(--t-sm);
  outline: none;
}
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(290px, 1fr));
  gap: 14px;
}
.tcard {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-width: 0;
  padding: 16px;
  border: 1px solid var(--line);
  border-radius: var(--r-lg);
  background: var(--cloth);
}
.tcard:hover {
  box-shadow: var(--shadow-card);
}
.row1 {
  display: flex;
  align-items: center;
  gap: 10px;
}
.row1 h3 {
  flex: 1;
  min-width: 0;
  margin: 0;
  overflow: hidden;
  font-size: var(--t-md);
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ticon {
  display: grid;
  flex: none;
  place-items: center;
  width: 36px;
  height: 36px;
  border-radius: 10px;
  background: var(--indigo-wash);
  color: var(--indigo);
}
.tag {
  padding: 1px 8px;
  border-radius: 10px;
  background: var(--chip);
  color: var(--ink-soft);
  font-size: var(--t-xs);
  white-space: nowrap;
}
.tag.it {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.tag.personal {
  background: var(--thread-wash);
  color: var(--thread);
}
.tag.warn {
  background: var(--amber-wash);
  color: var(--amber);
}
.desc {
  margin: 0;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
.meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  font-size: var(--t-xs);
  color: var(--ink-soft);
}
.meta.faint {
  color: var(--ink-faint);
}
.stack {
  display: inline-flex;
  align-items: center;
}
.stack > * + * {
  margin-left: -6px;
}
.stack :deep(.off) {
  opacity: 0.35;
}
.leader {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border: 2px solid var(--cloth);
  border-radius: 50%;
  background: var(--ink);
  color: var(--loom);
  font-size: 11px;
  font-weight: 600;
}
.acts {
  display: flex;
  gap: 8px;
  margin-top: auto;
}
.acts.end {
  justify-content: flex-end;
}
.btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}
.btn.sm {
  padding: 3px 10px;
  font-size: var(--t-xs);
}
.btn.ghost {
  border-color: transparent;
  background: none;
}
.btn.danger {
  margin-left: auto;
  color: var(--red);
}
.btn.danger-fill {
  border-color: var(--red);
  background: var(--red);
  color: #fff;
}
.add {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 4px;
  min-height: 170px;
  border: 1.5px dashed var(--line-strong);
  border-radius: var(--r-lg);
  background: none;
  color: var(--ink-soft);
  cursor: pointer;
}
.add:hover {
  border-color: var(--indigo);
  color: var(--indigo);
}
.add small {
  color: var(--ink-faint);
}
.empty {
  color: var(--ink-faint);
  font-size: var(--t-sm);
}
.scrim {
  position: fixed;
  inset: 0;
  z-index: 50;
  display: grid;
  place-items: center;
  background: rgba(10, 10, 14, 0.35);
}
.dialog {
  width: min(420px, 92vw);
  padding: 18px;
  border-radius: var(--r-lg);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
.dialog h3 {
  margin: 0 0 8px;
  font-size: var(--t-md);
}
.dialog p {
  margin: 0 0 14px;
  color: var(--ink-soft);
  font-size: var(--t-sm);
}
</style>
