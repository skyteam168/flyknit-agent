/** 全局搜索（宿主 Core/Code/CodeSearch）：一处匹配。行、列从 1 开始；text 是这一行（太长时截了一段，textOffset 是这段在原行里的起点） */
export interface CodeSearchHit {
  line: number
  column: number
  length: number
  text: string
  textOffset: number
}

export interface CodeSearchFile {
  path: string
  hits: CodeSearchHit[]
}

export interface CodeSearchResult {
  files: CodeSearchFile[]
  total: number
  truncated: boolean
  error: string
}

/** 源代码管理（宿主 Core/Git/SourceControl）：一个改动。path 相对仓库根；workspacePath 相对工作区（不在工作区里为 null，打不开） */
export interface ScmChange {
  path: string
  workspacePath: string | null
  /** M 修改、A 新增（已暂存）、D 删除、R 改名、U 没跟踪的新文件、C 冲突 */
  status: 'M' | 'A' | 'D' | 'R' | 'U' | 'C' | string
  staged: boolean
  oldPath: string
}

export interface ScmStatus {
  isRepo: boolean
  root: string
  branch: string
  detached: boolean
  ahead: number
  behind: number
  hasUpstream: boolean
  changes: ScmChange[]
  error: string
}

export interface ScmResult {
  ok: boolean
  message: string
}

/** 语言服务（宿主 Core/Lsp） */
export interface LspServerInfo {
  id: string
  name: string
  extensions: string[]
  available: boolean
  disabled: boolean
  exe: string
  installHint: string
  running: boolean
}

export interface LspStatus {
  enabled: boolean
  trusted: boolean
  servers: LspServerInfo[]
}

/** error：untrusted / missing / disabled / unsupported / badPath / 启动失败的原因 */
export interface LspReply {
  ok: boolean
  error?: string
}

export interface LspRange {
  start: { line: number; character: number }
  end: { line: number; character: number }
}

export interface LspDiagnostic {
  range: LspRange
  severity?: number
  message: string
  source?: string
  code?: string | number
}
