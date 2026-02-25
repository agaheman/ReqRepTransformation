using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SampleApiTestApp.Controllers;

/// <summary>
/// Orders controller — demonstrates JSON body transformation.
/// When POST /api/orders is called, the pipeline adds:
/// - X-Correlation-Id header
/// - X-Request-Id header
/// - X-User-Id header (from JWT sub claim)
/// - _gateway metadata object to the JSON body
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController : ControllerBase
{
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(ILogger<OrdersController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Create an order. The request body will have _gateway metadata injected
    /// by the transformation pipeline before this endpoint receives it.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateOrder([FromBody] JsonElement body)
    {
        // Log the correlation ID that the pipeline injected
        var correlationId = Request.Headers["X-Correlation-Id"].ToString();
        var userId = Request.Headers["X-User-Id"].ToString();

        _logger.LogInformation(
            "Processing order | CorrelationId={CorrelationId} UserId={UserId}",
            string.IsNullOrEmpty(correlationId) ? "none" : correlationId,
            string.IsNullOrEmpty(userId) ? "anonymous" : userId);

        // Echo back the received body (including _gateway metadata from pipeline)
        return CreatedAtAction(nameof(GetOrder), new { id = Guid.NewGuid() }, new OrderResponse
        {
            OrderId    = Guid.NewGuid(),
            Status     = "Created",
            ReceivedBody = body,
            Headers = new
            {
                CorrelationId = correlationId,
                UserId        = userId,
                RequestId     = Request.Headers["X-Request-Id"].ToString()
            }
        });
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    public IActionResult GetOrder(Guid id)
    {
        return Ok(new OrderResponse
        {
            OrderId = id,
            Status  = "Found",
            Headers = new
            {
                CorrelationId = Request.Headers["X-Correlation-Id"].ToString()
            }
        });
    }
}

/// <summary>
/// Products controller — demonstrates path rewrite.
/// /api/products/* is rewritten to /catalog/* by the pipeline.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ProductsController : ControllerBase
{
    /// <summary>
    /// List products. The pipeline rewrites /api/products → /catalog.
    /// In a real gateway this would forward to the catalog service.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ProductDto>), StatusCodes.Status200OK)]
    public IActionResult GetProducts()
    {
        return Ok(new[]
        {
            new ProductDto { Id = 1, Name = "Widget A", Price = 9.99m },
            new ProductDto { Id = 2, Name = "Widget B", Price = 19.99m },
            new ProductDto { Id = 3, Name = "Widget C", Price = 29.99m }
        });
    }
}

/// <summary>
/// Admin controller — demonstrates Authorization stripping and internal key injection.
/// The pipeline removes the JWT and adds X-Internal-Key before forwarding.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class AdminController : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType(typeof(AdminStatus), StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        return Ok(new AdminStatus
        {
            HasInternalKey    = Request.Headers.ContainsKey("X-Internal-Key"),
            HasAuthorization  = Request.Headers.ContainsKey("Authorization"),
            InternalKeyPrefix = Request.Headers["X-Internal-Key"].ToString().Length > 6
                ? Request.Headers["X-Internal-Key"].ToString()[..6] + "..."
                : "absent",
            CorrelationId     = Request.Headers["X-Correlation-Id"].ToString()
        });
    }
}

/// <summary>
/// Diagnostics endpoint — echoes all received headers.
/// Useful for verifying what the pipeline injected / removed.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class DiagnosticsController : ControllerBase
{
    [HttpGet("headers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHeaders()
    {
        var headers = Request.Headers
            .Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value.ToString());

        return Ok(new
        {
            Method  = Request.Method,
            Path    = Request.Path.ToString(),
            Headers = headers
        });
    }

    [HttpPost("echo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Echo([FromBody] JsonElement body)
    {
        var headers = Request.Headers
            .Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value.ToString());

        return Ok(new
        {
            Method  = Request.Method,
            Path    = Request.Path.ToString(),
            Headers = headers,
            Body    = body
        });
    }
}

// ──────────────────────────────────────────────────────────────────
// DTOs
// ──────────────────────────────────────────────────────────────────

public sealed record OrderResponse
{
    public Guid OrderId { get; init; }
    public string Status { get; init; } = string.Empty;
    public object? ReceivedBody { get; init; }
    public object? Headers { get; init; }
}

public sealed record ProductDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public decimal Price { get; init; }
}

public sealed record AdminStatus
{
    public bool HasInternalKey { get; init; }
    public bool HasAuthorization { get; init; }
    public string InternalKeyPrefix { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
}
