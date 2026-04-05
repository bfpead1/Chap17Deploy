import { useEffect, useState } from "react";
import { shopApi } from "../api";

export default function ScoringPage() {
  const [status, setStatus] = useState<any>(null);
  const [rows, setRows] = useState<any[]>([]);
  const [warning, setWarning] = useState<string | null>(null);
  const [queueError, setQueueError] = useState<string | null>(null);
  const [running, setRunning] = useState<null | "score" | "retrain">(null);

  async function refreshQueue() {
    try {
      const result = await shopApi.getPriorityQueue();
      setRows(result.rows ?? []);
      setWarning(result.warning ?? null);
      setQueueError(null);
    } catch (err) {
      setQueueError(err instanceof Error ? err.message : "Failed to load priority queue.");
    }
  }

  useEffect(() => {
    void refreshQueue();
  }, []);

  async function runScoring() {
    setRunning("score");
    try {
      const result = await shopApi.runScoring();
      setStatus(result);
      await refreshQueue();
    } catch (err) {
      setStatus({ success: false, message: err instanceof Error ? err.message : "Unknown error" });
    } finally {
      setRunning(null);
    }
  }

  async function retrainModel() {
    setRunning("retrain");
    try {
      const result = await shopApi.retrainModel();
      setStatus(result);
      await refreshQueue();
    } catch (err) {
      setStatus({ success: false, message: err instanceof Error ? err.message : "Unknown error" });
    } finally {
      setRunning(null);
    }
  }

  return (
    <section>
      <h2>Run Scoring</h2>
      <div className="actions">
        <button disabled={running !== null} onClick={runScoring}>
          {running === "score" ? "Running..." : "Run Scoring"}
        </button>
        <button disabled={running !== null} onClick={retrainModel}>
          {running === "retrain" ? "Retraining..." : "Retrain Model"}
        </button>
      </div>

      {status ? (
        <div className="panel">
          <p>Status: {status.success ? "Success" : "Failed"}</p>
          <p>Orders scored: {status.ordersScored ?? "N/A"}</p>
          <p>Timestamp: {status.timestamp ?? "N/A"}</p>
          {status.message ? <p>{status.message}</p> : null}
          {status.stderr ? <pre>{status.stderr}</pre> : null}
        </div>
      ) : null}

      <h3>Priority Queue</h3>
      <p>Queue refreshes when fraud scoring completes.</p>
      {warning ? <p>{warning}</p> : null}
      {queueError ? <p className="error">{queueError}</p> : null}
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
