// 输入框里的内置斜杠命令（参照 Claude Code 的 /compact、/permissions、/model、/context……，见 docs/backlog.md B1）。
//
// 规矩：
// 1. 内置命令优先：和快捷指令（IT 发布的、员工自己加的）重名时以内置为准。
// 2. 不发给模型：调权限、换模型这类在本地执行，结果用提示条说一声，不占 token；只有 /compact、/init 会调用模型。
// 3. 不开后门：每个命令走和界面按钮完全相同的 store 函数 / 宿主接口；IT 锁定、部门 / 单台电脑的安全设置、
//    专家只读、「完全权限」的二次确认一个不少。
// 4. 命令名统一英文；说明和提示三语（i18n ui.slash.cmd.* / ui.slash.run.*）。

import { i18n } from './i18n'
import {
  askSide,
  compactConversation,
  current,
  currentExpert,
  forkConversation,
  loadChanges,
  newConversation,
  selectModel,
  selectedPermission,
  setPermission,
  setPlanMode,
  state,
  toast,
  toggleSessionPanel,
} from './store'
import type { Mode, Permission } from './types'

// 键是拼出来的（ui.slash.cmd.<名字>、ui.permission.<档>），绕开 vue-i18n 按字面量检查键名的类型
type Translate = (key: string, named?: Record<string, unknown>, options?: { locale?: string }) => string
const tr = i18n.global.t as unknown as Translate
const t = (key: string, args?: Record<string, unknown>) => tr(key, args ?? {})

export interface BuiltinContext {
  mode: Mode
  busy: boolean
  /** 开发者工具开着、办事模式：/init 可用 */
  initAvailable: boolean
  planMode: boolean
}

/** 命令执行完要输入框做的事 */
export type BuiltinOutcome =
  /** 已经在本地办完（或提示过为什么办不了），清空输入框 */
  | { kind: 'done' }
  /** 要把这段话作为普通消息发出去（/init） */
  | { kind: 'send'; text: string }
  /** 「完全权限」要先弹风险确认框（和权限选择器一样） */
  | { kind: 'confirmFull' }
  /** 不是可用的内置命令：交给快捷指令处理 */
  | { kind: 'pass' }

export interface BuiltinArg {
  value: string
  label: string
}

export interface BuiltinCommand {
  name: string
  aliases?: string[]
  /** 参数提示，如「[只读|工作区|完全]」；没有参数的为空 */
  argHint?: () => string
  /** 输完命令名按空格后列出的可选参数 */
  args?: () => BuiltinArg[]
  /** 这时能不能用（不能用的不出现在列表里、也不拦截同名快捷指令） */
  available?: (ctx: BuiltinContext) => boolean
  run: (arg: string, ctx: BuiltinContext) => Promise<BuiltinOutcome> | BuiltinOutcome
}

const done: BuiltinOutcome = { kind: 'done' }

/** 需要一个已经开始的对话 */
function needConversation(): string | null {
  if (current.value) return current.value.id
  toast(t('ui.slash.run.needConversation'))
  return null
}

const permissions: Permission[] = ['readonly', 'workspace', 'full']

/** 「完全权限」被工作区隔离锁住时不能选（和权限选择器里置灰的条件一样） */
const fullLocked = () => state.app?.sandboxed !== false

/** 参数可以写英文 key，也可以写界面上显示的名字（只读 / 工作区 / 完全，三种语言都认） */
export function matchPermission(arg: string): Permission | null {
  const a = arg.trim().toLowerCase()
  if (!a) return null
  for (const p of permissions) {
    if (a === p) return p
    const labels = ['zh-CN', 'vi-VN', 'en-US'].map((l) => tr(`ui.permission.${p}`, {}, { locale: l }).toLowerCase())
    if (labels.some((x) => x === a || x.startsWith(a))) return p
  }
  // 常见的简写
  const short: Record<string, Permission> = { ro: 'readonly', read: 'readonly', 'read-only': 'readonly', '只读': 'readonly', '只看': 'readonly', ws: 'workspace', edit: 'workspace', '完全': 'full', all: 'full' }
  return short[a] ?? null
}

