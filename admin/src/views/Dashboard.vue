<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { Coin, Connection, DataLine, Monitor, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Dashboard } from '@/api/types'
import EChart from '@/components/EChart.vue'
import StatCard from '@/components/StatCard.vue'
import { isDark } from '@/store/theme'
import { areaFill, baseAxis, palette, tooltip } from '@/utils/chart'
import { change, num, relative, sceneLabel, short } from '@/utils/format'

const days = ref(14)
const data = ref<Dashboard | null>(null)
const loading = ref(false)
let timer: number | undefined

async function load() {
  loading.value = true
  try {
    data.value = await api.dashboard(days.value)
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void load()
  // 在线人数是活的数据，一分钟刷一次
  timer = window.setInterval(load, 60_000)
})
onBeforeUnmount(() => clearInterval(timer))

const dayLabel = (d: string) => d.slice(5).replace('-', '/')

const tokenDelta = computed(() => (data.value ? change(data.value.today.tokens, data.value.yesterday.tokens) : null))
const requestDelta = computed(() => (data.value ? change(data.value.today.requests, data.value.yesterday.requests) : null))
const rangeTotal = computed(() => data.value?.daily.reduce((s, d) => s + d.tokens, 0) ?? 0)

// ---------- 每日消耗：输入 / 输出堆叠面积 + 请求次数折线 ----------
const dailyOption = computed(() => {
  void isDark.value
  const d = data.value
  if (!d) return {}
  const p = palette()
  const axis = baseAxis()
  return {
    color: [p.series[0], p.series[1], p.series[2]],
    tooltip: {
      ...tooltip(),
      trigger: 'axis',
      valueFormatter: (v: number) => num(v),
    },
    legend: { top: 0, right: 0, icon: 'roundRect', itemWidth: 10, itemHeight: 10, textStyle: { color: p.soft } },
    grid: { left: 8, right: 8, top: 36, bottom: 4, containLabel: true },
    xAxis: { type: 'category', boundaryGap: false, data: d.days.map(dayLabel), ...axis, splitLine: { show: false } },
    yAxis: [
      { type: 'value', ...axis, axisLabel: { ...axis.axisLabel, formatter: (v: number) => short(v) } },
      { type: 'value', ...axis, splitLine: { show: false }, axisLabel: { ...axis.axisLabel, formatter: (v: number) => short(v) } },
    ],
    series: [
      {
        name: '输入 token',
        type: 'line',
        stack: 'tokens',
        smooth: true,
        symbol: 'none',
        lineStyle: { width: 2 },
        areaStyle: { color: areaFill(p.series[0], 0.32) },
        data: d.daily.map((x) => x.prompt),
      },
      {
        name: '输出 token',
        type: 'line',
        stack: 'tokens',
        smooth: true,
        symbol: 'none',
        lineStyle: { width: 2 },
        areaStyle: { color: areaFill(p.series[1], 0.32) },
        data: d.daily.map((x) => x.completion),
      },
      {
        name: '请求次数',
        type: 'line',
        yAxisIndex: 1,
        smooth: true,
        symbol: 'circle',
        symbolSize: 5,
        lineStyle: { width: 1.5, type: 'dashed' },
        data: d.daily.map((x) => x.requests),
      },
    ],
  }
})

// ---------- 每人每日：前几名用户的堆叠面积 ----------
const userSeriesOption = computed(() => {
  void isDark.value
  const d = data.value
  if (!d) return {}
  const p = palette()
  const axis = baseAxis()
  return {
    color: p.series,
    tooltip: { ...tooltip(), trigger: 'axis', valueFormatter: (v: number) => num(v) },
    legend: { top: 0, type: 'scroll', textStyle: { color: p.soft }, icon: 'roundRect', itemWidth: 10, itemHeight: 10 },
    grid: { left: 8, right: 8, top: 36, bottom: 4, containLabel: true },
    xAxis: { type: 'category', boundaryGap: false, data: d.days.map(dayLabel), ...axis, splitLine: { show: false } },
    yAxis: { type: 'value', ...axis, axisLabel: { ...axis.axisLabel, formatter: (v: number) => short(v) } },
    series: d.user_series.map((u, i) => ({
      name: u.label,
      type: 'line',
      stack: 'users',
      smooth: true,
      symbol: 'none',
      lineStyle: { width: 1.5 },
      areaStyle: { color: areaFill(p.series[i % p.series.length], 0.35) },
      emphasis: { focus: 'series' },
      data: u.data,
    })),
  }
})

