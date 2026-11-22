/** 一个项目（宿主 Core/Projects 的 Project）：工作区文件夹 + 名字 + 目标 + 说明 */
export interface ProjectInfo {
  id: string
  name: string
  path: string
  goal: string
  notes: string
  createdAt: string
  openedAt: string
  /** 文件夹还在不在（被删了、换了盘） */
  exists: boolean
}
