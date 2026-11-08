<script setup lang="ts">
/** 列表里的「使用范围」：显示全公司 / 哪些部门，点一下改。 */
import { computed, ref } from 'vue'
import type { ItemScope, ScopeKind } from '@/api/types'
import ScopeDialog from './ScopeDialog.vue'

const props = defineProps<{ kind: ScopeKind; itemId: string; itemName: string; scope?: ItemScope }>()
const emit = defineEmits<{ saved: [ItemScope] }>()
const open = ref(false)

const restricted = computed(() => !!props.scope && (props.scope.departments.length > 0 || props.scope.devices.length > 0))
const text = computed(() => {
  if (!restricted.value) return '全公司'
  const parts: string[] = []
  const depts = props.scope!.departments
  if (depts.length) parts.push(depts.length <= 2 ? depts.join('、') : `${depts.slice(0, 2).join('、')} 等 ${depts.length} 个部门`)
  if (props.scope!.devices.length) parts.push(`${props.scope!.devices.length} 台电脑`)
  return parts.join(' + ')
})
</script>

<template>
  <el-tooltip content="使用范围：哪些部门 / 电脑能用。点击修改" placement="top">
    <el-tag
      class="scope-tag"
      size="small"
      :type="restricted ? 'warning' : 'info'"
      effect="plain"
      @click.stop="open = true"
    >{{ text }}</el-tag>
  </el-tooltip>
  <ScopeDialog v-model="open" :kind="kind" :item-id="itemId" :item-name="itemName" :scope="scope" @saved="(s) => emit('saved', s)" />
</template>

<style scoped>
.scope-tag {
  cursor: pointer;
  max-width: 220px;
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
