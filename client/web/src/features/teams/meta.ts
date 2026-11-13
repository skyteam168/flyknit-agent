import { ChartColumn, ClipboardList, Search, Settings, Truck, UsersRound } from '@lucide/vue'
import type { Component } from 'vue'

/** 团队图标：只认这几个，其余按「团队」显示（和后台的选项一致） */
export const TEAM_ICONS: Record<string, Component> = {
  team: UsersRound,
  truck: Truck,
  chart: ChartColumn,
  search: Search,
  clip: ClipboardList,
  gear: Settings,
}

export const teamIcon = (name: string): Component => TEAM_ICONS[name] ?? UsersRound

/** 团队可以关掉的工具类别（只能收紧） */
export const TEAM_TOOL_KINDS = ['shell', 'write', 'browser', 'mcp', 'developer'] as const
