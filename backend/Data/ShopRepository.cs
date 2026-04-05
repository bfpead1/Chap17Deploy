using System.Data.Common;
using backend.Models;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace backend.Data;

public sealed class ShopRepository
{
    private readonly bool _usePostgres;
    private readonly string _sqliteDbPath;
    private readonly string? _postgresConnectionString;

    public ShopRepository(IHostEnvironment env)
    {
        var rawConnectionString =
            Environment.GetEnvironmentVariable("SUPABASE_DB_URL")
            ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Supabase");

        _postgresConnectionString = NormalizePostgresConnectionString(rawConnectionString);
        _usePostgres = !string.IsNullOrWhiteSpace(_postgresConnectionString);
        _sqliteDbPath = ResolveDbPath(env.ContentRootPath);
    }

    public string DbPath => _sqliteDbPath;
    public bool IsUsingPostgres => _usePostgres;

    public List<CustomerDto> GetCustomers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT customer_id, full_name, email FROM customers ORDER BY full_name;";

        var rows = new List<CustomerDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var (first, last) = SplitName(reader.GetString(1));
            rows.Add(new CustomerDto(reader.GetInt64(0), first, last, reader.GetString(2)));
        }

        return rows;
    }

    public CustomerDto? GetCustomer(long customerId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT customer_id, full_name, email FROM customers WHERE customer_id = @customerId;";
        AddParameter(command, "@customerId", customerId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var (first, last) = SplitName(reader.GetString(1));
        return new CustomerDto(reader.GetInt64(0), first, last, reader.GetString(2));
    }

    public DashboardDto? GetDashboard(long customerId)
    {
        using var connection = OpenConnection();
        var customer = GetCustomer(customerId);
        if (customer is null)
        {
            return null;
        }

        using var totalsCmd = connection.CreateCommand();
        totalsCmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(order_total), 0) FROM orders WHERE customer_id = @customerId;";
        AddParameter(totalsCmd, "@customerId", customerId);

        long orderCount;
        double totalSpend;
        using (var totalsReader = totalsCmd.ExecuteReader())
        {
            totalsReader.Read();
            orderCount = totalsReader.GetInt64(0);
            totalSpend = totalsReader.GetDouble(1);
        }

        using var recentCmd = connection.CreateCommand();
        recentCmd.CommandText = @"
            SELECT o.order_id, o.order_datetime,
                   CASE WHEN s.order_id IS NULL THEN 0 ELSE 1 END AS fulfilled,
                   o.order_total
            FROM orders o
            LEFT JOIN shipments s ON s.order_id = o.order_id
            WHERE o.customer_id = @customerId
            ORDER BY o.order_datetime DESC
            LIMIT 5;";
        AddParameter(recentCmd, "@customerId", customerId);

        var recent = new List<OrderSummaryDto>();
        using var recentReader = recentCmd.ExecuteReader();
        while (recentReader.Read())
        {
            recent.Add(new OrderSummaryDto(recentReader.GetInt64(0), recentReader.GetString(1), recentReader.GetInt64(2), recentReader.GetDouble(3)));
        }

        return new DashboardDto(customer, orderCount, totalSpend, recent);
    }

    public List<ProductDto> GetProducts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT product_id, product_name, price FROM products WHERE is_active = 1 ORDER BY product_name;";

        var rows = new List<ProductDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ProductDto(reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2)));
        }

        return rows;
    }

    public PlaceOrderResponseDto PlaceOrder(long customerId, CreateOrderRequest request)
    {
        if (request.Items.Count == 0)
        {
            throw new InvalidOperationException("At least one line item is required.");
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        string? billingZip = null;
        string? shippingZip = null;
        string? shippingState = null;

        using (var customerCmd = connection.CreateCommand())
        {
            customerCmd.Transaction = transaction;
            customerCmd.CommandText = "SELECT zip_code, state FROM customers WHERE customer_id = @customerId;";
            AddParameter(customerCmd, "@customerId", customerId);
            using var reader = customerCmd.ExecuteReader();
            if (reader.Read())
            {
                billingZip = reader.IsDBNull(0) ? null : reader.GetString(0);
                shippingZip = reader.IsDBNull(0) ? null : reader.GetString(0);
                shippingState = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        var lines = new List<(long ProductId, int Quantity, double UnitPrice, double LineTotal)>();
        double subtotal = 0;

        foreach (var item in request.Items)
        {
            if (item.ProductId <= 0 || item.Quantity <= 0)
            {
                throw new InvalidOperationException("Each line item needs a valid productId and quantity > 0.");
            }

            using var priceCmd = connection.CreateCommand();
            priceCmd.Transaction = transaction;
            priceCmd.CommandText = "SELECT price FROM products WHERE product_id = @productId AND is_active = 1;";
            AddParameter(priceCmd, "@productId", item.ProductId);
            var priceObj = priceCmd.ExecuteScalar();
            if (priceObj is null)
            {
                throw new InvalidOperationException($"Product {item.ProductId} does not exist or is inactive.");
            }

            var unitPrice = Convert.ToDouble(priceObj);
            var lineTotal = unitPrice * item.Quantity;
            subtotal += lineTotal;
            lines.Add((item.ProductId, item.Quantity, unitPrice, lineTotal));
        }

        var shippingFee = 0.0;
        var taxAmount = 0.0;
        var total = subtotal;
        var paymentMethod = string.IsNullOrWhiteSpace(request.PaymentMethod) ? "card" : request.PaymentMethod.Trim();
        var deviceType = string.IsNullOrWhiteSpace(request.DeviceType) ? "web" : request.DeviceType.Trim();
        var ipCountry = string.IsNullOrWhiteSpace(request.IpCountry) ? "US" : request.IpCountry.Trim().ToUpperInvariant();
        var promoUsed = request.PromoUsed == true ? 1 : 0;
        var promoCode = promoUsed == 1 && !string.IsNullOrWhiteSpace(request.PromoCode)
            ? request.PromoCode.Trim()
            : null;

        long orderId;

        using var orderCmd = connection.CreateCommand();
        orderCmd.Transaction = transaction;

        if (_usePostgres)
        {
            orderCmd.CommandText = @"
                INSERT INTO orders (
                    customer_id, order_datetime, billing_zip, shipping_zip, shipping_state,
                    payment_method, device_type, ip_country, promo_used, promo_code,
                    order_subtotal, shipping_fee, tax_amount, order_total, risk_score, is_fraud
                )
                VALUES (
                    @customerId, @orderDatetime, @billingZip, @shippingZip, @shippingState,
                    @paymentMethod, @deviceType, @ipCountry, @promoUsed, @promoCode,
                    @orderSubtotal, @shippingFee, @taxAmount, @orderTotal, 0.0, 0
                )
                RETURNING order_id;";
        }
        else
        {
            orderCmd.CommandText = @"
                INSERT INTO orders (
                    customer_id, order_datetime, billing_zip, shipping_zip, shipping_state,
                    payment_method, device_type, ip_country, promo_used, promo_code,
                    order_subtotal, shipping_fee, tax_amount, order_total, risk_score, is_fraud
                )
                VALUES (
                    @customerId, @orderDatetime, @billingZip, @shippingZip, @shippingState,
                    @paymentMethod, @deviceType, @ipCountry, @promoUsed, @promoCode,
                    @orderSubtotal, @shippingFee, @taxAmount, @orderTotal, 0.0, 0
                );";
        }

        AddParameter(orderCmd, "@customerId", customerId);
        AddParameter(orderCmd, "@orderDatetime", timestamp);
        AddParameter(orderCmd, "@billingZip", (object?)billingZip ?? DBNull.Value);
        AddParameter(orderCmd, "@shippingZip", (object?)shippingZip ?? DBNull.Value);
        AddParameter(orderCmd, "@shippingState", (object?)shippingState ?? DBNull.Value);
        AddParameter(orderCmd, "@paymentMethod", paymentMethod);
        AddParameter(orderCmd, "@deviceType", deviceType);
        AddParameter(orderCmd, "@ipCountry", ipCountry);
        AddParameter(orderCmd, "@promoUsed", promoUsed);
        AddParameter(orderCmd, "@promoCode", (object?)promoCode ?? DBNull.Value);
        AddParameter(orderCmd, "@orderSubtotal", subtotal);
        AddParameter(orderCmd, "@shippingFee", shippingFee);
        AddParameter(orderCmd, "@taxAmount", taxAmount);
        AddParameter(orderCmd, "@orderTotal", total);

        if (_usePostgres)
        {
            orderId = Convert.ToInt64(orderCmd.ExecuteScalar() ?? 0L);
        }
        else
        {
            orderCmd.ExecuteNonQuery();
            using var idCmd = connection.CreateCommand();
            idCmd.Transaction = transaction;
            idCmd.CommandText = "SELECT last_insert_rowid();";
            orderId = Convert.ToInt64(idCmd.ExecuteScalar() ?? 0L);
        }

        foreach (var line in lines)
        {
            using var itemCmd = connection.CreateCommand();
            itemCmd.Transaction = transaction;
            itemCmd.CommandText = @"
                INSERT INTO order_items (order_id, product_id, quantity, unit_price, line_total)
                VALUES (@orderId, @productId, @quantity, @unitPrice, @lineTotal);";
            AddParameter(itemCmd, "@orderId", orderId);
            AddParameter(itemCmd, "@productId", line.ProductId);
            AddParameter(itemCmd, "@quantity", line.Quantity);
            AddParameter(itemCmd, "@unitPrice", line.UnitPrice);
            AddParameter(itemCmd, "@lineTotal", line.LineTotal);
            itemCmd.ExecuteNonQuery();
        }

        transaction.Commit();

        return new PlaceOrderResponseDto(orderId, total, "Order placed successfully.");
    }

    public List<OrderSummaryDto> GetOrders(long customerId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT o.order_id, o.order_datetime,
                   CASE WHEN s.order_id IS NULL THEN 0 ELSE 1 END AS fulfilled,
                   o.order_total
            FROM orders o
            LEFT JOIN shipments s ON s.order_id = o.order_id
            WHERE o.customer_id = @customerId
            ORDER BY o.order_datetime DESC;";
        AddParameter(command, "@customerId", customerId);

        var rows = new List<OrderSummaryDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new OrderSummaryDto(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetDouble(3)));
        }

        return rows;
    }

    public List<AdminOrderSummaryDto> GetAdminOrders()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT o.order_id, o.order_datetime,
                   CASE WHEN s.order_id IS NULL THEN 0 ELSE 1 END AS fulfilled,
                   o.order_total,
                   c.customer_id,
                   c.full_name
            FROM orders o
            JOIN customers c ON c.customer_id = o.customer_id
            LEFT JOIN shipments s ON s.order_id = o.order_id
            ORDER BY o.order_datetime DESC;";

        var rows = new List<AdminOrderSummaryDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AdminOrderSummaryDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetDouble(3),
                reader.GetInt64(4),
                reader.GetString(5)));
        }

        return rows;
    }

    public OrderDetailsDto? GetOrderDetails(long customerId, long orderId)
    {
        using var connection = OpenConnection();

        using var orderCmd = connection.CreateCommand();
        orderCmd.CommandText = @"
            SELECT o.order_id, o.order_datetime,
                   CASE WHEN s.order_id IS NULL THEN 0 ELSE 1 END AS fulfilled,
                   o.order_total
            FROM orders o
            LEFT JOIN shipments s ON s.order_id = o.order_id
            WHERE o.customer_id = @customerId AND o.order_id = @orderId;";
        AddParameter(orderCmd, "@customerId", customerId);
        AddParameter(orderCmd, "@orderId", orderId);

        using var orderReader = orderCmd.ExecuteReader();
        if (!orderReader.Read())
        {
            return null;
        }

        var order = new OrderSummaryDto(orderReader.GetInt64(0), orderReader.GetString(1), orderReader.GetInt64(2), orderReader.GetDouble(3));

        using var itemCmd = connection.CreateCommand();
        itemCmd.CommandText = @"
            SELECT p.product_name, oi.quantity, oi.unit_price, oi.line_total
            FROM order_items oi
            JOIN products p ON p.product_id = oi.product_id
            WHERE oi.order_id = @orderId
            ORDER BY p.product_name;";
        AddParameter(itemCmd, "@orderId", orderId);

        var items = new List<OrderItemDto>();
        using var itemReader = itemCmd.ExecuteReader();
        while (itemReader.Read())
        {
            items.Add(new OrderItemDto(itemReader.GetString(0), itemReader.GetInt64(1), itemReader.GetDouble(2), itemReader.GetDouble(3)));
        }

        return new OrderDetailsDto(order, items);
    }

    public PriorityQueueResponseDto GetPriorityQueue()
    {
        using var connection = OpenConnection();
        if (!TableExists(connection, "order_predictions"))
        {
            return new PriorityQueueResponseDto(new List<PriorityQueueRowDto>(), "Table order_predictions does not exist yet. Run your ML inference pipeline first.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT
              o.order_id,
              o.order_datetime AS order_timestamp,
              o.order_total AS total_value,
              CASE WHEN s.order_id IS NULL THEN 0 ELSE 1 END AS fulfilled,
              c.customer_id,
              c.full_name AS customer_name,
              p.fraud_probability,
              p.predicted_fraud,
              p.prediction_timestamp
            FROM orders o
            JOIN customers c ON c.customer_id = o.customer_id
            LEFT JOIN shipments s ON s.order_id = o.order_id
            JOIN order_predictions p ON p.order_id = o.order_id
            WHERE s.order_id IS NULL
            ORDER BY p.fraud_probability DESC, o.order_datetime ASC
            LIMIT 50;";

        var rows = new List<PriorityQueueRowDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PriorityQueueRowDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetDouble(6),
                reader.GetInt64(7),
                reader.GetString(8)));
        }

        return new PriorityQueueResponseDto(rows);
    }

    public SchemaResponseDto GetSchema()
    {
        using var connection = OpenConnection();

        if (_usePostgres)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT table_name, column_name, data_type, is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public'
                ORDER BY table_name, ordinal_position;";

            var tableMap = new Dictionary<string, List<SchemaColumnDto>>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var table = reader.GetString(0);
                var name = reader.GetString(1);
                var type = reader.GetString(2);
                var nullable = string.Equals(reader.GetString(3), "YES", StringComparison.OrdinalIgnoreCase);

                if (!tableMap.TryGetValue(table, out var cols))
                {
                    cols = new List<SchemaColumnDto>();
                    tableMap[table] = cols;
                }

                cols.Add(new SchemaColumnDto(name, type, nullable, false));
            }

            var tables = tableMap
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new SchemaTableDto(kvp.Key, kvp.Value))
                .ToList();

            return new SchemaResponseDto(tables);
        }

        using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";

        var sqliteTables = new List<SchemaTableDto>();
        using var tableReader = tableCmd.ExecuteReader();
        while (tableReader.Read())
        {
            var tableName = tableReader.GetString(0);
            using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA table_info({tableName});";

            var cols = new List<SchemaColumnDto>();
            using var r = pragma.ExecuteReader();
            while (r.Read())
            {
                cols.Add(new SchemaColumnDto(
                    r["name"]?.ToString() ?? string.Empty,
                    r["type"]?.ToString() ?? string.Empty,
                    Convert.ToInt64(r["notnull"]) == 0,
                    Convert.ToInt64(r["pk"]) == 1));
            }

            sqliteTables.Add(new SchemaTableDto(tableName, cols));
        }

        return new SchemaResponseDto(sqliteTables);
    }

    public static bool TryGetCustomerId(HttpContext context, out long customerId)
    {
        customerId = 0;
        return context.Request.Cookies.TryGetValue("customer_id", out var value)
            && long.TryParse(value, out customerId)
            && customerId > 0;
    }

    public static (string FirstName, string LastName) SplitName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return ("Unknown", string.Empty);
        }

        var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return (parts[0], string.Empty);
        }

        return (parts[0], string.Join(' ', parts.Skip(1)));
    }

    private DbConnection OpenConnection()
    {
        if (_usePostgres)
        {
            var pgConn = new NpgsqlConnection(_postgresConnectionString);
            pgConn.Open();
            return pgConn;
        }

        if (!File.Exists(_sqliteDbPath))
        {
            throw new FileNotFoundException($"Database not found at {_sqliteDbPath}");
        }

        var sqlite = new SqliteConnection($"Data Source={_sqliteDbPath}");
        sqlite.Open();
        using var pragma = sqlite.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return sqlite;
    }

    private bool TableExists(DbConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();

        if (_usePostgres)
        {
            cmd.CommandText = @"
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = @tableName
                LIMIT 1;";
            AddParameter(cmd, "@tableName", tableName);
            return cmd.ExecuteScalar() is not null;
        }

        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name = @tableName LIMIT 1;";
        AddParameter(cmd, "@tableName", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string? NormalizePostgresConnectionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            // Accept Supabase URI style: postgresql://user:pass@host:port/db?sslmode=require
            if (raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                {
                    return null;
                }

                var userInfo = uri.UserInfo.Split(':', 2);
                var username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty;
                var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
                var database = uri.AbsolutePath.Trim('/');

                var builder = new NpgsqlConnectionStringBuilder
                {
                    Host = uri.Host,
                    Port = uri.Port > 0 ? uri.Port : 5432,
                    Database = string.IsNullOrWhiteSpace(database) ? "postgres" : database,
                    Username = username,
                    Password = password
                };

                var query = uri.Query.TrimStart('?');
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    var key = Uri.UnescapeDataString(parts[0]);
                    var value = Uri.UnescapeDataString(parts[1]);

                    if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase) &&
                        Enum.TryParse<SslMode>(value, true, out var sslMode))
                    {
                        builder.SslMode = sslMode;
                    }
                }

                if (builder.SslMode == SslMode.Disable)
                {
                    builder.SslMode = SslMode.Require;
                }

                return builder.ConnectionString;
            }

            return raw;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveDbPath(string backendRoot)
    {
        var parentPath = Path.GetFullPath(Path.Combine(backendRoot, "..", "shop.db"));
        if (File.Exists(parentPath))
        {
            return parentPath;
        }

        return Path.Combine(backendRoot, "shop.db");
    }
}
