/**
 * 改动前后的对比：文本按行（Word 的正文也按行），表格先按行对齐再找出改了的格子。
 * 文件不大（预览本身就截断到几百行），用最朴素的最长公共子序列就够了。
 */

export type LineOp =
  | { type: 'same'; text: string; a: number; b: number }
  | { type: 'del'; text: string; a: number }
  | { type: 'add'; text: string; b: number }

/** 两边加起来超过这个规模就不逐行对齐了（只标「整段替换」），免得卡住界面 */
const MAX_CELLS = 4_000_000

/** 最长公共子序列对齐，返回 same / del / add 的序列（行号从 1 开始） */
export function diffLines(before: string[], after: string[]): LineOp[] {
  const n = before.length
  const m = after.length
  if (n * m > MAX_CELLS) {
    return [
      ...before.map((text, i) => ({ type: 'del' as const, text, a: i + 1 })),
      ...after.map((text, i) => ({ type: 'add' as const, text, b: i + 1 })),
    ]
  }
  // lcs[i][j]：before[i..] 和 after[j..] 的最长公共子序列长度
  const lcs: Uint32Array[] = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1))
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      lcs[i][j] = before[i] === after[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1])
  const ops: LineOp[] = []
  let i = 0
  let j = 0
  while (i < n && j < m) {
    if (before[i] === after[j]) {
      ops.push({ type: 'same', text: before[i], a: i + 1, b: j + 1 })
      i++
      j++
    } else if (lcs[i + 1][j] >= lcs[i][j + 1]) {
      ops.push({ type: 'del', text: before[i], a: i + 1 })
      i++
    } else {
      ops.push({ type: 'add', text: after[j], b: j + 1 })
      j++
    }
  }
  for (; i < n; i++) ops.push({ type: 'del', text: before[i], a: i + 1 })
  for (; j < m; j++) ops.push({ type: 'add', text: after[j], b: j + 1 })
  return ops
}

/** 只留改动附近几行，其余折成「… 省略 N 行」 */
export function withContext(ops: LineOp[], context = 3): (LineOp | { type: 'gap'; count: number })[] {
  const keep = new Array(ops.length).fill(false)
  ops.forEach((op, k) => {
    if (op.type === 'same') return
    for (let x = Math.max(0, k - context); x <= Math.min(ops.length - 1, k + context); x++) keep[x] = true
  })
  const out: (LineOp | { type: 'gap'; count: number })[] = []
  let skipped = 0
  ops.forEach((op, k) => {
    if (keep[k]) {
      if (skipped) out.push({ type: 'gap', count: skipped })
      skipped = 0
      out.push(op)
    } else skipped++
  })
  if (skipped) out.push({ type: 'gap', count: skipped })
  return out
}

export interface TableRowDiff {
  /** same：没变；changed：同一行里有格子变了；add：新增的行；del：删掉的行 */
  type: 'same' | 'changed' | 'add' | 'del'
  /** 现在的行号（删掉的行为原来的行号），从 1 开始 */
  row: number
  cells: { text: string; before?: string; changed: boolean }[]
}

/** 表格对比：先按整行对齐（插入、删除行不会让后面全部变红），挨着的一删一增当成同一行被改了，再逐格比较 */
export function diffTable(before: string[][], after: string[][]): TableRowDiff[] {
  const key = (r: string[]) => r.join('\u0001')
  const ops = diffLines(before.map(key), after.map(key))
  const out: TableRowDiff[] = []
  for (let k = 0; k < ops.length; k++) {
    const op = ops[k]
    if (op.type === 'same') {
      out.push({ type: 'same', row: op.b, cells: after[op.b - 1].map((text) => ({ text, changed: false })) })
      continue
    }
    // 连续的一段删除 + 一段新增：一一配对成「改了的行」
    const dels: number[] = []
    const adds: number[] = []
    while (k < ops.length && ops[k].type !== 'same') {
      const o = ops[k]
      if (o.type === 'del') dels.push(o.a)
      else if (o.type === 'add') adds.push(o.b)
      k++
    }
    k--
    // 删掉的行和新增的行配对：同一列相同的格子过半才算「同一行被改了」（比如只改了数量），
    // 否则是真的新增 / 删除——插入一行时不会把下面那行错当成被改
    const similarity = (a: string[], b: string[]) => {
      const width = Math.max(a.length, b.length, 1)
      let same = 0
      for (let c = 0; c < width; c++) if ((a[c] ?? '') === (b[c] ?? '')) same++
      return same / width
    }
    const pairOf = new Map<number, number>() // add 下标 → del 下标
    let from = 0
    for (let d = 0; d < dels.length; d++) {
      let best = -1
      let score = 0.5
      for (let x = from; x < adds.length; x++) {
        const sim = similarity(before[dels[d] - 1], after[adds[x] - 1])
        if (sim >= score && !pairOf.has(x)) {
          best = x
          score = sim
        }
      }
      if (best >= 0) {
        pairOf.set(best, d)
        from = best + 1
      }
    }
    const paired = new Set(pairOf.values())
    for (let x = 0; x < adds.length; x++) {
      const a = after[adds[x] - 1]
      const d = pairOf.get(x)
      if (d === undefined) {
        out.push({ type: 'add', row: adds[x], cells: a.map((text) => ({ text, changed: true })) })
        continue
      }
      const b = before[dels[d] - 1]
      const width = Math.max(a.length, b.length)
      out.push({
        type: 'changed',
        row: adds[x],
        cells: Array.from({ length: width }, (_, c) => {
          const now = a[c] ?? ''
          const was = b[c] ?? ''
          return { text: now, before: was, changed: now !== was }
        }),
      })
    }
    for (let d = 0; d < dels.length; d++)
      if (!paired.has(d)) out.push({ type: 'del', row: dels[d], cells: before[dels[d] - 1].map((text) => ({ text, before: text, changed: true })) })
  }
  return out
}
