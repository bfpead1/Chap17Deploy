export type Customer = {
  customerId: number;
  firstName: string;
  lastName: string;
  email: string;
};

export type Product = {
  productId: number;
  productName: string;
  price: number;
};

export type OrderSummary = {
  orderId: number;
  orderTimestamp: string;
  fulfilled: number;
  totalValue: number;
};

export type AdminOrderSummary = {
  orderId: number;
  orderTimestamp: string;
  fulfilled: number;
  totalValue: number;
  customerId: number;
  customerName: string;
};

const API_BASE = import.meta.env.VITE_API_BASE ?? "http://localhost:5000";

async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    ...init,
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      ...(init?.headers ?? {})
    }
  });

  const body = await response.json().catch(() => null);
  if (!response.ok) {
    const msg = body?.error ?? body?.title ?? `Request failed (${response.status})`;
    throw new Error(msg);
  }

  return body as T;
}

export const shopApi = {
  getCustomers: () => api<Customer[]>("/api/customers"),
  selectCustomer: (customerId: number) =>
    api<Customer>("/api/select-customer", {
      method: "POST",
      body: JSON.stringify({ customerId })
    }),
  clearCustomer: async () => {
    try {
      return await api<{ success: boolean }>("/api/customer/clear", { method: "POST" });
    } catch (err) {
      if (err instanceof Error && err.message.includes("(404)")) {
        document.cookie = "customer_id=; Max-Age=0; path=/";
        return { success: true };
      }
      throw err;
    }
  },
  getCurrentCustomer: () => api<Customer>("/api/customer/current"),
  getDashboard: () =>
    api<{
      customer: Customer;
      orderCount: number;
      totalSpend: number;
      recentOrders: OrderSummary[];
    }>("/api/dashboard"),
  getProducts: () => api<Product[]>("/api/products"),
  placeOrder: (items: Array<{ productId: number; quantity: number }>) =>
    api<{ orderId: number; totalValue: number; message: string }>("/api/orders", {
      method: "POST",
      body: JSON.stringify({ items })
    }),
  placeOrderWithFeatures: (payload: {
    items: Array<{ productId: number; quantity: number }>;
    paymentMethod: string;
    deviceType: string;
    ipCountry: string;
    promoUsed: boolean;
    promoCode?: string;
  }) =>
    api<{ orderId: number; totalValue: number; message: string }>("/api/orders", {
      method: "POST",
      body: JSON.stringify(payload)
    }),
  getOrders: () => api<OrderSummary[]>("/api/orders"),
  getAdminOrders: async () => {
    try {
      return await api<AdminOrderSummary[]>("/api/admin/orders");
    } catch (err) {
      if (err instanceof Error && err.message.includes("(404)")) {
        throw new Error("Admin order endpoint is missing (404). Restart the backend so /api/admin/orders is available.");
      }
      throw err;
    }
  },
  getOrderDetails: (orderId: number) =>
    api<{
      order: OrderSummary;
      items: Array<{ productName: string; quantity: number; unitPrice: number; lineTotal: number }>;
    }>(`/api/orders/${orderId}`),
  getPriorityQueue: () =>
    api<{
      warning?: string;
      rows: Array<{
        orderId: number;
        orderTimestamp: string;
        totalValue: number;
        fulfilled: number;
        customerId: number;
        customerName: string;
        fraudProbability: number;
        predictedFraud: number;
        predictionTimestamp: string;
      }>;
    }>("/api/warehouse/priority"),
  runScoring: () =>
    api<{
      success: boolean;
      exitCode?: number;
      ordersScored?: number;
      stdout?: string;
      stderr?: string;
      timestamp: string;
      message?: string;
    }>("/api/scoring/run", { method: "POST" }),
  retrainModel: () =>
    api<{
      success: boolean;
      exitCode?: number;
      ordersScored?: number;
      stdout?: string;
      stderr?: string;
      timestamp: string;
      message?: string;
    }>("/api/scoring/retrain", { method: "POST" }),
  getSchema: () => api<{ tables: Array<{ table: string; columns: Array<{ name: string; type: string }> }> }>("/api/debug/schema")
};
