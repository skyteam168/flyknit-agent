import { computed } from 'vue'
import { isFinal, state } from './store'
import { i18n } from './i18n'
import type { ScheduledTask, SubtaskView, TerminalRun } from './types'

/**
 * 右侧「后台」标签要列的东西，分散在好几处：正在跑的任务（任务中心那份）、上次没做完的、
 * 各个对话里还在跑的后台命令、专家子任务、快要到点的定时任务。这里统一取出来，标签和计数共用。
 */

/** 所有对话里还在跑的后台命令（开发服务器之类）。终端事件对所有对话都推，所以这里是全的 */
export const backgroundCommands = computed<TerminalRun[]>(() =>
  Object.values(state.terminal)
    .flat()
    .filter((r) => r.background && r.running),
)

/** 还没结束的专家子任务（只有打开过、或者正在界面上跑的对话才有） */
export const activeSubtasks = computed<{ conversationId: string; sub: SubtaskView }[]>(() =>
  Object.entries(state.byId).flatMap(([conversationId, s]) =>
    Object.values(s.subtasks)
      .filter((x) => !isFinal(x.status))
      .map((sub) => ({ conversationId, sub })),
  ),
)

/** 24 小时内要跑的定时任务，按时间排 */
export const upcomingSchedules = computed<ScheduledTask[]>(() => {
  const until = Date.now() + 24 * 3600_000
  return state.schedules
    .filter((s) => s.enabled && s.nextRunAt && new Date(s.nextRunAt).getTime() <= until)
    .sort((a, b) => new Date(a.nextRunAt!).getTime() - new Date(b.nextRunAt!).getTime())
})

/** 现在有几件事在后台进行（标签上的数字）：不算定时任务（还没开始） */
export const backgroundCount = computed(
  () => state.runs.running.length + state.runs.interrupted.length + backgroundCommands.value.length + activeSubtasks.value.length,
)

/** 有没有要在「后台」标签里显示的 */
export const hasBackground = computed(() => backgroundCount.value > 0 || upcomingSchedules.value.length > 0)

/** 这个对话的标题（侧栏里有就用侧栏的；找不到时不显示内部编号） */
export function conversationTitle(id: string): string {
  return state.conversations.find((c) => c.id === id)?.title || i18n.global.t('ui.tasks.untitled')
}