// ---------- 用户排行：横向柱状 ----------
const rankOption = computed(() => {
  void isDark.value
  const d = data.value
  if (!d) return {}
  const p = palette()
  const axis = baseAxis()
  const rows = [...d.users].reverse()
  return {
    tooltip: {
      ...tooltip(),
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      valueFormatter: (v: number) => num(v),
    },
    grid: { left: 8, right: 56, top: 4, bottom: 4, containLabel: true },
    xAxis: { type: 'value', ...axis, axisLabel: { show: false } },
    yAxis: {
      type: 'category',
      data: rows.map((u) => u.user_name || u.machine_name || `#${u.device_id}`),
      ...axis,
      axisLabel: { color: p.soft, fontSize: 12 },
      splitLine: { show: false },
    },
    series: [
      {
        name: `近 ${days.value} 天`,
        type: 'bar',
        barWidth: 12,
        itemStyle: { color: p.series[0], borderRadius: [0, 6, 6, 0] },
        label: { show: true, position: 'right', color: p.soft, fontSize: 11, formatter: (x: { value: number }) => short(x.value) },
        data: rows.map((u) => u.tokens),
      },
    ],
  }
})

// ---------- 今日场景分布：环形图 ----------
const sceneOption = computed(() => {
  void isDark.value
  const d = data.value
  if (!d) return {}
  const p = palette()
  const list = d.scenes_today.length ? d.scenes_today : d.scenes
  return {
    color: p.series,
    tooltip: { ...tooltip(), trigger: 'item', valueFormatter: (v: number) => num(v) },
    legend: { bottom: 0, icon: 'circle', itemWidth: 8, itemHeight: 8, textStyle: { color: p.soft } },
    series: [
      {
        type: 'pie',
        radius: ['52%', '76%'],
        center: ['50%', '44%'],
        itemStyle: { borderColor: p.cloth, borderWidth: 3, borderRadius: 6 },
        label: { show: false },
        data: list.map((s) => ({ name: sceneLabel(s.scene), value: s.tokens })),
      },
    ],
  }
})
const sceneTitle = computed(() => (data.value?.scenes_today.length ? '今日场景分布' : `近 ${days.value} 天场景分布`))

// ---------- 每日模型消耗：按模型堆叠柱状 ----------
const modelSeriesOption = computed(() => {
  void isDark.value
  const d = data.value
  if (!d?.model_series) return {}
  const p = palette()
  const axis = baseAxis()
  return {
    color: p.series,
    tooltip: { ...tooltip(), trigger: 'axis', axisPointer: { type: 'shadow' }, valueFormatter: (v: number) => num(v) },
    legend: { top: 0, type: 'scroll', textStyle: { color: p.soft }, icon: 'roundRect', itemWidth: 10, itemHeight: 10 },
    grid: { left: 8, right: 8, top: 36, bottom: 4, containLabel: true },
    xAxis: { type: 'category', data: d.days.map(dayLabel), ...axis, splitLine: { show: false } },
    yAxis: { type: 'value', ...axis, axisLabel: { ...axis.axisLabel, formatter: (v: number) => short(v) } },
    series: d.model_series.map((m) => ({
      name: m.model,
      type: 'bar',
      stack: 'models',
      barMaxWidth: 28,
      emphasis: { focus: 'series' },
      data: m.data,
    })),
  }
})
const modelTotal = computed(() => data.value?.models?.reduce((s, m) => s + m.tokens, 0) ?? 0)
const modelShare = (tokens: number) => (modelTotal.value ? Math.round((tokens / modelTotal.value) * 1000) / 10 : 0)
const modelScenes = (scenes: { scene: string; tokens: number }[]) =>
  scenes.map((s) => `${sceneLabel(s.scene)} ${short(s.tokens)}`).join(' · ')

