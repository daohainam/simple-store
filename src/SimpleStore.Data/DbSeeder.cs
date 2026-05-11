using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Data.Identity;
using SimpleStore.Data.Models;

namespace SimpleStore.Data;

public static class DbSeeder
{
    public const string DemoUserEmail = "demo@simplestore.local";
    public const string DemoUserPassword = "Demo123!";

    public static async Task SeedIdentityAsync(UserManager<ApplicationUser> userManager)
    {
        if (await userManager.FindByEmailAsync(DemoUserEmail) is not null)
        {
            return;
        }

        var user = new ApplicationUser
        {
            UserName = DemoUserEmail,
            Email = DemoUserEmail,
            EmailConfirmed = true,
            FullName = "Demo User"
        };

        await userManager.CreateAsync(user, DemoUserPassword);
    }

    public static async Task SeedAsync(StoreDbContext context)
    {
        await context.Database.MigrateAsync();
        
        if (!await context.Categories.AnyAsync())
        {
            var categories = new List<Category>
            {
                new() { Name = "Electronics", Description = "Electronic devices and accessories" },
                new() { Name = "Clothing", Description = "Fashion and apparel" },
                new() { Name = "Books", Description = "Books and educational materials" },
                new() { Name = "Home & Garden", Description = "Home and garden products" }
            };
            context.Categories.AddRange(categories);
            await context.SaveChangesAsync();
        }
        
        if (!await context.Products.AnyAsync())
        {
            var electronics = await context.Categories.FirstAsync(c => c.Name == "Electronics");
            var clothing = await context.Categories.FirstAsync(c => c.Name == "Clothing");
            var books = await context.Categories.FirstAsync(c => c.Name == "Books");
            var homeGarden = await context.Categories.FirstAsync(c => c.Name == "Home & Garden");
            
            var products = new List<Product>
            {
                new() { Name = "Wireless Headphones", Description = "Premium wireless headphones with noise cancellation", Price = 199.99m, Stock = 50, CategoryId = electronics.Id, ImageUrl = "/images/products/headphones.jpg" },
                new() { Name = "Smartphone Pro", Description = "Latest flagship smartphone with advanced camera", Price = 999.99m, Stock = 30, CategoryId = electronics.Id, ImageUrl = "/images/products/smartphone.jpg" },
                new() { Name = "Laptop Ultra", Description = "High-performance ultrabook for professionals", Price = 1499.99m, Stock = 20, CategoryId = electronics.Id, ImageUrl = "/images/products/laptop.jpg" },
                new() { Name = "Smart Watch", Description = "Feature-rich smartwatch with health monitoring", Price = 299.99m, Stock = 40, CategoryId = electronics.Id, ImageUrl = "/images/products/smartwatch.jpg" },
                new() { Name = "Classic T-Shirt", Description = "Comfortable cotton t-shirt in various colors", Price = 29.99m, Stock = 100, CategoryId = clothing.Id, ImageUrl = "/images/products/tshirt.jpg" },
                new() { Name = "Denim Jeans", Description = "Premium quality denim jeans", Price = 79.99m, Stock = 75, CategoryId = clothing.Id, ImageUrl = "/images/products/jeans.jpg" },
                new() { Name = "ASP.NET Core in Action", Description = "Complete guide to building web apps with ASP.NET Core", Price = 49.99m, Stock = 60, CategoryId = books.Id, ImageUrl = "/images/products/aspnet-book.jpg" },
                new() { Name = "Clean Code", Description = "A Handbook of Agile Software Craftsmanship", Price = 39.99m, Stock = 55, CategoryId = books.Id, ImageUrl = "/images/products/clean-code.jpg" },
                new() { Name = "Garden Tool Set", Description = "Complete set of essential garden tools", Price = 89.99m, Stock = 35, CategoryId = homeGarden.Id, ImageUrl = "/images/products/garden-tools.jpg" },
                new() { Name = "Indoor Plant Kit", Description = "Starter kit for indoor plants with pots and soil", Price = 34.99m, Stock = 45, CategoryId = homeGarden.Id, ImageUrl = "/images/products/plant-kit.jpg" }
            };
            context.Products.AddRange(products);
            await context.SaveChangesAsync();
        }
    }
}
