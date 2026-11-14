import { computed, nextTick, reactive, watch } from 'vue'
import { bridge } from '../../bridge'
import { current, draftMode, fail, fillInput, loadExperts, markRunning, newConversation, registerFeature, state, toast } from '../../store'
import type { HostEvent, Team, TeamInput, TeamMemberState, TeamNote, TeamRunRecord, TeamRunView } from '../../types'
import { i18n } from '../../i18n'
import { registerAssistantIdentity, registerToolCard } from '../../toolCards'
import TeamPlanInline from './TeamPlanInline.vue'
import TeamReportCard from './TeamReportCard.vue'

/**
 * 团队模式（docs/team-mode.md）在界面上的状态和操作：团队列表、团队任务的计划卡、成员进度、看板。
 * 作为一个功能模块接进来（docs/extension-architecture.md）：订阅 team.* 事件、在一轮结束时收尾，
 * 底座的 store.ts 不认识团队。
 */
export const teams = reactive({
  list: [] as Team[],
  loading: false,
  /** 对话 ID → 这个对话最近一次团队任务 */
  runs: {} as Record<string, TeamRunView>,
  /** 已经在对话里画了报告卡的团队任务（评价在报告卡里；没有报告卡的才在面板底下问） */
  reported: {} as Record<string, boolean>,
})

// ---------- 团队列表 ----------

export function openTeams() {
  state.view = 'teams'
  void loadExperts()
  void loadTeams()
}

export async function loadTeams() {
  teams.loading = true
  try {
    teams.list = await bridge.listTeams()
  } catch (e) {
    fail(e)
  } finally {
    teams.loading = false
  }
}

/** 保存团队：成功返回保存后的团队；失败（重名、成员不在范围内、别人刚改过……）提示服务端的原话，返回 null */
export async function saveTeam(id: string | null, input: TeamInput): Promise<Team | null> {
  try {
    const saved = await bridge.saveTeam(id, input)
    await loadTeams()
    return saved
  } catch (e) {
    fail(e)
    return null
  }
}

export async function dissolveTeam(id: string) {
  try {
    await bridge.dissolveTeam(id)
    teams.list = teams.list.filter((t) => t.id !== id)
  } catch (e) {
    fail(e)
  }
}

export async function copyTeam(id: string): Promise<Team | null> {
  try {
    const team = await bridge.copyTeam(id)
    await loadTeams()
    return team
  } catch (e) {
    fail(e)
    return null
  }
}

/** 团队卡片上点「开始任务」（或点某条示例任务）：进入这个团队的新对话，发第一句话时才真正创建 */
export function startTeamTask(id: string, text = '') {
  newConversation('agent')
  draftMode.teamId = id
  void nextTick(() => {
    if (text) fillInput(text)
    window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
  })
}

// ---------- 团队经验（团队积累）----------

export async function loadTeamNotes(teamId: string): Promise<TeamNote[]> {
  try {
    return await bridge.listTeamNotes(teamId)
  } catch (e) {
    fail(e)
    return []
  }
}

export async function addTeamNote(teamId: string, text: string, kind: TeamNote['kind']): Promise<TeamNote | null> {
  try {
    return await bridge.addTeamNote(teamId, text, kind)
  } catch (e) {
    fail(e)
    return null
  }
}

export async function deleteTeamNote(teamId: string, noteId: string): Promise<boolean> {
  try {
    await bridge.deleteTeamNote(teamId, noteId)
    return true
  } catch (e) {
    fail(e)
    return false
  }
}

/** 当前对话（或新对话草稿）的团队 */
export const currentTeam = computed<Team | null>(() => {
  const id = current.value ? current.value.teamId : draftMode.teamId
  return id ? (teams.list.find((t) => t.id === id) ?? null) : null
})

// ---------- 团队任务 ----------

/** 当前对话的团队任务（没有就是 null） */
export const currentTeamRun = computed<TeamRunView | null>(() => (state.currentId ? (teams.runs[state.currentId] ?? null) : null))

