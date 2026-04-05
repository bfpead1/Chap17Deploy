from __future__ import annotations

import os
import sqlite3
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal
from urllib.parse import parse_qsl, urlparse

import joblib
import numpy as np
import pandas as pd
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import (
    average_precision_score,
    classification_report,
    f1_score,
    precision_recall_curve,
    precision_score,
    recall_score,
    roc_auc_score,
)
from sklearn.model_selection import GridSearchCV, train_test_split
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

try:
    import psycopg
    from psycopg.rows import dict_row
except ModuleNotFoundError:  # pragma: no cover
    psycopg = None
    dict_row = None


REPO_ROOT = Path(__file__).resolve().parent.parent
SQLITE_PATH = REPO_ROOT / "shop.db"
MODEL_PATH = REPO_ROOT / "fraud_model_v1.0.sav"
FRAUD_THRESHOLD_FLOOR = 0.70

DbProvider = Literal["postgres", "sqlite"]


@dataclass
class FraudModelBundle:
    pipeline: Any
    feature_columns: list[str]
    threshold: float
    model_name: str
    trained_at_utc: str
    source_provider: str

    def transform_raw(self, raw_df: pd.DataFrame) -> pd.DataFrame:
        prepared = run_etl(raw_df)
        features = prepared.drop(columns=["is_fraud"], errors="ignore")

        for column in self.feature_columns:
            if column not in features.columns:
                features[column] = 0

        extra_columns = [column for column in features.columns if column not in self.feature_columns]
        if extra_columns:
            features = features.drop(columns=extra_columns)

        features = features[self.feature_columns].apply(pd.to_numeric, errors="coerce").fillna(0)
        return features

    def predict_raw(self, raw_df: pd.DataFrame) -> pd.DataFrame:
        feature_frame = self.transform_raw(raw_df)
        probabilities = self.pipeline.predict_proba(feature_frame)[:, 1]
        predictions = (probabilities >= self.threshold).astype(int)

        return pd.DataFrame(
            {
                "order_id": raw_df["order_id"].astype(int).to_numpy(),
                "fraud_probability": probabilities.astype(float),
                "predicted_fraud": predictions.astype(int),
            }
        )


def get_connection_and_provider() -> tuple[Any, DbProvider]:
    connection_string = (
        os.environ.get("SUPABASE_DB_URL")
        or os.environ.get("POSTGRES_CONNECTION_STRING")
        or os.environ.get("ConnectionStrings__Supabase")
    )
    if connection_string:
        if psycopg is None:
            raise ModuleNotFoundError(
                "psycopg is required for Supabase connections. Install it with `python -m pip install psycopg[binary]`."
            )
        return psycopg.connect(normalize_postgres_conninfo(connection_string)), "postgres"

    if not SQLITE_PATH.exists():
        raise FileNotFoundError(f"SQLite database not found at {SQLITE_PATH}")
    return sqlite3.connect(SQLITE_PATH), "sqlite"


def normalize_postgres_conninfo(raw: str) -> str:
    raw = raw.strip()
    if raw.startswith("postgresql://") or raw.startswith("postgres://"):
        parsed = urlparse(raw)
        mapping = {
            "host": parsed.hostname or "",
            "port": str(parsed.port or 5432),
            "dbname": parsed.path.lstrip("/") or "postgres",
            "user": parsed.username or "",
            "password": parsed.password or "",
        }
        for key, value in parse_qsl(parsed.query, keep_blank_values=True):
            key_lower = key.lower()
            if key_lower == "sslmode":
                mapping["sslmode"] = value
        return " ".join(f"{key}={quote_conn_value(value)}" for key, value in mapping.items() if value)

    if ";" in raw and "=" in raw:
        mapping: dict[str, str] = {}
        for segment in raw.split(";"):
            if not segment.strip() or "=" not in segment:
                continue
            key, value = segment.split("=", 1)
            key_lower = key.strip().lower().replace(" ", "")
            normalized_key = {
                "host": "host",
                "server": "host",
                "port": "port",
                "database": "dbname",
                "dbname": "dbname",
                "username": "user",
                "user": "user",
                "userid": "user",
                "password": "password",
                "sslmode": "sslmode",
            }.get(key_lower)
            if normalized_key:
                normalized_value = value.strip()
                if normalized_key == "sslmode":
                    normalized_value = normalized_value.lower()
                mapping[normalized_key] = normalized_value
        if "sslmode" not in mapping:
            mapping["sslmode"] = "require"
        return " ".join(f"{key}={quote_conn_value(value)}" for key, value in mapping.items() if value)

    return raw


