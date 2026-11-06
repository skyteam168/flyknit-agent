import type { SlashCommand } from './types'

/**
 * 快捷指令的展开：「/周报 2026-11-03 织造车间」→ 把模板里的 {参数} 按顺序替换。
 * 参数用空格分开；最后一个参数拿走剩下的全部文字（可以带空格）。模板没有参数而后面写了字的，接在模板后面作为补充。
 */

/** 模板里的参数名（按第一次出现的顺序，去重） */
export function templateParams(template: string): string[] {
  return [...new Set([...template.matchAll(/\{([^{}\s]{1,20})\}/g)].map((m) => m[1]))]
}

/** 「/名字 参数…」→ 名字和后面的文字；不是 / 开头返回 null */
export function parseSlash(text: string): { name: string; rest: string } | null {
  const m = /^\/([^\s/]+)(?:\s+([\s\S]*))?$/.exec(text.trim())
  return m ? { name: m[1], rest: (m[2] ?? '').trim() } : null
}

export function expandCommand(cmd: SlashCommand, rest: string): { text: string; missing: string[] } {
  const params = templateParams(cmd.template)
  if (params.length === 0) {
    return { text: rest ? `${cmd.template}\n\n${rest}` : cmd.template, missing: [] }
  }
  const words = rest ? rest.split(/\s+/) : []
  const values: Record<string, string> = {}
  params.forEach((p, i) => {
    const v = i === params.length - 1 ? words.slice(i).join(' ') : words[i]
    if (v) values[p] = v
  })
  const missing = params.filter((p) => !values[p])
  const text = cmd.template.replace(/\{([^{}\s]{1,20})\}/g, (all, p: string) => values[p] ?? all)
  return { text, missing }
}

/** 按名字找（不分大小写）：先找员工自己的，再找 IT 发布的 */
export function findCommand(name: string, mine: SlashCommand[], it: SlashCommand[]): SlashCommand | null {
  const n = name.toLowerCase()
  return mine.find((c) => c.name.toLowerCase() === n) ?? it.find((c) => c.name.toLowerCase() === n) ?? null
}
