<script setup>
import { onMounted, ref } from 'vue'
import { listOrders } from '@/api/order'
import { listCustomerDeliveryTracking } from '@/api/fulfilment'
import { orderStatusText } from '@/utils/order'
import { formatYuan } from '@/utils/money'

const orders = ref([])
const loading = ref(false)

function formatDate(value) {
  if (!value) return '—'
  return new Intl.DateTimeFormat('en-GB', { dateStyle: 'medium', timeStyle: 'short' })
    .format(new Date(value))
}

async function loadOrders() {
  loading.value = true
  try {
    const [history, tracking] = await Promise.all([
      listOrders(),
      listCustomerDeliveryTracking().catch(() => []),
    ])
    const trackingByOrder = new Map(tracking.map(item => [item.orderId, item]))
    orders.value = history.map((order) => {
      const courier = trackingByOrder.get(order.id)
      return {
        ...order,
        // Keep the legacy view contract while accepting the microservice names.
        price: order.price ?? order.total,
        fulfillmentStatus: order.fulfillmentStatus ?? courier?.kitchenStatus,
        deliveryStatus: order.deliveryStatus ?? courier?.deliveryStatus,
        riderName: order.riderName ?? courier?.riderName,
        riderPhone: order.riderPhone ?? courier?.riderPhone,
      }
    })
  } finally {
    loading.value = false
  }
}

onMounted(loadOrders)
</script>

<template>
  <section class="order-page">
    <div class="page-heading">
      <div>
        <h1>Your orders</h1>
        <p>Track each order from payment through restaurant preparation and delivery.</p>
      </div>
      <el-button :loading="loading" @click="loadOrders">Refresh</el-button>
    </div>

    <el-card shadow="never">
      <template #header>Order history</template>
      <el-table v-loading="loading" :data="orders" stripe>
        <el-table-column prop="id" label="Order ID" width="100" />
        <el-table-column label="Placed" min-width="170">
          <template #default="scope">{{ formatDate(scope.row.createdAtUtc) }}</template>
        </el-table-column>
        <el-table-column label="Amount" width="130">
          <template #default="scope">{{ formatYuan(scope.row.price ?? scope.row.total) }}</template>
        </el-table-column>
        <el-table-column label="Status" min-width="180">
          <template #default="scope">{{ orderStatusText(scope.row.status) }}</template>
        </el-table-column>
        <el-table-column label="Items" min-width="250">
          <template #default="scope">
            <div v-for="line in scope.row.lines" :key="`${scope.row.id}-${line.dishId}`">
              {{ line.quantity }} × {{ line.dishName }}
            </div>
          </template>
        </el-table-column>
      </el-table>
      <el-empty v-if="!loading && orders.length === 0" description="No orders yet." />
    </el-card>
  </section>
</template>

<style scoped>
.order-page { max-width: 1000px; margin: 0 auto; }
.page-heading { display: flex; justify-content: space-between; align-items: center; margin-bottom: 18px; }
.page-heading h1 { margin: 0; }
.page-heading p { color: #6b7280; margin: 6px 0 0; }
</style>