def quote_conn_value(value: str) -> str:
    if any(char.isspace() for char in value) or "'" in value:
        return "'" + value.replace("\\", "\\\\").replace("'", "\\'") + "'"
    return value


def fetch_dataframe(conn: Any, provider: DbProvider, sql: str, params: tuple[Any, ...] | None = None) -> pd.DataFrame:
    params = params or ()
    if provider == "postgres":
        with conn.cursor(row_factory=dict_row) as cur:
            cur.execute(sql, params)
            rows = cur.fetchall()
        return pd.DataFrame(rows)

    conn.row_factory = sqlite3.Row
    cur = conn.execute(sql, params)
    rows = cur.fetchall()
    return pd.DataFrame([dict(row) for row in rows])


def execute(conn: Any, provider: DbProvider, sql: str, params: tuple[Any, ...] | None = None) -> None:
    params = params or ()
    if provider == "postgres":
        with conn.cursor() as cur:
            cur.execute(sql, params)
        conn.commit()
        return

    conn.execute(sql, params)
    conn.commit()


def executemany(conn: Any, provider: DbProvider, sql: str, rows: list[tuple[Any, ...]]) -> None:
    if provider == "postgres":
        with conn.cursor() as cur:
            cur.executemany(sql, rows)
        conn.commit()
        return

    conn.executemany(sql, rows)
    conn.commit()


def fetch_rows(conn: Any, provider: DbProvider, sql: str, params: tuple[Any, ...] | None = None) -> list[dict[str, Any]]:
    params = params or ()
    if provider == "postgres":
        with conn.cursor(row_factory=dict_row) as cur:
            cur.execute(sql, params)
            return [dict(row) for row in cur.fetchall()]

    conn.row_factory = sqlite3.Row
    cur = conn.execute(sql, params)
    return [dict(row) for row in cur.fetchall()]


def load_order_level_dataframe(conn: Any, provider: DbProvider, *, unfulfilled_only: bool) -> pd.DataFrame:
    predicate = "WHERE s.order_id IS NULL" if unfulfilled_only else ""
    sql = f"""
    WITH review_stats AS (
        SELECT
            customer_id,
            COUNT(*) AS review_count,
            AVG(rating) AS avg_rating
        FROM product_reviews
        GROUP BY customer_id
    )
    SELECT
        o.order_id,
        o.customer_id,
        o.order_datetime,
        o.billing_zip,
        o.shipping_zip,
        o.shipping_state,
        o.payment_method,
        o.device_type,
        o.ip_country,
        o.promo_used,
        o.promo_code,
        o.order_subtotal,
        o.shipping_fee,
        o.tax_amount,
        o.order_total,
        o.risk_score,
        o.is_fraud,
        COUNT(oi.order_item_id) AS num_items,
        COUNT(DISTINCT oi.product_id) AS num_unique_skus,
        COUNT(DISTINCT p.category) AS num_categories,
        MAX(p.price) AS max_item_price,
        AVG(p.price) AS avg_item_price,
        COALESCE(SUM(oi.line_total), 0) AS cart_total,
        COALESCE(SUM(p.cost * oi.quantity), 0) AS total_cost,
        MIN(COALESCE(p.is_active, 1)) AS all_products_active,
        s.ship_datetime,
        s.carrier,
        s.shipping_method,
        s.distance_band,
        s.promised_days,
        s.actual_days,
        s.late_delivery,
        c.full_name,
        c.email,
        c.gender,
        c.birthdate,
        c.created_at,
        c.city,
        c.state,
        c.zip_code,
        c.customer_segment,
        c.loyalty_tier,
        c.is_active,
        COALESCE(rs.review_count, 0) AS review_count,
        COALESCE(rs.avg_rating, 0) AS avg_rating
    FROM orders o
    JOIN customers c ON c.customer_id = o.customer_id
    LEFT JOIN shipments s ON s.order_id = o.order_id
    LEFT JOIN order_items oi ON oi.order_id = o.order_id
    LEFT JOIN products p ON p.product_id = oi.product_id
    LEFT JOIN review_stats rs ON rs.customer_id = o.customer_id
    {predicate}
    GROUP BY
        o.order_id,
        o.customer_id,
        o.order_datetime,
        o.billing_zip,
        o.shipping_zip,
        o.shipping_state,
        o.payment_method,
        o.device_type,
        o.ip_country,
        o.promo_used,
        o.promo_code,
        o.order_subtotal,
        o.shipping_fee,
        o.tax_amount,
        o.order_total,
        o.risk_score,
        o.is_fraud,
        s.ship_datetime,
        s.carrier,
        s.shipping_method,
        s.distance_band,
        s.promised_days,
        s.actual_days,
        s.late_delivery,
        c.full_name,
        c.email,
        c.gender,
        c.birthdate,
        c.created_at,
        c.city,
        c.state,
        c.zip_code,
        c.customer_segment,
        c.loyalty_tier,
        c.is_active,
        rs.review_count,
        rs.avg_rating
    ORDER BY o.order_datetime DESC;
    """
    return fetch_dataframe(conn, provider, sql)


