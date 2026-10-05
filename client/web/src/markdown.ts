import MarkdownIt from 'markdown-it'
import DOMPurify from 'dompurify'
import hljs from 'highlight.js/lib/core'
import powershell from 'highlight.js/lib/languages/powershell'
import python from 'highlight.js/lib/languages/python'
import json from 'highlight.js/lib/languages/json'
import sql from 'highlight.js/lib/languages/sql'
import bash from 'highlight.js/lib/languages/bash'
import xml from 'highlight.js/lib/languages/xml'
import csharp from 'highlight.js/lib/languages/csharp'
import javascript from 'highlight.js/lib/languages/javascript'
import { i18n } from './i18n'

hljs.registerLanguage('powershell', powershell)
hljs.registerLanguage('python', python)
hljs.registerLanguage('json', json)
hljs.registerLanguage('sql', sql)
hljs.registerLanguage('bash', bash)
hljs.registerLanguage('xml', xml)
hljs.registerLanguage('csharp', csharp)
hljs.registerLanguage('javascript', javascript)

const md: MarkdownIt = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
  highlight(code, lang) {
    const language = lang && hljs.getLanguage(lang) ? lang : null
    const body = language ? hljs.highlight(code, { language }).value : md.utils.escapeHtml(code)
    const label = md.utils.escapeHtml(lang || 'text')
    return `<pre class="code"><div class="code-head"><span>${label}</span><button class="code-copy" type="button" data-copy>${md.utils.escapeHtml(i18n.global.t('message.copy'))}</button></div><code>${body}</code></pre>`
  },
})

// 链接在系统浏览器中打开
const defaultLink = md.renderer.rules.link_open ?? ((tokens, idx, options, _env, self) => self.renderToken(tokens, idx, options))
md.renderer.rules.link_open = (tokens, idx, options, env, self) => {
  tokens[idx].attrSet('target', '_blank')
  tokens[idx].attrSet('rel', 'noopener')
  return defaultLink(tokens, idx, options, env, self)
}

export function renderMarkdown(text: string): string {
  return DOMPurify.sanitize(md.render(text), { ADD_ATTR: ['target', 'data-copy'] })
}