// 用途：客户端 Gateway/Purposes.cs、服务端 usage_store.PURPOSES
const PURPOSES: Record<string, { title: string; hint: string }> = {
  turn: { title: '正常对话 / 办事', hint: '员工发起的对话、办事、翻译（含旧版客户端的全部调用）' },
  compact: { title: '压缩上下文', hint: '对话快满时把较早的内容压缩成摘要' },
  session_memory: { title: '后台会话摘要', hint: '提前准备的摘要，新版默认已关闭' },
  reflect: { title: '任务复盘', hint: '办事结束后提炼记忆、经验和技能' },
  memory: { title: '整理记忆', hint: '合并重复、归类、找矛盾' },
  title: { title: '生成标题', hint: '给新对话起标题' },
  enhance: { title: '优化提示词', hint: '员工点「优化」改写输入' },
  sidechat: { title: '顺便问一句', hint: '任务进行中的旁路提问' },
  watchdog: { title: '看门狗', hint: '任务一直在动却没进展时，判断是不是卡住 / 跑偏' },
  router: { title: '调度', hint: '钉钉追问归到原任务、问进度、合并重复事件、拆分建议' },
  other: { title: '其他', hint: '客户端报了认不出的用途' },
}
const purposeLabel = (p: string) => PURPOSES[p] ?? { title: p, hint: '' }
const purposeTotal = computed(() => (data.value?.purposes ?? []).reduce((a, p) => a + p.tokens, 0))
const purposeShare = (tokens: number) => (purposeTotal.value ? Math.round((tokens / purposeTotal.value) * 1000) / 10 : 0)

const userShare = (tokens: number) => (rangeTotal.value ? Math.round((tokens / rangeTotal.value) * 1000) / 10 : 0)
</script>