/** 回答计划卡 */
export async function answerTeamPlan(conversationId: string, action: 'start' | 'revise' | 'cancel', removed: string[] = [], text = '') {
  const run = teams.runs[conversationId]
  const pending = run?.pending
  if (!run || !pending) return
  run.pending = null
  await bridge.teamPlanAnswer(pending.requestId, action, removed, text).catch(fail)
}

/** 面板上停掉某一位成员（其余照常） */
export async function stopTeamMember(conversationId: string, expertId: string) {
  await bridge.stopTeamMember(conversationId, expertId).catch(fail)
}

/** 宿主存在本地的面板数据 → 面板用的样子 */
function fromRecord(r: TeamRunRecord): TeamRunView {
  const members: TeamRunView['members'] = {}
  for (const m of r.members) {
    members[m.expertId] = {
      expertId: m.expertId, status: m.status as TeamMemberState, note: m.note, callId: m.callId, subtaskId: m.subtaskId,
      task: m.task, done: m.done, dependsOn: m.dependsOn, delivery: m.delivery, retries: m.retries, reused: m.reused,
      doneSeconds: m.doneSeconds,
    }
  }
  return {
    runId: r.runId, pending: null, members, order: r.members.map((m) => m.expertId), board: r.board, startedAt: r.startedAt,
    finished: r.finished, goal: r.goal, interrupted: r.interrupted, autoStarted: r.autoStarted, rating: r.rating, feedback: r.feedback,
    tokens: r.tokens, notes: r.notes, budgetTokens: r.budgetTokens, teamName: r.teamName,
    grantLabels: r.grantLabels, granted: r.granted ?? undefined,
  }
}

/** 打开一个团队对话：面板不在内存里时向宿主要本地存的那份（关掉再打开、重启后照样能看） */
export async function loadTeamRun(conversationId: string) {
  if (teams.runs[conversationId]) return
  try {
    const record = await bridge.getTeamRun(conversationId)
    if (record && !teams.runs[conversationId]) teams.runs[conversationId] = fromRecord(record)
  } catch {
    // 老版本宿主没有这个接口：面板照旧从这一轮的事件开始
  }
}

/** 对团队报告评价：👍 = 1，👎 = -1，再点一次撤回 = 0 */
export async function rateTeamRun(conversationId: string, rating: number, feedback = '') {
  const run = teams.runs[conversationId]
  if (!run) return
  try {
    const saved = await bridge.rateTeamRun(conversationId, run.runId, rating, feedback)
    if (saved) {
      run.rating = saved.rating
      run.feedback = saved.feedback
      toast(i18n.global.t(rating ? 'ui.teamRun.rated' : 'ui.teamRun.unrated'))
    }
  } catch (e) {
    fail(e)
  }
}

function runOf(conversationId: string, runId: string): TeamRunView {
  const existing = teams.runs[conversationId]
  if (existing && existing.runId === runId) return existing
  teams.runs[conversationId] = { runId, pending: null, members: {}, order: [], board: [], startedAt: new Date().toISOString(), finished: false }
  return teams.runs[conversationId]
}

const FINAL = ['done', 'failed', 'stopped']

