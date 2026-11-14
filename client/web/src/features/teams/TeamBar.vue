<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowLeft } from '@lucide/vue'
import ExpertAvatar from '../../components/ExpertAvatar.vue'
import { state } from '../../store'
import { currentTeam, openTeams } from './store'
import { teamIcon } from './meta'

/**
 * 团队对话顶部那一条（和演示一致）：「团队」标签 · 团队图标 · 团队名 · 队长和成员头像 · 「← 团队列表」。
 * 团队解散了、看不到了时只留「团队」标签和返回。
 */
const { t } = useI18n()
const team = computed(() => currentTeam.value)
const expert = (id: string) => state.experts.find((e) => e.id === id)
const leader = computed(() => (team.value?.leader_expert_id && team.value.leader_available ? expert(team.value.leader_expert_id) : null))
</script>

<template>
  <div class="team-bar">
    <span class="mode">{{ t('ui.teams.barTag') }}</span>
    <template v-if="team">
      <span class="ticon"><component :is="teamIcon(team.icon)" :size="15" /></span>
      <strong>{{ team.name }}</strong>
      <span class="stack">
        <span class="lead" :title="leader ? t('ui.teams.leaderExpert', { name: leader.name }) : t('ui.teams.leader')">{{ leader ? leader.name.slice(0, 1) : t('ui.teams.leaderShort') }}</span>
        <ExpertAvatar
          v-for="m in team.members.filter((x) => x.available !== false)"
          :key="m.expert_id"
          :avatar="expert(m.expert_id)?.avatar ?? ''"
          :name="expert(m.expert_id)?.name ?? m.expert_id"
          :id="m.expert_id"
          :size="22"
          :title="expert(m.expert_id)?.name ?? m.expert_id"
        />
      </span>
    </template>
    <span class="spacer" />
    <button type="button" class="btn sm" @click="openTeams()"><ArrowLeft :size="13" /> {{ t('ui.teams.backToList') }}</button>
  </div>
</template>

<style scoped>
.team-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  max-width: var(--column);
  min-width: 0;
  margin: 0 auto;
  padding: 8px 24px 6px;
  font-size: var(--t-sm);
}
.mode {
  flex: none;
  padding: 2px 9px;
  border-radius: 10px;
  background: var(--chip, var(--cloth-sunk));
  color: var(--ink-soft);
  font-size: var(--t-xs);
}
.ticon {
  display: grid;
  flex: none;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 8px;
  background: var(--indigo-wash);
  color: var(--indigo);
}
strong {
  overflow: hidden;
  min-width: 0;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.stack {
  display: inline-flex;
  flex: none;
}
.stack > * + * {
  margin-left: -7px;
}
.stack :deep(.avatar),
.stack > * {
  border: 2px solid var(--loom);
  border-radius: 50%;
}
.lead {
  display: inline-grid;
  place-items: center;
  width: 22px;
  height: 22px;
  background: var(--ink);
  color: var(--loom);
  font-size: 10.5px;
  font-weight: 600;
}
.spacer {
  flex: 1;
}
.btn.sm {
  display: inline-flex;
  flex: none;
  align-items: center;
  gap: 4px;
  padding: 3px 10px;
  font-size: var(--t-xs);
}
</style>
