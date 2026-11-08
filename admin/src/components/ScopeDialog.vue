<script setup lang="ts">
/**
 * 技能 / 专家 / 连接器的使用范围：选部门和单台电脑（两者取并集）。都不选 = 全公司。
 * 范围在服务端执行：范围外的电脑看不到、下载不到、派遣不到，之前装过的技能会被删掉、连接器会断开。
 */
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '@/api'
import type { Department, Device, ItemScope, ScopeKind } from '@/api/types'

const props = defineProps<{
  modelValue: boolean
  kind: ScopeKind
  itemId: string
  itemName: string
  scope?: ItemScope
}>()
const emit = defineEmits<{ 'update:modelValue': [boolean]; saved: [ItemScope] }>()

const KIND_LABEL: Record<ScopeKind, string> = { skill: '技能', expert: '专家', mcp: '连接器' }

const departments = ref<Department[]>([])
const devices = ref<Device[]>([])
const pickedDepartments = ref<string[]>([])
const pickedDevices = ref<number[]>([])
const saving = ref(false)

watch(
  () => props.modelValue,
  async (open) => {
    if (!open) return
    pickedDepartments.value = [...(props.scope?.departments ?? [])]
    pickedDevices.value = [...(props.scope?.devices ?? [])]
    const [d, v] = await Promise.all([api.departments(), api.devices()])
    departments.value = d
    devices.value = v
  },
)

const everyone = computed(() => !pickedDepartments.value.length && !pickedDevices.value.length)

function deviceLabel(d: Device) {
  const who = d.owner || d.user_name
  return `${d.machine_name}${who ? ' · ' + who : ''}${d.department ? ' · ' + d.department : ''}`
}

async function save() {
  saving.value = true
  try {
    const saved = await api.setScope(props.kind, props.itemId, {
      departments: pickedDepartments.value,
      devices: pickedDevices.value,
    })
    ElMessage.success(saved.departments.length || saved.devices.length ? '已保存，范围外的电脑几秒内收回' : '已保存：全公司可用')
    emit('saved', saved)
    emit('update:modelValue', false)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="`使用范围 · ${KIND_LABEL[kind]}「${itemName}」`"
    width="560px"
    @update:model-value="(v: boolean) => emit('update:modelValue', v)"
  >
    <el-form label-position="top">
      <el-form-item label="哪些部门可以用">
        <el-select v-model="pickedDepartments" multiple filterable placeholder="不选 = 不按部门限制" style="width: 100%">
          <el-option v-for="d in departments" :key="d.id" :label="`${d.name}（${d.devices} 台）`" :value="d.name" />
        </el-select>
        <small class="tip">部门在「部门」页维护；电脑属于哪个部门在「设备列表」里改。</small>
      </el-form-item>
      <el-form-item label="另外再给这些电脑">
        <el-select v-model="pickedDevices" multiple filterable placeholder="可选：单独放开给某几台电脑" style="width: 100%">
          <el-option v-for="d in devices" :key="d.id" :label="deviceLabel(d)" :value="d.id" />
        </el-select>
      </el-form-item>
    </el-form>
    <el-alert
      :type="everyone ? 'info' : 'warning'"
      :closable="false"
      show-icon
      :title="everyone ? '没有限制：全公司的电脑都能用' : '只有选中的部门和电脑能用'"
      :description="
        everyone
          ? ''
          : '其他电脑在员工端看不到这一项，服务端也拒绝它们下载、使用或派遣；之前装过的技能会被删掉，连上的连接器会断开。'
      "
    />
    <template #footer>
      <el-button @click="emit('update:modelValue', false)">取消</el-button>
      <el-button type="primary" :loading="saving" @click="save">保存</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.tip {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>
