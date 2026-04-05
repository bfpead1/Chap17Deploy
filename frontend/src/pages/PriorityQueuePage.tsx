import { useEffect, useState } from "react";
import { shopApi } from "../api";

export default function PriorityQueuePage() {
  const [rows, setRows] = useState<any[]>([]);
  const [warning, setWarning] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    shopApi
      .getPriorityQueue()
      .then((result) => {
        setRows(result.rows ?? []);
        setWarning(result.warning ?? null);
      })
      .catch((err) => setError(err.message));
  }, []);

  return (
    <section>
      <h2>Fraud Review Priority Queue</h2>
      <p>
        This queue highlights unfulfilled orders with the highest predicted fraud risk so administrators can review them before fulfillment.
      </p>
      {warning ? <p>{warning}</p> : null}
      {error ? <p className="error">{error}</p> : null}
      <table>
        <thead>
          <tr>
            <th>Order</th>
            <th>Customer</th>
            <th>Timestamp</th>
            <th>Total</th>
            <th>Fraud Probability</th>
            <th>Predicted Fraud</th>
            <th>Prediction Time</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.orderId}>
              <td>{r.orderId}</td>
              <td>{r.customerName}</td>
              <td>{r.orderTimestamp}</td>
              <td>${Number(r.totalValue).toFixed(2)}</td>
              <td>{(Number(r.fraudProbability) * 100).toFixed(1)}%</td>
              <td>{r.predictedFraud ? "Yes" : "No"}</td>
              <td>{r.predictionTimestamp}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
