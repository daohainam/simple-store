using System.ComponentModel.DataAnnotations;

namespace SimpleStore.Catalog.API.Models;

public class Product
{
    public int Id { get; set; }
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    [MaxLength(500)]
    public string ImageUrl { get; set; } = string.Empty;
    public int Stock { get; set; }
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
}
