<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts/core'
import { BarChart, LineChart, PieChart } from 'echarts/charts'
import { DataZoomComponent, GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import type { EChartsCoreOption } from 'echarts/core'
import { isDark } from '@/store/theme'

echarts.use([LineChart, BarChart, PieChart, GridComponent, TooltipComponent, LegendComponent, DataZoomComponent, CanvasRenderer])

// option 由父组件按当前主题算好传进来；切主题时父组件重算，这里整体替换
const props = defineProps<{ option: EChartsCoreOption; height?: string }>()

const el = ref<HTMLDivElement>()
const chart = shallowRef<echarts.ECharts>()
let observer: ResizeObserver | undefined

function render() {
  chart.value?.setOption(props.option, { notMerge: true })
}

onMounted(() => {
  chart.value = echarts.init(el.value!, undefined, { renderer: 'canvas' })
  render()
  observer = new ResizeObserver(() => chart.value?.resize())
  observer.observe(el.value!)
})
watch(() => props.option, render, { deep: true })
watch(isDark, render)
onBeforeUnmount(() => {
  observer?.disconnect()
  chart.value?.dispose()
})
</script>

<template>
  <div ref="el" class="chart" :style="{ height: height ?? '300px' }" />
</template>

<style scoped>
.chart {
  width: 100%;
}
</style>