/** 按名字找模型：完全相等优先，其次包含；包含的有好几个就算没找到（让员工说清楚） */
export function matchModel(arg: string, models: { id: number; name: string; model: string }[]): { id: number | null; ambiguous: string[] } | null {
  const a = arg.trim().toLowerCase()
  if (!a) return null
  if (['auto', 'default', '自动', '默认', 'tự động'].includes(a)) return { id: null, ambiguous: [] }
  const exact = models.find((m) => m.name.toLowerCase() === a || m.model.toLowerCase() === a)
  if (exact) return { id: exact.id, ambiguous: [] }
  const hits = models.filter((m) => m.name.toLowerCase().includes(a) || m.model.toLowerCase().includes(a))
  if (hits.length === 1) return { id: hits[0].id, ambiguous: [] }
  if (hits.length > 1) return { id: -1, ambiguous: hits.map((m) => m.name) }
  return null
}

export const BUILTINS: BuiltinCommand[] = [
  {
    name: 'help',
    run: () => {
      state.commandInfo = 'help'
      return done
    },
  },
  {
    name: 'btw',
    argHint: () => t('ui.slash.arg.question'),
    available: () => !!current.value,
    run: (arg) => {
      if (!arg) {
        toast(t('ui.slash.run.btwEmpty'))
        return done
      }
      void askSide(arg)
      return done
    },
  },
  {
    name: 'init',
    available: (ctx) => ctx.initAvailable,
    run: (arg) => ({ kind: 'send', text: t('ui.slash.initPrompt') + (arg ? `\n\n${arg}` : '') }),
  },
  {
    name: 'compact',
    argHint: () => t('ui.slash.arg.focus'),
    available: (ctx) => ctx.mode !== 'translate',
    run: async (arg, ctx) => {
      const id = needConversation()
      if (!id) return done
      if (ctx.busy) {
        toast(t('ui.slash.run.busy'))
        return done
      }
      // 结果（整理了多少、或者为什么没整理）是宿主给的话，和「会话信息」里点「整理对话」一样
      const message = await compactConversation(id, arg || undefined)
      if (message) toast(message)
      return done
    },
  },
  {
    name: 'permission',
    aliases: ['permissions'],
    argHint: () => permissions.map((p) => t(`ui.permission.${p}`)).join(' | '),
    args: () => permissions.map((p) => ({ value: p, label: t(`ui.permission.${p}`) })),
    // 和输入框下面的权限选择器同样的条件：办事模式、不是只读专家
    available: (ctx) => ctx.mode === 'agent' && !currentExpert.value?.readOnly,
    run: async (arg) => {
      if (!arg) {
        toast(t('ui.slash.run.permissionNow', { now: t(`ui.permission.${selectedPermission.value}`), all: permissions.map((p) => t(`ui.permission.${p}`)).join(' / ') }))
        return done
      }
      const p = matchPermission(arg)
      if (!p) {
        toast(t('ui.slash.run.permissionUnknown', { arg }))
        return done
      }
      if (p === selectedPermission.value) {
        toast(t('ui.slash.run.permissionSame', { name: t(`ui.permission.${p}`) }))
        return done
      }
      if (p === 'full') {
        if (fullLocked()) {
          toast(t('ui.permission.fullManaged'))
          return done
        }
        return { kind: 'confirmFull' }
      }
      await setPermission(p)
      toast(t('ui.slash.run.permissionSet', { name: t(`ui.permission.${p}`) }))
      return done
    },
  },
  {
    name: 'model',
    argHint: () => t('ui.slash.arg.model'),
    args: () => [{ value: 'auto', label: t('ui.model.auto') }, ...state.models.map((m) => ({ value: m.name, label: m.provider }))],
    run: async (arg) => {
      if (!arg) {
        const names = state.models.map((m) => m.name)
        toast(names.length ? t('ui.slash.run.modelList', { names: names.join('、') }) : t('ui.slash.run.modelNone'))
        return done
      }
      const hit = matchModel(arg, state.models)
      if (!hit) {
        toast(t('ui.slash.run.modelUnknown', { arg }))
        return done
      }
      if (hit.id === -1) {
        toast(t('ui.slash.run.modelAmbiguous', { names: hit.ambiguous.join('、') }))
        return done
      }
      await selectModel(hit.id)
      const name = hit.id === null ? t('ui.model.auto') : state.models.find((m) => m.id === hit.id)?.name ?? arg
      toast(t('ui.slash.run.modelSet', { name }))
      return done
    },
  },
  {
    name: 'plan',
    // 和输入框下面的「计划」按钮同样的条件
    available: (ctx) => ctx.mode === 'agent',
    run: async (_arg, ctx) => {
      await setPlanMode(!ctx.planMode)
      toast(ctx.planMode ? t('ui.slash.run.planOff') : t('ui.slash.run.planOn'))
      return done
    },
  },
  {
    name: 'new',
    aliases: ['clear'],
    run: (_arg, ctx) => {
      // 沿用当前模式；工作区跟着最近一次选的走（和侧栏「新任务」一样）
      newConversation(ctx.mode)
      return done
    },
  },
  {
    name: 'context',
    run: () => {
      toggleSessionPanel(true)
      return done
    },
  },
  {
    name: 'usage',
    run: () => {
      state.usageOpen = true
      return done
    },
  },
  {
    name: 'rewind',
    available: (ctx) => ctx.mode === 'agent',
    run: async () => {
      const id = needConversation()
      if (!id) return done
      await loadChanges(id)
      const turns = state.changes[id] ?? []
      const last = turns[turns.length - 1]
      if (!last) {
        toast(t('ui.slash.run.rewindNone'))
        return done
      }
      // 打开现有的「改动的文件」对话框，在里面看清楚再点还原，不直接还原
      state.changesView = { conversationId: id, turnId: last.turnId }
      return done
    },
  },
  {
    name: 'fork',
    available: () => !!current.value,
    run: async (_arg, ctx) => {
      const id = needConversation()
      if (!id) return done
      if (ctx.busy) {
        toast(t('ui.slash.run.busy'))
        return done
      }
      const messages = state.byId[id]?.messages ?? []
      const last = [...messages].reverse().find((m) => m.role === 'assistant')
      if (!last) {
        toast(t('ui.slash.run.forkNone'))
        return done
      }
      await forkConversation(last.id)
      return done
    },
  },
  {
    name: 'memory',
    run: () => {
      state.memoryOpen = true
      return done
    },
  },
  {
    name: 'status',
    run: () => {
      state.commandInfo = 'status'
      return done
    },
  },
]