def basic_wrangling(df: pd.DataFrame) -> pd.DataFrame:
    df = df.copy()
    df["zip_match"] = (df["billing_zip"] == df["shipping_zip"]).astype(int)
    df["foreign_ip"] = (df["ip_country"] != "US").astype(int)
    drop_cols = [
        "order_id",
        "customer_id",
        "full_name",
        "email",
        "city",
        "state",
        "zip_code",
        "billing_zip",
        "shipping_zip",
        "ip_country",
        "promo_code",
    ]
    return df.drop(columns=[column for column in drop_cols if column in df.columns])


def missing_data(df: pd.DataFrame) -> pd.DataFrame:
    df = df.copy()
    bool_cols = ["promo_used", "all_products_active", "late_delivery", "is_active"]
    if "review_count" in df.columns:
        df["review_count"] = df["review_count"].fillna(0)
    if "avg_rating" in df.columns:
        df["avg_rating"] = df["avg_rating"].fillna(df["avg_rating"].median())
    for column in bool_cols:
        if column in df.columns:
            df[column] = pd.to_numeric(df[column], errors="coerce").fillna(0)

    for column in [col for col in df.select_dtypes(include="object").columns if col not in bool_cols]:
        df[column] = df[column].fillna("missing")
    for column in df.select_dtypes(exclude="object").columns:
        df[column] = df[column].fillna(0)
    return df


def feature_engineering(df: pd.DataFrame) -> pd.DataFrame:
    df = df.copy()
    ref_date = pd.Timestamp("2026-01-01")

    df["order_datetime"] = pd.to_datetime(df["order_datetime"])
    df["order_hour"] = df["order_datetime"].dt.hour
    df["order_dow"] = df["order_datetime"].dt.dayofweek
    df["order_month"] = df["order_datetime"].dt.month

    df["birthdate"] = pd.to_datetime(df["birthdate"])
    df["customer_age"] = ((ref_date - df["birthdate"]).dt.days / 365.25).round(1)

    df["created_at"] = pd.to_datetime(df["created_at"])
    df["account_age_days"] = (ref_date - df["created_at"]).dt.days

    return df.drop(columns=[column for column in ["order_datetime", "birthdate", "created_at", "ship_datetime"] if column in df.columns])


def math_transformations(df: pd.DataFrame) -> pd.DataFrame:
    df = df.copy()
    skewed = [
        "order_subtotal",
        "shipping_fee",
        "tax_amount",
        "order_total",
        "max_item_price",
        "avg_item_price",
        "cart_total",
        "total_cost",
        "num_items",
        "review_count",
    ]
    for column in skewed:
        if column in df.columns:
            df[column] = np.log1p(df[column])
    return df


def manage_outliers(df: pd.DataFrame) -> pd.DataFrame:
    df = df.copy()
    for column in df.select_dtypes(include="number").columns:
        if column == "is_fraud":
            continue
        q1 = df[column].quantile(0.25)
        q3 = df[column].quantile(0.75)
        iqr = q3 - q1
        df[column] = df[column].clip(lower=q1 - 1.5 * iqr, upper=q3 + 1.5 * iqr)
    return df