<template>
  <div class="page" v-loading="loading && !data">
    <div class="page-head">
      <div>
        <h1>首页统计</h1>
        <p>
          数据截至 {{ data ? relative(data.generated_at) : '—' }}，每分钟自动刷新。日期按工厂所在时区（UTC+8）统计。
        </p>
      </div>
      <div class="toolbar">
        <el-radio-group v-model="days" @change="load">
          <el-radio-button :value="7">近 7 天</el-radio-button>
          <el-radio-button :value="14">近 14 天</el-radio-button>
          <el-radio-button :value="30">近 30 天</el-radio-button>
        </el-radio-group>
        <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      </div>
    </div>

    <template v-if="data">
      <div class="stats">
        <StatCard
          title="当前在线设备"
          :value="num(data.online_devices)"
          :unit="`/ ${num(data.total_devices)} 台`"
          :icon="Monitor"
          tone="indigo"
          :foot="`最近 ${data.online_minutes} 分钟内有连接`"
        />
        <StatCard
          title="今日消耗 Token"
          :value="short(data.today.tokens)"
          :icon="Coin"
          tone="thread"
          :delta="tokenDelta"
          :foot="`输入 ${short(data.today.prompt)} · 输出 ${short(data.today.completion)}`"
        />
        <StatCard
          title="今日请求次数"
          :value="num(data.today.requests)"
          unit="次"
          :icon="Connection"
          tone="amber"
          :delta="requestDelta"
        />
        <StatCard
          title="今日活跃设备"
          :value="num(data.active_today)"
          :unit="`/ ${num(data.total_devices)} 台`"
          :icon="DataLine"
          tone="violet"
          :foot="(data.daily_limit ? `每台每天上限 ${short(data.daily_limit)}` : '未设置每日上限') + (data.custom_limits ? `，${data.custom_limits} 台单独设置` : '')"
        />
      </div>

      <div class="grid">
        <section class="panel span-2">
          <div class="panel-title">
            <h3>每日 Token 消耗 <small>近 {{ days }} 天合计 {{ short(rangeTotal) }}</small></h3>
          </div>
          <EChart :option="dailyOption" height="300px" />
        </section>
        <section class="panel">
          <div class="panel-title"><h3>{{ sceneTitle }}</h3></div>
          <EChart v-if="(data.scenes_today.length || data.scenes.length)" :option="sceneOption" height="300px" />
          <el-empty v-else description="还没有用量数据" :image-size="80" />
        </section>

        <section class="panel span-2">
          <div class="panel-title">
            <h3>每日模型消耗 <small>近 {{ days }} 天，按实际回答的模型（备用模型顶上时算在备用模型）</small></h3>
          </div>
          <EChart v-if="data.model_series?.length" :option="modelSeriesOption" height="300px" />
          <el-empty v-else description="还没有按模型的用量数据" :image-size="80" />
        </section>
        <section class="panel">
          <div class="panel-title"><h3>模型用量排行 <small>近 {{ days }} 天</small></h3></div>
          <el-empty v-if="!data.models?.length" description="还没有按模型的用量数据" :image-size="80" />
          <ul v-else class="models">
            <li v-for="m in data.models" :key="m.model">
              <div class="model-head">
                <strong :title="m.model">{{ m.model }}</strong>
                <span>{{ short(m.tokens) }}</span>
              </div>
              <el-progress :percentage="modelShare(m.tokens)" :stroke-width="6" />
              <small>今日 {{ short(m.today_tokens) }} · 请求 {{ num(m.requests) }} 次 · 输入 {{ short(m.prompt) }} / 输出 {{ short(m.completion) }}<template v-if="m.cached && m.prompt"> · 缓存命中 {{ Math.round((m.cached / m.prompt) * 100) }}%</template></small>
              <small v-if="m.scenes.length" class="model-scenes">{{ modelScenes(m.scenes) }}</small>
            </li>
          </ul>
        </section>

        <section class="panel span-2">
          <div class="panel-title">
            <h3>每人每日消耗 <small>消耗最多的 {{ data.user_series.length }} 位</small></h3>
          </div>
          <EChart v-if="data.user_series.length" :option="userSeriesOption" height="300px" />
          <el-empty v-else description="还没有用量数据" :image-size="80" />
        </section>
        <section class="panel">
          <div class="panel-title"><h3>用户消耗排行 <small>近 {{ days }} 天</small></h3></div>
          <EChart v-if="data.users.length" :option="rankOption" height="300px" />
          <el-empty v-else description="还没有用量数据" :image-size="80" />
        </section>

        <section class="panel span-2">
          <div class="panel-title">
            <h3>用户消耗明细 <small>近 {{ days }} 天</small></h3>
            <router-link to="/quota" class="link">查看全部 ›</router-link>
          </div>
          <el-table :data="data.users" size="default" empty-text="还没有用量数据">
            <el-table-column type="index" label="#" width="48" />
            <el-table-column label="用户 / 电脑" min-width="180">
              <template #default="{ row }">
                <div class="who">
                  <strong>{{ row.user_name || '—' }}</strong>
                  <small>{{ row.machine_name }}</small>
                </div>
              </template>
            </el-table-column>
            <el-table-column label="今日" width="110" align="right">
              <template #default="{ row }">{{ short(row.today_tokens) }}</template>
            </el-table-column>
            <el-table-column :label="`近 ${days} 天`" width="120" align="right">
              <template #default="{ row }">{{ short(row.tokens) }}</template>
            </el-table-column>
            <el-table-column label="请求" width="90" align="right">
              <template #default="{ row }">{{ num(row.requests) }}</template>
            </el-table-column>
            <el-table-column label="占比" min-width="150">
              <template #default="{ row }">
                <el-progress :percentage="userShare(row.tokens)" :stroke-width="6" :show-text="true" />
              </template>
            </el-table-column>
          </el-table>
        </section>
        <section class="panel">
          <div class="panel-title">
            <h3>在线设备 <small>{{ data.online_devices }} 台</small></h3>
            <router-link to="/devices" class="link">设备列表 ›</router-link>
          </div>
          <el-empty v-if="!data.online.length" description="当前没有在线设备" :image-size="80" />
          <ul v-else class="online">
            <li v-for="d in data.online" :key="d.device_id">
              <span class="dot" />
              <span class="who">
                <strong>{{ d.user_name || d.machine_name }}</strong>
                <small>{{ d.machine_name }}<template v-if="d.client_version"> · v{{ d.client_version }}</template></small>
              </span>
              <time>{{ relative(d.last_seen) }}</time>
            </li>
          </ul>
        </section>

        <section class="panel span-3">
          <div class="panel-title">
            <h3>按用途 <small>近 {{ days }} 天。正常对话以外的都是客户端在后台发起的调用，同样计入员工的额度</small></h3>
          </div>
          <el-table :data="data.purposes ?? []" size="default" empty-text="还没有按用途的数据（新版客户端和服务端才会记）">
            <el-table-column label="用途" min-width="220">
              <template #default="{ row }">
                <div class="who">
                  <strong>{{ purposeLabel(row.purpose).title }}</strong>
                  <small>{{ purposeLabel(row.purpose).hint }}</small>
                </div>
              </template>
            </el-table-column>
            <el-table-column label="占比" min-width="160">
              <template #default="{ row }"><el-progress :percentage="purposeShare(row.tokens)" :stroke-width="6" /></template>
            </el-table-column>
            <el-table-column :label="`近 ${days} 天`" width="110" align="right">
              <template #default="{ row }">{{ short(row.tokens) }}</template>
            </el-table-column>
            <el-table-column label="今日" width="100" align="right">
              <template #default="{ row }">{{ short(row.today_tokens) }}</template>
            </el-table-column>
            <el-table-column label="请求" width="90" align="right">
              <template #default="{ row }">{{ num(row.requests) }}</template>
            </el-table-column>
            <el-table-column label="输入 / 输出" width="140" align="right">
              <template #default="{ row }">{{ short(row.prompt) }} / {{ short(row.completion) }}</template>
            </el-table-column>
            <el-table-column label="缓存命中" width="100" align="right">
              <template #default="{ row }">{{ row.prompt ? Math.round((row.cached / row.prompt) * 100) + '%' : '—' }}</template>
            </el-table-column>
          </el-table>
        </section>
      </div>
    </template>
  </div>
