# Shop Web App (Frontend + Backend)

This repo is split into:

- `frontend/`: React + TypeScript (TSX) UI
- `backend/`: ASP.NET Core API using SQLite by default, or Supabase Postgres when configured

The app reads/writes:
- SQLite file `shop.db` (default fallback)
- Supabase Postgres when `SUPABASE_DB_URL` (or `POSTGRES_CONNECTION_STRING`) is set

## Required Files

- `shop.db` at `./shop.db` (only needed for SQLite mode)
- inference script at: `./jobs/run_inference.py`
- notebook at: `./Pipeline.ipynb`

## Database Connection Notes

- The ASP.NET backend resolves SQLite from the repo root as `./shop.db` when `SUPABASE_DB_URL` is not set.
- The Jupyter notebook should also connect to the same repo-root file with `sqlite3.connect('shop.db')`.
- This keeps the web app and notebook pointed at the same operational database during local development.

## Backend Setup (.NET)

```powershell
cd backend
dotnet restore
dotnet run
```

Default backend API base is typically `http://localhost:5000` (or the URL shown in terminal).

### Supabase Mode

Set your Supabase/Postgres connection string before starting backend:

```powershell
$env:SUPABASE_DB_URL="Host=...;Port=5432;Database=postgres;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true"
cd backend
dotnet run
```

Notes:
- If `SUPABASE_DB_URL` is set, backend uses Postgres.
- If not set, backend falls back to SQLite `shop.db`.
- Supabase tables/columns must match what the app queries (`customers`, `orders`, `order_items`, `products`, `shipments`, optional `order_predictions`).

### Quick Start Scripts (PowerShell)
###
From repo root:

```powershell
.\start-backend-supabase.ps1
```

This prompts for DB password and starts backend on `http://localhost:5000` using Supabase.

In a second terminal:

```powershell
.\start-frontend.ps1
```

while backend is runing you can run: Invoke-RestMethod http://localhost:5000/api/health
to double check that it is running through supabase. If postgres = using supabase, if sqlite = using local shop.db
###

You can override defaults:

```powershell
.\start-backend-supabase.ps1 -ProjectRef "<your-project-ref>" -Port 5432
.\start-frontend.ps1 -ApiBase "http://localhost:5000"
```

### One-Time Data Migration (shop.db -> Supabase)

A migration tool is included at `backend/tools/ShopDbToSupabase`.

Run it like this:

```powershell
$env:SUPABASE_DB_URL="Host=...;Port=5432;Database=postgres;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true"
dotnet run --project backend/tools/ShopDbToSupabase --configuration Release -- --sqlite shop.db
```

What it does:
- Creates required tables in Supabase (if missing)
- Truncates target tables and resets identities
- Copies all rows from SQLite tables:
  - `customers`
  - `products`
  - `orders`
  - `order_items`
  - `shipments`
  - `product_reviews`
- Resets Postgres sequences to max IDs

## Frontend Setup (TSX)

```powershell
cd frontend
npm install
npm run dev
```

Set API URL if needed:

```powershell
$env:VITE_API_BASE="http://localhost:5000"
npm run dev
```

## Features Implemented

- Select customer and store `customer_id` in cookie
- Customer dashboard with stats and recent orders
- Place order with line items and validation
- Order history and order detail pages
- Fraud review priority queue (top 50 unfulfilled orders)
- Run scoring button (`python jobs/run_inference.py`) with timeout/stdout/stderr handling
- Debug schema page (`/debug/schema`) that lists tables + columns

## Manual QA Checklist

1. Open `/select-customer` and choose a customer.
2. Confirm selected customer banner updates.
3. Place an order at `/place-order`.
4. Verify success message and new order in `/admin/orders`.
5. Run scoring in `/scoring`.
6. Confirm `/api/warehouse/priority` returns refreshed fraud prediction rows.
7. Open `/debug/schema` and verify tables/columns.

## API Endpoints (Backend)

- `GET /api/customers`
- `POST /api/select-customer`
- `GET /api/customer/current`
- `GET /api/dashboard`
- `GET /api/products`
- `POST /api/orders`
- `GET /api/orders`
- `GET /api/orders/{orderId}`
- `GET /api/warehouse/priority`
- `POST /api/scoring/run`
- `GET /api/debug/schema`
