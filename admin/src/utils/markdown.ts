// 协议预览用的极简 Markdown：标题、段落、列表、粗体、斜体、行内代码、引用、分隔线。
// 先整体转义再加标签，输入里的 HTML 一律当文字显示。员工端登录界面用的是完整的 markdown-it，
// 这里只求编辑时看得出大致效果，不再为后台单独引一个库。

function escape(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

function inline(text: string): string {
  return escape(text)
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*]+)\*/g, '<em>$1</em>')
}

export function renderMarkdown(source: string): string {
  const out: string[] = []
  let list: 'ul' | 'ol' | null = null
  let paragraph: string[] = []
  const flush = () => {
    if (paragraph.length) out.push(`<p>${paragraph.map(inline).join('<br>')}</p>`)
    paragraph = []
  }
  const closeList = () => {
    if (list) out.push(`</${list}>`)
    list = null
  }
  for (const raw of source.replace(/\r/g, '').split('\n')) {
    const line = raw.trimEnd()
    const heading = /^(#{1,4})\s+(.*)$/.exec(line)
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line)
    const ordered = /^\s*\d+[.、]\s+(.*)$/.exec(line)
    if (!line.trim()) {
      flush()
      closeList()
    } else if (heading) {
      flush()
      closeList()
      out.push(`<h${heading[1].length}>${inline(heading[2])}</h${heading[1].length}>`)
    } else if (/^(-{3,}|\*{3,})$/.test(line.trim())) {
      flush()
      closeList()
      out.push('<hr>')
    } else if (bullet || ordered) {
      flush()
      const kind = bullet ? 'ul' : 'ol'
      if (list !== kind) {
        closeList()
        out.push(`<${kind}>`)
        list = kind
      }
      out.push(`<li>${inline((bullet ?? ordered)![1])}</li>`)
    } else if (line.startsWith('>')) {
      flush()
      closeList()
      out.push(`<blockquote>${inline(line.replace(/^>\s?/, ''))}</blockquote>`)
    } else {
      closeList()
      paragraph.push(line)
    }
  }
  flush()
  closeList()
  return out.join('\n')
}