</template>

<style scoped>
.stats {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 16px;
}
.grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 16px;
}
.span-3 {
  grid-column: 1 / -1;
}
.span-2 {
  grid-column: span 2;
}
@media (max-width: 1200px) {
  .stats {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .grid {
    grid-template-columns: 1fr;
  }
  .span-2 {
    grid-column: auto;
  }
}
.link {
  color: var(--indigo);
  font-size: 12.5px;
  text-decoration: none;
}
.who {
  display: flex;
  flex-direction: column;
  min-width: 0;
  line-height: 1.35;
}
.who strong {
  font-weight: 500;
}
.who small {
  color: var(--ink-faint);
  font-size: 12px;
}
.online {
  display: flex;
  flex-direction: column;
  max-height: 340px;
  margin: 0;
  padding: 0;
  overflow-y: auto;
  list-style: none;
}
.online li {
  display: flex;
  gap: 10px;
  align-items: center;
  padding: 9px 2px;
  border-top: 1px solid var(--line);
}
.online li:first-child {
  border-top: 0;
}
.online .who {
  flex: 1;
}
.online time {
  color: var(--ink-faint);
  font-size: 12px;
  white-space: nowrap;
}
.models {
  display: flex;
  flex-direction: column;
  max-height: 340px;
  margin: 0;
  padding: 0;
  overflow-y: auto;
  list-style: none;
}
.models li {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 10px 2px;
  border-top: 1px solid var(--line);
}
.models li:first-child {
  border-top: 0;
}
.model-head {
  display: flex;
  gap: 8px;
  align-items: baseline;
  justify-content: space-between;
}
.model-head strong {
  min-width: 0;
  overflow: hidden;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.model-head span {
  flex: none;
  font-variant-numeric: tabular-nums;
}
.models small {
  color: var(--ink-faint);
  font-size: 12px;
}
.dot {
  flex: none;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--thread);
  box-shadow: 0 0 0 3px var(--thread-wash);
}
</style>
