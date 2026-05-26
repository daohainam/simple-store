namespace SimpleStore.Inventory.API.Client;

public interface IInventoryApiClient
{
    Task<DeliveryNoteDto> CreateDeliveryNoteAsync(CreateDeliveryNoteRequest request, CancellationToken cancellationToken = default);
    Task<DeliveryNoteDto?> GetDeliveryNoteByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResult<DeliveryNoteDto>> GetDeliveryNotesAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    Task<ReceiptNoteDto> CreateReceiptNoteAsync(CreateReceiptNoteRequest request, CancellationToken cancellationToken = default);
    Task<ReceiptNoteDto?> GetReceiptNoteByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResult<ReceiptNoteDto>> GetReceiptNotesAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    Task<PagedResult<StockLevelDto>> GetStockLevelsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);
    Task<StockLevelDto?> GetStockLevelAsync(int productId, CancellationToken cancellationToken = default);
    Task<PagedResult<StockMovementDto>> GetStockMovementsAsync(int productId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);
}
