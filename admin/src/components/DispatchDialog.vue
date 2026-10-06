<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '@/api'
import type { AgentTaskKind, SoftwarePackage } from '@/api/types'
import { CLEAN_TARGETS, REPAIR_ACTIONS, TASK_KINDS } from '@/utils/tasks'
import { bytes } from '@/utils/format'

const props = defineProps<{ modelValue: boolean; agentIds: number[]; agentNames: string[] }>()
const emit = defineEmits<{ 'update:modelValue': [boolean]; dispatched: [] }>()

const visible = computed({
  get: () => props.modelValue,
  set: (v) => emit('update:modelValue', v),
})

const kind = ref<AgentTaskKind>('collect_info')
const submitting = ref(false)

// 各任务的参数，默认值与服务端 CATALOG 保持一致
const params = reactive({
  cleanTargets: ['windows_temp', 'user_temp', 'recycle_bin', 'browser_cache', 'thumbnails'] as string[],
  optimize: { clean: true, flush_dns: true, optimize_disks: true },
  repairActions: ['dism', 'sfc'] as string[],
  packageId: null as number | null,
  restartMinutes: 5,
  restartMessage: 'IT 将重启这台电脑以完成维护，请保存好正在编辑的文件。',
})

const packages = ref<SoftwarePackage[]>([])
const packagesLoaded = ref(false)

watch(
  () => props.modelValue,
  async (open) => {
    if (open) {
      kind.value = 'collect_info'
      if (!packagesLoaded.value) {
        try {
          packages.value = await api.packages()
          packagesLoaded.value = true
        } catch {
          /* 安装包列表拉取失败不挡其它任务 */
        }
      }
    }
  },
)

const currentDesc = computed(() => TASK_KINDS.find((t) => t.kind === kind.value)?.desc ?? '')

function buildParams(): Record<string, unknown> {
  switch (kind.value) {
    case 'clean':
      return { targets: params.cleanTargets }
    case 'optimize':
      return { ...params.optimize }
    case 'repair':
      return { actions: params.repairActions }
    case 'install':
      return { package_id: params.packageId }
    case 'restart':
      return { delay_minutes: params.restartMinutes, message: params.restartMessage }
    default:
      return {}
  }
}

const canSubmit = computed(() => {
  if (!props.agentIds.length) return false
  if (kind.value === 'clean') return params.cleanTargets.length > 0
  if (kind.value === 'repair') return params.repairActions.length > 0
  if (kind.value === 'install') return params.packageId != null
  return true
})

async function submit() {
  submitting.value = true
  try {
    const job = await api.createJob({ kind: kind.value, params: buildParams(), agent_ids: props.agentIds })
    ElMessage.success(`已下发「${job.title}」到 ${job.total} 台电脑`)
    visible.value = false
    emit('dispatched')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog v-model="visible" title="下发运维任务" width="560px" :close-on-click-modal="false">
    <el-alert
      :title="`将下发到 ${agentIds.length} 台电脑：${agentNames.slice(0, 6).join('、')}${agentNames.length > 6 ? ` 等 ${agentNames.length} 台` : ''}`"
      type="info"
      :closable="false"
      show-icon
      style="margin-bottom: 16px"
    />
    <el-form label-position="top">
      <el-form-item label="任务类型">
        <el-select v-model="kind" style="width: 100%">
          <el-option v-for="t in TASK_KINDS" :key="t.kind" :label="t.label" :value="t.kind" />
        </el-select>
        <p class="desc">{{ currentDesc }}</p>
      </el-form-item>

      <el-form-item v-if="kind === 'clean'" label="清理项目">
        <el-checkbox-group v-model="params.cleanTargets">
          <el-checkbox v-for="t in CLEAN_TARGETS" :key="t.value" :value="t.value">{{ t.label }}</el-checkbox>
        </el-checkbox-group>
      </el-form-item>

      <el-form-item v-else-if="kind === 'optimize'" label="提速项目">
        <div class="switches">
          <el-checkbox v-model="params.optimize.clean">清理常见缓存</el-checkbox>
          <el-checkbox v-model="params.optimize.flush_dns">刷新 DNS 缓存</el-checkbox>
          <el-checkbox v-model="params.optimize.optimize_disks">优化磁盘（TRIM / 碎片整理）</el-checkbox>
        </div>
      </el-form-item>

      <el-form-item v-else-if="kind === 'repair'" label="修复项目">
        <el-checkbox-group v-model="params.repairActions">
          <el-checkbox v-for="a in REPAIR_ACTIONS" :key="a.value" :value="a.value">{{ a.label }}</el-checkbox>
        </el-checkbox-group>
      </el-form-item>

      <template v-else-if="kind === 'install'">
        <el-form-item label="安装包">
          <el-select v-model="params.packageId" placeholder="选择要安装的软件" style="width: 100%" :empty-values="[null]">
            <el-option
              v-for="p in packages"
              :key="p.id"
              :label="`${p.name} ${p.version}`"
              :value="p.id"
            >
              <span>{{ p.name }} {{ p.version }}</span>
              <span class="opt-meta">{{ p.kind.toUpperCase() }} · {{ bytes(p.size) }}</span>
            </el-option>
          </el-select>
        </el-form-item>
        <el-alert
          v-if="packagesLoaded && !packages.length"
          type="warning"
          :closable="false"
          title="还没有安装包，请先到「任务中心 - 安装包」上传。"
        />
      </template>

      <template v-else-if="kind === 'restart'">
        <el-form-item label="倒计时（分钟）">
          <el-input-number v-model="params.restartMinutes" :min="1" :max="60" />
        </el-form-item>
        <el-form-item label="提示员工的话">
          <el-input v-model="params.restartMessage" type="textarea" :rows="2" maxlength="120" show-word-limit />
        </el-form-item>
      </template>

      <el-alert
        v-else
        type="info"
        :closable="false"
        title="采集任务无需参数，员工无感知，结果会写入该电脑的台账。"
      />
    </el-form>

    <template #footer>
      <el-button @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">下发</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.desc {
  margin: 6px 0 0;
  color: var(--ink-faint);
  font-size: 12.5px;
  line-height: 1.5;
}
.switches {
  display: flex;
  flex-direction: column;
}
.opt-meta {
  float: right;
  color: var(--ink-faint);
  font-size: 12px;
}
</style>
