import { Navigate, Route, Routes } from "react-router-dom";
import { useEffect, useState } from "react";
import Nav from "./components/Nav";
import { Customer, shopApi } from "./api";
import PlaceOrderPage from "./pages/PlaceOrderPage";
import OrdersPage from "./pages/OrdersPage";
import ScoringPage from "./pages/ScoringPage";
import SelectCustomerPage from "./pages/SelectCustomerPage";

export default function App() {
  const [customer, setCustomer] = useState<Customer | null>(null);
  const [customerResolved, setCustomerResolved] = useState(false);

  useEffect(() => {
    shopApi
      .getCurrentCustomer()
      .then(setCustomer)
      .catch(() => setCustomer(null))
      .finally(() => setCustomerResolved(true));
  }, []);

  return (
    <div className="container">
      <Nav customerLabel={customer ? `${customer.firstName} ${customer.lastName} (${customer.email})` : "None"} />
      <main>
        <Routes>
          <Route path="/" element={<Navigate to="/select-customer" replace />} />
          <Route
            path="/select-customer"
            element={<SelectCustomerPage onSelected={setCustomer} />}
          />
          <Route
            path="/place-order"
            element={
              <PlaceOrderPage
                customer={customer}
                customerResolved={customerResolved}
                onCustomerSelected={setCustomer}
                onCustomerCleared={() => setCustomer(null)}
              />
            }
          />
          <Route path="/admin/orders" element={<OrdersPage />} />
          <Route path="/scoring" element={<ScoringPage />} />
          <Route path="*" element={<Navigate to="/select-customer" replace />} />
        </Routes>
      </main>
    </div>
  );
}
