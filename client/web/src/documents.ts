import MarkdownIt from 'markdown-it'
import type { Token } from 'markdown-it'
import DOMPurify from 'dompurify'

/**
 * 文档画布用的 Markdown 处理：
 * - 渲染成可编辑的 HTML（不带聊天里的代码高亮、复制按钮，免得编辑时被改进正文）；
 * - 员工改完把编辑器里的 HTML 转回 Markdown 存盘；
 * - 导出 Word 时拆成块（DocModel）交给宿主写 docx。
 * 渲染和拆块用同一个 markdown-it，导出的结构和画布里看到的一致。
 */
const md: MarkdownIt = new MarkdownIt({ html: false, linkify: false, breaks: true })

export function renderDocument(text: string): string {
  return DOMPurify.sanitize(md.render(text))
}

// ---------------------------------------------------------------------
// 编辑器 HTML → Markdown
// ---------------------------------------------------------------------

const BLOCK = new Set(['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'UL', 'OL', 'BLOCKQUOTE', 'PRE', 'TABLE', 'HR'])

/** 把编辑器（contenteditable）里的内容转回 Markdown。只认画布能产生的那些标签，别的按普通文字处理。 */
export function htmlToMarkdown(root: HTMLElement): string {
  return blocks(root, '').join('\n\n').replace(/\n{3,}/g, '\n\n').trim() + '\n'
}

function blocks(parent: Node, indent: string): string[] {
  const out: string[] = []
  let loose: Node[] = []
  const flush = () => {
    const text = inlineOf(loose).trim()
    if (text) out.push(escapeLineStart(text))
    loose = []
  }
  for (const node of Array.from(parent.childNodes)) {
    if (node.nodeType === Node.ELEMENT_NODE && BLOCK.has((node as Element).tagName)) {
      flush()
      const b = block(node as HTMLElement, indent)
      if (b.trim()) out.push(b)
    } else {
      loose.push(node)
    }
  }
  flush()
  return out
}

function block(el: HTMLElement, indent: string): string {
  const tag = el.tagName
  if (/^H[1-6]$/.test(tag)) return '#'.repeat(Number(tag[1])) + ' ' + inline(el).trim().replace(/\n+/g, ' ')
  if (tag === 'P' || tag === 'DIV') {
    // 回车产生的 <div> 里可能又套了块（粘贴、列表里退出来）
    if (Array.from(el.children).some((c) => BLOCK.has(c.tagName))) return blocks(el, indent).join('\n\n')
    return escapeLineStart(inline(el).trim())
  }
  if (tag === 'HR') return '---'
  if (tag === 'PRE') {
    const code = (el.textContent ?? '').replace(/\n$/, '')
    const lang = /language-(\S+)/.exec(el.querySelector('code')?.className ?? '')?.[1] ?? ''
    const fence = code.includes('```') ? '~~~' : '```'
    return `${fence}${lang}\n${code}\n${fence}`
  }
  if (tag === 'BLOCKQUOTE') {
    return blocks(el, indent)
      .join('\n\n')
      .split('\n')
      .map((l) => (l ? '> ' + l : '>'))
      .join('\n')
  }
  if (tag === 'UL' || tag === 'OL') return list(el, indent)
  if (tag === 'TABLE') return table(el)
  return inline(el).trim()
}

function list(el: HTMLElement, indent: string): string {
  const ordered = el.tagName === 'OL'
  let n = Number(el.getAttribute('start') ?? '1') || 1
  const lines: string[] = []
  for (const li of Array.from(el.children)) {
    if (li.tagName !== 'LI') continue
    const marker = ordered ? `${n++}. ` : '- '
    const pad = ' '.repeat(marker.length)
    // 列表项自己的文字 + 里面的子列表（缩进一层）
    const own: Node[] = []
    const nested: string[] = []
    for (const c of Array.from(li.childNodes)) {
      if (c.nodeType === Node.ELEMENT_NODE && ((c as Element).tagName === 'UL' || (c as Element).tagName === 'OL')) {
        nested.push(list(c as HTMLElement, indent + pad))
      } else if (c.nodeType === Node.ELEMENT_NODE && ((c as Element).tagName === 'P' || (c as Element).tagName === 'DIV')) {
        own.push(...Array.from(c.childNodes), document.createElement('br'))
      } else {
        own.push(c)
      }
    }
    const text = inlineOf(own).trim().replace(/\n/g, '\n' + indent + pad)
    lines.push(indent + marker + text)
    lines.push(...nested)
  }
  return lines.join('\n')
}

function table(el: HTMLElement): string {
  const rows = Array.from(el.querySelectorAll('tr')).map((tr) =>
    Array.from(tr.children).map((c) => inline(c as HTMLElement).trim().replace(/\n+/g, ' ').replace(/\|/g, '\\|')),
  )
  if (!rows.length) return ''
  const cols = Math.max(...rows.map((r) => r.length))
  const pad = (r: string[]) => [...r, ...Array(cols - r.length).fill('')]
  const line = (r: string[]) => '| ' + pad(r).join(' | ') + ' |'
  return [line(rows[0]), '|' + ' --- |'.repeat(cols), ...rows.slice(1).map(line)].join('\n')
}

function inline(el: Node): string {
  return inlineOf(Array.from(el.childNodes))
}

function inlineOf(nodes: Node[]): string {
  let s = ''
  for (const n of nodes) s += inlineNode(n)
  // 结尾的换行（编辑器在段尾补的 <br>）不算内容；<br> 后面跟着的那个空白也去掉
  return s.replace(/\n /g, '\n').replace(/\n+$/, '')
}

function inlineNode(n: Node): string {
  // HTML 里文字中的换行只是空白（渲染出来的 <br> 后面还跟着一个换行）；真正的换行只认 <br>
  if (n.nodeType === Node.TEXT_NODE) return escapeInline((n.textContent ?? '').replace(/\u00a0/g, ' ').replace(/[ \t]*\n[ \t]*/g, ' '))
  if (n.nodeType !== Node.ELEMENT_NODE) return ''
  const el = n as HTMLElement
  const inner = () => inline(el)
  switch (el.tagName) {
    case 'BR':
      return '\n'
    case 'STRONG':
    case 'B':
      return wrap(inner(), '**')
    case 'EM':
    case 'I':
      return wrap(inner(), '*')
    case 'S':
    case 'DEL':
    case 'STRIKE':
      return wrap(inner(), '~~')
    case 'CODE': {
      const code = el.textContent ?? ''
      const tick = code.includes('`') ? '``' : '`'
      return code ? `${tick}${code}${tick}` : ''
    }
    case 'A': {
      const href = el.getAttribute('href') ?? ''
      const text = inner()
      return href ? `[${text || href}](${href.replace(/\)/g, '%29')})` : text
    }
    case 'IMG':
      return ''
    default:
      // span、font 之类：编辑器自己插进来的样式，只取文字
      return BLOCK.has(el.tagName) ? '\n' + block(el, '') + '\n' : inner()
  }
}

/** 加粗、斜体：标记要紧贴文字，前后的空格放到标记外面（"** 文字**" 不是加粗） */
function wrap(text: string, mark: string): string {
  const m = /^(\s*)([\s\S]*?)(\s*)$/.exec(text)!
  return m[2] ? `${m[1]}${mark}${m[2]}${mark}${m[3]}` : text
}

/** 正文里会被当成格式的字符转义；下划线只在可能变成强调时转（file_name 这种不动，免得源文件里满是反斜杠） */
function escapeInline(text: string): string {
  return text
    .replace(/([\\`*[\]~])/g, '\\$1')
    .replace(/(^|[^\p{L}\p{N}])_|_(?=$|[^\p{L}\p{N}])/gu, (m) => m.replace('_', '\\_'))
}

/** 段首看起来像标题、列表、引用的，前面加反斜杠，免得存回去变了格式 */
function escapeLineStart(text: string): string {
  return text
    .split('\n')
    .map((l) => l.replace(/^(\s*)(#{1,6}\s|>|[-+]\s|\d+[.)]\s)/, (_m, sp: string, mark: string) => sp + mark.replace(/^(\d+)([.)])/, '$1\\$2').replace(/^([#>+-])/, '\\$1')))
    .join('\n')
}

// ---------------------------------------------------------------------
// Markdown → Word 的块（宿主按块写 docx，见 DocxWriter.cs）
// ---------------------------------------------------------------------

export interface DocRun {
  text: string
  b?: boolean
  i?: boolean
  s?: boolean
  code?: boolean
  link?: string
}
export interface DocBlock {
  type: 'heading' | 'paragraph' | 'list' | 'quote' | 'code' | 'table' | 'hr'
  level?: number
  ordered?: boolean
  listId?: string
  runs?: DocRun[]
  text?: string
  rows?: DocRun[][][]
  header?: boolean
}
export interface DocModel {
  title: string
  blocks: DocBlock[]
}

export function markdownToDocModel(text: string, title: string): DocModel {
  const tokens = md.parse(text, {})
  const blocks: DocBlock[] = []
  const lists: { ordered: boolean; id: string }[] = []
  let quote = 0
  let listSeq = 0
  let tableRows: DocRun[][][] | null = null
  let header = false

  for (let i = 0; i < tokens.length; i++) {
    const t = tokens[i]
    switch (t.type) {
      case 'heading_open':
        blocks.push({ type: 'heading', level: Number(t.tag.slice(1)), runs: runsOf(tokens[i + 1]) })
        i += 2
        break
      case 'bullet_list_open':
      case 'ordered_list_open':
        lists.push({ ordered: t.type === 'ordered_list_open', id: `l${++listSeq}` })
        break
      case 'bullet_list_close':
      case 'ordered_list_close':
        lists.pop()
        break
      case 'blockquote_open':
        quote++
        break
      case 'blockquote_close':
        quote--
        break
      case 'paragraph_open': {
        const runs = runsOf(tokens[i + 1])
        const top = lists[lists.length - 1]
        if (top) blocks.push({ type: 'list', ordered: top.ordered, listId: top.id, level: lists.length - 1, runs })
        else blocks.push({ type: quote > 0 ? 'quote' : 'paragraph', runs })
        i += 2
        break
      }
      case 'fence':
      case 'code_block':
        blocks.push({ type: 'code', text: t.content })
        break
      case 'hr':
        blocks.push({ type: 'hr' })
        break
      case 'table_open':
        tableRows = []
        header = false
        break
      case 'thead_open':
        header = true
        break
      case 'tr_open':
        tableRows?.push([])
        break
      case 'th_open':
      case 'td_open':
        tableRows?.[tableRows.length - 1]?.push(runsOf(tokens[i + 1]))
        break
      case 'table_close':
        if (tableRows) blocks.push({ type: 'table', rows: tableRows, header })
        tableRows = null
        break
    }
  }
  return { title, blocks }
}

function runsOf(inlineToken: Token | undefined): DocRun[] {
  const runs: DocRun[] = []
  const fmt = { b: 0, i: 0, s: 0 }
  let link = ''
  const push = (text: string, extra: Partial<DocRun> = {}) => {
    if (!text) return
    runs.push({ text, b: fmt.b > 0, i: fmt.i > 0, s: fmt.s > 0, ...(link ? { link } : {}), ...extra })
  }
  for (const c of inlineToken?.children ?? []) {
    switch (c.type) {
      case 'text':
        push(c.content)
        break
      case 'code_inline':
        push(c.content, { code: true })
        break
      case 'softbreak':
      case 'hardbreak':
        push('\n')
        break
      case 'strong_open':
        fmt.b++
        break
      case 'strong_close':
        fmt.b--
        break
      case 'em_open':
        fmt.i++
        break
      case 'em_close':
        fmt.i--
        break
      case 's_open':
        fmt.s++
        break
      case 's_close':
        fmt.s--
        break
      case 'link_open':
        link = c.attrGet('href') ?? ''
        break
      case 'link_close':
        link = ''
        break
      case 'image':
        push(c.content)
        break
    }
  }
  return runs
}

/** 打印 / 另存 PDF 用的一页 HTML（只有文档本身，不带界面） */
export function printableHtml(title: string, text: string): string {
  const esc = title.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!)
  return `<!doctype html><html><head><meta charset="utf-8"><title>${esc}</title><style>
body{font-family:"Be Vietnam Pro","Microsoft YaHei","Segoe UI",sans-serif;color:#1f2328;line-height:1.7;margin:0;padding:24px 40px;font-size:14px}
h1{font-size:24px}h2{font-size:20px}h3{font-size:17px}h1,h2,h3{color:#1f3864;margin:1.2em 0 .5em}
blockquote{margin:.8em 0;padding:.2em 1em;border-left:4px solid #c8ccd4;color:#555}
table{border-collapse:collapse;width:100%;margin:.8em 0}th,td{border:1px solid #c8ccd4;padding:6px 8px;text-align:left}th{background:#f2f2f2}
pre{background:#f5f5f5;padding:10px;border-radius:4px;white-space:pre-wrap}code{font-family:Consolas,monospace}
@page{margin:18mm}
</style></head><body>${renderDocument(text)}</body></html>`
}