def encode_categoricals(df: pd.DataFrame) -> pd.DataFrame:
    df = df.copy()
    cat_cols = [
        "shipping_state",
        "payment_method",
        "device_type",
        "carrier",
        "shipping_method",
        "distance_band",
        "gender",
        "customer_segment",
        "loyalty_tier",
    ]
    bool_cols = ["promo_used", "all_products_active", "late_delivery", "is_active"]

    df = pd.get_dummies(df, columns=[column for column in cat_cols if column in df.columns], drop_first=True, dtype=int)
    for column in bool_cols:
        if column in df.columns:
            df[column] = pd.to_numeric(df[column], errors="coerce").fillna(0).astype(int)
    return df


def run_etl(raw_df: pd.DataFrame) -> pd.DataFrame:
    df = basic_wrangling(raw_df)
    df = missing_data(df)
    df = feature_engineering(df)
    df = math_transformations(df)
    df = manage_outliers(df)
    df = encode_categoricals(df)
    return df


def train_and_save_model(model_path: Path = MODEL_PATH) -> dict[str, Any]:
    conn, provider = get_connection_and_provider()
    try:
        fraud_df = load_order_level_dataframe(conn, provider, unfulfilled_only=False)
    finally:
        conn.close()

    prepared = run_etl(fraud_df)
    prepared["is_fraud"] = prepared["is_fraud"].round().astype(int)
    X = prepared.drop(columns=["is_fraud"])
    y = prepared["is_fraud"].astype(int)

    X_train, X_test, y_train, y_test = train_test_split(
        X, y, test_size=0.20, stratify=y, random_state=27
    )

    search = GridSearchCV(
        Pipeline(
            [
                ("scaler", StandardScaler()),
                ("lr", LogisticRegression(max_iter=2000, random_state=27, class_weight="balanced", solver="liblinear")),
            ]
        ),
        param_grid={
            "lr__C": [0.01, 0.1, 1.0, 10.0, 100.0],
        },
        cv=5,
        scoring="roc_auc",
        refit=True,
        n_jobs=1,
        verbose=0,
    )
    search.fit(X_train, y_train)

    test_probabilities = search.best_estimator_.predict_proba(X_test)[:, 1]
    precision, recall, thresholds = precision_recall_curve(y_test, test_probabilities)
    f2_scores = np.where(
        (4 * precision + recall) > 0,
        5 * precision * recall / (4 * precision + recall),
        0,
    )
    valid = recall[:-1] >= FRAUD_THRESHOLD_FLOOR
    if valid.any():
        best_index = int(np.argmax(f2_scores[:-1][valid]))
        threshold = float(thresholds[valid][best_index])
    else:
        threshold = float(thresholds[int(np.argmax(f2_scores[:-1]))])

    final_pipeline = Pipeline(
        [
            ("scaler", StandardScaler()),
            (
                "lr",
                LogisticRegression(
                    max_iter=2000,
                    random_state=27,
                    class_weight="balanced",
                    C=search.best_params_["lr__C"],
                    solver="liblinear",
                ),
            ),
        ]
    )
    final_pipeline.fit(X, y)

    bundle = FraudModelBundle(
        pipeline=final_pipeline,
        feature_columns=X.columns.tolist(),
        threshold=threshold,
        model_name="LogisticRegression",
        trained_at_utc=datetime.now(timezone.utc).isoformat(),
        source_provider=provider,
    )
    joblib.dump(bundle, model_path)

    test_predictions = (test_probabilities >= threshold).astype(int)
    metrics = {
        "model_path": str(model_path),
        "provider": provider,
        "rows_trained": int(len(X)),
        "features": int(X.shape[1]),
        "threshold": round(threshold, 4),
        "roc_auc": round(float(roc_auc_score(y_test, test_probabilities)), 4),
        "average_precision": round(float(average_precision_score(y_test, test_probabilities)), 4),
        "recall": round(float(recall_score(y_test, test_predictions)), 4),
        "precision": round(float(precision_score(y_test, test_predictions, zero_division=0)), 4),
        "f1": round(float(f1_score(y_test, test_predictions, zero_division=0)), 4),
        "best_params": search.best_params_,
        "report": classification_report(y_test, test_predictions, target_names=["Legit", "Fraud"], zero_division=0),
    }
    return metrics


