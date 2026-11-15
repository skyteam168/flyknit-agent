/**
 * 通知正文的第二道清洗（第一道在服务端 services/notice_service.py，规则一样）。
 * 正文会用 v-html 渲染，所以这里只留白名单里的标签和属性：
 * - 脚本、样式表、iframe、表单等整段去掉；不认识的标签去掉标签、留文字；
 * - 链接只认 http / https / mailto，一律新窗口打开（宿主会交给系统浏览器）；
 * - 图片、视频只认宿主代理的通知媒体（https://notices.flyknit.local/{id}）和内嵌的位图；
 * - 行内样式只留对齐和颜色。
 */
const ALLOWED: Record<string, string[]> = {
  p: [], br: [], hr: [], div: [], span: [], h1: [], h2: [], h3: [], h4: [],
  strong: [], b: [], em: [], i: [], u: [], s: [], del: [], sub: [], sup: [], mark: [], code: [], pre: [], blockquote: [],
  ul: [], ol: ['start'], li: [], a: ['href', 'title'], img: ['src', 'alt', 'width', 'height'], video: ['src', 'poster', 'width'],
  figure: [], figcaption: [], table: [], thead: [], tbody: [], tr: [], th: ['colspan', 'rowspan'], td: ['colspan', 'rowspan'],
}
const DROP = new Set(['script', 'style', 'iframe', 'object', 'embed', 'svg', 'math', 'template', 'noscript', 'form', 'textarea',
  'select', 'button', 'input', 'link', 'meta', 'frame', 'frameset', 'applet', 'base', 'title', 'head'])
const STYLE_TAGS = new Set(['p', 'div', 'span', 'h1', 'h2', 'h3', 'h4', 'td', 'th', 'li', 'mark', 'figcaption'])
const COLOR = /^(#[0-9a-fA-F]{3,8}|rgba?\([\d\s.,%]+\)|[a-zA-Z]{3,20})$/
const STYLE: Record<string, RegExp> = { 'text-align': /^(left|right|center|justify)$/, color: COLOR, 'background-color': COLOR }
const MEDIA = /^(https:\/\/notices\.flyknit\.local\/[0-9a-f]{32}|data:image\/(png|jpeg|gif|webp);base64,[A-Za-z0-9+/=]+)$/

function safeHref(v: string) {
  const low = v.replace(/[\s\x00-\x1f]/g, '').toLowerCase()
  return /^(https?:\/\/|mailto:)/.test(low) ? v.trim() : null
}

function safeStyle(v: string) {
  const kept: string[] = []
  for (const part of v.split(';')) {
    const i = part.indexOf(':')
    if (i < 0) continue
    const prop = part.slice(0, i).trim().toLowerCase()
    const val = part.slice(i + 1).trim()
    if (STYLE[prop]?.test(val)) kept.push(`${prop}: ${val}`)
  }
  return kept.join('; ')
}

function clean(node: Node, out: Node, doc: Document) {
  for (const child of Array.from(node.childNodes)) {
    if (child.nodeType === Node.TEXT_NODE) {
      out.appendChild(doc.createTextNode(child.textContent ?? ''))
      continue
    }
    if (child.nodeType !== Node.ELEMENT_NODE) continue
    const el = child as Element
    const tag = el.tagName.toLowerCase()
    if (DROP.has(tag)) continue
    const allowed = ALLOWED[tag]
    if (!allowed) {
      clean(el, out, doc) // 不认识的标签：留文字
      continue
    }
    const copy = doc.createElement(tag)
    for (const name of allowed) {
      const v = el.getAttribute(name)
      if (v == null) continue
      if (name === 'href') {
        const href = safeHref(v)
        if (href) copy.setAttribute('href', href)
      } else if (name === 'src' || name === 'poster') {
        if (MEDIA.test(v.trim())) copy.setAttribute(name, v.trim())
      } else if (['width', 'height', 'colspan', 'rowspan', 'start'].includes(name)) {
        if (/^\d{1,4}$/.test(v.trim())) copy.setAttribute(name, v.trim())
      } else {
        copy.setAttribute(name, v.slice(0, 200))
      }
    }
    if (STYLE_TAGS.has(tag) && el.getAttribute('style')) {
      const st = safeStyle(el.getAttribute('style') ?? '')
      if (st) copy.setAttribute('style', st)
    }
    if ((tag === 'img' || tag === 'video') && !copy.getAttribute('src')) continue
    if (tag === 'a' && copy.getAttribute('href')) {
      copy.setAttribute('target', '_blank')
      copy.setAttribute('rel', 'noopener noreferrer')
    }
    if (tag === 'video') {
      copy.setAttribute('controls', '')
      copy.setAttribute('preload', 'metadata')
    }
    if (tag === 'img') copy.setAttribute('loading', 'lazy')
    if (tag !== 'video') clean(el, copy, doc)
    out.appendChild(copy)
  }
}

export function sanitizeNoticeHtml(html: string): string {
  const parsed = new DOMParser().parseFromString(`<body>${html ?? ''}</body>`, 'text/html')
  const doc = document.implementation.createHTMLDocument('')
  const box = doc.createElement('div')
  clean(parsed.body, box, doc)
  return box.innerHTML
}
