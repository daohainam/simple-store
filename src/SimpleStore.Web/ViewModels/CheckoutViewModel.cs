using System.ComponentModel.DataAnnotations;
using SimpleStore.Cart.API.Client;

namespace SimpleStore.Web.ViewModels;

public class CheckoutViewModel
{
    [Required]
    [Display(Name = "Full Name")]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Shipping Address")]
    public string ShippingAddress { get; set; } = string.Empty;

    [Required]
    [Display(Name = "City")]
    public string City { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Postal Code")]
    public string PostalCode { get; set; } = string.Empty;

    public List<CartItemDto> CartItems { get; set; } = new();
    public decimal Total { get; set; }
}
