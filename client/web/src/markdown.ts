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
import { encodeBlockCode, hasBlockRenderer } from './render/blocks'

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
    // 有渲染器的语言（mermaid / dot / chart）留个占位符，挂载后由 RichBlocks 画出来；
    // 这里只放数据，不放任何可执行内容，渲染仍然要过 DOMPurify
    if (lang && hasBlockRenderer(lang)) {
      return `<pre class="rich-block" data-lang="${md.utils.escapeHtml(lang.toLowerCase())}" data-code="${encodeBlockCode(code)}"></pre>`
    }
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

// 模型把工具调用写进正文时的标记（DeepSeek DSML、DeepSeek V3 特殊记号、Qwen/Hermes 的 <tool_call>）。
// 客户端会把它们识别出来执行或去掉（见 ToolMarkup.cs），这里只管显示：流式输出途中和旧消息里都不让用户看到这些标记。
const TOOL_MARKUP = /<\s*[｜|]\s*DSML\s*[｜|]|<\s*[｜|]\s*tool[▁_ ]calls?[▁_ ]begin\s*[｜|]\s*>|<tool_call>/i

/** 从第一个工具调用标记起截掉。没有标记时原样返回 */
export function stripToolMarkup(text: string): string {
  const m = TOOL_MARKUP.exec(text)
  return m ? text.slice(0, m.index).trimEnd() : text
}

export function renderMarkdown(text: string): string {
  return DOMPurify.sanitize(md.render(text), { ADD_ATTR: ['target', 'data-copy', 'data-lang', 'data-code'] })
}
