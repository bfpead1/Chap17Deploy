namespace backend.Models;

public sealed record CustomerDto(long CustomerId, string FirstName, string LastName, string Email);
public sealed record OrderSummaryDto(long OrderId, string OrderTimestamp, long Fulfilled, double TotalValue);
public sealed record AdminOrderSummaryDto(
    long OrderId,
    string OrderTimestamp,
    long Fulfilled,
    double TotalValue,
    long CustomerId,
    string CustomerName);
public sealed record ProductDto(long ProductId, string ProductName, double Price);
public sealed record OrderItemDto(string ProductName, long Quantity, double UnitPrice, double LineTotal);
public sealed record DashboardDto(CustomerDto Customer, long OrderCount, double TotalSpend, List<OrderSummaryDto> RecentOrders);
public sealed record OrderDetailsDto(OrderSummaryDto Order, List<OrderItemDto> Items);
public sealed record PriorityQueueRowDto(
    long OrderId,
    string OrderTimestamp,
    double TotalValue,
    long Fulfilled,
    long CustomerId,
    string CustomerName,
    double FraudProbability,
    long PredictedFraud,
    string PredictionTimestamp);
public sealed record PriorityQueueResponseDto(List<PriorityQueueRowDto> Rows, string? Warning = null);
public sealed record SchemaColumnDto(string Name, string Type, bool Nullable, bool PrimaryKey);
public sealed record SchemaTableDto(string Table, List<SchemaColumnDto> Columns);
public sealed record SchemaResponseDto(List<SchemaTableDto> Tables);
public sealed record SelectCustomerRequest(long CustomerId);
public sealed record OrderLineItemRequest(long ProductId, int Quantity);
public sealed record CreateOrderRequest(
    List<OrderLineItemRequest> Items,
    string? PaymentMethod = null,
    string? DeviceType = null,
    string? IpCountry = null,
    bool? PromoUsed = null,
    string? PromoCode = null);
public sealed record PlaceOrderResponseDto(long OrderId, double TotalValue, string Message);
public sealed record ScoringResultDto(bool Success, DateTime Timestamp, int? ExitCode = null, int? OrdersScored = null, string? Stdout = null, string? Stderr = null, string? Message = null);
