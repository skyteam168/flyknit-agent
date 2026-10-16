/**
 * 宽松的 JSON 解析：模型写图表、界面卡片时常见的小毛病先修好再解析，而不是整张卡片报错。
 *
 * 能修的：字符串里没转义的英文引号（"换"鼎盛"再查"）、字符串里直接换行、单引号字符串、
 * 字符串外面的中文逗号冒号和中文引号、// 和 /* *\/ 注释、多余的逗号、输出被截断后缺的引号和括号。
 * 只在 JSON.parse 失败时才修，合法的 JSON 原样解析。
 */

export class LooseJsonError extends Error {}

export function parseLooseJson(text: string): unknown {
  const src = text.replace(/^﻿/, '').trim()
  try {
    return JSON.parse(src)
  } catch (first) {
    const fixed = repairJson(src)
    try {
      return JSON.parse(fixed)
    } catch {
      throw new LooseJsonError((first as Error).message)
    }
  }
}

const closers = new Set([',', ':', '}', ']', '，', '：'])

/** 下一个非空白字符（和它的位置），注释也跳过；没有了返回 '' */
function peek(s: string, from: number): [string, number] {
  for (let i = from; i < s.length; i++) {
    if (/\s/.test(s[i])) continue
    if (s[i] === '/' && s[i + 1] === '/') {
      while (i < s.length && s[i] !== '\n') i++
      continue
    }
    if (s[i] === '/' && s[i + 1] === '*') {
      const end = s.indexOf('*/', i + 2)
      i = end < 0 ? s.length : end + 1
      continue
    }
    return [s[i], i]
  }
  return ['', s.length]
}

/** 从 at 处的引号开始的那个字符串后面跟的是冒号吗（也就是说它是个键） */
function isKeyAt(s: string, at: number): boolean {
  for (let i = at + 1; i < s.length; i++) {
    if (s[i] === '\\') i++
    else if (s[i] === '"') {
      const [next] = peek(s, i + 1)
      return next === ':' || next === '：'
    } else if (s[i] === '\n') return false
  }
  return false
}

/**
 * 字符串里遇到一个英文双引号：它是字符串的结尾，还是内容里没转义的引号？
 * 键后面必须是冒号；值后面是 , } ] 或结尾。对象里的值后面跟逗号时，逗号后面还得真是下一个键。
 */
function closesString(s: string, i: number, isKey: boolean, inObject: boolean): boolean {
  const [next, at] = peek(s, i + 1)
  if (next === '') return true
  if (isKey) return next === ':' || next === '：'
  if (!closers.has(next) || next === ':' || next === '：') return false
  if (next !== ',' && next !== '，') return true
  const [after, afterAt] = peek(s, at + 1)
  if (after === '' || after === '}' || after === ']') return true
  if (inObject) return (after === '"' || after === "'") && isKeyAt(s, afterAt)
  return after === '"' || after === '{' || after === '[' || /[-\d tfn]/.test(after)
}

export function repairJson(s: string): string {
  let out = ''
  const stack: string[] = []
  let inString = false
  let quote = '"'
  let isKey = false
  let keyNext = false // 对象里下一个字符串是键
  const inObject = () => stack[stack.length - 1] === '}'
  for (let i = 0; i < s.length; i++) {
    const c = s[i]
    if (inString) {
      if (c === '\\') {
        out += c + (s[i + 1] ?? '')
        i++
      } else if (c === quote && (quote !== '"' || closesString(s, i, isKey, inObject()))) {
        out += '"'
        inString = false
      } else if (c === '"') {
        out += '\\"' // 内容里的英文引号，或者单引号字符串里的双引号
      } else if (c === '\n') {
        out += '\\n'
      } else if (c === '\r') {
        // 丢掉
      } else if (c === '\t') {
        out += '\\t'
      } else {
        out += c
      }
      continue
    }
    // 字符串外面
    if (c === '"' || c === '“' || c === '”' || c === "'") {
      isKey = inObject() && keyNext
    }
    if (c === '"' || c === '“' || c === '”') {
      inString = true
      quote = c === '"' ? '"' : '”'
      if (c === '“') quote = '”'
      out += '"'
    } else if (c === "'") {
      inString = true
      quote = "'"
      out += '"'
    } else if (c === '/' && s[i + 1] === '/') {
      while (i < s.length && s[i] !== '\n') i++
    } else if (c === '/' && s[i + 1] === '*') {
      const end = s.indexOf('*/', i + 2)
      i = end < 0 ? s.length : end + 1
    } else if (c === ',' || c === '，') {
      keyNext = inObject()
      out += ','
    } else if (c === ':' || c === '：') {
      keyNext = false
      out += ':'
    } else if (c === '{' || c === '[') {
      stack.push(c === '{' ? '}' : ']')
      keyNext = c === '{'
      out += c
    } else if (c === '}' || c === ']') {
      out = out.replace(/,\s*$/, '') // 多余的逗号
      if (stack.length && stack[stack.length - 1] === c) stack.pop()
      out += c
    } else {
      out += c
    }
  }
  // 被截断：补上没写完的字符串和括号
  if (inString) out += '"'
  out = out.replace(/,\s*$/, '').replace(/:\s*$/, ': null')
  while (stack.length) out += stack.pop()
  return out
}