function onTeamEvent(e: HostEvent) {
  switch (e.type) {
    case 'team.plan': {
      markRunning(e.conversationId)
      const run = runOf(e.conversationId, e.runId)
      if (e.budgetTokens) run.budgetTokens = e.budgetTokens
      run.pending = {
        requestId: e.requestId, callId: e.callId, runId: e.runId, round: e.round, goal: e.goal, items: e.items,
        skipped: e.skipped, estimateTokens: e.estimateTokens, budgetTokens: e.budgetTokens,
      }
      run.finished = false
      break
    }
    case 'team.planClosed': {
      const run = teams.runs[e.conversationId]
      if (run?.pending?.requestId === e.requestId) run.pending = null
      break
    }
    case 'team.started': {
      const run = runOf(e.conversationId, e.runId)
      for (const item of e.items) {
        run.members[item.expertId] = {
          expertId: item.expertId, status: item.dependsOn.length ? 'upstream' : 'waiting', note: '', callId: `${e.callId}#${item.expertId}`,
          subtaskId: '', task: item.task, done: item.done, dependsOn: item.dependsOn,
        }
        if (!run.order.includes(item.expertId)) run.order.push(item.expertId)
      }
      run.finished = false
      if (e.round === 1 && e.goal) run.goal = e.goal
      if (e.autoStarted) run.autoStarted = true
      // 团队开工：右侧面板切到「团队」
      state.rightTab = 'team'
      state.rightClosed = false
      break
    }
    case 'team.member': {
      const run = teams.runs[e.conversationId]
      const m = run?.runId === e.runId ? run.members[e.expertId] : undefined
      if (m) {
        m.status = e.status
        m.note = e.note
        if (e.status === 'done' && m.doneSeconds == null) m.doneSeconds = Math.max(0, Math.round((Date.now() - Date.parse(run!.startedAt)) / 1000))
        if (e.subtaskId) m.subtaskId = e.subtaskId
        if (e.status === 'retrying') m.retries = (m.retries ?? 0) + 1
      }
      break
    }
    case 'team.board': {
      const run = teams.runs[e.conversationId]
      if (run?.runId === e.runId) run.board.push({ expertId: e.expertId, name: e.name, kind: e.kind, text: e.text, at: e.at })
      break
    }
    case 'team.grant': {
      const run = teams.runs[e.conversationId]
      if (run?.runId === e.runId) {
        run.grantLabels = e.labels
        run.granted = e.granted
      }
      break
    }
    case 'team.record': {
      // 一轮结束：宿主存好的那份为准（含沿用、评价、经验条数）
      teams.runs[e.conversationId] = fromRecord(e.record)
      break
    }
    case 'team.round': {
      const run = teams.runs[e.conversationId]
      if (run?.runId === e.runId) {
        for (const r of e.results) {
          const m = run.members[r.expertId]
          if (m) m.delivery = r.delivery
        }
        run.finished = Object.values(run.members).every((m) => FINAL.includes(m.status))
      }
      break
    }
  }
}

// 打开团队对话时把本地存的面板数据拿回来
watch(
  () => (current.value?.teamId ? current.value.id : null),
  (id) => {
    if (id) void loadTeamRun(id)
  },
  { immediate: true },
)

// 对话里的卡：分工计划 / 补派计划卡（含开工授权）、团队报告卡；团队对话里助手消息标成「队长」
registerToolCard('team_plan', TeamPlanInline)
registerToolCard('team_assign', TeamPlanInline)
registerToolCard('team_report', TeamReportCard)
registerAssistantIdentity((conv) => {
  if (!conv.teamId) return null
  const team = teams.list.find((x) => x.id === conv.teamId)
  const leader = team?.leader_expert_id && team.leader_available ? state.experts.find((e) => e.id === team.leader_expert_id) : null
  const t = i18n.global.t
  return leader
    ? { initial: leader.name.slice(0, 1), label: t('ui.teams.identityExpert', { name: leader.name }) }
    : { initial: t('ui.teams.leaderShort'), label: t('ui.teams.identity') }
})

registerFeature({
  id: 'teams',
  eventPrefix: 'team.',
  onEvent: onTeamEvent,
  // 一轮结束（正常结束、出错、停止）：没回答的计划卡作废，还没结束的成员算停下
  onRunFinished(conversationId) {
    const run = teams.runs[conversationId]
    if (!run) return
    run.pending = null
    run.finished = true
    for (const m of Object.values(run.members)) if (!FINAL.includes(m.status)) m.status = 'stopped'
  },
  // 团队模式被 IT 关掉：离开团队页
  onAppChanged() {
    if (state.app && !state.app.teams && state.view === 'teams') state.view = 'chat'
  },
})
