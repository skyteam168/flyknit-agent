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
