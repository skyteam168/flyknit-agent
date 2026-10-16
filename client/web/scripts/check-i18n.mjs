// 构建前检查所有界面文字能不能被 vue-i18n 编译。
// 文字里直接写 @ | { } 这类符号会被当成语法，生产包里一渲染到就整页报 SyntaxError（开发模式下不一定暴露）。
// 要原样显示这些符号，写成 {'@'} 这样的字面量。
// esbuild 随 vite、@intlify/message-compiler 随 vue-i18n 一起装好，不用另加依赖
import { build } from 'esbuild'
import { baseCompile } from '@intlify/message-compiler'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL, fileURLToPath } from 'node:url'

const dir = mkdtempSync(join(tmpdir(), 'i18n-check-'))
const out = join(dir, 'messages.mjs')
try {
  await build({
    entryPoints: [fileURLToPath(new URL('../src/i18n/messages.ts', import.meta.url))],
    bundle: true,
    platform: 'node',
    format: 'esm',
    outfile: out,
    logLevel: 'warning',
  })
  const { messages } = await import(pathToFileURL(out).href)
  const bad = []
  let total = 0
  const walk = (obj, path) => {
    for (const [k, v] of Object.entries(obj)) {
      const p = path ? `${path}.${k}` : k
      if (typeof v === 'string') {
        total++
        baseCompile(v, { onError: (e) => bad.push(`${p}: ${e.message}（${JSON.stringify(v).slice(0, 60)}）`) })
      } else if (v && typeof v === 'object') walk(v, p)
    }
  }
  walk(messages, '')
  if (bad.length) {
    console.error(`界面文字有 ${bad.length} 条编译不过（@ | { } 要写成 {'@'} 这样的字面量）：\n` + bad.join('\n'))
    process.exit(1)
  }
  console.log(`界面文字检查通过：${total} 条`)
} finally {
  rmSync(dir, { recursive: true, force: true })
}