/** 所有内置命令名和别名（小写）：后台、设置里发布快捷指令时用来提示「和内置命令重名，不会生效」 */
export const BUILTIN_NAMES: string[] = BUILTINS.flatMap((c) => [c.name, ...(c.aliases ?? [])])

export function findBuiltin(name: string, ctx: BuiltinContext): BuiltinCommand | null {
  const n = name.toLowerCase()
  const cmd = BUILTINS.find((c) => c.name === n || (c.aliases ?? []).includes(n))
  if (!cmd) return null
  // 当前用不了的（比如没开开发者工具时的 /init）：不拦截，同名快捷指令照常生效
  return !cmd.available || cmd.available(ctx) ? cmd : null
}

export function availableBuiltins(ctx: BuiltinContext): BuiltinCommand[] {
  return BUILTINS.filter((c) => !c.available || c.available(ctx))
}

export async function runBuiltin(name: string, arg: string, ctx: BuiltinContext): Promise<BuiltinOutcome> {
  const cmd = findBuiltin(name, ctx)
  return cmd ? await cmd.run(arg.trim(), ctx) : { kind: 'pass' }
}

/** 列表里的说明：ui.slash.cmd.<名字> */
export function describeBuiltin(c: BuiltinCommand): string {
  return t(`ui.slash.cmd.${c.name}`)
}
