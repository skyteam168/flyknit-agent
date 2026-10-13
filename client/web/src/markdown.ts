import MarkdownIt from 'markdown-it'
import DOMPurify from 'dompurify'
import { i18n } from './i18n'
import { encodeBlockCode, hasBlockRenderer } from './render/blocks'
import { highlightCode } from './highlight'

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
    const body = highlightCode(code, lang)
    const label = md.utils.escapeHtml(lang || 'text')
    return `<pre class="code"><div class="code-head"><span>${label}</span><button class="code-copy" type="button" data-copy>${md.utils.escapeHtml(i18n.global.t('message.copy'))}</button></div><code>${body}</code></pre>`
  },
})

// 流式输出时，最后一个还没写完的图表 / 流程图代码块（没有结尾的 ```）：先放一个「正在生成」的占位，
// 写完了再画。半截的代码画不出来，每来一个字报一次错、换一次样子，看起来就是一直在闪
const defaultFence = md.renderer.rules.fence!
md.renderer.rules.fence = (tokens, idx, options, env, self) => {
  const token = tokens[idx]
  const lang = token.info.trim().split(/\s+/)[0] ?? ''
  if (env?.streaming && lang && hasBlockRenderer(lang) && !fenceClosed(token.map, token.markup, env.lines)) {
    return `<pre class="rich-block pending" data-lang="${md.utils.escapeHtml(lang.toLowerCase())}"><span>${md.utils.escapeHtml(i18n.global.t('message.chartPending'))}</span></pre>`
  }
  return defaultFence(tokens, idx, options, env, self)
}

function fenceClosed(map: [number, number] | null, markup: string, lines: string[] | undefined): boolean {
  if (!map || !lines) return true
  const last = map[1] - 1
  return last > map[0] && (lines[last] ?? '').trim().startsWith(markup)
}

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

/** streaming：正在输出的回答，没写完的图表块先显示占位 */
export function renderMarkdown(text: string, opts?: { streaming?: boolean }): string {
  const env = opts?.streaming ? { streaming: true, lines: text.split('\n') } : {}
  return DOMPurify.sanitize(md.render(text, env), { ADD_ATTR: ['target', 'data-copy', 'data-lang', 'data-code'] })
}