def load_model_bundle(model_path: Path = MODEL_PATH) -> FraudModelBundle:
    if not model_path.exists():
        raise FileNotFoundError(f"Model artifact not found at {model_path}")
    bundle = joblib.load(model_path)
    if not isinstance(bundle, FraudModelBundle):
        raise TypeError("Saved model artifact is not a FraudModelBundle.")
    return bundle


def ensure_prediction_table(conn: Any, provider: DbProvider) -> None:
    rows = fetch_rows(
        conn,
        provider,
        """
        SELECT column_name
        FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'order_predictions'
        """
        if provider == "postgres"
        else "PRAGMA table_info(order_predictions);",
    )

    existing_columns = {
        (row.get("column_name") if provider == "postgres" else row.get("name"))
        for row in rows
    }

    if provider == "postgres" and {"late_delivery_probability", "predicted_late_delivery"}.issubset(existing_columns):
        execute(conn, provider, 'ALTER TABLE order_predictions RENAME COLUMN late_delivery_probability TO fraud_probability;')
        execute(conn, provider, 'ALTER TABLE order_predictions RENAME COLUMN predicted_late_delivery TO predicted_fraud;')
        existing_columns = {"order_id", "fraud_probability", "predicted_fraud", "prediction_timestamp"}
    elif provider == "sqlite" and {"late_delivery_probability", "predicted_late_delivery"}.issubset(existing_columns):
        execute(conn, provider, 'ALTER TABLE order_predictions RENAME COLUMN late_delivery_probability TO fraud_probability;')
        execute(conn, provider, 'ALTER TABLE order_predictions RENAME COLUMN predicted_late_delivery TO predicted_fraud;')
        existing_columns = {"order_id", "fraud_probability", "predicted_fraud", "prediction_timestamp"}

    if provider == "postgres":
        sql = """
        CREATE TABLE IF NOT EXISTS order_predictions (
          order_id BIGINT PRIMARY KEY REFERENCES orders(order_id),
          fraud_probability DOUBLE PRECISION NOT NULL,
          predicted_fraud INTEGER NOT NULL,
          prediction_timestamp TEXT NOT NULL
        );
        """
    else:
        sql = """
        CREATE TABLE IF NOT EXISTS order_predictions (
          order_id INTEGER PRIMARY KEY,
          fraud_probability REAL NOT NULL,
          predicted_fraud INTEGER NOT NULL,
          prediction_timestamp TEXT NOT NULL
        );
        """
    execute(conn, provider, sql)


def score_and_write_predictions(model_path: Path = MODEL_PATH) -> dict[str, Any]:
    bundle = load_model_bundle(model_path)
    conn, provider = get_connection_and_provider()
    try:
        ensure_prediction_table(conn, provider)
        raw_df = load_order_level_dataframe(conn, provider, unfulfilled_only=True)
        if raw_df.empty:
            return {"provider": provider, "orders_scored": 0, "message": "No unfulfilled orders to score."}

        predictions = bundle.predict_raw(raw_df)
        timestamp = datetime.now(timezone.utc).isoformat()
        rows = [
            (
                int(row.order_id),
                float(row.fraud_probability),
                int(row.predicted_fraud),
                timestamp,
            )
            for row in predictions.itertuples(index=False)
        ]

        if provider == "postgres":
            upsert_sql = """
            INSERT INTO order_predictions (order_id, fraud_probability, predicted_fraud, prediction_timestamp)
            VALUES (%s, %s, %s, %s)
            ON CONFLICT (order_id) DO UPDATE SET
              fraud_probability = EXCLUDED.fraud_probability,
              predicted_fraud = EXCLUDED.predicted_fraud,
              prediction_timestamp = EXCLUDED.prediction_timestamp;
            """
        else:
            upsert_sql = """
            INSERT INTO order_predictions (order_id, fraud_probability, predicted_fraud, prediction_timestamp)
            VALUES (?, ?, ?, ?)
            ON CONFLICT(order_id) DO UPDATE SET
              fraud_probability = excluded.fraud_probability,
              predicted_fraud = excluded.predicted_fraud,
              prediction_timestamp = excluded.prediction_timestamp;
            """
        executemany(conn, provider, upsert_sql, rows)
        return {
            "provider": provider,
            "orders_scored": len(rows),
            "threshold": bundle.threshold,
            "model_name": bundle.model_name,
            "message": f"Scored {len(rows)} unfulfilled orders using {bundle.model_name}.",
        }
    finally:
        conn.close()
